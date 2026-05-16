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
        public string Status =>
            _openState == 2 ? $"open ({_channel.ResolvedInfo})"
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

            // SimHub fills RpmPercent on its source; raw UDP sources don't, so
            // fall back to Rpms/MaxRpm there. iRacing runs through the SimHub
            // source so it gets the good (idle→shift band) signal.
            double pct = rpmPercent;
            if (pct <= 0 && maxRpm > 0) pct = Math.Min(1.0, rpms / maxRpm);

            Push(pct, redline, force: false);
        }

        private void Push(double pct, bool redline, bool force)
        {
            int bucket = redline ? WheelLedChannel.LedCount
                                 : (int)Math.Floor(pct * WheelLedChannel.LedCount + 0.5);
            if (bucket > WheelLedChannel.LedCount) bucket = WheelLedChannel.LedCount;

            long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            bool changed = bucket != _lastBucket || redline != _lastRedline;
            if (!force && !changed) return;
            if (!force && (nowMs - _lastPushTicks) < MinPushIntervalMs && !redline) return;

            try { _channel.ApplyRevBar(pct, redline); }
            catch (Exception ex) { _log($"[RPM-LED] push error: {ex.Message}"); }

            _lastBucket = bucket;
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

            // Effect-mode finder. The protocol latches (LEDs stick on) but
            // mescon's RS50 "mode 5" doesn't render a clean bar on the G PRO,
            // and the wheel's onboard profile has its own rev-LED behaviour
            // that may be fighting our writes. Sweep effect modes 1..8 with an
            // unmistakable asymmetric pattern held 3 s each so the user can
            // call out which mode shows EXACTLY:
            //   LED1=red  LED2=green  LED3=blue  LED4=white  LED5-10=off
            // steady (not animated, not the onboard rev sweep). The asymmetry
            // also reveals physical order / fill direction (user's profile is
            // set to outside-in, so a correct static mode should ignore that
            // and show our literal LED1..4).
            byte[] pattern = new byte[WheelLedChannel.LedCount * 3];
            void Set(int led, byte r, byte g, byte b)
            { pattern[led*3]=r; pattern[led*3+1]=g; pattern[led*3+2]=b; }
            Set(0, 255, 0, 0); Set(1, 0, 255, 0); Set(2, 0, 0, 255); Set(3, 255, 255, 255);

            byte[] modes = { 1, 2, 3, 4, 5, 6, 7, 8 };
            const int holdMs = 3000;
            int total = modes.Length * holdMs + 500;

            _testing = true;
            Task.Run(() =>
            {
                try
                {
                    foreach (byte m in modes)
                    {
                        if (!_channel.IsReady) break;
                        _log($"[RPM-LED] Test: trying effect mode {m} " +
                             "(expect LED1=red 2=green 3=blue 4=white, rest off, steady)");
                        _channel.ApplyRgbMode(m, pattern);
                        Thread.Sleep(holdMs);
                    }
                }
                catch (Exception ex) { _log($"[RPM-LED] test error: {ex.Message}"); }
                finally
                {
                    try { _channel.TurnOff(); } catch { }
                    _lastBucket = -1;
                    _testing = false;
                    _log("[RPM-LED] Test: finished, LEDs turned off (effect mode 0).");
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
