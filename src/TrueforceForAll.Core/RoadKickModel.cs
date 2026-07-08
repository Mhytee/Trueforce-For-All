// Road kick — test layer 9 (GAP #3 in docs/steering-feel-physics.md).
//
// A real rack kicks the rim when ONE front wheel hits a bump: asymmetric
// vertical load × trail = signed torque blip. Symmetric events (both wheels
// over the same crest) cancel and should NOT kick — which is exactly what
// the left-minus-right difference gives us for free. The derivative makes
// it transient by construction: steady-state asymmetry (banked corner,
// stationary on a kerb) reads as zero rate and produces no force.
//
// Input is per-corner suspension travel at telemetry rate (~60 Hz for FM8);
// output is a signed kick in [-1, 1] for the FORCE channel (not the texture
// channel — this is a steering event, the whole rim moves). The short EMA
// tames the 60 Hz sample staircase without burying sub-100 ms transients.
//
// Sign: FL compressing faster than FR = positive. Whether positive should
// pull the rim left or right on a given rig is validated on the wheel (the
// caller owns any flip, same as ModeBSign).

using System;

namespace TrueforceForAll.Core
{
    public sealed class RoadKickModel
    {
        /// <summary>Asymmetric compression rate (m/s of FL−FR travel delta)
        /// that maps to a full-scale kick. FM8 kerb strikes at speed measure
        /// roughly 0.5–2 m/s of single-wheel travel rate.</summary>
        public double FullKickRateMps { get; set; } = 1.5;

        /// <summary>Low-pass time constant on the rate signal. Long enough
        /// to melt the telemetry-rate staircase AND frame-rate sign
        /// alternation, short enough that a kerb strike (a one-sided pulse,
        /// ~80+ ms) still lands as a distinct kick. 15 ms proved too short
        /// on the wheel: driveline torque reaction under a hard launch rocks
        /// the chassis so FL−FR alternates every telemetry frame, and at
        /// 15 ms that alternation passed through as a ~50 Hz force buzz
        /// (tenth wheel test, trace-confirmed).</summary>
        public double SmoothMs { get; set; } = 35.0;

        /// <summary>Kicks below this fraction of full scale are zero, with
        /// the range above rescaled from the floor (no step). A real
        /// one-wheel bump is discrete and sizeable; continuous residue from
        /// suspension jitter is noise by definition and must produce exactly
        /// zero force at the rim.</summary>
        public double Deadband { get; set; } = 0.12;

        private bool _seeded;
        private double _prevDelta;
        private double _rateEma;

        /// <summary>Advance one telemetry tick and return the signed kick
        /// in [-1, 1]. First tick after Reset seeds and returns 0.</summary>
        public double Tick(double suspFlMeters, double suspFrMeters, double dtMs)
        {
            double delta = suspFlMeters - suspFrMeters;
            if (!_seeded)
            {
                _seeded = true;
                _prevDelta = delta;
                return 0;
            }

            double dt = Math.Min(Math.Max(dtMs, 0.5), 100.0);
            double rate = (delta - _prevDelta) * 1000.0 / dt;
            _prevDelta = delta;

            double a = 1.0 - Math.Exp(-dt / Math.Max(1.0, SmoothMs));
            _rateEma += (rate - _rateEma) * a;

            double k = _rateEma / Math.Max(0.05, FullKickRateMps);
            k = Math.Min(1.0, Math.Max(-1.0, k));

            double d = Math.Min(Math.Max(Deadband, 0.0), 0.9);
            double mag = Math.Abs(k);
            if (mag <= d) return 0.0;
            return Math.Sign(k) * (mag - d) / (1.0 - d);
        }

        /// <summary>Clear state (car/game switch, telemetry stall) so the
        /// first frame of a new session can't difference against a stale
        /// travel value and produce a phantom kick.</summary>
        public void Reset()
        {
            _seeded = false;
            _rateEma = 0;
        }
    }
}
