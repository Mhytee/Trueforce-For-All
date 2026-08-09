// Layered sidechain ducking, extracted verbatim from TrueforcePlugin's
// UpdateDucking in phase 0c of docs/haptic-engine-plan.md so the replay
// harness runs the exact production arbitration. Phase 6 replaces the fixed
// tiers with the band-overlap priority arbiter; until then this is a pure
// move with the effect wiring injected instead of read off plugin fields.
//
// Tier model (an effect is ducked by the strongest activity at a STRICTLY
// higher tier; same/lower tiers never duck it):
//
//   L3  ABS, gear shift, collision        (top: sharp momentary alerts;
//                                          sources only, never sidechain-ducked)
//   L2  rev limiter, pit limiter          (mode buzzes; duck L0/L1, ducked by L3)
//   L1  road feel, traction loss, DRS hum (duck L0, ducked by L2/L3)
//   L0  engine pulse, captured audio      (bottom: ducked by anything)
//
// The airborne stage is separate and orthogonal: it ramps toward
// (1 - Reduction) while the car is in the air and back to 1 on landing, and
// is folded multiplicatively into the opted-in targets so airborne +
// sidechain ducking stack.

using System;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Core
{
    /// <summary>A duck target that isn't a TelemetryEffect (the captured-audio
    /// source in the plugin). Only the multiplier is needed: audio is a duck
    /// TARGET, never a trigger.</summary>
    public interface IDuckable
    {
        float DuckMultiplier { get; set; }
    }

    public sealed class DuckingController
    {
        // Effect wiring, assigned once by the host after it constructs the
        // effects. Null members are simply skipped (same null-guards as the
        // original plugin code) so a partial rig — e.g. the headless harness
        // with only the effects under test — works unchanged.
        public EnginePulseEffect  EnginePulse;
        public RoadBumpsEffect    RoadBumps;
        public TractionLossEffect TractionLoss;
        public AxleSlipEffect     AxleSlip;      // L1 tier, beside TractionLoss
        public LockupJudderEffect LockupJudder;  // L1 tier: a grip-state texture
        public KerbThumpEffect    KerbThump;     // L3 tier: momentary alert
        public ImplementThudEffect ImplementThud; // L3 tier: momentary alert (FS linkage clunk)
        public GearShiftEffect    GearShift;
        public AbsClickEffect     AbsClick;
        public PitLimiterEffect   PitLimiter;
        public DrsEffect          Drs;
        public CollisionEffect    Collision;
        public RevLimiterEffect   RevLimiter;
        public AirborneEffect     Airborne;
        public IDuckable          Audio;

        // Phase 6: frequency-aware priority arbiter (see BandArbiter). Owned
        // here so the host's only switch is the flag: when true, sidechain
        // multipliers come from class x activity x band-overlap instead of
        // the fixed tier ladder. The airborne stage applies identically in
        // both modes. Volatile: toggled from the UI thread, read on the
        // engine thread.
        public volatile bool UseBandArbiter;
        public BandArbiter Arbiter { get; } = new BandArbiter();
        private TelemetryEffect[] _arbiterTargets;

        // Smoothed per-tier multipliers + the airborne envelope. State lives
        // here so the harness can snapshot it per tick.
        private float _duckL0 = 1.0f;   // engine + audio
        private float _duckL1 = 1.0f;   // road feel + traction + DRS hum
        private float _duckL2 = 1.0f;   // rev + pit limiter
        private float _duckAir = 1.0f;

        /// <summary>Current smoothed tier multipliers, exposed for the harness's
        /// ducking-envelope assertions and the live band visualizer later.</summary>
        public float DuckL0  => _duckL0;
        public float DuckL1  => _duckL1;
        public float DuckL2  => _duckL2;
        public float DuckAir => _duckAir;

        private static float SmoothDuck(float current, float target, float attackMs, float releaseMs)
        {
            // Fast attack (duck quickly when an event hits), slow release.
            // dt ≈ 1 ms (the engine loop runs ~1 batch/ms); alpha = 1 - exp(-dt/tau).
            float tau   = (target < current) ? attackMs : releaseMs;
            float alpha = (float)(1.0 - Math.Exp(-1.0 / Math.Max(0.5, tau)));
            return current * (1f - alpha) + target * alpha;
        }

        /// <summary>One arbitration step. Called once per engine tick with the
        /// host's current tuning values (read live so slider changes apply
        /// without re-wiring).</summary>
        public void Update(float depth, float attackMs, float releaseMs)
        {
            if (UseBandArbiter)
            {
                UpdateViaArbiter(depth, attackMs, releaseMs);
                return;
            }

            // Max activity at each tier.
            double l1 = 0, l2 = 0, l3 = 0;
            if (RoadBumps    != null) l1 = Math.Max(l1, RoadBumps.ActivityLevel);
            if (TractionLoss != null) l1 = Math.Max(l1, TractionLoss.ActivityLevel);
            if (AxleSlip     != null) l1 = Math.Max(l1, AxleSlip.ActivityLevel);
            if (LockupJudder != null) l1 = Math.Max(l1, LockupJudder.ActivityLevel);
            if (Drs          != null) l1 = Math.Max(l1, Drs.ActivityLevel);
            if (RevLimiter   != null) l2 = Math.Max(l2, RevLimiter.ActivityLevel);
            if (PitLimiter   != null) l2 = Math.Max(l2, PitLimiter.ActivityLevel);
            if (AbsClick     != null) l3 = Math.Max(l3, AbsClick.ActivityLevel);
            if (GearShift    != null) l3 = Math.Max(l3, GearShift.ActivityLevel);
            if (Collision    != null) l3 = Math.Max(l3, Collision.ActivityLevel);
            if (KerbThump    != null) l3 = Math.Max(l3, KerbThump.ActivityLevel);
            if (ImplementThud != null) l3 = Math.Max(l3, ImplementThud.ActivityLevel);

            // Strongest activity strictly above each target tier.
            double above0 = Math.Max(l1, Math.Max(l2, l3));   // ducks L0 (engine/audio)
            double above1 = Math.Max(l2, l3);                  // ducks L1 (road/traction/DRS hum)
            double above2 = l3;                                 // ducks L2 (rev/pit limiter)

            float t0 = (float)Math.Max(0.0, 1.0 - depth * above0);
            float t1 = (float)Math.Max(0.0, 1.0 - depth * above1);
            float t2 = (float)Math.Max(0.0, 1.0 - depth * above2);

            _duckL0 = SmoothDuck(_duckL0, t0, attackMs, releaseMs);
            _duckL1 = SmoothDuck(_duckL1, t1, attackMs, releaseMs);
            _duckL2 = SmoothDuck(_duckL2, t2, attackMs, releaseMs);

            // Airborne stage. Ramp the envelope toward (1 - Reduction) while
            // airborne, back to 1 on landing, then fold it into each target the
            // user opted in. Multiplicative on top of the sidechain values so
            // the two duckers compose. Reuses the duck attack/release times
            // (fast in on takeoff, smooth out on touchdown).
            bool  airActive = Airborne != null && Airborne.AirborneActive;
            float airReduce = Airborne?.Reduction ?? 0f;
            float airTarget = airActive ? Math.Max(0f, 1f - airReduce) : 1f;
            _duckAir = SmoothDuck(_duckAir, airTarget, attackMs, releaseMs);

            float a0 = _duckL0, a1 = _duckL1, a2 = _duckL2;
            // Build a per-effect airborne factor: _duckAir if opted in, else 1.
            float fEngine   = (Airborne != null && Airborne.DuckEngine)       ? _duckAir : 1f;
            float fAudio    = (Airborne != null && Airborne.DuckAudio)        ? _duckAir : 1f;
            float fBumps    = (Airborne != null && Airborne.DuckRoadBumps)    ? _duckAir : 1f;
            float fTraction = (Airborne != null && Airborne.DuckTractionLoss) ? _duckAir : 1f;
            float fRev      = (Airborne != null && Airborne.DuckRevLimiter)   ? _duckAir : 1f;
            float fPit      = (Airborne != null && Airborne.DuckPitLimiter)   ? _duckAir : 1f;
            float fDrs      = (Airborne != null && Airborne.DuckDrs)          ? _duckAir : 1f;
            // L3 alert voices (gear shift, ABS, collision) sit above every
            // sidechain tier so the sidechain never touches their multiplier;
            // the airborne factor is therefore the ONLY thing that ducks them,
            // and their base is 1.0.
            float fShift    = (Airborne != null && Airborne.DuckGearShift)    ? _duckAir : 1f;
            float fAbs      = (Airborne != null && Airborne.DuckAbs)          ? _duckAir : 1f;
            float fColl     = (Airborne != null && Airborne.DuckCollision)    ? _duckAir : 1f;

            if (EnginePulse  != null) EnginePulse.DuckMultiplier    = a0 * fEngine;
            if (Audio        != null) Audio.DuckMultiplier          = a0 * fAudio;
            if (RoadBumps    != null) RoadBumps.DuckMultiplier      = a1 * fBumps;
            if (TractionLoss != null) TractionLoss.DuckMultiplier   = a1 * fTraction;
            // Airborne opt-in rides the traction-loss flag: all three are slip
            // textures, and a jump should silence them for the same reason.
            if (AxleSlip     != null) AxleSlip.DuckMultiplier       = a1 * fTraction;
            if (LockupJudder != null) LockupJudder.DuckMultiplier   = a1 * fTraction;
            // Kerb thump is an L3 alert (base 1.0); it rides the road-bumps
            // airborne opt-in — mid-air there is no kerb.
            if (KerbThump    != null) KerbThump.DuckMultiplier      = fBumps;
            // Implement thud: same L3 alert shape and same airborne ride as
            // kerb thump — a linkage event is ground work.
            if (ImplementThud != null) ImplementThud.DuckMultiplier = fBumps;
            if (Drs          != null) Drs.SustainedDuckMultiplier   = a1 * fDrs;
            if (RevLimiter   != null) RevLimiter.DuckMultiplier     = a2 * fRev;
            if (PitLimiter   != null) PitLimiter.DuckMultiplier     = a2 * fPit;
            if (GearShift    != null) GearShift.DuckMultiplier      = fShift;
            if (AbsClick     != null) AbsClick.DuckMultiplier       = fAbs;
            if (Collision    != null) Collision.DuckMultiplier      = fColl;
        }

        /// <summary>Phase 6 sidechain path: multipliers come from the band
        /// arbiter (class x activity x band overlap) instead of the tier
        /// ladder. The airborne stage is unchanged and folds in identically —
        /// it is a physical-state duck, orthogonal to masking.</summary>
        private void UpdateViaArbiter(float depth, float attackMs, float releaseMs)
        {
            if (_arbiterTargets == null)
            {
                var list = new System.Collections.Generic.List<TelemetryEffect>(12);
                if (EnginePulse  != null) list.Add(EnginePulse);
                if (RoadBumps    != null) list.Add(RoadBumps);
                if (TractionLoss != null) list.Add(TractionLoss);
                if (AxleSlip     != null) list.Add(AxleSlip);
                if (LockupJudder != null) list.Add(LockupJudder);
                if (KerbThump    != null) list.Add(KerbThump);
                if (ImplementThud != null) list.Add(ImplementThud);
                if (GearShift    != null) list.Add(GearShift);
                if (AbsClick     != null) list.Add(AbsClick);
                if (PitLimiter   != null) list.Add(PitLimiter);
                if (Drs          != null) list.Add(Drs);
                if (Collision    != null) list.Add(Collision);
                if (RevLimiter   != null) list.Add(RevLimiter);
                _arbiterTargets = list.ToArray();
                Arbiter.Bind(_arbiterTargets);
            }

            Arbiter.Arbitrate(depth, attackMs, releaseMs);

            // Airborne stage, verbatim from the tier path.
            bool  airActive = Airborne != null && Airborne.AirborneActive;
            float airReduce = Airborne?.Reduction ?? 0f;
            float airTarget = airActive ? Math.Max(0f, 1f - airReduce) : 1f;
            _duckAir = SmoothDuck(_duckAir, airTarget, attackMs, releaseMs);

            float fEngine   = (Airborne != null && Airborne.DuckEngine)       ? _duckAir : 1f;
            float fAudio    = (Airborne != null && Airborne.DuckAudio)        ? _duckAir : 1f;
            float fBumps    = (Airborne != null && Airborne.DuckRoadBumps)    ? _duckAir : 1f;
            float fTraction = (Airborne != null && Airborne.DuckTractionLoss) ? _duckAir : 1f;
            float fRev      = (Airborne != null && Airborne.DuckRevLimiter)   ? _duckAir : 1f;
            float fPit      = (Airborne != null && Airborne.DuckPitLimiter)   ? _duckAir : 1f;
            float fDrs      = (Airborne != null && Airborne.DuckDrs)          ? _duckAir : 1f;
            float fShift    = (Airborne != null && Airborne.DuckGearShift)    ? _duckAir : 1f;
            float fAbs      = (Airborne != null && Airborne.DuckAbs)          ? _duckAir : 1f;
            float fColl     = (Airborne != null && Airborne.DuckCollision)    ? _duckAir : 1f;

            if (EnginePulse  != null) EnginePulse.DuckMultiplier    = Arbiter.MultiplierFor(EnginePulse) * fEngine;
            if (Audio        != null) Audio.DuckMultiplier          = Arbiter.AudioMultiplier * fAudio;
            if (RoadBumps    != null) RoadBumps.DuckMultiplier      = Arbiter.MultiplierFor(RoadBumps) * fBumps;
            if (TractionLoss != null) TractionLoss.DuckMultiplier   = Arbiter.MultiplierFor(TractionLoss) * fTraction;
            if (AxleSlip     != null) AxleSlip.DuckMultiplier       = Arbiter.MultiplierFor(AxleSlip) * fTraction;
            if (LockupJudder != null) LockupJudder.DuckMultiplier   = Arbiter.MultiplierFor(LockupJudder) * fTraction;
            if (KerbThump    != null) KerbThump.DuckMultiplier      = Arbiter.MultiplierFor(KerbThump) * fBumps;
            if (ImplementThud != null) ImplementThud.DuckMultiplier = Arbiter.MultiplierFor(ImplementThud) * fBumps;
            if (Drs          != null) Drs.SustainedDuckMultiplier   = Arbiter.MultiplierFor(Drs) * fDrs;
            if (RevLimiter   != null) RevLimiter.DuckMultiplier     = Arbiter.MultiplierFor(RevLimiter) * fRev;
            if (PitLimiter   != null) PitLimiter.DuckMultiplier     = Arbiter.MultiplierFor(PitLimiter) * fPit;
            if (GearShift    != null) GearShift.DuckMultiplier      = Arbiter.MultiplierFor(GearShift) * fShift;
            if (AbsClick     != null) AbsClick.DuckMultiplier       = Arbiter.MultiplierFor(AbsClick) * fAbs;
            if (Collision    != null) Collision.DuckMultiplier      = Arbiter.MultiplierFor(Collision) * fColl;
        }
    }
}
