// The wheel as a DirectInput device: position and velocity at ~1 kHz, and
// (exclusively acquired) the wheel's own damper effect.
//
// Why DirectInput and not the game: the synthesized damper has to work in
// every title, and its calibration has to happen with no game running, so
// wheel speed must come from something game-agnostic. DirectInput gives the
// position of any wheel at whatever rate we poll it, alongside a game that
// holds the device exclusively (non-exclusive readers are always allowed).
// Creating force effects (the calibration's native-damper reference) does
// need exclusive acquisition, which is only possible with no game running:
// exactly the wizard's situation.
//
// Units: Position is -1..1 across the wheel's configured range, Velocity is
// that per second. The calibration compares decay RATES, which are unit-free,
// and the synthesis gain is calibrated in these same units, so the range the
// driver happens to be set to cancels out end to end.

using System;
using System.Diagnostics;
using System.Threading;
using SharpDX.DirectInput;

namespace TrueforceForAll.Plugin
{
    internal sealed class DirectInputWheel : IDisposable
    {
        private readonly Action<string> _log;
        private DirectInput _di;
        private Joystick _js;
        private Effect _fx;
        private Thread _poll;
        private volatile bool _stop;
        private long _posBits, _velBits;
        private long _reads;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        /// <summary>Log the read rate every 10 s (the DIDAMP spike wants it).</summary>
        public bool LogRate { get; set; }
        /// <summary>Stop polling after this many ms and call OnAutoStop (0 =
        /// never). Measured from when it is ARMED, not from construction (a
        /// reused reader's stopwatch is older than its current owner; audit
        /// F5/AT2-07).</summary>
        public int AutoStopMs
        {
            get => _autoStopMs;
            set { _autoStopMs = value; _autoStopArmedMs = _sw.ElapsedMilliseconds; }
        }
        private int _autoStopMs;
        private long _autoStopArmedMs;
        public Action OnAutoStop { get; set; }
        /// <summary>Every poll: (velocity in units/s, time in seconds). Poll thread.</summary>
        public Action<double, double> OnSample { get; set; }

        public bool   IsExclusive { get; private set; }
        public bool   IsRunning   => _poll != null && !_stop;
        public double Position    => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _posBits));
        public double Velocity    => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _velBits));
        public long   ReadsPerSecond => _sw.ElapsedMilliseconds > 0
            ? Interlocked.Read(ref _reads) * 1000 / _sw.ElapsedMilliseconds : 0;

        public DirectInputWheel(Action<string> log) { _log = log; }

        public bool Start(int vid, int pid, IntPtr hwnd, bool exclusive)
        {
            _di = new DirectInput();
            DeviceInstance found = null;
            foreach (var d in _di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
            {
                // ProductGuid.Data1 = (PID << 16) | VID for HID devices.
                int data1 = BitConverter.ToInt32(d.ProductGuid.ToByteArray(), 0);
                if ((data1 & 0xFFFF) == vid && ((data1 >> 16) & 0xFFFF) == pid) { found = d; break; }
            }
            if (found == null)
            {
                _log($"DirectInput: no game controller with VID {vid:X4} PID {pid:X4}.");
                return false;
            }
            _js = new Joystick(_di, found.InstanceGuid);
            var level = (exclusive ? CooperativeLevel.Exclusive : CooperativeLevel.NonExclusive)
                      | CooperativeLevel.Background;
            _js.SetCooperativeLevel(hwnd, level);
            _js.Properties.AxisMode = DeviceAxisMode.Absolute;
            _js.Acquire();
            IsExclusive = exclusive;
            _log($"DirectInput: acquired '{found.ProductName}' {(exclusive ? "exclusively" : "shared")}.");

            _poll = new Thread(PollLoop) { IsBackground = true, Name = "TF4ALL DirectInput wheel" };
            _poll.Start();
            return true;
        }

        /// <summary>Start the wheel's own damper effect at coefPercent (0..100).
        /// Exclusive acquisition only.</summary>
        public bool StartDamper(int coefPercent) => StartNativeEffect("DAMPER", coefPercent, 0);

        /// <summary>Start one of the wheel's own DirectInput effects, rendered
        /// by the FIRMWARE (the FXTEST native side; the caller stops the
        /// Trueforce stream outright so the wheel is exactly as it is
        /// without the plugin). Kinds
        /// match FxTestPayloads: conditions (DAMPER/SPRING/FRICTION/INERTIA)
        /// at strength% coefficient with full saturation and no deadband,
        /// periodics (SINE/SQUARE/TRIANGLE/SAWUP/SAWDOWN) at strength%
        /// magnitude and periodMs, RAMP +strength to -strength over 3 s.
        /// Exclusive acquisition only (so no game may be running).</summary>
        /// <param name="offset">For conditions, where the effect is centred,
        /// -1..1 of the axis. A centring spring has to pull toward the
        /// position the wheel actually rested at, which is not in general the
        /// axis centre.</param>
        public bool StartNativeEffect(string kind, int strengthPercent, int periodMs,
                                      double offset = 0)
        {
            if (_js == null || !IsExclusive) { _log("DirectInput: a native effect needs exclusive acquisition."); return false; }
            StopDamper();

            Guid guid;
            switch ((kind ?? "").Trim().ToUpperInvariant())
            {
                case "DAMPER":   guid = EffectGuid.Damper; break;
                case "SPRING":   guid = EffectGuid.Spring; break;
                case "FRICTION": guid = EffectGuid.Friction; break;
                case "INERTIA":  guid = EffectGuid.Inertia; break;
                case "SINE":     guid = EffectGuid.Sine; break;
                case "SQUARE":   guid = EffectGuid.Square; break;
                case "TRIANGLE": guid = EffectGuid.Triangle; break;
                case "SAWUP":    guid = EffectGuid.SawtoothUp; break;
                case "SAWDOWN":  guid = EffectGuid.SawtoothDown; break;
                case "RAMP":     guid = EffectGuid.RampForce; break;
                case "CONSTANT": guid = EffectGuid.ConstantForce; break;   // auto-tune probe pulses
                default: _log($"DirectInput: unknown effect kind '{kind}'."); return false;
            }
            bool supported = false;
            foreach (var e in _js.GetEffects()) if (e.Guid == guid) supported = true;
            if (!supported) { _log($"DirectInput: the device reports no {kind} effect."); return false; }

            // Signed strength: the sign becomes the direction (auto-tune
            // pulses toward center); conditions ignore direction anyway.
            int dir = strengthPercent < 0 ? -1 : 1;
            int mag = Math.Min(100, Math.Abs(strengthPercent)) * 100;   // 0..10000
            if (periodMs <= 0) periodMs = 250;
            bool isCondition = guid == EffectGuid.Damper || guid == EffectGuid.Spring
                            || guid == EffectGuid.Friction || guid == EffectGuid.Inertia;
            bool isRamp = guid == EffectGuid.RampForce;
            bool isConstant = guid == EffectGuid.ConstantForce;

            var p = new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = isRamp ? 3_000_000 : -1,   // microseconds; -1 infinite
                SamplePeriod = 0,
                Gain = 10000,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                StartDelay = 0,
                Axes = new[] { (int)JoystickOffset.X },
                // Conditions ignore direction; force/periodic effects need a
                // non-zero direction along the axis.
                Directions = new[] { isCondition ? 0 : dir },
            };
            if (isCondition)
            {
                p.Parameters = new ConditionSet
                {
                    Conditions = new[]
                    {
                        new Condition
                        {
                            Offset = (int)(Math.Max(-1, Math.Min(1, offset)) * 10000),
                            PositiveCoefficient = mag,
                            NegativeCoefficient = mag,
                            PositiveSaturation = 10000,
                            NegativeSaturation = 10000,
                            DeadBand = 0,
                        }
                    }
                };
            }
            else if (isRamp)
            {
                p.Parameters = new RampForce { Start = mag, End = -mag };
            }
            else if (isConstant)
            {
                p.Parameters = new ConstantForce { Magnitude = mag };
            }
            else
            {
                p.Parameters = new PeriodicForce
                {
                    Magnitude = mag,
                    Offset = 0,
                    Period = periodMs * 1000,   // microseconds
                    Phase = 0,
                };
            }
            _fx = new Effect(_js, guid, p);
            _fx.Start(1);
            if (!QuietEffects)
                _log($"DirectInput: native {kind} started at {strengthPercent}%"
                     + (isCondition || isRamp || isConstant ? "." : $", period {periodMs} ms."));
            return true;
        }

        /// <summary>Suppress per-effect log lines (auto-tune creates dozens
        /// of probe pulses; one line each would drown the log).</summary>
        public bool QuietEffects { get; set; }

        // ---- Dedicated pulse slot (auto-tune) --------------------------
        // The probe pulse must NOT share _fx with the condition effect under
        // test: a shared slot means every pulse destroys the damper/spring/
        // friction being measured (audit AT-02). The pulse effect is created
        // ONCE and then driven by parameter updates, so force onset is
        // near-instant instead of paying enumeration + creation + download
        // per pulse (audit AT-06).
        private Effect _fxPulse;
        private EffectParameters _pulseParams;

        /// <summary>Create the dedicated constant-force pulse effect (idempotent).
        /// Exclusive acquisition only.</summary>
        public bool PrepareConstantPulse()
        {
            if (_js == null || !IsExclusive) return false;
            if (_fxPulse != null) return true;
            try
            {
                _pulseParams = new EffectParameters
                {
                    Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                    Duration = -1,
                    SamplePeriod = 0,
                    Gain = 10000,
                    TriggerButton = -1,
                    TriggerRepeatInterval = 0,
                    StartDelay = 0,
                    Axes = new[] { (int)JoystickOffset.X },
                    Directions = new[] { 1 },
                    Parameters = new ConstantForce { Magnitude = 0 },
                };
                _fxPulse = new Effect(_js, EffectGuid.ConstantForce, _pulseParams);
                return true;
            }
            catch (Exception ex)
            {
                _log("DirectInput: pulse effect create failed: " + ex.Message);
                _fxPulse = null;
                return false;
            }
        }

        /// <summary>Drive the pulse effect: signed -1..1; 0 stops it. A fast
        /// parameter update on the existing effect, no re-creation.</summary>
        public void SetConstantPulse(double cmd)
        {
            var fx = _fxPulse;
            if (fx == null) return;
            try
            {
                if (cmd == 0) { fx.Stop(); return; }
                _pulseParams.Directions = new[] { cmd < 0 ? -1 : 1 };
                _pulseParams.Parameters = new ConstantForce
                {
                    Magnitude = (int)(Math.Min(1.0, Math.Abs(cmd)) * 10000),
                };
                fx.SetParameters(_pulseParams,
                    EffectParameterFlags.Direction
                    | EffectParameterFlags.TypeSpecificParameters
                    | EffectParameterFlags.Start);
            }
            catch (Exception ex) { _log("DirectInput: pulse drive failed: " + ex.Message); }
        }

        public void DisposeConstantPulse()
        {
            var fx = _fxPulse;
            _fxPulse = null;
            if (fx == null) return;
            try { fx.Stop(); } catch { }
            try { fx.Dispose(); } catch { }
        }

        /// <summary>Silences or restores the loaded effect WITHOUT destroying
        /// it. Auto-tune's friction phase alternates loaded and unloaded
        /// trials seconds apart so both ride the same wheel conditions, and a
        /// destroy/recreate between every trial would change what is being
        /// measured (and risks a device that refuses the re-creation). Stop
        /// and Start only.</summary>
        public bool SetEffectActive(bool active)
        {
            var fx = _fx;
            if (fx == null) return false;
            try
            {
                if (active) fx.Start(1);
                else        fx.Stop();
                return true;
            }
            catch (Exception ex)
            {
                _log("DirectInput: could not " + (active ? "start" : "stop")
                     + " the effect for a baseline trial: " + ex.Message);
                return false;
            }
        }

        private Effect _fxCenterDamp;

        /// <summary>Holds a DAMPER alongside the centring spring, or releases
        /// it when pct is 0. Two conditions at once: the spring pulls the
        /// wheel home, the damper stops it ringing on the way. Both are
        /// rendered by the firmware in the WHEEL's frame, so neither needs to
        /// know which way our own commands push, which is what lets centring
        /// work before the direction probe has even run. Returns false if the
        /// device will not hold a second condition.</summary>
        public bool SetCenteringDamper(int pct)
        {
            if (pct <= 0)
            {
                var old = _fxCenterDamp;
                _fxCenterDamp = null;
                if (old != null) { try { old.Stop(); } catch { } try { old.Dispose(); } catch { } }
                return true;
            }
            if (_js == null || !IsExclusive) return false;
            try
            {
                int mag = Math.Min(100, pct) * 100;
                var p = new EffectParameters
                {
                    Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                    Duration = -1,
                    SamplePeriod = 0,
                    Gain = 10000,
                    TriggerButton = -1,
                    TriggerRepeatInterval = 0,
                    StartDelay = 0,
                    Axes = new[] { (int)JoystickOffset.X },
                    Directions = new[] { 0 },
                };
                p.Parameters = new ConditionSet
                {
                    Conditions = new[]
                    {
                        new Condition
                        {
                            Offset = 0,
                            PositiveCoefficient = mag,
                            NegativeCoefficient = mag,
                            PositiveSaturation = 10000,
                            NegativeSaturation = 10000,
                            DeadBand = 0,
                        }
                    }
                };
                if (_fxCenterDamp == null)
                    _fxCenterDamp = new Effect(_js, EffectGuid.Damper, p);
                else
                    _fxCenterDamp.SetParameters(p, EffectParameterFlags.TypeSpecificParameters);
                _fxCenterDamp.Start(1);
                return true;
            }
            catch (Exception ex)
            {
                if (!QuietEffects)
                    _log("DirectInput: no second condition for centring damping (" + ex.Message + ").");
                _fxCenterDamp = null;
                return false;
            }
        }

        public void StopDamper()
        {
            SetCenteringDamper(0);
            var fx = _fx;
            _fx = null;
            if (fx == null) return;
            try { fx.Stop(); } catch { }
            try { fx.Dispose(); } catch { }
            if (!QuietEffects) _log("DirectInput: damper stopped.");
        }

        // A poll closer together than this carries no usable derivative: the
        // position quantum divided by a very short interval is arbitrarily
        // large. Sleep(1) can return early, so this is not hypothetical.
        private const double MinSampleDt      = 0.0004;   // seconds
        private const double MaxPlausibleVel  = 25.0;     // range/s

        private void PollLoop()
        {
            double lastPos = double.NaN, lastT = 0, vel = 0;
            long nextReport = 10000;
            int min = int.MaxValue, max = int.MinValue;
            int pollFails = 0;
            try
            {
                while (!_stop)
                {
                    long ms = _sw.ElapsedMilliseconds;
                    if (_autoStopMs > 0 && ms - _autoStopArmedMs >= _autoStopMs) break;
                    int x;
                    try
                    {
                        // A transient DirectInput error (device momentarily
                        // grabbed, USB hiccup) must not silently kill the
                        // thread that a bench/auto-tune run depends on
                        // (audit AT-01/F4): retry, give up only when it is
                        // clearly gone, and always fall through to
                        // OnAutoStop below so the owner of this reader gets
                        // told either way.
                        _js.Poll();
                        x = _js.GetCurrentState().X;
                        pollFails = 0;
                    }
                    catch (Exception ex)
                    {
                        if (++pollFails >= 40)
                        {
                            _log("DirectInput: poll failing persistently (" + ex.Message + "); giving up.");
                            break;
                        }
                        try
                        {
                            _js.Acquire();
                            // A re-acquire after DIERR_INPUTLOST loses
                            // downloaded effect playback: restart the
                            // condition under test so a bench phase does not
                            // silently measure a bare wheel (audit
                            // reacquire-loses-effects).
                            try { _fx?.Start(1); } catch { }
                        }
                        catch { }
                        Thread.Sleep(5);
                        continue;
                    }
                    double t = _sw.ElapsedTicks / (double)Stopwatch.Frequency;
                    double pos = (x - 32767.5) / 32767.5;
                    if (!double.IsNaN(lastPos))
                    {
                        double dt = t - lastT;
                        // Reject the physically impossible instead of
                        // smoothing it. A wheel cannot turn at 20 lock-to-lock
                        // sweeps per second; a reading that says so is a
                        // glitched poll, a clock hiccup that makes dt tiny, or
                        // the first sample after a re-acquire. One such sample
                        // reached the auto-tuner as "vel 143.0" and aborted
                        // the run (rig, 2026-09-02 16:47). An EMA cannot help
                        // here: it passes a scaled fraction of the spike
                        // straight through, and the guard reads instantaneous
                        // velocity.
                        if (dt >= MinSampleDt)
                        {
                            double raw = (pos - lastPos) / dt;
                            if (Math.Abs(raw) <= MaxPlausibleVel)
                            {
                                // Light smoothing: 1 ms samples of a 16-bit axis are
                                // quantized; a 4-sample EMA keeps the decay shape.
                                vel += (raw - vel) * 0.25;
                            }
                            else if (!QuietEffects)
                            {
                                _log($"DirectInput: ignoring an impossible velocity sample "
                                     + $"({raw:F0} range/s over {dt * 1000:F2} ms).");
                            }
                        }
                    }
                    lastPos = pos; lastT = t;
                    Interlocked.Exchange(ref _posBits, BitConverter.DoubleToInt64Bits(pos));
                    Interlocked.Exchange(ref _velBits, BitConverter.DoubleToInt64Bits(vel));
                    Interlocked.Increment(ref _reads);
                    if (x < min) min = x;
                    if (x > max) max = x;
                    try { OnSample?.Invoke(vel, t); } catch { }
                    if (LogRate && ms >= nextReport)
                    {
                        _log($"DirectInput: {ReadsPerSecond} reads/s, X range {min}..{max}, now {x}, velocity {vel:F2}/s.");
                        nextReport += 10000;
                    }
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex) { _log("DirectInput: poll loop ended: " + ex.Message); }
            // The reader is DEAD from here: IsRunning must say so, or a
            // frozen Position/Velocity keeps feeding the condition renderer
            // as a constant torque bias for the rest of the session and
            // EnsureWheelMotion keeps returning the corpse (audit AT2-04 /
            // frozen-velocity-latch, confirmed in three areas).
            bool wasExternalStop = _stop;
            _stop = true;
            // Fire the stop callback on ANY non-disposal exit, not just the
            // timed one: an abnormal poll death must reach the owner (the
            // auto-tune watchdog path) so it can clean up instead of
            // freezing mid-run with outputs latched (audit AT-01).
            if (!wasExternalStop)
            {
                _log(_autoStopMs > 0 && _sw.ElapsedMilliseconds - _autoStopArmedMs >= _autoStopMs
                    ? "DirectInput: auto-stop reached."
                    : "DirectInput: poll loop exited; notifying the owner.");
                try { OnAutoStop?.Invoke(); } catch { }
            }
        }

        public void Dispose()
        {
            _stop = true;
            // Wait for the poll thread (never from itself): a disposer must
            // not race an in-flight OnSample callback that is still driving
            // effects or suspending the stream (audit AT2-03).
            var poll = _poll;
            if (poll != null && poll != Thread.CurrentThread)
            {
                try { poll.Join(300); } catch { }
            }
            DisposeConstantPulse();
            StopDamper();
            try { _js?.Unacquire(); } catch { }
            try { _js?.Dispose(); } catch { }
            try { _di?.Dispose(); } catch { }
            _js = null; _di = null;
        }
    }
}
