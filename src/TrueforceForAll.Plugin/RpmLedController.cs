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

        private int _openState;        // 0=idle 1=opening 2=open-ok 3=open-failed
        private int _openFails;        // completed failed open attempts (auto path only)
        private long _nextOpenRetryMs; // no automatic re-probe before this
        private const int OpenRetryCooldownMs = 30_000;
        private const int OpenMaxAttempts = 5;
        private int _lastBucket = -1;  // last LED count pushed (0..10), -1 = none
        private bool _lastRedline;
        private long _lastPushTicks;
        private volatile bool _testing;
        private volatile string _testStatus = "";

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
            Push(rpmPercent, redline, force: false);
        }

        private int _hystLevel = -1;

        private void Push(double pct, bool redline, bool force)
        {
            long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            // Per-wheel step count from fn0: 10 on the G PRO/RS50, 5 on a
            // G923. Scaling to the wheel's own range keeps a 50% fill half a
            // bar on every wheel instead of clipping at the channel.
            int steps = _channel.StripLength;
            int target;
            if (redline)
            {
                // Peak: blink the full bar (~2.7 Hz) like iRacing's shift
                // blink, instead of holding it solid.
                bool on = ((nowMs / 185L) & 1L) == 0L;
                target = on ? steps : 0;
            }
            else
            {
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
            if (_testing) return 0;

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
            _testing = true;
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
                    _testing = false;
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
            if (_testing) return false;   // never mid-sweep
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
        public int PreviewPattern()
        {
            if (_testing) return 0;
            const int stepMs = 180;   // just above the channel's 160 ms change floor

            _testing = true;
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
                    for (int lvl = 0; lvl <= steps && _channel.IsReady; lvl++)
                    {
                        _testStatus = $"▶ pattern preview - level {lvl}/{steps}";
                        _channel.SetLevel(lvl);
                        Thread.Sleep(stepMs);
                    }
                    for (int lvl = steps - 1; lvl >= 0 && _channel.IsReady; lvl--)
                    {
                        _testStatus = $"▶ pattern preview - level {lvl}/{steps}";
                        _channel.SetLevel(lvl);
                        Thread.Sleep(stepMs);
                    }
                }
                catch (Exception ex) { _log($"[RPM-LED] preview error: {ex.Message}"); }
                finally
                {
                    if (opened)
                    {
                        try { _channel.TurnOff(); } catch { }
                        _lastBucket = -1;
                        _testStatus = "";
                    }
                    _testing = false;
                }
            });

            int stepsGuess = _channel.IsReady ? _channel.StripLength : WheelLedChannel.LedCount;
            return (2 * stepsGuess + 1) * stepMs + 300;
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
