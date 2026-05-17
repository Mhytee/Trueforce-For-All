// Owns the WheelLedChannel and decides when/what to push to the rim LEDs.
//
// Scope is deliberately iRacing-only (see TrueforceSettings.RpmLedsEnabled):
// iRacing's native rev lights ride its Trueforce SDK hook, so MAIRA users who
// disable in-game Trueforce lose them. Other games either drive the LEDs
// themselves or aren't in scope.
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
        public string Status =>
            _testing          ? _testStatus
          : _openState == 2 ? $"open ({_channel.ResolvedInfo})"
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
                if (_lastBucket > 0 && _channel.IsReady)
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
            int target;
            if (redline)
            {
                // Peak: blink the full bar (~2.7 Hz) like iRacing's shift
                // blink, instead of holding it solid.
                bool on = ((nowMs / 185L) & 1L) == 0L;
                target = on ? WheelLedChannel.LedCount : 0;
            }
            else
            {
                double scaled = pct * WheelLedChannel.LedCount;
                int lvl = (int)Math.Floor(scaled + 0.5);
                if (lvl < 0) lvl = 0;
                else if (lvl > WheelLedChannel.LedCount) lvl = WheelLedChannel.LedCount;
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

        /// <summary>Kick a one-shot background open if we haven't yet. Returns
        /// true once the open attempt has completed successfully.</summary>
        private bool EnsureOpening()
        {
            int prev = Interlocked.CompareExchange(ref _openState, 1, 0);
            if (prev == 0)
            {
                Task.Run(() =>
                {
                    bool ok = false;
                    try { ok = _channel.OpenAndResolve(); }
                    catch (Exception ex) { _log($"[RPM-LED] open threw: {ex.Message}"); }
                    Interlocked.Exchange(ref _openState, ok ? 2 : 3);
                });
                return false;
            }
            return prev == 2;
        }

        /// <summary>Simulated rev + shift sweep for the settings "Test" button.
        /// Forces the channel open regardless of game (so the user can verify
        /// hardware with nothing running) and drives the bar directly. Returns
        /// the total duration in ms (0 if the channel can't be opened).</summary>
        public int RunTest()
        {
            if (_testing) return 0;

            // Test path opens synchronously so we can report failure to the
            // user immediately rather than silently doing nothing.
            if (!_channel.IsReady)
            {
                bool ok;
                try { ok = _channel.OpenAndResolve(); }
                catch (Exception ex) { _log($"[RPM-LED] test open threw: {ex.Message}"); ok = false; }
                Interlocked.Exchange(ref _openState, ok ? 2 : 3);
                if (!ok)
                {
                    _log("[RPM-LED] Test: could not open the LED channel. " +
                         "Check the log above for which interfaces were probed.");
                    return 0;
                }
            }

            // Rev-level sweep using the real (captured) G PRO protocol: walk
            // the level 0..10..0 a couple of times, then a brief redline hold.
            // Colours / direction come from the wheel's own profile (the user
            // set outside-in); we only drive how many LEDs.
            const int stepMs   = 220;   // a touch slower than the ~156 ms keepalive
            const int redlineMs = 1500;
            int total = (2 * (2 * WheelLedChannel.LedCount + 1)) * stepMs + redlineMs + 400;

            _testing = true;
            Task.Run(() =>
            {
                try
                {
                    for (int cycle = 0; cycle < 2 && _channel.IsReady; cycle++)
                    {
                        for (int lvl = 0; lvl <= WheelLedChannel.LedCount && _channel.IsReady; lvl++)
                        {
                            _testStatus = $"▶ rev sweep — level {lvl}/{WheelLedChannel.LedCount}";
                            _channel.SetLevel(lvl);
                            Thread.Sleep(stepMs);
                        }
                        for (int lvl = WheelLedChannel.LedCount - 1; lvl >= 0 && _channel.IsReady; lvl--)
                        {
                            _testStatus = $"▶ rev sweep — level {lvl}/{WheelLedChannel.LedCount}";
                            _channel.SetLevel(lvl);
                            Thread.Sleep(stepMs);
                        }
                    }
                    if (_channel.IsReady)
                    {
                        _testStatus = "▶ redline (all LEDs)";
                        _log("[RPM-LED] Test: redline hold (level 10)");
                        _channel.SetLevel(WheelLedChannel.LedCount);
                        Thread.Sleep(redlineMs);
                    }
                }
                catch (Exception ex) { _log($"[RPM-LED] test error: {ex.Message}"); }
                finally
                {
                    try { _channel.TurnOff(); } catch { }
                    _lastBucket = -1;
                    _testStatus = "test finished — LEDs off";
                    _testing = false;
                    _log("[RPM-LED] Test: finished, LEDs off (level 0).");
                }
            });
            return total;
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
