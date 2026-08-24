// Owns the WheelLedChannel and decides when/what to push to the rim LEDs.
//
// Two gates feed OnFrame (both computed in TrueforcePlugin.DataUpdate):
//   * Mode B: rev lights whenever Telemetry Based FFB is armed and the FFB
//     tap PROVES the game's own FFB quiet (fail-closed). User-toggleable via
//     TrueforceSettings.ModeBRevLightsEnabled (default on) for games that
//     drive the wheel's lights natively.
//   A second, iRacing-specific gate rode an external FFB handoff that has
//   been removed; native iRacing telemetry FFB will bring its own.
//
// The HID++ channel open is a probe (enumerate + getFeature with timeouts) so
// it can take a beat; it runs once on a background task, never on SimHub's
// DataUpdate thread. Live updates are bucket-quantized and rate-limited so we
// only hit the wheel when the visible bar actually changes.

using System;
using System.Threading;
using System.Threading.Tasks;
using TrueforceForAll.Core;

namespace TrueforceForAll.Plugin
{
    public sealed class RpmLedController : IDisposable
    {
        private readonly Action<string> _log;
        private readonly WheelLedChannel _channel;

        /// <summary>The underlying HID++ channel, for read-only diagnostics that
        /// need to question the wheel directly (the slot-feature probe).</summary>
        public WheelLedChannel Channel => _channel;

        private int _openState;        // 0=idle 1=opening 2=open-ok 3=open-failed
        private int _openFails;        // completed failed open attempts (auto path only)
        private long _nextOpenRetryMs; // no automatic re-probe before this
        private const int OpenRetryCooldownMs = 30_000;
        private const int OpenMaxAttempts = 5;
        private int _lastBucket = -1;  // last LED count pushed (0..10), -1 = none
        private bool _lastRedline;
        private long _lastPushTicks;
        /// <summary>Who currently owns the strip. One token instead of a pair of
        /// booleans that had to be read together: every guard used to spell out
        /// its own combination of them, and each time one was wrong it showed up
        /// as a separate bug (a pattern pick refused mid-preview, a preview
        /// dropped mid-pick). Priority runs None, then Preview, then Test.</summary>
        private volatile int _owner;      // OwnerNone / OwnerPreview / OwnerTest
        private volatile bool _testing;   // mirrors _owner != OwnerNone for the status poll
        private volatile string _testStatus = "";
        /// <summary>Bumped by every preview so a running one can tell it has been
        /// superseded and stand down.</summary>
        private volatile int _previewGen;
        private const int OwnerNone = 0, OwnerPreview = 1, OwnerTest = 2;

        /// <summary>The Test sweep is a hardware check the user asked for and is
        /// watching, so it keeps the strip until it ends. A preview exists only
        /// to show a pick, so it gives way to the next one.</summary>
        private bool TestOwnsLeds => _owner == OwnerTest;

        /// <summary>Bumped by every deliberate lighting action so a pending
        /// auto-off knows it has been superseded and stands down.</summary>
        private volatile int _holdGen;

        private void TakeStrip(int owner) { _owner = owner; _testing = owner != OwnerNone; }
        private void DropStrip()          { _owner = OwnerNone; _testing = false; }

        // Don't pound the wheel: at most ~50 Hz, and only when the visible
        // state changed. A full rev sweep is ~10 discrete steps so this is
        // plenty smooth while keeping HID++ traffic minimal.
        private const long MinPushIntervalMs = 20;

        public RpmLedController(Action<string> log)
        {
            _log = log ?? (_ => { });
            _channel = new WheelLedChannel(_log);
        }

        public bool IsReady => _channel.IsReady;
        public bool IsTesting => _testing;

        /// <summary>True while the gate is open and frames are being pushed,
        /// i.e. the live bar itself will render a pattern change and a
        /// preview sweep would only interrupt it.</summary>
        public bool IsDriving => _lastBucket != -1 && _channel.IsReady;

        /// <summary>The live bar is actually LIT, so a preview would fight
        /// something the user can see.
        ///
        /// Not the same question as IsDriving, which only means a level has been
        /// pushed at least once since the gate opened. Sitting in a car at idle
        /// pushes level 0, so IsDriving is true while the strip is dark, and
        /// gating the preview on it meant switching pattern in the pits gave a
        /// flicker and then nothing: the write repainted, the live bar reasserted
        /// zero, and the user never saw what they picked.</summary>
        public bool LiveBarIsLit => _lastBucket > 0 && _channel.IsReady;
        // The open state used to append WheelLedChannel.ResolvedInfo, i.e. the
        // HID++ feature index and the device path. That is log material, not
        // panel material, and the log already carries the same string from the
        // open itself ("[RPM-LED] resolved ..."). Same change as the OLED
        // status one section down the tab.
        public string Status =>
            _testing          ? _testStatus
          : _openState == 2 ? "open"
          : _openState == 3 ? "channel not found (see log)"
          : _openState == 1 ? "opening…"
          : "idle";

        /// <summary>Called every telemetry frame. <paramref name="gateOpen"/>
        /// is the iRacing + setting-enabled gate. Off-gate releases the LEDs
        /// once (so a stale bar doesn't linger) and does nothing else.</summary>
        /// <summary>Optional per-car fill curve. Given the raw revs and this
        /// wheel's step count, returns the level to light, or null to say nothing
        /// about this frame. Set by the plugin when the active car has published
        /// rev-light data and the user has turned that on; null the rest of the
        /// time, which leaves every existing behaviour untouched.
        ///
        /// Takes revs rather than the percentage because the whole point is the
        /// car's own switch-on RPMs: a percentage has already been flattened
        /// through somebody else's rev-band model and cannot be un-flattened.</summary>
        public Func<double, int, int?> LevelCurve { get; set; }

        /// <summary>Half-period of the redline flash in milliseconds, i.e. how
        /// long the bar stays on before it goes off. 185 ms (about 2.7 Hz) is the
        /// default and matches iRacing's shift blink.
        ///
        /// Set from the active car's own published blink rate when it has one, so
        /// a car that flashes fast flashes fast here. Unlike the pulse rate this
        /// is an exact match for the published figure: it IS the redline blink.</summary>
        public int RedlineBlinkHalfMs { get; set; } = 185;

        /// <summary>The level most recently computed, whether or not it reached
        /// the wheel. The dash mirror reads this: it wants what the strip WOULD
        /// show, including in games where nothing is written to the hardware.</summary>
        public int LastLevel { get; private set; }

        public void OnFrame(double rpmPercent, double rpms, double maxRpm, bool redline, bool gateOpen)
        {
            if (_testing) return;   // test sweep owns the LEDs while it runs

            if (!gateOpen)
            {
                // Release the wheel when the gate closes (game switch, pause,
                // Mode B disarm). Clear() also STOPS the keepalive thread, so
                // we stop writing entirely and whatever drives this game's LEDs
                // natively (or SimHub) has the wheel to itself. Fire on any
                // prior drive, not just level > 0: leaving during the redline
                // flash's off-phase (last level 0) must still stop the
                // keepalive, else it keeps resending 0 and fights the native
                // writer. Once cleared (_lastBucket = -1) this no-ops.
                if (_lastBucket != -1 && _channel.IsReady)
                {
                    try { _channel.Clear(); } catch { }
                }
                _lastBucket = -1;
                return;
            }

            if (!EnsureOpening()) return;
            if (!_channel.IsReady) return;

            // Trust rpmPercent as-is. The SimHub source already computes the
            // sim-matched rev-band fill AND owns the fallback chain (shift
            // band -> displayed% -> rpm/max). 0 is a LEGITIMATE value here
            // (below shift-light onset = lights off); the old "pct<=0 ->
            // rpm/maxRpm" fallback clobbered that, lighting ~1 LED at idle
            // and looping at low revs. Do not second-guess the source.
            Push(rpmPercent, redline, force: false, rpms: rpms);
        }

        private int _hystLevel = -1;

        private void Push(double pct, bool redline, bool force, double rpms = double.NaN)
        {
            long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            // Per-wheel step count from fn0: 10 on the G PRO/RS50, 5 on a
            // G923. Scaling to the wheel's own range keeps a 50% fill half a
            // bar on every wheel instead of clipping at the channel.
            int steps = _channel.StripLength;
            int target;
            if (redline)
            {
                // Peak: blink the full bar instead of holding it solid. The rate
                // is the car's own where it publishes one, else iRacing's ~2.7 Hz.
                long half = RedlineBlinkHalfMs > 0 ? RedlineBlinkHalfMs : 185L;
                bool on = ((nowMs / half) & 1L) == 0L;
                target = on ? steps : 0;
            }
            else
            {
                // Per-car curve first when one is set. It already returns whole
                // levels derived from the car's own switch-on points, so the
                // fractional hysteresis below does not apply; a threshold is a
                // hard edge, and the push-rate limit further down still absorbs
                // jitter around one. Any failure falls through to the ramp rather
                // than dropping the frame.
                int? curved = null;
                var curve = LevelCurve;
                if (curve != null && !double.IsNaN(rpms))
                {
                    try { curved = curve(rpms, steps); }
                    catch { curved = null; }
                }
                if (curved.HasValue)
                {
                    int cl = curved.Value;
                    if (cl < 0) cl = 0; else if (cl > steps) cl = steps;
                    _hystLevel = cl;
                    Commit(cl, redline, force, nowMs);
                    return;
                }

                double scaled = pct * steps;
                int lvl = (int)Math.Floor(scaled + 0.5);
                if (lvl < 0) lvl = 0;
                else if (lvl > steps) lvl = steps;
                // Hysteresis: need ~0.55 LED past the boundary to change, so a
                // steady RPM with telemetry jitter doesn't flicker the bar
                // (the mid-RPM flashing the user saw).
                if (_hystLevel >= 0)
                {
                    if (lvl > _hystLevel && scaled < _hystLevel + 0.55) lvl = _hystLevel;
                    else if (lvl < _hystLevel && scaled > _hystLevel - 0.55) lvl = _hystLevel;
                }
                _hystLevel = lvl;
                target = lvl;
            }

            Commit(target, redline, force, nowMs);
        }

        /// <summary>Write a level to the wheel, subject to the change and rate
        /// gates. Shared by the ramp and the per-car curve so both take exactly
        /// the same path onto the wire.</summary>
        private void Commit(int target, bool redline, bool force, long nowMs)
        {
            // Recorded before the gates below, so the dash still mirrors the bar
            // in a game where the write itself never happens.
            LastLevel = target;
            bool changed = target != _lastBucket || redline != _lastRedline;
            if (!force && !changed) return;
            if (!force && (nowMs - _lastPushTicks) < MinPushIntervalMs && !redline) return;

            try { _channel.SetLevel(target); }
            catch (Exception ex) { _log($"[RPM-LED] push error: {ex.Message}"); }

            _lastBucket = target;
            _lastRedline = redline;
            _lastPushTicks = nowMs;
        }

        /// <summary>Kick a background open if we haven't yet. Returns true once
        /// an open attempt has completed successfully. A failed attempt is
        /// retried on a cooldown a few times before latching for the session,
        /// mirroring OledDashController: losing the HID++ pipe to another
        /// talker at startup is not proof the wheel lacks the feature.</summary>
        private bool EnsureOpening()
        {
            int prev = Interlocked.CompareExchange(ref _openState, 1, 0);
            if (prev == 3
                && _openFails < OpenMaxAttempts
                && NowMs() >= _nextOpenRetryMs
                && Interlocked.CompareExchange(ref _openState, 1, 3) == 3)
                prev = 0;   // claimed the retry slot: kick a fresh probe below
            if (prev == 0)
            {
                Task.Run(() =>
                {
                    bool ok = false;
                    try { ok = _channel.OpenAndResolve(); }
                    catch (Exception ex) { _log($"[RPM-LED] open threw: {ex.Message}"); }
                    if (ok) _openFails = 0;
                    else
                    {
                        _openFails++;
                        _nextOpenRetryMs = NowMs() + OpenRetryCooldownMs;
                        if (_openFails < OpenMaxAttempts)
                            _log($"[RPM-LED] open attempt {_openFails} failed; retrying in "
                                 + $"{OpenRetryCooldownMs / 1000} s");
                        else
                            _log($"[RPM-LED] open failed {_openFails} times; giving up for this session.");
                    }
                    Interlocked.Exchange(ref _openState, ok ? 2 : 3);
                });
                return false;
            }
            return prev == 2;
        }

        private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>Simulated rev + shift sweep for the settings "Test" button.
        /// Forces the channel open regardless of game (so the user can verify
        /// hardware with nothing running) and drives the bar directly. Returns
        /// the total duration in ms (0 if the channel can't be opened).</summary>
        public int RunTest()
        {
            if (TestOwnsLeds) return 0;
            // A preview is only showing a pick, so the Test button takes the
            // strip off it. Cancelling first also stops the superseded sweep
            // clearing _testing out from under the test.
            CancelPreview();

            // Rev-level sweep using the real (captured) G PRO protocol: walk
            // the level 0..10..0 a couple of times, then a brief redline hold.
            // Colors / direction come from the wheel's own profile (the user
            // set outside-in); we only drive how many LEDs.
            const int stepMs   = 220;   // a touch slower than the ~156 ms keepalive
            const int redlineMs = 1500;
            // Exact once the channel is open; an upper bound before that
            // (StripLength is only known post-open, and a G923 sweeps 5
            // steps, not 10). The UI stops polling off RpmLedIsTesting, so
            // overestimating only pads its fallback timer.
            int stepsGuess = _channel.IsReady ? _channel.StripLength : WheelLedChannel.LedCount;
            int total = (2 * (2 * stepsGuess + 1)) * stepMs + redlineMs + 400;

            // Mark testing BEFORE returning so the UI's status-poll timer sees the
            // test immediately (it self-stops ~1 s after RpmLedIsTesting clears).
            TakeStrip(OwnerTest);
            Task.Run(() =>
            {
                bool opened = _channel.IsReady;
                try
                {
                    // Open here, not on the caller's (UI) thread: OpenAndResolve can
                    // block up to ~3 s on a wheel that doesn't answer. Failure
                    // surfaces via _testStatus, which the panel is already polling.
                    if (!opened)
                    {
                        bool ok;
                        try { ok = _channel.OpenAndResolve(); }
                        catch (Exception ex) { _log($"[RPM-LED] test open threw: {ex.Message}"); ok = false; }
                        Interlocked.Exchange(ref _openState, ok ? 2 : 3);
                        opened = ok;
                    }
                    if (!opened)
                    {
                        _testStatus = "could not open the LED channel (see log)";
                        _log("[RPM-LED] Test: could not open the LED channel. " +
                             "Check the log above for which interfaces were probed.");
                        return;
                    }

                    // The wheel's own step count (10 G PRO/RS50, 5 G923),
                    // known once the channel resolved.
                    int steps = _channel.StripLength;
                    for (int cycle = 0; cycle < 2 && _channel.IsReady; cycle++)
                    {
                        for (int lvl = 0; lvl <= steps && _channel.IsReady; lvl++)
                        {
                            _testStatus = $"▶ rev sweep - level {lvl}/{steps}";
                            _channel.SetLevel(lvl);
                            Thread.Sleep(stepMs);
                        }
                        for (int lvl = steps - 1; lvl >= 0 && _channel.IsReady; lvl--)
                        {
                            _testStatus = $"▶ rev sweep - level {lvl}/{steps}";
                            _channel.SetLevel(lvl);
                            Thread.Sleep(stepMs);
                        }
                    }
                    if (_channel.IsReady)
                    {
                        _testStatus = "▶ redline (all LEDs)";
                        _log($"[RPM-LED] Test: redline hold (level {steps})");
                        _channel.SetLevel(steps);
                        Thread.Sleep(redlineMs);
                    }
                }
                catch (Exception ex) { _log($"[RPM-LED] test error: {ex.Message}"); }
                finally
                {
                    // Only tidy up / claim "finished" when we actually opened;
                    // the open-failure path keeps its own status message.
                    if (opened)
                    {
                        try { _channel.TurnOff(); } catch { }
                        _lastBucket = -1;
                        _testStatus = "test finished - LEDs off";
                        _log("[RPM-LED] Test: finished, LEDs off (level 0).");
                    }
                    DropStrip();
                }
            });
            return total;
        }

        /// <summary>The wheel's pattern selection as last read or written
        /// (0 = not seen yet). What both settings pickers display.</summary>
        public int KnownSelection => _channel.KnownSelection;

        /// <summary>Kick the background open (which reads the wheel's
        /// selection as part of resolving) without waiting for the gate or a
        /// button. Idempotent: EnsureOpening's own state machine handles
        /// already-open / opening / retry-cooldown / latched.</summary>
        public void OpenInBackground() => EnsureOpening();

        /// <summary>Set the wheel's pattern selection now, opening the
        /// channel first if nothing has yet (so a pick works cold, like the
        /// preview). Blocking (a cold open can take seconds); callers run it
        /// off the UI thread. Caller decides the pipe is safe.</summary>
        public bool SelectPatternNow(int effect)
        {
            // Never mid-TEST. A preview is a different matter: picking down a
            // list is exactly when previews run, so refusing here meant the
            // cycle binding silently dropped presses until the sweep ended.
            // The pick is NOT cancelled into the preview, it lands through the
            // channel's live switch path and shows up mid-sweep.
            if (TestOwnsLeds) return false;
            if (!_channel.IsReady)
            {
                bool ok;
                try { ok = _channel.OpenAndResolve(); }
                catch (Exception ex) { _log($"[RPM-LED] pattern-select open threw: {ex.Message}"); ok = false; }
                Interlocked.Exchange(ref _openState, ok ? 2 : 3);
                if (!ok) return false;
            }
            return _channel.SelectPatternNow(effect);
        }

        /// <summary>A car with a remembered pattern just became active:
        /// stage it (the next arm applies it regardless), and land it now
        /// when the caller says the pipe is writable and the channel is up.</summary>
        public void ApplyRemembered(int effect, bool canWriteNow)
        {
            try
            {
                _channel.StagePattern(effect);
                if (canWriteNow && _channel.IsReady && !_testing)
                    _channel.SelectPatternNow(effect);
            }
            catch (Exception ex) { _log($"[RPM-LED] remembered-pattern apply failed: {ex.Message}"); }
        }

        /// <summary>Remember a pick for the next arm (unsafe-to-write-now
        /// path; the log explains at the call site).</summary>
        public void StagePattern(int effect)
        {
            try { _channel.StagePattern(effect); } catch { }
        }

        /// <summary>One rev cycle (0 -> full -> 0) so a pattern pick shows on
        /// the wheel the moment it is made. The CALLER decides it is safe to
        /// write (no game telemetry arriving); this only keeps itself off the
        /// Test button's toes via the same _testing latch. The arm applies
        /// the stored pattern override and the closing TurnOff restores the
        /// wheel's own selection, so the preview leaves no trace. A pick made
        /// while a preview is still sweeping applies live mid-sweep through
        /// the channel's own switch path, so no re-entry is needed.</summary>
        /// <summary>Stand down any running preview WITHOUT blanking the strip.
        /// For callers that are about to drive the LEDs themselves (a slot
        /// write): the preview's level loop would otherwise keep stepping and
        /// stomp the level the write just set. Deliberately does not TurnOff,
        /// because the caller wants what it is about to write to be visible.
        /// Harmless when no preview is running.</summary>
        public void CancelPreview()
        {
            if (_owner != OwnerPreview) return;
            System.Threading.Interlocked.Increment(ref _previewGen);
            DropStrip();
            _testStatus = "";
        }

        /// <param name="endLevel">How many LEDs to leave lit when the sweep
        /// finishes, or -1 to turn the strip off. Parked, a pick that ends dark
        /// shows the user nothing, which is the whole reason they pressed.</param>
        /// <param name="holdMs">How long to leave the strip lit after the sweep
        /// before fading it out, or 0 to leave it lit indefinitely.</param>
        public int PreviewPattern(int endLevel = -1, int holdMs = 0)
        {
            const int stepMs = 180;   // just above the channel's 160 ms change floor

            // The Test sweep keeps the strip; a preview does not.
            if (TestOwnsLeds) return 0;

            // A new preview INTERRUPTS the one running. Clicking down a list of
            // patterns is the normal way to use this, and dropping the request
            // because the previous sweep had not finished meant you watched the
            // OLD pattern play while the new one was already selected. Each run
            // takes a generation number and stops as soon as it is superseded.
            int gen = System.Threading.Interlocked.Increment(ref _previewGen);
            TakeStrip(OwnerPreview);
            Task.Run(() =>
            {
                bool opened = _channel.IsReady;
                try
                {
                    if (!opened)
                    {
                        bool ok;
                        try { ok = _channel.OpenAndResolve(); }
                        catch (Exception ex) { _log($"[RPM-LED] preview open threw: {ex.Message}"); ok = false; }
                        Interlocked.Exchange(ref _openState, ok ? 2 : 3);
                        opened = ok;
                    }
                    if (!opened)
                    {
                        _testStatus = "could not open the LED channel (see log)";
                        return;
                    }

                    int steps = _channel.StripLength;
                    for (int lvl = 0; lvl <= steps && _channel.IsReady && _previewGen == gen; lvl++)
                    {
                        _testStatus = $"▶ pattern preview - level {lvl}/{steps}";
                        _channel.SetLevel(lvl);
                        Thread.Sleep(stepMs);
                    }
                    for (int lvl = steps - 1; lvl >= 0 && _channel.IsReady && _previewGen == gen; lvl--)
                    {
                        _testStatus = $"▶ pattern preview - level {lvl}/{steps}";
                        _channel.SetLevel(lvl);
                        Thread.Sleep(stepMs);
                    }
                }
                catch (Exception ex) { _log($"[RPM-LED] preview error: {ex.Message}"); }
                finally
                {
                    // Only the CURRENT run tidies up. A superseded one must not
                    // blank the strip or drop the testing flag, or it would put
                    // the lights out from under the preview that replaced it.
                    if (_previewGen == gen)
                    {
                        if (opened)
                        {
                            // Settle where the caller asked. A pick made from the
                            // settings tab wants the bar left LIT so the colours
                            // can be looked at; the Test button wants the wheel
                            // handed back dark.
                            try
                            {
                                if (endLevel >= 0) _channel.SetLevel(Math.Min(endLevel, _channel.StripLength));
                                else _channel.TurnOff();
                            }
                            catch { }
                            _lastBucket = -1;
                            _testStatus = "";
                        }
                        DropStrip();
                        // Armed AFTER the owner is dropped, so the auto-off can
                        // see that nothing else has taken the strip.
                        if (endLevel >= 0) ArmAutoOff(holdMs);
                    }
                }
            });

            int stepsGuess = _channel.IsReady ? _channel.StripLength : WheelLedChannel.LedCount;
            return (2 * stepsGuess + 1) * stepMs + 300;
        }

        /// <summary>Light the strip for a deliberate action and start the clock on
        /// putting it out again.
        ///
        /// Re-arming is the whole point: keep working and it stays lit, stop and it
        /// goes out by itself a few seconds later. A parked wheel holding a full
        /// bar indefinitely is wrong, but going dark while someone is choosing
        /// colours is worse.</summary>
        public void HoldLit(int level, int holdMs)
        {
            if (TestOwnsLeds) return;
            // LIT, not merely "a game is loaded". At idle the strip is dark and
            // there is nothing to fight, and refusing here meant editing a
            // colour in the pits showed the user nothing.
            if (LiveBarIsLit) return;
            if (!_channel.IsReady) return;
            try { _channel.SetLevel(Math.Max(0, Math.Min(level, _channel.StripLength))); } catch { }
            ArmAutoOff(holdMs);
        }

        /// <summary>Fade the strip out after a quiet period. The fade is a level
        /// ramp rather than a brightness ramp: brightness is the user's own
        /// setting and dimming it here would leave their wheel changed.</summary>
        private void ArmAutoOff(int holdMs)
        {
            if (holdMs <= 0) return;
            int gen = System.Threading.Interlocked.Increment(ref _holdGen);
            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(holdMs);
                    // Anything at all happened since: another pick, an edit, the
                    // Test button, or a game started driving the bar.
                    if (_holdGen != gen || _owner != OwnerNone) return;
                    if (LiveBarIsLit || !_channel.IsReady) return;

                    for (int lvl = _channel.StripLength - 1; lvl >= 0; lvl--)
                    {
                        if (_holdGen != gen || _owner != OwnerNone || LiveBarIsLit) return;
                        _channel.SetLevel(lvl);
                        Thread.Sleep(70);
                    }
                    if (_holdGen == gen && _owner == OwnerNone && !LiveBarIsLit)
                    {
                        _channel.TurnOff();
                        _lastBucket = -1;
                    }
                }
                catch (Exception ex) { _log($"[RPM-LED] auto-off error: {ex.Message}"); }
            });
        }

        /// <summary>Explicitly turn the rim LEDs off now. Called when the
        /// user unchecks the feature or disables the plugin, since no further
        /// telemetry frames will arrive to trigger the gate-off path.</summary>
        public void ForceOff()
        {
            if (_testing) return;
            try { if (_channel.IsReady) _channel.TurnOff(); } catch { }
            _lastBucket = -1;
        }

        public void Dispose()
        {
            try { _channel.Dispose(); } catch { }
        }
    }
}
