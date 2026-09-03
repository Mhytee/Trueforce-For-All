// DAMPCAL: measure the wheel's native damper against the synthesized one, in
// game, parked, from the driver's seat.
//
// Why a measurement: the firmware constant behind "100% damper" (Nm per
// rad/s at coefficient 1) is unpublished, and it differs per wheel. But a
// linear damper has one observable signature, how fast a released wheel's
// speed decays, and that rate does not depend on how hard the flick was. So
// the plugin compares decay RATES across three conditions and never needs
// the constant itself:
//   1. native:   the plugin stands aside (stream in keepalive), so the game's
//                own damper acts on the wheel;         rate = (D_native + F) / J
//   2. friction: stream on, synthesis off;             rate = F / J
//   3. synth:    stream on, synthesis at a probe gain; rate = (D_synth + F) / J
// Friction F and inertia J cancel in (rate1 - rate2) / (rate3 - rate2), which
// is the ratio of the native damper to ours at the probe gain, and the
// matching gain follows by proportion. Three flicks per condition, medians,
// so one bad flick does not skew it.
//
// Instrument: the bridge's steerInputSpeed at physics rate. Each flick is
// tracked from its peak (the release) down to 10% of the peak, and ln|speed|
// against time is fitted by least squares; the slope is the decay rate. A
// flick that re-accelerates (the hand grabbed it) or is too short is thrown
// away with a message rather than counted.

using System;
using System.Collections.Generic;

namespace TrueforceForAll.Core
{
    public sealed class CspDamperCalibration
    {
        public enum Phase { Native, Friction, Synth, Done, Aborted }

        public const int    FlicksPerPhase = 3;
        public const double PeakThreshold  = 0.35;   // |steerInputSpeed| that counts as a flick
        public const double PhaseTimeoutS  = 90;
        public const double SettleS        = 1.5;    // ignore motion right after a mode change
        private const int   MinPoints      = 8;

        private static readonly Phase[]  Order = { Phase.Native, Phase.Friction, Phase.Synth };
        private static readonly string[] Names =
        {
            "native damper (the plugin stands aside)",
            "no damper at all (friction only)",
            "synthesized damper",
        };

        private readonly double _probeGain;
        private readonly Action<Phase>  _enterPhase;
        private readonly Action<string> _status;
        private readonly Action<string> _screen;
        private readonly List<double>[] _rates = { new List<double>(), new List<double>(), new List<double>() };

        private Phase  _phase = Phase.Aborted;
        private int    _condition;
        private double _phaseStartT, _settleUntilT;

        // flick tracking
        private bool   _armed, _tracking;
        private double _prevAbs, _peak;
        private readonly List<double> _t = new List<double>();
        private readonly List<double> _lnv = new List<double>();

        public Phase  Current    => _phase;
        public bool   Running    => _phase != Phase.Done && _phase != Phase.Aborted;
        public double ResultGain { get; private set; } = -1;

        /// <param name="probeGain">The synthesized gain in force at condition 3.</param>
        /// <param name="enterPhase">Host applies the condition (stream hold, synthesis on/off).</param>
        /// <param name="status">Full sentences for the settings panel and the log.</param>
        /// <param name="screen">Short strings for the wheel's screen.</param>
        public CspDamperCalibration(double probeGain, Action<Phase> enterPhase,
                                    Action<string> status, Action<string> screen)
        {
            _probeGain = probeGain;
            _enterPhase = enterPhase;
            _status = status;
            _screen = screen;
        }

        public void Start(double nowS)
        {
            _condition = 0;
            BeginCondition(nowS);
        }

        public void Cancel(string why)
        {
            if (!Running) return;
            _phase = Phase.Aborted;
            _enterPhase(Phase.Aborted);
            _status("DAMPCAL stopped: " + why);
            _screen("CAL OFF");
        }

        private void BeginCondition(double nowS)
        {
            _phase = Order[_condition];
            _enterPhase(_phase);
            _phaseStartT = nowS;
            _settleUntilT = nowS + SettleS;
            ResetTracking();
            _status($"DAMPCAL {_condition + 1}/3: {Names[_condition]}. Flick the wheel and let go, {FlicksPerPhase} times.");
            _screen($"CAL {_condition + 1}/3 FLICK");
        }

        /// <summary>Feed every bridge sample: the steering input speed and the
        /// time in seconds. Drives the whole state machine.</summary>
        public void Sample(double speed, double nowS)
        {
            if (!Running) return;
            if (nowS - _phaseStartT > PhaseTimeoutS) { Cancel("no usable flicks for 90 s"); return; }
            double a = Math.Abs(speed);
            if (nowS < _settleUntilT) { _prevAbs = a; return; }

            if (!_tracking)
            {
                // Arm on the rise; start tracking at the turn-over, which is
                // the release: the hand gave the peak, the damper owns the rest.
                if (a >= PeakThreshold && a >= _prevAbs) { _armed = true; }
                else if (_armed && a < _prevAbs && _prevAbs >= PeakThreshold)
                {
                    _tracking = true;
                    _peak = _prevAbs;
                    _t.Clear(); _lnv.Clear();
                    _t.Add(nowS); _lnv.Add(Math.Log(Math.Max(a, 1e-4)));
                }
                _prevAbs = a;
                return;
            }

            // Tracking the decay.
            if (a > _peak * 1.05)
            {
                // Grabbed or re-flicked before it settled: start over quietly.
                ResetTracking();
                _prevAbs = a;
                return;
            }
            double floor = Math.Max(0.03, _peak * 0.10);
            if (a > floor)
            {
                _t.Add(nowS); _lnv.Add(Math.Log(a));
                _prevAbs = a;
                return;
            }

            // Down to the floor: fit this flick.
            bool ok = Fit(out double rate, out double r2);
            ResetTracking();
            _prevAbs = a;
            if (!ok)
            {
                _status("DAMPCAL: that flick was not usable (too short or ragged); flick harder and let go cleanly.");
                _screen("RETRY");
                return;
            }
            _rates[_condition].Add(rate);
            int n = _rates[_condition].Count;
            _status($"DAMPCAL {_condition + 1}/3, flick {n}/{FlicksPerPhase}: decay {rate:F2}/s (fit {r2:F2}).");
            _screen($"FLICK {n}/{FlicksPerPhase} OK");
            if (n < FlicksPerPhase) return;
            if (_condition < Order.Length - 1) { _condition++; BeginCondition(nowS); }
            else Finish();
        }

        private void ResetTracking()
        {
            _tracking = false; _armed = false; _peak = 0;
            _t.Clear(); _lnv.Clear();
        }

        // ln|v| = a + b t over the tracked window; rate = -b. Rejects short
        // windows and poor fits (a hand-assisted "flick" is not exponential).
        private bool Fit(out double rate, out double r2)
        {
            rate = 0; r2 = 0;
            int n = _t.Count;
            if (n < MinPoints || _t[n - 1] - _t[0] < 0.12) return false;
            double t0 = _t[0], sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double x = _t[i] - t0, y = _lnv[i];
                sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            double den = n * sxx - sx * sx;
            if (den <= 0) return false;
            double slope = (n * sxy - sx * sy) / den;
            double icpt  = (sy - slope * sx) / n;
            double my = sy / n, ssTot = 0, ssRes = 0;
            for (int i = 0; i < n; i++)
            {
                double x = _t[i] - t0, y = _lnv[i], f = icpt + slope * x;
                ssRes += (y - f) * (y - f);
                ssTot += (y - my) * (y - my);
            }
            r2 = ssTot > 0 ? 1 - ssRes / ssTot : 0;
            rate = -slope;
            return rate > 0 && r2 >= 0.7;
        }

        private static double Median(List<double> v)
        {
            var c = new List<double>(v);
            c.Sort();
            int n = c.Count;
            return n % 2 == 1 ? c[n / 2] : 0.5 * (c[n / 2 - 1] + c[n / 2]);
        }

        private void Finish()
        {
            double rN = Median(_rates[0]), rF = Median(_rates[1]), rS = Median(_rates[2]);
            double nativeD = rN - rF, synthD = rS - rF;
            if (nativeD > 0.05 && synthD > 0.05)
                ResultGain = _probeGain * nativeD / synthD;
            _phase = Phase.Done;
            _enterPhase(Phase.Done);

            if (nativeD <= 0.05)
            {
                _status($"DAMPCAL: native decay {rN:F2}/s was no faster than friction {rF:F2}/s, so the wheel showed no native damper while the plugin stood aside (keepalive may not hand the effect path back). No gain change.");
                _screen("CAL: NO NATIVE");
                return;
            }
            if (synthD <= 0.05)
            {
                _status($"DAMPCAL: synthesized decay {rS:F2}/s was no faster than friction {rF:F2}/s at gain {_probeGain:F2}, so the synthesis added no damping (if the wheel got livelier, the sign is inverted; check with CSPFFB DAMP). No gain change.");
                _screen("CAL: NO SYNTH");
                return;
            }
            _status($"DAMPCAL result: native {rN:F2}/s, friction {rF:F2}/s, synthesized {rS:F2}/s at gain {_probeGain:F2}. "
                  + $"The native damper is {nativeD / synthD:F2}x the probe, so the matching gain is {ResultGain:F2}. "
                  + $"Applied for this session; CSPFFB DAMPK {ResultGain:F2} sets it again.");
            _screen($"CAL DONE {ResultGain:F2}");
        }
    }
}
