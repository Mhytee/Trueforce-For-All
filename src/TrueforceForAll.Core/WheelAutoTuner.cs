// AUTO-TUNE: hands-free calibration of the condition renderer against the
// wheel's own firmware, by self-excited system identification.
//
// The insight (owner, 2026-09-01): the plugin can excite the wheel itself.
// Command a brief torque pulse, cut the force, and the coast-down IS the
// measurement; how the wheel was spun up cancels out of a decay rate, and
// the wheel's friction and inertia cancel between phases exactly as in the
// hand-flick DAMPCAL. Phases:
//
//   NATIVE group (Trueforce stream STOPPED, effects + pulses via
//   DirectInput, firmware renders everything = ground truth):
//     NativeForce    pulse only            -> peak speed per pulse
//     NativeDamper   pulse + DI damper     -> exponential decay rate
//     NativeSpring   pulse + DI spring     -> oscillation frequency
//     NativeFriction pulse + DI friction   -> linear deceleration
//   ENGINE group (stream active, pulses + effects rendered by us as cur):
//     EngineForce    pulse only            -> peak speed; the ratio to
//                    NativeForce measures the REAL ep0-vs-ep3 force scale
//                    (what FfbScale has been guessing at) and equalizes all
//                    later engine pulses.
//     EngineBaseline pulse, no effect      -> the wheel's own friction and
//                    inertia, cancelled out of the damper/friction results.
//     EngineDamper / EngineSpring / EngineFriction -> same metrics at the
//                    CURRENT engine gains.
//
// Results (ratios of matched metrics, per DAMPCAL's math):
//   damperGain   = g0 * (rateNative - rateBaseline) / (rateEngine - rateBaseline)
//   springGain   = s0 * (freqNative / freqEngine)^2      (inertia cancels)
//   frictionGain = f0 * (decelNative - decelBaseline) / (decelEngine - decelBaseline)
//   forceRatio   = peakNative / peakEngine  (probe equalization; also the
//                  measured chain-vs-firmware force scale at current settings)
//
// Threading: Sample() is called from ONE thread (the DirectInput poll loop)
// and is the only mutator. EngineCommand is read by the pump thread
// (volatile double via long bits). Callbacks (phase configuration, native
// pulse drive, status) run on the Sample thread, the same contract the
// DAMPCAL wizard already uses. Time comes in as seconds from the caller's
// clock; nothing here reads a clock of its own (testable with a simulated
// wheel).
//
// Safety: every drive is bounded (pulse amplitude starts at 0.22 of full
// scale, escalates at most to 0.4 on weak-response retries), pulses always
// push TOWARD center, and any sample with |pos| > 0.9 or |vel| > 10
// range/s aborts the run with all outputs zeroed. The caller adds its own
// timeout on top.

using System;
using System.Collections.Generic;
using System.Threading;

namespace TrueforceForAll.Core
{
    public sealed class WheelAutoTuner
    {
        public enum Phase
        {
            Idle,
            DirProbe,
            NativeForce, NativeDamper, NativeSpring, NativeFriction,
            EngineDirProbe,
            EngineForce, EngineDamper, EngineSpring, EngineFriction,
            Done, Aborted,
        }

        public sealed class PhaseConfig
        {
            public Phase  Phase;
            public bool   Native;
            public string EffectKind;   // null = no effect (force/baseline)
            public int    EffectPct;
        }

        // Probe strengths (percent coefficient handed to both sides equally).
        public const int DamperProbePct   = 50;
        public const int SpringProbePct   = 50;
        // 10, and a bigger pulse to go with it. Friction is measured from a
        // coast, so the measurement lasts exactly as long as the wheel keeps
        // moving, and a strong friction effect on a direct-drive wheel with
        // its base damper off stops it almost immediately: at 15 % the coast
        // ran about 70 ms, some 58 samples, and the resulting fit swung by a
        // factor of 2.2 between runs while the engine side held to 12 % (rig,
        // 2026-09-02 18:24). Weaker effect plus faster start is a longer
        // coast and more of it to fit, and it costs nothing in signal here
        // because on this wheel the effect IS the whole deceleration.
        public const int FrictionProbePct = 10;

        public const int    TrialsPerPhase = 3;
        // Friction is the noisiest of the three, so it takes more samples to
        // land the same confidence in the median.
        public const int    FrictionTrials  = 5;
        private const int    MaxAttemptsPerPhase = 12;
        // An interleaved phase spends a trial on a baseline for every loaded
        // one, so it needs twice as many trials to reach the same quota and
        // would otherwise have half the retries left for failures, on the
        // phase that fails most.
        // Plateau phases spend trials on adaptation (finding a force that
        // both moves the wheel and stays inside its travel) before they spend
        // any on measuring, and an interleaved phase needs a baseline for
        // every loaded trial. Neither should die with the search half done.
        private static int AttemptsFor(Phase p)
            => IsInterleavedPhase(p) ? MaxAttemptsPerPhase * 2
             : IsPlateauPhase(p)     ? MaxAttemptsPerPhase * 3 / 2
             : MaxAttemptsPerPhase;
        private const double HandsOffSec  = 2.5;
        private const double SettleSec    = 0.6;
        private const double SettleVel    = 0.10;
        // "Make sure the wheel has fully settled before each test" (owner,
        // 2026-09-02). A velocity threshold alone is a weak test of stopped:
        // it sits near the encoder's noise floor and passes a wheel that is
        // still drifting. Position is unambiguous, so the real gate is that
        // the wheel has not MOVED for a continuous hold.
        //
        // It matters most in the EngineBaseline phase, which runs with no
        // effect loaded and so decays only through the wheel's own friction:
        // it settles slowest of all the phases, and its rate is the number
        // both the damper and the friction gains are measured against.
        private const double SettleDrift   = 0.010;   // of full range, over the hold
        private const double SettleHoldSec = 0.50;
        private const double SettleTimeoutSec = 9.0;
        private const double PulseSec     = 0.15;
        private const double MeasureSec   = 3.0;
        private const double StartPulseAmp = 0.22;
        private const double MaxPulseAmp   = 0.40;
        // Friction phases start harder and are allowed further: their whole
        // measurement window is the coast, so the faster the wheel is going
        // when the force is cut, the more of a coast there is to fit.
        private const double FrictionPulseAmp = 0.38;
        private const double FrictionMaxPulse = 0.60;

        private static double StartAmpFor(Phase p)
            => (p == Phase.DirProbe || p == Phase.EngineDirProbe) ? DirProbeAmp
             : IsInterleavedPhase(p) ? FrictionPulseAmp
             : StartPulseAmp;

        private static double MaxAmpFor(Phase p)
            => IsInterleavedPhase(p) ? FrictionMaxPulse : MaxPulseAmp;
        // Re-centering (owner's rule after the near-lock runaway,
        // 2026-09-01: center, play, center, play): every trial begins by
        // driving the wheel gently back to center so a pulse always has
        // room on both sides.
        private const double CenterTol        = 0.06;
        // Starting floor for the centering drive, not a constant: a wheel
        // whose breakaway friction exceeds it sits just outside the tolerance
        // until the phase gives up ("could not re-center"). The floor
        // escalates while the wheel refuses to move, bounded by the cap.
        private const double DirProbeAmp      = 0.15;
        // Walking to centre with the frame unknown: gentle on position, heavy
        // on velocity. The velocity term is the "damping" that keeps the walk
        // slow, and it works in either sign, so a wrong guess creeps a little
        // and is corrected long before it matters.
        private const double CenterArriveVel     = 0.10;
        // Centring spring: start modest, escalate if the wheel is still not
        // home, and damp it with velocity feedback once the frame is known.
        private const int    CenterSpringPct     = 40;
        private const int    CenterDampPct       = 25;
        private const int    CenterDampMaxPct    = 100;
        private const int    CenterSpringMaxPct  = 100;
        private const double CenterSpringStepSec = 1.8;
        // Driving the wheel home when no firmware spring can hold it. Only
        // ever used with the frame already measured.
        private const double DrivenHomeKp         = 0.60;
        private const double DrivenHomeKd         = 0.50;
        private const double DrivenHomeMax        = 0.25;
        private const double HomeTimeoutSec      = 20.0;
        private const double HomeMaxOffset       = 0.60;
        // How long a direction gets to prove itself, and how slow the wheel
        // must be before its motion can be blamed on the drive rather than on
        // what it inherited.
        // Discard-and-shrink long before the hard abort: a pulse that flies
        // this far was too strong or the wrong way (owner: shorter pulses).
        private const double SoftTravelLimit  = 0.75;
        // Friction trials are deliberately long coasts, so cutting them at
        // the general limit leaves nothing between the cut and the lock: the
        // wheel sails from 0.75 to 0.95 with the drive already off and trips
        // the runaway guard for doing exactly what it was asked to do. Cut
        // them earlier and let the coast have the room.
        private const double CoastTravelLimit = 0.55;

        private static double TravelLimitFor(Phase p)
            => IsInterleavedPhase(p) ? CoastTravelLimit : SoftTravelLimit;

        // ---- Damper by steady state, not by coast ----
        // A damper is the thing that makes a constant force produce a BOUNDED
        // speed, so measure exactly that: hold a force, let the wheel settle,
        // average the speed. Two force levels remove Coulomb friction without
        // needing a baseline at all:
        //
        //     F1 = c*v1 + mu,  F2 = c*v2 + mu   ->   c = (F2 - F1) / (v2 - v1)
        //
        // The coast-down this replaces could not be made to work on the rig.
        // A damped coast there is over in 0.04..0.15 s, and a tenth of a
        // second of quantized, EMA-smoothed velocity does not determine a
        // decay curve: fits came back with R2 of 0.15, -0.03, -0.85, whether
        // solving for one unknown or two (2026-09-02 12:08 and 12:19). The
        // old code had no quality check and accepted them all, which is why
        // the damper scattered 67..115 % while the spring, measured from an
        // oscillation frequency, held 1 %. Averaging a plateau is the same
        // kind of integrative measurement the spring gets, and it is immune
        // to filter lag, to quantization, and to how briefly the wheel coasts.
        //
        // Both plateaus push the SAME way. Reversing between them was a
        // travel convenience and it broke the very identity the method rests
        // on: c = (F2-F1)/(v2-v1) needs the SAME mu in both equations, and
        // Coulomb friction reverses sign with direction. Worse, the starting
        // direction is decided by which side of centre the wheel happened to
        // settle on, so when the two damper phases started opposite ways the
        // offsets added instead of cancelling: 25 % out at 20 % left/right
        // friction asymmetry, and a median cannot repair a coin flip.
        //
        // Pushing the same way also makes the INTERCEPT meaningful:
        // F = mu + c*v, so the two levels give the friction torque as well as
        // the drag, from one trial, in one protocol.
        // A BASE force and a STEP above it, not two multiples of one force.
        // The base has to clear the friction threshold before the wheel moves
        // at all, and the step sets how far apart the two speeds land. With
        // two multiples those are the same knob, so raising it until the
        // wheel moves at the low level sends the high level's speed through
        // the roof: with a friction effect loaded the rig's own simulator hit
        // "0.00 then 2.74 range/s" and then ran out of travel. Separate knobs,
        // separate failures.
        private const double PlateauBase      = 0.12;
        private const double PlateauStep      = 0.10;
        private const double PlateauHoldSec   = 0.45;   // settle to terminal speed
        private const double PlateauWindowSec = 0.15;   // averaged tail of the hold
        // Every plateau starts from a wheel at rest, and a wheel at rest
        // has to be broken loose before it slides. That is STICTION, and it is
        // not what F = mu + c*v describes: the model is about the kinetic
        // regime. Left in, it forced the low level up until the wheel would
        // start, which sent the high level's speed out of the travel window,
        // and the two requirements could never be met at once (the low level
        // read 0.00 on every trial of both friction phases). So break it loose
        // deliberately, then drop to the level being measured, with the
        // averaging window well clear of the kick.
        private const double PlateauKickSec   = 0.12;
        // A plateau ends at terminal speed, by design, a long way from
        // centre. Handing that to the next phase is dangerous: the spring
        // phase loads a centring effect onto a wheel that is already
        // travelling, converts the displacement into a slingshot and trips the
        // runaway guard (rig, 2026-09-02 14:22: 16.1 range/s crossing centre,
        // 1.7 s after the damper phase handed over). Every other phase ends
        // with a coast, so nothing used to inherit motion. Bring the wheel to
        // rest before leaving.
        private const double BrakeTimeoutSec  = 3.0;
        private const double BrakeVel         = 0.30;
        private const double BrakeMaxDrive    = 0.20;
        private const double BrakePos         = 0.30;
        private const double PlateauFlatFrac  = 0.04;
        private const double PlateauFlatSigma = 2.5;
        // Absolute floor on what counts as a rise, in range/s. Derived
        // velocity comes off a 16-bit axis differenced at about 1 kHz, so its
        // quantum is roughly 0.024 range/s: a step smaller than that is not a
        // trend, it is the sensor changing its mind by one count.
        //
        // The scatter test alone does not catch this. The signal is EMA
        // smoothed, so consecutive samples are correlated, and a half-window
        // can sit on a single quantum level with almost no scatter while the
        // next half sits on the one above it. Then sigma is tiny, the step is
        // one quantum, and noise reads as a trend every time. At a 0.15
        // range/s plateau this asked the test to resolve 0.006 range/s with a
        // 0.024 quantum, and it rejected roughly seven trials in ten (rig,
        // 2026-09-02 18:05).
        private const double PlateauFlatAbs   = 0.022;
        // Measure where the signal clears the noise. Derived velocity is
        // quantized at roughly 0.024 range/s on this encoder, so a plateau
        // averaged at 0.05 is a quarter noise, and the flatness test then
        // cannot tell a rising plateau from a still one. A strong damper is
        // exactly the case that lands there: the stronger it is, the LOWER
        // the terminal speed, which is why the native damper phase was the
        // one starving (rig, 2026-09-02 17:49). Demanding a faster plateau
        // pushes the base force up until the measurement is worth taking.
        private const double PlateauMinSpeed  = 0.15;
        private const double PlateauMinGap    = 0.06;   // v2 must exceed v1 by this
        private const double PlateauMaxScale  = 3.0;
        private const double PlateauMinScale  = 0.2;
        // A plateau runs at a steady speed, so it EATS travel: cut it well
        // before the soft limit, leaving room to brake a wheel that may be
        // moving quickly. A weakly damped side reaches a high terminal speed
        // and would otherwise sail into the lock (caught in simulation).
        private const double PlateauTravelCut = 0.50;

        // Every phase that measures a drag/friction pair by holding force.
        // The baselines run the identical protocol with NO effect loaded, so
        // subtracting one from the other cancels the protocol's own biases
        // instead of importing a second measurement's errors.
        // Both plateau levels push one way, so the pair needs the whole
        // sweep, not half of it: the wheel is parked on the far side first and
        // the pair runs across centre. The push direction alternates from
        // trial to trial, so any left/right asymmetry in the wheel averages
        // out across a phase instead of biasing it.
        private const double PlateauLaunch = 0.35;

        // Plateaus measure the DAMPER only. Friction cannot be measured
        // this way: a constant friction torque produces no terminal speed at
        // all. Above the threshold the wheel simply accelerates (there is
        // nothing velocity-proportional to balance the force against) and
        // below it the wheel does not move, so there is no plateau to average
        // and the two force levels can never both be valid. The simulator
        // shows it plainly: every friction trial reads "0.00 then 1.57".
        private static bool IsPlateauPhase(Phase p)
            => p == Phase.NativeDamper || p == Phase.EngineDamper;

        private readonly double _damperG0, _springG0, _frictionG0;
        private readonly Action<PhaseConfig> _enterPhase;
        private readonly Action<double> _driveNative;   // signed -1..1; 0 = stop
        private readonly Action<string> _status;
        // Turns the phase's effect on or off WITHOUT reconfiguring the phase,
        // so a baseline trial can run seconds away from a loaded one instead
        // of minutes away in another phase.
        private readonly Action<bool> _setEffectActive;
        // Loads a centring SPRING of the given strength in place of the
        // phase's effect, or restores the phase's effect when passed 0.
        // Returns false if the device would not take it.
        private readonly Func<int, int, double, bool> _centerSpring;

        private Phase  _phase = Phase.Idle;
        private int    _validTrials;
        private int    _attempts;
        private double _pulseAmp = StartPulseAmp;
        private int    _trialState;          // 0 settle, 1 pulse, 2 measure
        private double _stateT, _settleOkT, _phaseStartT, _settleRefPos;
        // Where the wheel sat when the run began: the reference every trial
        // returns to. Captured once, after the hands-off pause, so it is a
        // resting position rather than wherever a previous phase happened to
        // leave things.
        private bool   _springHeld, _springWarned;
        private double _springSince;
        private int    _centerDampPct = CenterDampPct;
        private int    _centerSpringPct = CenterSpringPct;
        private double _homePos;
        private bool   _homeSet;
        private bool   _started;
        private int    _pulseDir = 1;
        private double _lastDrive = double.NaN;
        private string _rejectReason;
        private string _fitDetail;
        private int    _lastFitSamples;
        private double _lastFitSpan;
        // The wheel's own Coulomb friction, in range/s^2, measured on each
        // side by the phase that runs with NO effect loaded. Held out of the
        // damper fits so those solve for one unknown instead of two: see
        // FitViscousRate.
        // The unloaded wheel, measured on each side from the force phase's
        // own free coast. THREE numbers are needed and all three come out of
        // work the run already does:
        //
        //   mu   (range/s^2)  Coulomb, from the coast fit's intercept
        //   c    (1/s)        viscous, from the coast fit's slope
        //   J/K               inertia per unit command, from the pulse itself
        //
        // The coast fit works in ACCELERATION, the plateaus in FORCE, and the
        // two differ by exactly the wheel's inertia. That is why the coast
        // fit's outputs could never simply be subtracted from a plateau (they
        // are a decay rate in 1/s against a force per range/s, out by a factor
        // of J). The missing conversion is recoverable: a pulse of amplitude A
        // held for T seconds from rest reaches
        //
        //     v_peak = (A - mu*J/K) * T / (J/K)   ->   J/K = A*T / (v_peak + mu*T)
        //
        // so multiplying a coast-fit rate by J/K puts it in the plateaus'
        // units. No extra phase, and nothing has to be measured on a wheel
        // that has no terminal speed to measure.
        private double _muNative = double.NaN, _muEngine = double.NaN;
        private double _frictionBaseNative = double.NaN, _frictionBaseEngine = double.NaN;
        private double _cRateNative = double.NaN, _cRateEngine = double.NaN;
        private double _jOverKNative = double.NaN, _jOverKEngine = double.NaN;
        private readonly List<double> _muSamples = new List<double>();
        private readonly List<double> _cRateSamples = new List<double>();
        private readonly List<double> _jSamples = new List<double>();
        // The wheel's OWN viscous drag, from the same no-effect coasts. The
        // damper plateaus measure wheel plus effect, so this comes back off.
        // Plateau state: scale on the two force levels (adapts if the wheel
        // barely moves or runs out of travel), and the two measured speeds.
        // Starts gentle and grows if the wheel barely moves: overshooting
        // travel costs a discarded trial, being too slow costs only a retry.
        // Friction's baseline is measured INSIDE its own phase, alternating
        // loaded and unloaded trials seconds apart, rather than being taken
        // from the force phase minutes earlier and colder. Friction is the
        // only gain built from a difference of two separately measured
        // quantities, so a baseline that drifts between the two measurements
        // lands entirely in the answer: the rig's x5 walked 0.49, 0.68, 0.74,
        // 0.83, 0.93, an exponential-approach signature rather than scatter.
        // Interleaving makes both halves ride whatever the wheel is doing.
        // Baseline FIRST: it is the trial that measures the wheel alone, and
        // the loaded trials need its drag figure to hold fixed. Running it
        // second meant the first loaded trial had to borrow a drag from the
        // force phase, which is the stale number this whole change exists to
        // stop using.
        private bool _baselineTrial = true;
        private readonly List<double> _baseSamples = new List<double>();
        private readonly List<double> _baseDragSamples = new List<double>();

        // With no effect loaded the wheel coasts far further on the same
        // pulse, so a baseline trial that matched the loaded one's amplitude
        // simply ran out of travel (rig, 2026-09-02: over-travel on every
        // alternate trial). Coulomb friction is speed-independent by
        // definition, so measuring it from a gentler pulse costs nothing.
        private const double BaselinePulseScale = 0.55;

        // Each trial's walk starts fresh: a reversal that was right for one
        // approach says nothing about the next.
        // Each trial starts its centring afresh.
        private void ResetWalk()
        {
            _springHeld = false;
            _centerDampPct = CenterDampPct;
            _centerSpringPct = CenterSpringPct;
        }

        /// <summary>Where this trial should start from: home, or a launch
        /// point to one side of it when the trial needs a run-up.</summary>
        // True once this side's direction probe has decided which way our
        // commands push, which is what velocity damping needs to be safe.
        private bool _frameKnown => IsNativePhase(_phase)
            ? _phase != Phase.DirProbe
            : _engineDirSet;

        private double CenterTarget()
        {
            double target = _homePos;
            if (IsPlateauPhase(_phase)) target -= _plateauDir * PlateauLaunch;
            // Never aim somewhere the wheel cannot comfortably sit.
            if (target > HomeMaxOffset) target = HomeMaxOffset;
            else if (target < -HomeMaxOffset) target = -HomeMaxOffset;
            return target;
        }

        private static bool IsInterleavedPhase(Phase p)
            => p == Phase.NativeFriction || p == Phase.EngineFriction;

        private double _plateauScale = 0.6;      // scales the base
        private double _plateauStepScale = 1.0;  // scales the step
        private int    _plateauDir = 1;
        private double _plateauHold = PlateauHoldSec;
        private double _plateauV1, _plateauV2;
        // Two halves of the averaging window, so a plateau that has not
        // levelled off yet can be told from one that has.
        private double _plateauSumA, _plateauSumB;
        private double _plateauSqA, _plateauSqB;
        private int    _plateauNA, _plateauNB;
        private bool   _plateauUnsettled;
        private string _plateauFlatDetail;
        // Where the wheel started the pulse, and how far it had moved by
        // the end of it. The direction frame is that displacement's sign:
        // push the wheel, see which way it went (owner, 2026-09-02).
        private double _pulseStartPos, _pulseDisp;
        private double _forceEq = 1.0;
        // Direction frames are MEASURED, never assumed (first rig run,
        // 2026-09-01: the DirectInput direction sign on the owner's G PRO
        // opposes the assumed frame, so 'toward center' pulses and the
        // centering drive itself pushed the wheel into the lock). DirProbe
        // measures the native frame from one small raw pulse; the engine
        // frame is detected on the FIRST EngineForce trial.
        private int    _nativeDir = 1;
        private bool   _engineDirSet;
        public  bool   NativeDirFlipped { get; private set; }
        private double _nextHandNagT;
        // The engine chain's sign convention vs the DirectInput frame on
        // this rig (FfbInvertSign and friends): detected from the force
        // phases' signed responses; engine pulses compensate so they always
        // push toward center too (audit F5).
        private int _engineDir = 1;
        public bool EngineDirFlipped { get; private set; }

        // What the engine probe ACTUALLY settles (rig run 2026-09-01, and
        // the correction it forced): the probe drives a known command into
        // cur and watches the velocity the RENDERER reads back, so it
        // measures the sign of the loop the renderer closes, nothing else.
        //
        //   cur = +k x velocity          (the damper, velTermSign +1)
        //   velocity_response = G x cur  (G = what the probe measures)
        //
        // so the loop decays only when G < 0, i.e. when a positive cur
        // command drives the measured velocity NEGATIVE: EngineDirFlipped.
        // The renderer's sign contract (positive cur pulls toward lower
        // steer, positive velocity is the other way) was authored for
        // exactly that, and the rig confirmed it in AC. So a flipped engine
        // frame is the CORRECT state, not a fault: the earlier reading
        // ("an inverted chain inverts the rendered effects") had it
        // backwards and advised a flip that would have made an anti-damper.
        // Only the un-flipped case is a real problem, and velTermSign alone
        // cannot rescue it, because springs never flip with it.
        public bool RenderSignMeasured { get; private set; }
        public bool RenderSignCorrect  { get; private set; }

        private readonly List<double> _vt = new List<double>(4096);
        private readonly List<double> _vv = new List<double>(4096);
        private readonly List<double> _vp = new List<double>(4096);
        private readonly List<double> _metricA = new List<double>();   // per-phase primary metric
        private readonly List<double> _metricB = new List<double>();   // baseline: linear decel too

        // The probe strength each phase actually ran at, so every gain is a
        // ratio of PER-UNIT quantities rather than of raw readings. Both
        // sides of a pair use the same constant today, which makes this a
        // no-op right now; it exists so that the two sides are allowed to
        // differ. Every measured quantity is proportional to the coefficient
        // (drag and friction directly, stiffness through frequency squared),
        // so dividing by the strength used is all that is required.
        //
        // An automatic probe escalation was built on top of this and removed
        // the same day: on a simulated wheel with weak effects it changed
        // which error the run died of but rescued none of them. The binding
        // constraint there was not effect strength but the plateau's TRAVEL
        // budget, which a weakly damped wheel exhausts before it reaches a
        // steady speed. That is a limit of the plateau method itself, and it
        // is the strongest practical argument for an oscillation-based
        // measurement, which needs no sustained travel at all.
        private readonly Dictionary<Phase, double> _pctUsed = new Dictionary<Phase, double>();

        private readonly Dictionary<Phase, double> _resultA = new Dictionary<Phase, double>();
        private readonly Dictionary<Phase, double> _resultB = new Dictionary<Phase, double>();

        private long _engineCmdBits;
        /// <summary>Pump thread: the engine-side pulse command right now,
        /// -1..1 of full scale (already force-equalized). 0 outside pulses
        /// and through every native phase.</summary>
        public double EngineCommand => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _engineCmdBits));
        private void SetCommand(double v) => Interlocked.Exchange(ref _engineCmdBits, BitConverter.DoubleToInt64Bits(v));

        /// <summary>True only while the effect under test should be acting:
        /// the pulse and the measurement. During re-centering it must NOT be,
        /// because it is not being measured then, it is only resisting: the
        /// engine friction phase repeatedly could not drag the wheel back to
        /// centre against our own rendered friction and skipped, losing the
        /// friction gain in two runs of three (rig x3, 2026-09-02 11:44).
        /// The renderer keeps evaluating either way, so its filters and the
        /// friction lock point stay tracking the wheel and nothing jumps when
        /// the effect re-engages.</summary>
        public bool   EffectEngaged
            => _trialState >= 2 && (!IsInterleavedPhase(_phase) || !_baselineTrial);

        public Phase  Current => _phase;
        public bool   Running => _phase != Phase.Done && _phase != Phase.Aborted;
        public string AbortReason { get; private set; }

        // Final results (valid when Current == Done).
        public double MeasuredForceRatio { get; private set; } = double.NaN;
        public double DamperGain   { get; private set; } = double.NaN;
        public double SpringGain   { get; private set; } = double.NaN;
        public double FrictionGain { get; private set; } = double.NaN;
        public bool   ResultsSuspect { get; private set; }
        /// <summary>The drag each side actually produced under load, in
        /// force per unit speed. Reported so a NaN gain can be traced to
        /// which side failed to measure rather than guessed at.</summary>
        public double MeasuredDragNative { get; private set; } = double.NaN;
        public double MeasuredDragEngine { get; private set; } = double.NaN;

        public WheelAutoTuner(double damperG0, double springG0, double frictionG0,
                              Action<PhaseConfig> enterPhase, Action<double> driveNative,
                              Action<string> status, Action<bool> setEffectActive = null,
                              Func<int, int, double, bool> centerSpring = null)
        {
            _setEffectActive = setEffectActive;
            _centerSpring = centerSpring;
            _damperG0   = damperG0  > 0 ? damperG0  : 0.25;
            _springG0   = springG0  > 0 ? springG0  : 1.0;
            _frictionG0 = frictionG0 > 0 ? frictionG0 : 1.0;
            _enterPhase = enterPhase;
            _driveNative = driveNative;
            _status = status;
        }

        // Cancel may come from any thread; Sample (the single mutator thread)
        // consumes the flag at its next tick so state stays single-threaded.
        private volatile bool _cancelReq;
        private volatile string _cancelWhy;

        public void Cancel(string why)
        {
            _cancelWhy = why ?? "cancelled";
            _cancelReq = true;
            SetCommand(0);   // pump-side output dies immediately either way
        }

        /// <summary>One wheel sample: position -1..1, velocity in range/s,
        /// time in seconds (any epoch, monotonic).</summary>
        public void Sample(double pos, double vel, double t)
        {
            if (!Running) return;
            if (_cancelReq) { Abort(_cancelWhy); return; }

            // Runaway guard: everything off, immediately.
            // The position guard stands down while parked (Idle / the
            // hand-center wait): a wheel LEFT near the lock by an earlier
            // abort must not kill the next run before the user can center
            // it (audit AT2-05 raw). Velocity always guards.
            bool parkedPhase = _phase == Phase.Idle || _phase == Phase.DirProbe;
            if ((!parkedPhase && Math.Abs(pos) > 0.95) || Math.Abs(vel) > 10)
            {
                Abort($"the wheel ran away (pos {pos:F2}, vel {vel:F1}); everything zeroed");
                return;
            }

            if (_phase == Phase.Idle)
            {
                if (!_started)
                {
                    _started = true;
                    _stateT = t;
                    Status($"hands OFF the wheel; auto-tune starts in {HandsOffSec:F0} s");
                }
                if (t - _stateT >= HandsOffSec)
                {
                    if (!_homeSet) { _homePos = pos; _homeSet = true; }
                    NextPhase(t);
                }
                return;
            }

            switch (_trialState)
            {
                case 0:   // spring home: let the wheel's own centring do it
                {
                    // Hold a SPRING and wait, instead of steering the wheel
                    // home by hand.
                    //
                    // A DirectInput spring is defined in the WHEEL's frame, so
                    // the firmware pulls it to centre correctly whether or not
                    // we have worked out which way our own commands run. That
                    // removes the entire problem the previous approach existed
                    // to solve: no sign to discover, no reversal to detect, no
                    // acceleration test, and no gains fitted to one wheel.
                    //
                    // It also removes the failure that drove this rewrite. A
                    // hand-driven walk has to cap its output for safety, and a
                    // capped drive cannot brake a wheel that arrives with
                    // speed: it becomes bang-bang, overshoots, turns round,
                    // overshoots again, and burns attempts (owner, rig). A
                    // spring has the wheel's full authority behind it and is
                    // tuned to that wheel's inertia, so it does not hunt.
                    //
                    // Damping is ours to add, because a spring alone on a
                    // direct-drive wheel with almost no friction rings for a
                    // long time. It is pure velocity feedback, so it needs the
                    // frame: before the frame is known we simply wait longer
                    // and let the spring settle on its own.
                    double target = CenterTarget();
                    double err = pos - target;

                    if (!_springHeld)
                    {
                        _springHeld = _centerSpring != null
                                      && _centerSpring(_centerSpringPct, _centerDampPct, target);
                        _springSince = t;
                        if (!_springHeld && !_springWarned)
                        {
                            _springWarned = true;
                            Status("no centring spring available; settling on friction alone");
                        }
                    }

                    // Arrived AND stopped. Both: a wheel passing through home
                    // at speed has not arrived, and the pulse that follows
                    // would measure the leftover momentum.
                    if (Math.Abs(err) < CenterTol && Math.Abs(vel) < CenterArriveVel)
                    {
                        DriveProbe(0);
                        _centerSpring?.Invoke(0, 0, 0);          // hand the slot back
                        _springHeld = false;
                        _centerDampPct = CenterDampPct;
                        _centerSpringPct = CenterSpringPct;
                        _trialState = 1;
                        _stateT = t;
                        _settleOkT = 0;
                        break;
                    }

                    // With a firmware spring holding, nothing of ours is
                    // driven: the pull and the damping are both effects, which
                    // is what lets it work before the frame is known.
                    //
                    // Without one, drive the wheel home. That is the case on
                    // the ENGINE side, where the stream is live and the
                    // firmware has dropped its slot engine, so a DirectInput
                    // spring would do nothing at all. It is sound there for
                    // the reason it was not sound before the direction probe:
                    // by then the frame HAS been measured. Braking authority
                    // is what the old walk lacked, so this is allowed a good
                    // deal more of it than a frame-blind push ever could be.
                    if (_springHeld) DriveProbe(0);
                    else
                    {
                        double push = -err * DrivenHomeKp - vel * DrivenHomeKd;
                        if (push > DrivenHomeMax) push = DrivenHomeMax;
                        else if (push < -DrivenHomeMax) push = -DrivenHomeMax;
                        DriveProbe(push);
                    }

                    // Not home yet: escalate whichever thing is in the way,
                    // because the two failures are opposites.
                    //
                    // STILL MOVING means it is ringing through home, and more
                    // spring would only make it ring faster: it wants DAMPING.
                    //
                    // STOPPED SHORT is stiction. A spring cannot pull closer
                    // than friction divided by stiffness, so the wheel parks in
                    // a deadband and stays there however long you wait. That
                    // one wants more SPRING, which shrinks the deadband.
                    if (_springHeld && t - _springSince > CenterSpringStepSec)
                    {
                        bool stuck = Math.Abs(vel) < CenterArriveVel;
                        if (stuck && _centerSpringPct < CenterSpringMaxPct)
                        {
                            _centerSpringPct = Math.Min(CenterSpringMaxPct,
                                                        (int)(_centerSpringPct * 1.5) + 10);
                            _springHeld = false;
                            Status($"stopped short of home; centring spring to {_centerSpringPct}%");
                        }
                        else if (!stuck && _centerDampPct < CenterDampMaxPct)
                        {
                            _centerDampPct = Math.Min(CenterDampMaxPct,
                                                      (int)(_centerDampPct * 1.7) + 10);
                            _springHeld = false;
                            Status($"still ringing; centring damper to {_centerDampPct}%");
                        }
                        else _springSince = t;             // both maxed: keep waiting
                    }

                    if (t - _stateT > HomeTimeoutSec)
                    {
                        DriveProbe(0);
                        _centerSpring?.Invoke(0, 0, 0);
                        _springHeld = false;
                        if (IsOptionalPhase(_phase))
                            SkipPhase(t, "could not get the wheel home");
                        else
                            Abort($"{_phase}: the wheel would not come home "
                                + $"(at {pos:+0.00;-0.00}, home {target:+0.00;-0.00})");
                        break;
                    }
                    if (t >= _nextHandNagT)
                    {
                        _nextHandNagT = t + 4;
                        Status($"centring ({pos:+0.00;-0.00} -> {target:+0.00;-0.00}); hands off");
                    }
                    break;
                }

                case 1:   // settle: outputs quiet, wait for a STOPPED wheel
                {
                    DriveProbe(0);
                    // The hold restarts the moment the wheel moves past
                    // either gate, so a spring still ringing itself out or a
                    // baseline still coasting cannot start the next pulse
                    // with leftover energy in it.
                    if (Math.Abs(vel) < SettleVel && _settleOkT != 0
                        && Math.Abs(pos - _settleRefPos) <= SettleDrift)
                    {
                        // holding still: keep the hold running
                    }
                    else
                    {
                        _settleOkT = t;
                        _settleRefPos = pos;
                    }
                    if (t - _stateT >= SettleSec && t - _settleOkT >= SettleHoldSec)
                    {
                        if (IsInterleavedPhase(_phase))
                            try { _setEffectActive?.Invoke(!_baselineTrial); } catch { }
                        _pulseDir = pos > 0 ? -1 : 1;   // always push toward center
                        _vt.Clear(); _vv.Clear(); _vp.Clear();
                        _pulseStartPos = pos; _pulseDisp = 0;
                        // Drive FIRST, then stamp the window: the native path
                        // does device work before force starts, and the pulse
                        // clock must not run during it (audit AT-06).
                        double amp = _pulseAmp * (IsInterleavedPhase(_phase) && _baselineTrial
                                                  ? BaselinePulseScale : 1.0);
                        if (IsPlateauPhase(_phase))
                        {
                            ResetPlateauWindow();
                            _plateauV1 = _plateauV2 = 0;
                            _plateauUnsettled = false;
                            // Parked on the far side by the centring step,
                            // so the pair sweeps across centre.
                            _pulseDir = _plateauDir;
                            DriveProbe(_pulseDir * PlateauLevel(1));
                            _trialState = 4;
                        }
                        else
                        {
                            DriveProbe(_pulseDir * amp);
                            _trialState = 2;
                        }
                        _stateT = t;
                        break;
                    }
                    if (t - _stateT >= SettleTimeoutSec)
                    {
                        // Never pulse a moving wheel just because the clock
                        // ran out: measuring one is exactly what this state
                        // exists to prevent. Discard and re-center instead.
                        _attempts++;
                        Status($"{_phase}: wheel would not settle; trial discarded");
                        if (_attempts >= AttemptsFor(_phase))
                        {
                            if (IsOptionalPhase(_phase)) SkipPhase(t, "the wheel never settled");
                            else Abort($"{_phase}: the wheel never came to rest between trials");
                            break;
                        }
                        _trialState = 0;
                        _stateT = t;
                        _settleOkT = 0;
                        ResetWalk();
                    }
                    break;
                }

                case 2:   // pulse
                    if (OverTravel(pos, t)) break;
                    _vt.Add(t); _vv.Add(vel); _vp.Add(pos);
                    if (t - _stateT >= PulseSec)
                    {
                        DriveProbe(0);
                        _pulseDisp = pos - _pulseStartPos;
                        _trialState = 3;
                        _stateT = t;
                    }
                    break;

                case 4:   // break stiction, then hold F1 and average the tail
                {
                    if (PlateauOverTravel(pos, t)) break;
                    double since = t - _stateT;
                    // Kick at the higher level to get it sliding, then settle
                    // to the level actually being measured.
                    DriveProbe(_pulseDir * PlateauLevel(since < PlateauKickSec ? 2 : 1));
                    AccumulatePlateau(vel, since - PlateauKickSec);
                    if (since - PlateauKickSec >= _plateauHold)
                    {
                        _plateauV1 = ClosePlateauWindow();
                        ResetPlateauWindow();
                        // Same direction for the second level: see the note on
                        // PlateauBase. Travel is bought back by launching the
                        // pair from the far side instead. Already sliding, so
                        // no second kick.
                        DriveProbe(_pulseDir * PlateauLevel(2));
                        _trialState = 5;
                        _stateT = t;
                    }
                    break;
                }

                case 5:   // second plateau: hold F2, average the flat tail
                    if (PlateauOverTravel(pos, t)) break;
                    AccumulatePlateau(vel, t - _stateT);
                    if (t - _stateT >= _plateauHold)
                    {
                        _plateauV2 = ClosePlateauWindow();
                        DriveProbe(0);
                        FinishTrial(t);
                    }
                    break;

                case 6:   // settle out: hand the next phase a stopped wheel at home
                {
                    // Do this with the CURRENT phase's means, before the next
                    // phase changes them. It matters most at the native-to-
                    // engine handover: the firmware spring is available here
                    // and gone a moment later, because resuming the stream
                    // makes the firmware drop its slot engine. Leaving that
                    // handover uncentred stranded the engine direction probe,
                    // which has no way to centre and no frame to steer by
                    // (rig, 2026-09-02 20:10, wheel parked at -0.29).
                    if (!_springHeld)
                    {
                        _springHeld = _centerSpring != null
                                      && _centerSpring(_centerSpringPct, _centerDampPct, _homePos);
                        _springSince = t;
                    }
                    double berr = pos - _homePos;
                    if (_springHeld) DriveProbe(0);
                    else
                    {
                        double drive = -berr * 0.7 - vel * 0.35;
                        if (drive > BrakeMaxDrive) drive = BrakeMaxDrive;
                        else if (drive < -BrakeMaxDrive) drive = -BrakeMaxDrive;
                        DriveProbe(drive);
                    }
                    if ((Math.Abs(vel) < BrakeVel && Math.Abs(berr) < BrakePos)
                        || t - _stateT > BrakeTimeoutSec)
                    {
                        DriveProbe(0);
                        _centerSpring?.Invoke(0, 0, 0);
                        _springHeld = false;
                        NextPhase(t);
                    }
                    break;
                }

                case 3:   // measure the free response
                    if (OverTravel(pos, t)) break;
                    _vt.Add(t); _vv.Add(vel); _vp.Add(pos);
                    if (t - _stateT >= MeasureSec)
                    {
                        FinishTrial(t);
                    }
                    break;
            }
        }

        // A trial whose motion reaches SoftTravelLimit is cut and discarded
        // with a REDUCED pulse (never escalated): it was too strong or the
        // wrong direction, and the hard runaway guard should stay a last
        // resort (owner, after the engine-test runaway).
        private bool OverTravel(double pos, double t)
        {
            if (Math.Abs(pos) <= TravelLimitFor(_phase)) return false;
            DriveProbe(0);
            SetCommand(0);
            if (IsPlateauPhase(_phase))
                _plateauStepScale = Math.Max(PlateauMinScale, _plateauStepScale * 0.8);
            else
                _pulseAmp = Math.Max(0.10, _pulseAmp * 0.8);
            _attempts++;
            Status($"over-travel at {pos:F2}; trial discarded, pulse reduced to {_pulseAmp:F2}");
            if (_attempts >= AttemptsFor(_phase))
            {
                Abort("repeated over-travel (wrong direction frame, or the wheel is too free); run aborted");
                return true;
            }
            _trialState = 0;
            _stateT = t;
            _settleOkT = 0;
            ResetWalk();
            return true;
        }

        // One drive router for centering and pulses: change-deduplicated
        // (the native path's SetConstantPulse is a device call).
        private static bool IsOptionalPhase(Phase p)
            => p == Phase.NativeSpring   || p == Phase.NativeFriction
            || p == Phase.EngineSpring   || p == Phase.EngineFriction;

        private void SkipPhase(double t, string why)
        {
            Status($"{_phase} SKIPPED ({why}); its gain will read as suspect");
            DriveProbe(0);
            SetCommand(0);
            // Same as a completed phase: the wheel may be travelling or far
            // from centre, and the next phase must not inherit that.
            _trialState = 6;
            _stateT = t;
        }

        private bool PlateauOverTravel(double pos, double t)
        {
            if (Math.Abs(pos) <= PlateauTravelCut) return false;
            DriveProbe(0);
            SetCommand(0);
            // Shrink the STEP, not the base. The base is pinned from below
            // by the friction the wheel has to overcome to move at all, so
            // pulling it down to save travel just stalls the low level and the
            // two requirements fight each other forever. The step is what
            // turns into speed at the high level, and it is free to shrink
            // until the slope gets too small to read, which has its own
            // separate remedy.
            if (_plateauStepScale > PlateauMinScale * 1.01)
                _plateauStepScale = Math.Max(PlateauMinScale, _plateauStepScale * 0.7);
            else
                _plateauScale = Math.Max(PlateauMinScale, _plateauScale * 0.8);
            _attempts++;
            Status($"plateau reached {pos:F2}; trial discarded, "
                 + $"base x{_plateauScale:F2}, step x{_plateauStepScale:F2}");
            if (_attempts >= AttemptsFor(_phase))
            {
                // A wheel free enough that no force level gives it a terminal
                // speed simply cannot be baselined. That is a fact about the
                // wheel, not a failure of the run.
                if (IsOptionalPhase(_phase))
                    SkipPhase(t, "the wheel runs out of travel before it settles under load");
                else
                    Abort($"{_phase}: the wheel runs out of travel before it settles under load");
                return true;
            }
            _trialState = 0;
            _stateT = t;
            _settleOkT = 0;
            ResetWalk();
            return true;
        }

        private double PlateauLevel(int which)
            => which == 1 ? PlateauBase * _plateauScale
                          : PlateauBase * _plateauScale + PlateauStep * _plateauStepScale;

        private void ResetPlateauWindow()
        {
            _plateauSumA = _plateauSumB = 0;
            _plateauSqA = _plateauSqB = 0;
            _plateauNA = _plateauNB = 0;
        }

        private void AccumulatePlateau(double vel, double heldFor)
        {
            double intoWindow = heldFor - (_plateauHold - PlateauWindowSec);
            if (intoWindow < 0) return;
            double v = Math.Abs(vel);
            if (intoWindow < PlateauWindowSec / 2)
            { _plateauSumA += v; _plateauSqA += v * v; _plateauNA++; }
            else
            { _plateauSumB += v; _plateauSqB += v * v; _plateauNB++; }
        }

        // Mean speed over the window, and a check that it had actually
        // levelled off: a plateau still climbing is not a terminal speed, and
        // averaging it understates the speed by an amount that differs
        // between the two force levels, which biases c.
        private double ClosePlateauWindow()
        {
            if (_plateauNA == 0 || _plateauNB == 0) return 0;
            double a = _plateauSumA / _plateauNA, b = _plateauSumB / _plateauNB;
            double mean = (a + b) / 2;
            if (mean <= 1e-6) return mean;

            // "Still rising" has to mean rising by more than this measurement
            // can resolve. A flat percentage cannot: derived velocity is
            // quantized, and at plateau speeds a few percent IS the noise, so
            // a fixed 4 % threshold rejected good trials at random. On the rig
            // that cost ten rejections for two passes and then the phase
            // (2026-09-02 17:49), while a looser fixed threshold reintroduces
            // a real settling bias. Compare the step against the scatter the
            // window itself shows, and require it to clear BOTH that and the
            // percentage before calling it a trend.
            double varA = Math.Max(0, _plateauSqA / _plateauNA - a * a);
            double varB = Math.Max(0, _plateauSqB / _plateauNB - b * b);
            double stderr = Math.Sqrt(varA / _plateauNA + varB / _plateauNB);
            double step = Math.Abs(b - a);
            _plateauFlatDetail = $"step {step:F4} of mean {mean:F3} "
                               + $"({step / mean:P1}), sigma {stderr:F4}";
            if (step / mean > PlateauFlatFrac
                && step > PlateauFlatSigma * stderr
                && step > PlateauFlatAbs)
                _plateauUnsettled = true;
            return mean;
        }

        private int _sameDriveCount;

        private void DriveProbe(double cmd)
        {
            // Change-dedup, but re-send periodically: a swallowed device
            // error must not leave a stale effect state undetected for the
            // rest of a phase (audit F6).
            if (cmd == _lastDrive && ++_sameDriveCount < 400) return;
            _sameDriveCount = 0;
            _lastDrive = cmd;
            if (IsNativePhase(_phase)) _driveNative?.Invoke(cmd * _nativeDir);
            else SetCommand(cmd * _forceEq * _engineDir);
        }

        private static bool IsNativePhase(Phase p)
            => p == Phase.DirProbe
            || p == Phase.NativeForce || p == Phase.NativeDamper
            || p == Phase.NativeSpring || p == Phase.NativeFriction;

        private void FinishTrial(double t)
        {
            _attempts++;
            _rejectReason = null;
            _fitDetail = null;
            bool valid = false;
            double a = double.NaN, b = double.NaN;
            switch (_phase)
            {
                case Phase.DirProbe:
                {
                    double rawP = PeakSpeed();
                    valid = rawP > 0.15;
                    if (valid)
                    {
                        double resp = _pulseDisp * _pulseDir;
                        _nativeDir = resp >= 0 ? 1 : -1;
                        NativeDirFlipped = _nativeDir < 0;
                        Status($"native force frame: the pulse moved the wheel "
                             + $"{_pulseDisp:+0.000;-0.000} while pushing {(_pulseDir > 0 ? "+" : "-")} -> "
                             + (NativeDirFlipped ? "INVERTED; compensating" : "normal"));
                        a = rawP;
                    }
                    break;
                }
                case Phase.EngineDirProbe:
                {
                    double rawP = PeakSpeed();
                    valid = rawP > 0.15;
                    if (valid)
                    {
                        double resp = _pulseDisp * _pulseDir;
                        _engineDirSet = true;
                        NoteRenderSign(resp);
                        Status($"stream force frame: the pulse moved the wheel "
                             + $"{_pulseDisp:+0.000;-0.000} while pushing {(_pulseDir > 0 ? "+" : "-")}");
                        if (resp < 0)
                        {
                            _engineDir = -1;
                            EngineDirFlipped = true;
                            Status("stream commands drive the wheel opposite to the raw frame; compensating (this is the direction the renderer expects)");
                        }
                        a = rawP;
                    }
                    break;
                }
                case Phase.NativeForce:
                case Phase.EngineForce:
                {
                    double raw = PeakSpeed();
                    valid = raw > 0.25;
                    // Normalize by the amplitude actually used, and add back
                    // the friction that never became speed.
                    //
                    // The response is NOT proportional to the pulse. Coulomb
                    // friction subtracts a constant torque before anything
                    // accelerates, so peak-over-amplitude is affine with a
                    // non-zero intercept: (K - mu/A) * T/J. Dividing by A does
                    // not remove it, and the intercept is larger on the weaker
                    // side, so the ratio is always pushed AWAY from 1 and the
                    // engine always looks weaker than it really is. That error
                    // lands 1:1 on the damper gain by way of _forceEq, and
                    // vanishes only where the two sides already match, which
                    // is the one place no correction is needed.
                    //
                    // (peak/T + mu)/A restores proportionality exactly: the
                    // wheel's inertia cancels in the ratio. Both force coasts
                    // run with no effect loaded, so each trial fits its own
                    // friction term from the very coast being measured.
                    if (valid && FitViscousRate(out double cRate, out double muF, false, double.NaN))
                    {
                        _muSamples.Add(muF);
                        _cRateSamples.Add(cRate);
                        double denom = raw + muF * PulseSec;
                        if (denom > 1e-6) _jSamples.Add(_pulseAmp * PulseSec / denom);
                        a = (raw / PulseSec + muF) / _pulseAmp;
                    }
                    else a = raw / _pulseAmp;
                    // Response direction RELATIVE to the command, for the
                    // direction-frame check (audit F5): which way the push
                    // actually moved the wheel.
                    b = _pulseDisp * _pulseDir;
                    // Engine frame: decide on the FIRST valid engine trial so
                    // engine-phase centering never drives the wrong way.
                    if (_phase == Phase.EngineForce && valid && !_engineDirSet)
                    {
                        _engineDirSet = true;
                        NoteRenderSign(b);
                        if (b < 0)
                        {
                            _engineDir = -1;
                            EngineDirFlipped = true;
                            Status("stream commands drive the wheel opposite to the raw frame; compensating (this is the direction the renderer expects)");
                        }
                    }
                    break;
                }
                case Phase.NativeFriction:
                case Phase.EngineFriction:
                    // Friction in the PARAMETER domain, not the mean-
                    // deceleration domain. The coast obeys
                    // dv/dt = -(c*v + mu), and the integral fit separates the
                    // two: mu is the constant friction torque per unit
                    // inertia, exactly the quantity wanted, with any velocity-
                    // proportional part of the effect landing in c where it
                    // belongs rather than being charged to friction.
                    //
                    // This replaces a mean deceleration differenced against a
                    // baseline coast measured on the OTHER side of the
                    // experiment (stream running versus stream stopped), over
                    // a speed band that differed from trial to trial. It was
                    // the only metric with a cross-condition subtraction and
                    // the only one that scattered 59 %.
                    // A BASELINE trial is an effect-free coast: long, and the
                    // one case where solving for drag and friction together is
                    // well conditioned. Solve both, and keep the drag for the
                    // loaded trials to hold fixed.
                    //
                    // A LOADED trial is short and mostly constant deceleration,
                    // so solving for both returns a negative friction with a
                    // compensating drag. Hold the drag at what this phase's own
                    // baseline just measured. Borrowing it from the force phase
                    // was what drove the baselines negative (-0.20 to -0.74 on
                    // the rig): that figure came from a different phase, and an
                    // over-large drag over-explains a free coast.
                    if (_baselineTrial)
                    {
                        valid = FitViscousRate(out double bc, out b, false, double.NaN) && b > 0;
                        if (valid) _baseDragSamples.Add(bc);
                    }
                    else
                    {
                        double held = _baseDragSamples.Count > 0
                            ? Median(_baseDragSamples)
                            : (_phase == Phase.NativeFriction ? _cRateNative : _cRateEngine);
                        valid = FitViscousRate(out _, out b, false, double.NaN, held) && b > 0;
                    }
                    if (!valid && _rejectReason == null)
                        _rejectReason = $"friction came out non-positive ({b:F2})";
                    if (valid) _fitDetail = $"mu {b:F2}, {_lastFitSamples} samples over {_lastFitSpan:F3} s";
                    break;
                case Phase.NativeDamper:
                case Phase.EngineDamper:
                {
                    // c = (F2 - F1) / (v2 - v1): Coulomb friction cancels
                    // between the two levels, so no baseline is needed and
                    // nothing has to be fitted to a curve.
                    double dF = PlateauStep * _plateauStepScale;
                    double dV = _plateauV2 - _plateauV1;
                    if (_plateauV1 < PlateauMinSpeed || _plateauV2 < PlateauMinSpeed)
                    {
                        // The BASE is below what it takes to move the wheel.
                        _rejectReason = $"the wheel barely moved under load "
                                      + $"({_plateauV1:F2} then {_plateauV2:F2} range/s)";
                        _plateauScale = Math.Min(PlateauMaxScale, _plateauScale * 1.3);
                    }
                    else if (dV < PlateauMinGap)
                    {
                        // Both levels move it, but not far enough apart to
                        // measure a slope: that is the STEP, not the base.
                        _rejectReason = $"speed did not rise with force "
                                      + $"({_plateauV1:F2} then {_plateauV2:F2} range/s)";
                        _plateauStepScale = Math.Min(PlateauMaxScale, _plateauStepScale * 1.4);
                    }
                    else if (_plateauUnsettled)
                    {
                        _rejectReason = "the wheel was still speeding up at the end of the hold"
                                      + (_plateauFlatDetail != null ? $" [{_plateauFlatDetail}]" : "");
                        _plateauHold = Math.Min(1.4, _plateauHold * 1.5);
                    }
                    else
                    {
                        // F = mu + c*v at both levels: the slope is the
                        // velocity-proportional drag, the intercept is the
                        // constant (Coulomb) torque. One trial, both numbers,
                        // no baseline coast and no fitted curve.
                        a = dF / dV;                                   // c
                        b = PlateauLevel(1) - a * _plateauV1;           // mu
                        valid = true;
                    }
                    break;
                }

                case Phase.NativeSpring:
                case Phase.EngineSpring:
                    valid = FitOscillationFreq(out a);
                    // Step-0 ringdown reconnaissance (measurement audit,
                    // 2026-09-02): the spring trials already produce a kicked
                    // decay, so log its peak sequence and the affine peak-map
                    // fit. Two numbers gate the whole ringdown decision: how
                    // many usable same-side peak pairs a real trial yields,
                    // and how tight the map fits them. Diagnostic only.
                    if (valid) LogRingdownPeaks();
                    break;
            }

            if (IsPlateauPhase(_phase)) _plateauDir = -_plateauDir;
            if (valid && IsInterleavedPhase(_phase))
            {
                // Baseline trials do not count toward the phase's quota: the
                // phase needs its full set of LOADED trials, each with a
                // neighbouring baseline.
                if (_baselineTrial) { _baseSamples.Add(b); _baselineTrial = false; }
                else                { _metricB.Add(b); _validTrials++; _baselineTrial = true; }
                // A baseline is required before the loaded trials mean
                // anything, so never leave the phase without one.
                Status($"{_phase}: {(_baselineTrial ? "loaded" : "baseline")} trial ok "
                     + $"({_validTrials}/{FrictionTrials} loaded, {_baseSamples.Count} baseline)"
                     + (_fitDetail != null ? $" [{_fitDetail}]" : ""));
            }
            else if (valid)
            {
                _metricA.Add(a);
                if (!double.IsNaN(b)) _metricB.Add(b);
                _validTrials++;
                Status($"{_phase}: trial {_validTrials}/{TrialsPerPhase} ok");
            }
            else
            {
                if (IsPlateauPhase(_phase))
                {
                    Status($"{_phase}: trial unusable ({_rejectReason ?? "no usable response"}), "
                         + $"retrying (base x{_plateauScale:F2}, step x{_plateauStepScale:F2}, "
                         + $"hold {_plateauHold:F2} s)");
                }
                else
                {
                    // Weak response: push a little harder next time, bounded.
                    _pulseAmp = Math.Min(MaxAmpFor(_phase), _pulseAmp * 1.3);
                    Status($"{_phase}: trial unusable ({_rejectReason ?? "no usable response"}), "
                         + $"retrying (pulse {_pulseAmp:F2})");
                }
            }

            int needTrials = (_phase == Phase.DirProbe || _phase == Phase.EngineDirProbe) ? 1
                           : IsInterleavedPhase(_phase) ? FrictionTrials
                           : TrialsPerPhase;
            if (_validTrials >= needTrials)
            {
                LandPhaseResults();
                // Always hand the next phase a stopped, roughly centred
                // wheel. A plateau ends at speed by design and a friction
                // coast ends a long way out, and the phase that follows may
                // be one that cannot centre at all: the engine direction
                // probe has to read the wheel's response to a small pulse and
                // has no frame to steer by yet. It sat asking for hands
                // through its whole timeout (sim, 2026-09-02) because the
                // friction phase before it left the wheel past the tolerance.
                _trialState = 6;
                _stateT = t;
                DriveProbe(0);
                return;
            }
            else if (_attempts >= AttemptsFor(_phase))
            {
                // Spring/friction are OPTIONAL: a failed optional phase must
                // not throw away the completed force/damper measurements
                // (audit AT2-02); its gain just comes back NaN/suspect.
                if (IsOptionalPhase(_phase)) SkipPhase(t, "not enough usable trials");
                else Abort($"{_phase}: not enough usable trials (wheel touched, or the effect is not acting)");
            }
            else
            {
                _trialState = 0;
                _stateT = t;
                _settleOkT = 0;
                ResetWalk();
            }
        }

        private void LandPhaseResults()
        {
            _resultA[_phase] = Median(_metricA);
            if (_metricB.Count > 0) _resultB[_phase] = Median(_metricB);
            if (_baseSamples.Count > 0)
            {
                if (_phase == Phase.NativeFriction) _frictionBaseNative = Median(_baseSamples);
                else if (_phase == Phase.EngineFriction) _frictionBaseEngine = Median(_baseSamples);
            }
            if (_muSamples.Count > 0)
            {
                bool nat = _phase == Phase.NativeForce;
                if (nat) _muNative = Median(_muSamples); else _muEngine = Median(_muSamples);
                if (_cRateSamples.Count > 0)
                {
                    if (nat) _cRateNative = Median(_cRateSamples);
                    else     _cRateEngine = Median(_cRateSamples);
                }
                if (_jSamples.Count > 0)
                {
                    if (nat) _jOverKNative = Median(_jSamples);
                    else     _jOverKEngine = Median(_jSamples);
                }
            }
        }

        private void NextPhase(double t)
        {
            // Order: all native first (one stream suspend), then all engine.
            Phase next;
            switch (_phase)
            {
                case Phase.Idle:           next = Phase.DirProbe; break;
                case Phase.DirProbe:       next = Phase.NativeForce; break;
                case Phase.NativeForce:    next = Phase.NativeDamper; break;
                case Phase.NativeDamper:   next = Phase.NativeSpring; break;
                case Phase.NativeSpring:   next = Phase.NativeFriction; break;
                case Phase.NativeFriction: next = Phase.EngineDirProbe; break;
                case Phase.EngineDirProbe: next = Phase.EngineForce; break;
                case Phase.EngineForce:
                    ComputeForceEq();
                    next = Phase.EngineDamper; break;
                case Phase.EngineDamper:   next = Phase.EngineSpring; break;
                case Phase.EngineSpring:   next = Phase.EngineFriction; break;
                case Phase.EngineFriction: Finish(); return;
                default: return;
            }

            _phase = next;
            _phaseStartT = t;
            _trialState = 0;
            _stateT = t;
            _settleOkT = 0;
            ResetWalk();
            _validTrials = 0;
            _attempts = 0;
            _pulseAmp = StartAmpFor(next);
            _metricA.Clear();
            _metricB.Clear();
            _muSamples.Clear();
            _cRateSamples.Clear();
            _jSamples.Clear();
            _baseSamples.Clear();
            _baseDragSamples.Clear();
            _baselineTrial = true;
            _plateauScale = 0.6;
            _plateauStepScale = 1.0;
            _plateauHold = PlateauHoldSec;
            _plateauDir = 1;
            _lastDrive = double.NaN;
            SetCommand(0);

            var cfg = new PhaseConfig { Phase = next, Native = IsNativePhase(next) };
            switch (next)
            {
                case Phase.NativeDamper:
                case Phase.EngineDamper:   cfg.EffectKind = "DAMPER";   cfg.EffectPct = DamperProbePct; break;
                case Phase.NativeSpring:
                case Phase.EngineSpring:   cfg.EffectKind = "SPRING";   cfg.EffectPct = SpringProbePct; break;
                case Phase.NativeFriction:
                case Phase.EngineFriction: cfg.EffectKind = "FRICTION"; cfg.EffectPct = FrictionProbePct; break;
            }
            if (cfg.EffectPct > 0) _pctUsed[next] = cfg.EffectPct;
            try { _enterPhase?.Invoke(cfg); } catch { }
            Status($"phase: {next}");
        }

        private void ComputeForceEq()
        {
            double n = _resultA.TryGetValue(Phase.NativeForce, out var pn) ? pn : double.NaN;
            double e = _resultA.TryGetValue(Phase.EngineForce, out var pe) ? pe : double.NaN;
            if (n > 0 && e > 0)
            {
                MeasuredForceRatio = n / e;
                _forceEq = Math.Max(0.2, Math.Min(5.0, MeasuredForceRatio));
                if (MeasuredForceRatio != _forceEq)
                {
                    // Clamped: the engine drive is NOT equalized to the native
                    // one, so every later engine measurement is knowingly off
                    // by the amount that was clamped away. Sanitize flags an
                    // out-of-range gain; this rail deserves the same.
                    ResultsSuspect = true;
                    Status($"force ratio {MeasuredForceRatio:F2} is outside the equalizer's range; "
                         + "the engine phases are not force-matched and the gains are unreliable");
                }
                Status($"force scale measured: engine pulses deliver {1 / MeasuredForceRatio:P0} of native; equalizing");
            }
        }

        private void Finish()
        {
            // Damper and friction come from the SAME measurement: a pair of
            // constant-force plateaus gives F = mu + c*v, whose slope is the
            // velocity-proportional drag and whose intercept is the constant
            // friction torque. One trial, both numbers, no fitted curve and no
            // baseline coast measured in a different configuration of the
            // wheel.
            //
            // The wheel's OWN drag and friction are then removed. They do not
            // cancel by being present on both sides: an additive term common
            // to a ratio's numerator and denominator pulls the ratio toward 1,
            // which understates every correction and understates it most
            // exactly when the correction is largest.
            //
            // Units. Native plateaus are commanded through DirectInput and
            // engine plateaus through the stream, but the engine drive carries
            // _forceEq, which is the ratio of the two chains' torque per unit
            // command. That leaves BOTH sides' measured coefficients in the
            // native chain's units, which is why they may be compared
            // directly. The baselines come from coast fits in acceleration
            // units, so each is multiplied by that side's J/K and the engine's
            // is divided by _forceEq to land in the same place.
            double wN = WheelTerm(_cRateNative, _jOverKNative, 1);
            double wE = WheelTerm(_cRateEngine, _jOverKEngine, _forceEq);
            // The wheel's own drag carries no probe coefficient, so it
            // comes off first; what remains is the effect, normalized by the
            // strength that produced it.
            double cN = PerUnitOf(Get(_resultA, Phase.NativeDamper) - wN, Phase.NativeDamper);
            double cE = PerUnitOf(Get(_resultA, Phase.EngineDamper) - wE, Phase.EngineDamper);
            // Friction: the effect's own contribution is what its coast
            // shows minus what the unloaded wheel's coast shows, both fitted
            // the same way on the SAME side. Inertia cancels in the ratio.
            // Prefer the baseline measured inside the friction phase itself,
            // trials apart rather than phases apart. The force phase's figure
            // is the fallback for a phase that never got a baseline trial in.
            double baseN = double.IsNaN(_frictionBaseNative) ? _muNative : _frictionBaseNative;
            double baseE = double.IsNaN(_frictionBaseEngine) ? _muEngine : _frictionBaseEngine;
            // The baseline is the unloaded wheel, so it carries no probe
            // coefficient: subtract it first, then normalize what is left,
            // which is the effect's own contribution.
            double mN = PerUnitOf(Get(_resultB, Phase.NativeFriction) - baseN, Phase.NativeFriction);
            double mE = PerUnitOf(Get(_resultB, Phase.EngineFriction) - baseE, Phase.EngineFriction);

            if (double.IsNaN(wN) || double.IsNaN(wE))
            {
                cN = PerUnitOf(Get(_resultA, Phase.NativeDamper), Phase.NativeDamper);
                cE = PerUnitOf(Get(_resultA, Phase.EngineDamper), Phase.EngineDamper);
                if (!double.IsNaN(cN) && !double.IsNaN(cE))
                {
                    ResultsSuspect = true;
                    Status("the unloaded wheel could not be characterised: its own drag stays in "
                         + "both sides, so the damper and friction gains understate the correction");
                }
            }

            if (cN > 1e-3 && cE > 1e-3)
                DamperGain = _damperG0 * cN / cE;
            if (mN > 1e-4 && mE > 1e-4)
                FrictionGain = _frictionG0 * mN / mE;

            double fSN = Get(_resultA, Phase.NativeSpring), fSE = Get(_resultA, Phase.EngineSpring);
            if (fSE > 0.05 && fSN > 0.05)
            {
                // Stiffness goes as frequency squared, so the per-unit
                // stiffness is f^2 divided by the coefficient used.
                double kN = PerUnitOf(fSN * fSN, Phase.NativeSpring);
                double kE = PerUnitOf(fSE * fSE, Phase.EngineSpring);
                if (kE > 0) SpringGain = _springG0 * kN / kE;
            }

            MeasuredDragNative = cN;
            MeasuredDragEngine = cE;

            // Raw parts, so a drift across repeat runs names its own source
            // instead of leaving thermal-vs-estimator to argument. The
            // friction gain is a ratio of small DIFFERENCES, and differencing
            // amplifies: a 15 % shift in the wheel's own (unfelt) internal
            // friction moves the computed gain by far more. If mu.wheel is
            // flat across runs, thermal staleness is dead and the estimator
            // is the suspect; if it trends, the subtraction needs to ride the
            // same temperature as what it is subtracted from.
            Status($"parts: mu.fric N {Get(_resultB, Phase.NativeFriction):F2} E {Get(_resultB, Phase.EngineFriction):F2}; "
                 + $"mu.wheel N {_muNative:F2} E {_muEngine:F2}; "
                 + $"cRate N {_cRateNative:F2} E {_cRateEngine:F2}; "
                 + $"J/K N {_jOverKNative:F4} E {_jOverKEngine:F4}; forceEq {_forceEq:F3}");

            DamperGain   = Sanitize(DamperGain);
            SpringGain   = Sanitize(SpringGain);
            FrictionGain = Sanitize(FrictionGain);
            if (RenderSignMeasured && !RenderSignCorrect)
            {
                // Positive cur drives the measured velocity POSITIVE, so the
                // renderer's +k x velocity is positive feedback: its damper,
                // friction and inertia push motion along instead of
                // resisting it. Flip direction fixes those three; the spring
                // is not covered by velTermSign, so this needs a look before
                // the gains mean anything.
                ResultsSuspect = true;
                Status("the rendered effects run as POSITIVE feedback on this rig (an anti-damper): turn on Flip direction and re-run before trusting these gains");
            }

            SetCommand(0);
            _phase = Phase.Done;
            var cfg = new PhaseConfig { Phase = Phase.Done, Native = false };
            try { _enterPhase?.Invoke(cfg); } catch { }
        }

        // The engine probe's signed response IS the render loop's sign: a
        // negative response to a positive command is the negative feedback
        // the renderer's convention needs.
        private void NoteRenderSign(double resp)
        {
            if (resp == 0) return;
            RenderSignMeasured = true;
            RenderSignCorrect  = resp < 0;
        }

        // A coast-fit rate (per second) expressed in the plateaus' force
        // units, for the side whose drive carries the given equalizer.
        private static double WheelTerm(double rate, double jOverK, double forceEq)
        {
            if (double.IsNaN(rate) || double.IsNaN(jOverK) || forceEq <= 0) return double.NaN;
            double v = rate * jOverK / forceEq;
            return v > 0 ? v : 0;
        }

        private double Sanitize(double g)
        {
            if (double.IsNaN(g)) { ResultsSuspect = true; return g; }
            if (g < 0.02 || g > 10) { ResultsSuspect = true; return Math.Max(0.02, Math.Min(10, g)); }
            return g;
        }

        private void Abort(string why)
        {
            AbortReason = why;
            SetCommand(0);
            try { _driveNative?.Invoke(0); } catch { }
            _phase = Phase.Aborted;
            var cfg = new PhaseConfig { Phase = Phase.Aborted, Native = false };
            try { _enterPhase?.Invoke(cfg); } catch { }
            Status("aborted: " + why);
        }

        // ---------------- metric fits ----------------

        private double SignedVelAtPeak()
        {
            int iPeak = -1; double peak = 0;
            for (int i = 0; i < _vv.Count; i++)
            {
                double av = Math.Abs(_vv[i]);
                if (av > peak) { peak = av; iPeak = i; }
            }
            return iPeak >= 0 ? _vv[iPeak] : 0;
        }

        private double PeakSpeed()
        {
            double peak = 0;
            for (int i = 0; i < _vv.Count; i++)
            {
                double av = Math.Abs(_vv[i]);
                if (av > peak) peak = av;
            }
            return peak;
        }

        // Viscous damping coefficient from the coast-down, with Coulomb
        // friction separated out rather than assumed away.
        //
        // A real wheel's coast is NOT an exponential. Viscous damping and
        // Coulomb friction act together:
        //
        //     dv/dt = -(c*v + mu)
        //
        // so speed decays exponentially only while c*v dominates mu, and the
        // low-speed tail is governed by the constant mu instead. Fitting
        // ln|v| from each trial's own peak down to 12 % OF THAT PEAK meant
        // every trial measured over a different band of wheel speed, and the
        // rate genuinely differs between bands. That is a direct source of
        // trial-to-trial scatter in a gain that is a DIFFERENCE of rates
        // (rig x3: damper spread 115 %, then 67 %, while the spring's
        // baseline-free frequency metric held 1 %).
        //
        // INTEGRATE, do not differentiate. Integrating the model over any
        // interval from the start of the coast gives
        //
        //     v(t) - v(0) = -c * (area under v) - mu * (elapsed time)
        //
        // which is linear in c and mu with no derivative anywhere. Every
        // sample feeds the areas, so a coarsely sampled coast still fits, and
        // areas average noise instead of amplifying it. c is the number the
        // damper gain wants, with friction removed BY THE FIT rather than
        // left to cancel in a subtraction.
        //
        // requireDamping: the damper phases must SEE damping, and a fit that
        // explains nothing means the effect was not acting. The baseline is
        // the opposite case: a bare wheel may legitimately have almost no
        // viscous friction, so its c is allowed to be ~0 and the quality gate
        // does not apply (a fit is meaningless about a coefficient whose true
        // value is zero, and the friction the wheel does have lands in mu,
        // which is exactly where the subtraction wants it).
        private bool FitViscousRate(out double rate, out double friction,
                                    bool requireDamping, double muFixed,
                                    double cFixed = double.NaN)
        {
            rate = double.NaN;
            friction = double.NaN;
            int iPeak = -1; double peak = 0;
            for (int i = 0; i < _vv.Count; i++)
            {
                double av = Math.Abs(_vv[i]);
                if (av > peak) { peak = av; iPeak = i; }
            }
            if (peak < 0.3 || iPeak < 0)
            {
                _rejectReason = $"no usable peak, {peak:F2} of 0.30";
                return false;
            }

            var ts = new List<double>(); var vs = new List<double>();
            double tPeak = _vt[iPeak];
            for (int i = iPeak; i < _vv.Count; i++)
            {
                double av = Math.Abs(_vv[i]);
                if (av < FitSpeedFloor) break;
                if (_vt[i] - tPeak > 1.5) break;
                ts.Add(_vt[i]); vs.Add(av);
            }
            int n = ts.Count;
            _lastFitSamples = n;
            _lastFitSpan = n > 1 ? ts[n - 1] - ts[0] : 0;
            if (n < FitMinSamples)
            {
                _rejectReason = $"coast gave only {n} samples (need {FitMinSamples})";
                return false;
            }
            double range = vs[0] - vs[n - 1];
            if (double.IsNaN(muFixed) && range < FitMinSpeedRange)
            {
                // Only needed when friction is being solved for too: with it
                // held fixed, one unknown fits fine over a short coast.
                _rejectReason = $"coast only spanned {range:F2} range/s "
                              + $"(need {FitMinSpeedRange:F2}) to separate damping from friction";
                return false;
            }

            double sAA = 0, sAT = 0, sTT = 0, sAd = 0, sTd = 0, area = 0;
            var dvs = new List<double>(); var areas = new List<double>(); var els = new List<double>();
            for (int k = 1; k < n; k++)
            {
                area += (vs[k] + vs[k - 1]) * 0.5 * (ts[k] - ts[k - 1]);
                double T = ts[k] - ts[0];
                double dv = vs[k] - vs[0];
                areas.Add(area); els.Add(T); dvs.Add(dv);
                sAA += area * area; sAT += area * T; sTT += T * T;
                sAd += area * dv;   sTd += T * dv;
            }
            double c, mu;
            if (!double.IsNaN(cFixed))
            {
                // ONE unknown, the friction. Mirror of the muFixed case: a
                // friction-loaded coast is short and mostly constant
                // deceleration, so solving for both terms is ill-conditioned
                // and returns a negative friction with a compensating drag
                // (rig, 2026-09-02: every native friction trial rejected).
                // The drag belongs to the WHEEL and is measured where the
                // coast is long enough to see it, so hold it and solve for
                // the friction alone.
                if (sTT < 1e-12) { _rejectReason = "coast too short to fit"; return false; }
                c  = cFixed;
                mu = -(sTd + c * sAT) / sTT;
            }
            else if (!double.IsNaN(muFixed))
            {
                // ONE unknown. A damped coast is over in about a tenth of a
                // second, and over a window that short "area under v" and
                // "elapsed time" are nearly proportional, so solving for both
                // c and mu is ill-conditioned: the rig returned R2 of 0.06,
                // -0.47, and once c = -2.04 with friction 11.66 (2026-09-02
                // 12:08). Friction is a property of the WHEEL, not of the
                // effect under test, so it is measured where the coast is
                // long enough to see it and held fixed here.
                if (sAA < 1e-12)
                {
                    _rejectReason = "coast too small to fit";
                    return false;
                }
                mu = muFixed;
                c  = -(sAd + mu * sAT) / sAA;
            }
            else
            {
                double det = sAA * sTT - sAT * sAT;
                if (Math.Abs(det) < 1e-12)
                {
                    _rejectReason = "coast shape gives no independent fit";
                    return false;
                }
                c  = -(sAd * sTT - sTd * sAT) / det;
                mu = -(sTd * sAA - sAd * sAT) / det;
            }
            friction = mu;

            if (!requireDamping) { rate = c > 0 ? c : 0; return true; }
            if (c <= 0.05)
            {
                _rejectReason = $"no measurable viscous damping (c {c:F2}, friction {mu:F2})";
                return false;
            }

            double mean = 0;
            for (int k = 0; k < dvs.Count; k++) mean += dvs[k];
            mean /= dvs.Count;
            double ssRes = 0, ssTot = 0;
            for (int k = 0; k < dvs.Count; k++)
            {
                double pred = -c * areas[k] - mu * els[k];
                ssRes += (dvs[k] - pred) * (dvs[k] - pred);
                ssTot += (dvs[k] - mean) * (dvs[k] - mean);
            }
            double r2 = ssTot > 1e-12 ? 1.0 - ssRes / ssTot : 0;
            if (r2 < DecayFitMinR2)
            {
                _rejectReason = $"coast fit poor (R2 {r2:F2} of {DecayFitMinR2:F2}, "
                              + $"{n} samples over {ts[n - 1] - ts[0]:F2} s)";
                return false;
            }
            rate = c;
            return true;
        }

        private const double DecayFitMinR2 = 0.90;
        private const double FitSpeedFloor = 0.06;   // below this, stiction
        private const int    FitMinSamples = 8;
        // c and mu are not separable over a narrow band: the wheel has to
        // visibly slow down for the two terms to look different.
        private const double FitMinSpeedRange = 0.15;

        // Position extrema at the velocity sign changes, magnitudes of the
        // decaying swing. Same-side pairs (n, n+2) feed the affine map
        // A' = q*A - d whose slope is viscous decay and whose intercept is
        // Coulomb loss per cycle. Never log-decrement directly: with Coulomb
        // present the apparent decrement is amplitude-dependent.
        private void LogRingdownPeaks()
        {
            var amp = new List<double>();
            int lastSign = 0;
            for (int i = 0; i < _vv.Count; i++)
            {
                int sgn = _vv[i] > 1e-6 ? 1 : _vv[i] < -1e-6 ? -1 : 0;
                if (sgn == 0) continue;
                if (lastSign != 0 && sgn != lastSign && i < _vp.Count)
                    amp.Add(Math.Abs(_vp[i]));
                lastSign = sgn;
            }
            if (amp.Count < 3)
            {
                Status($"ringdown: only {amp.Count} peaks resolved");
                return;
            }
            // Same-side full-period pairs: (0,2), (1,3), (2,4)...
            double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
            for (int i = 0; i + 2 < amp.Count; i++)
            {
                double x = amp[i], y = amp[i + 2];
                sx += x; sy += y; sxx += x * x; sxy += x * y; n++;
            }
            string seq = string.Join(", ", amp.ConvertAll(v => v.ToString("F3")));
            if (n < 2)
            {
                Status($"ringdown peaks: {seq} (too few pairs to fit)");
                return;
            }
            double det = n * sxx - sx * sx;
            if (Math.Abs(det) < 1e-12) { Status($"ringdown peaks: {seq} (degenerate)"); return; }
            double q = (n * sxy - sx * sy) / det;
            double dInt = -(sy - q * sx) / n;
            double ssRes = 0;
            for (int i = 0; i + 2 < amp.Count; i++)
            {
                double r = amp[i + 2] - (q * amp[i] - dInt);
                ssRes += r * r;
            }
            double rms = Math.Sqrt(ssRes / n);
            Status($"ringdown peaks: {seq} | pairs {n}, q {q:F3}, d {dInt:F4}, "
                 + $"residual rms {rms:F4} ({(amp[0] > 1e-9 ? rms / amp[0] : 0):P1} of first peak)");
        }

        private bool FitOscillationFreq(out double freq)
        {
            freq = double.NaN;
            double peak = PeakSpeed();
            if (peak < 0.3) return false;
            double gate = peak * 0.12;
            var crossings = new List<double>();
            double lastSwing = 0;
            for (int i = 1; i < _vv.Count; i++)
            {
                lastSwing = Math.Max(lastSwing, Math.Abs(_vv[i]));
                bool crossed = (_vv[i - 1] > 0 && _vv[i] <= 0) || (_vv[i - 1] < 0 && _vv[i] >= 0);
                if (crossed)
                {
                    if (lastSwing >= gate) crossings.Add(_vt[i]);
                    lastSwing = 0;
                }
            }
            if (crossings.Count < 3) return false;
            double sum = 0; int n = 0;
            for (int i = 1; i < crossings.Count; i++) { sum += crossings[i] - crossings[i - 1]; n++; }
            double half = sum / n;
            if (half <= 1e-3) return false;
            freq = 1.0 / (2 * half);
            return freq > 0.2 && freq < 60;
        }

        private static double LinFitSlope(List<double> xs, List<double> ys)
        {
            int n = xs.Count;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                sx += xs[i]; sy += ys[i];
                sxx += xs[i] * xs[i]; sxy += xs[i] * ys[i];
            }
            double d = n * sxx - sx * sx;
            if (Math.Abs(d) < 1e-12) return 0;
            return (n * sxy - sx * sy) / d;
        }

        private static double Get(Dictionary<Phase, double> d, Phase p)
            => d.TryGetValue(p, out var v) ? v : double.NaN;

        private double PerUnitOf(double v, Phase p)
        {
            if (double.IsNaN(v)) return double.NaN;
            double pct = Get(_pctUsed, p);
            return double.IsNaN(pct) || pct <= 0 ? v : v / (pct / 100.0);
        }



        // NaN for an empty set rather than an index fault. The friction
        // phases populate only the intercept list, so the slope list is
        // legitimately empty for them, and a phase that skips populates
        // nothing at all: "no measurement" has to be representable.
        private static double Median(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            var c = new List<double>(v);
            c.Sort();
            return c[c.Count / 2];
        }

        private void Status(string s)
        {
            try { _status?.Invoke("AUTO-TUNE: " + s); } catch { }
        }
    }
}
