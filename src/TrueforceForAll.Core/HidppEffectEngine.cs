// Renders the game's DirectInput effects that the wheel firmware would have
// rendered, had a Trueforce session not silenced its 0x8123 slot engine.
//
// Windows delivers game FFB to Trueforce wheels as the mainline-Linux
// hidpp_ff slot dialect on HID++ feature 0x8123 (byte-for-byte match with
// drivers/hid/hid-logitech-hidpp.c, decoded from the owner's G PRO trace,
// 2026-08-31; see docs/di-condition-engine.md):
//
//   [reportId][0xff][featIdx][fn<<4|swid][slot][type|0x80][len:2][delay:2][block...]
//
// fn1 RESET_ALL, fn2 DOWNLOAD_EFFECT, fn3 SET_EFFECT_STATE (1 stop, 2 play,
// 3 pause), fn4 DESTROY_EFFECT, fn8 SET_GLOBAL_GAINS. While an ep3 stream is
// open the firmware ignores ALL of it (parked-flick proof, 2026-08-31), so
// the tap decodes the downloads and this engine re-renders them into cur.
//
// Scope: PARAMETRIC effects only: the four conditions (spring, damper,
// friction, inertia), the five periodics, and ramp. The CONSTANT force
// (type 0x00) stays on the tap's existing scalar path (arbiter, freshness
// windows, quiet-spell hold), which already mirrors it 1:1; routing the
// other types here is also what stops a condition download's saturation
// field (0x7FFF at offset 10-11) from ever being misread as a force again
// (the issue #8 "constant 32767 on 0x12").
//
// Sign contract: positive force pulls toward LOWER steer (left), positive
// position/velocity = rightward. This is the hardware-validated convention
// of the stationary spring and the classic spring emulation. In it, the
// DirectInput model comes out as F = +coeff x deviation (spring),
// F = +coeff x velocity (damper), F = +coeff x acceleration (inertia):
// each opposes what it measures. Friction is a stick-slip lock point (see
// FrictionTerm). Evaluate's velTermSign flips ONLY the velocity-derived
// terms (damper, friction, inertia) for a rig whose DirectInput axis frame
// runs opposite to the stream's torque frame; the spring's sign is pinned
// by the validated stationary-spring convention and never flips with it.
//
// Stability (from every shipping implementation of this model: Simucube 1
// filters condition force outputs at 200 Hz, OpenFFBoard runs a per-effect
// biquad, mescon damps emulated springs): a host-rendered condition closes
// its loop over USB and rings on a low-friction motor if unfiltered, so
// each condition's OUTPUT passes a one-pole low-pass
// (ConditionOutputCutoffHz, default 200; 0 disables).
//
// Threading: all ingest methods are parser-thread only. Evaluate and the
// per-effect render state it mutates (friction lock point, LPF state) are
// pump-thread only, against an immutable snapshot (reference-swapped array,
// the _playingSprings pattern). ClearPlayingSnapshot is the one cross-
// thread-safe method (a volatile reference write). Evaluation is
// allocation-free.

using System;

namespace TrueforceForAll.Core
{
    public sealed class HidppEffectEngine
    {
        // Effect types (mainline hidpp values; bit 7 = autostart).
        public const byte TypeConstant     = 0x00;
        public const byte TypeSine         = 0x01;
        public const byte TypeSquare       = 0x02;
        public const byte TypeTriangle     = 0x03;
        public const byte TypeSawtoothUp   = 0x04;
        public const byte TypeSawtoothDown = 0x05;
        public const byte TypeSpring       = 0x06;
        public const byte TypeDamper       = 0x07;
        public const byte TypeFriction     = 0x08;
        public const byte TypeInertia      = 0x09;
        public const byte TypeRamp         = 0x0a;
        public const byte AutostartBit     = 0x80;

        public const byte StateStop  = 0x01;
        public const byte StatePlay  = 0x02;
        public const byte StatePause = 0x03;

        private const int SlotCount = 16;   // mainline GET_INFO reports the pool; 16 covers it

        /// <summary>True for a type this engine renders (everything except
        /// the constant, which stays on the scalar path).</summary>
        public static bool IsParametricType(byte typeByte)
        {
            byte t = (byte)(typeByte & 0x7f);
            return t >= TypeSine && t <= TypeRamp;
        }

        // One downloaded effect. The parser builds a fresh instance per
        // download and never mutates a published one; the pump owns the
        // render-state fields (lock point, LPF) on the published copy.
        private sealed class Fx
        {
            public byte  Type;             // low 7 bits, no autostart
            public bool  Playing;
            public long  StartTicks;       // Stopwatch ticks at (re)start
            public int   LengthMs;         // 0 = infinite
            public int   DelayMs;

            // Conditions (normalized: coeff/center -1..1, sat 0..1, deadband
            // half-width in position units).
            public float LeftSat, LeftCoeff, Deadband, Center, RightCoeff, RightSat;

            // Periodic (magnitude/offset -1..1, phase 0..1 of a cycle) and
            // ramp (start/end -1..1). Envelope levels are fractions of the
            // magnitude; lengths in ms.
            public float Magnitude, Offset;
            public int   PeriodMs;
            public float Phase;
            public float RampStart, RampEnd;
            public float AttackLevel, FadeLevel;
            public int   AttackMs, FadeMs;

            // Pump-thread-only render state. Copied by CloneWith so a state
            // flip does not reset it; a re-download builds a fresh Fx and
            // deliberately does (new parameters, new lock/filter state).
            public float LockPos;          // friction stick-slip lock point
            public bool  LockInit;
            public float LpfState;         // per-effect output low-pass state
            public bool  LpfInit;

            public Fx CloneWith(bool playing, long startTicks)
            {
                var c = (Fx)MemberwiseClone();
                c.Playing = playing;
                c.StartTicks = startTicks;
                // Render state is PUMP-owned and may be mid-write on the pump
                // thread while this clone runs on the parser thread; copying
                // it can capture a torn pair (LockInit true with a stale
                // LockPos = a full-scale friction kick; verify workflow
                // 2026-09-01). Reset it instead: the pump re-seeds on its
                // next evaluate, one pass-through tick, imperceptible.
                c.LockInit = false;
                c.LockPos  = 0f;
                c.LpfInit  = false;
                c.LpfState = 0f;
                return c;
            }
        }

        // Parser-thread state: the slot table (1-based device slots; index
        // 0 unused) and the slot the last new-effect download was placed in
        // provisionally, so a device reply naming a different slot can move
        // it (new downloads carry slot 0; the wheel assigns the real one in
        // its interrupt-IN reply, params[0]).
        private readonly Fx[] _slots = new Fx[SlotCount + 1];
        private int _provisionalSlot;

        // Pump-side snapshot: playing effects only. Null when nothing plays.
        private volatile Fx[] _playing;
        private volatile float _globalGain = 1f;

        // Pump-thread only: evaluate-to-evaluate clock for the output LPFs.
        private long _lastEvalTicks;

        /// <summary>Downloads seen for parametric types (the spring analogue
        /// of FfbSamplesCaptured: proof the game commands FFB even when no
        /// scalar force appears). Parser thread writes; int reads are atomic
        /// on the 32-bit host.</summary>
        public int ParametricDownloads { get; private set; }

        /// <summary>fn2 downloads whose type byte this engine does not know
        /// (diagnostics; the tap decides what to do with the packet).</summary>
        public int UnknownTypeDownloads { get; private set; }

        /// <summary>Parser thread: count an unknown-type download the tap
        /// chose to drop or route elsewhere.</summary>
        public void CountUnknownType() => UnknownTypeDownloads++;

        /// <summary>True while the snapshot holds playing effects. NOT
        /// expiry-aware (a finite effect stays in the snapshot after its
        /// duration ends until the next table change); liveness gates should
        /// use <see cref="AnyPlayingAt"/>.</summary>
        public bool AnyPlaying => _playing != null;

        /// <summary>Expiry-aware liveness: any playing effect whose duration
        /// (plus start delay) has not run out at nowTicks.</summary>
        public bool AnyPlayingAt(long nowTicks, double ticksPerSecond)
        {
            var p = _playing;
            if (p == null) return false;
            for (int i = 0; i < p.Length; i++)
                if (IsLiveAt(p[i], nowTicks, ticksPerSecond)) return true;
            return false;
        }

        /// <summary>Expiry-aware: a damper or friction condition is live at
        /// nowTicks. The CSP-synthesized damper stands down only while a
        /// DECODED damper is actually live (an expired one must not pin the
        /// fallback off; audit 2026-09-01).</summary>
        public bool AnyDamperPlayingAt(long nowTicks, double ticksPerSecond)
        {
            var p = _playing;
            if (p == null) return false;
            for (int i = 0; i < p.Length; i++)
                if ((p[i].Type == TypeDamper || p[i].Type == TypeFriction)
                    && IsLiveAt(p[i], nowTicks, ticksPerSecond)) return true;
            return false;
        }

        private static bool IsLiveAt(Fx fx, long nowTicks, double ticksPerSecond)
        {
            if (fx.LengthMs <= 0) return true;   // infinite
            double elapsedMs = (nowTicks - fx.StartTicks) * 1000.0 / ticksPerSecond;
            return elapsedMs - fx.DelayMs <= fx.LengthMs;
        }

        /// <summary>Global gain as the device would apply it (fn8), 0..1.</summary>
        public float GlobalGain => _globalGain;

        // ---------------- ingest (parser thread) ----------------

        /// <summary>One fn2 DOWNLOAD_EFFECT. payload/off address the HID++
        /// report start ([reportId][devIdx][featIdx][fnByte][params...]).
        /// Returns true when the effect was a parametric type this engine
        /// took (the caller's scalar path must then leave the packet alone).</summary>
        public bool HandleDownload(byte[] payload, int off, int len, long nowTicks)
        {
            if (len < 12) return false;
            byte slotByte = payload[off + 4];
            byte typeByte = payload[off + 5];
            byte type     = (byte)(typeByte & 0x7f);
            if (type == TypeConstant) return false;      // scalar path owns it
            if (type > TypeRamp) { UnknownTypeDownloads++; return false; }

            var fx = new Fx
            {
                Type     = type,
                LengthMs = (payload[off + 6] << 8) | payload[off + 7],
                DelayMs  = (payload[off + 8] << 8) | payload[off + 9],
            };

            if (type >= TypeSpring && type <= TypeInertia)
            {
                if (len < 22) return false;              // condition block is 18 params
                fx.LeftSat    = U16(payload, off + 10) / 32767f;   // wire = sat >> 1
                fx.LeftCoeff  = S16(payload, off + 12) / 32767f;
                fx.Deadband   = U16(payload, off + 14) / 32767f;   // wire = deadband >> 1; half-width
                fx.Center     = S16(payload, off + 16) / 32767f;
                fx.RightCoeff = S16(payload, off + 18) / 32767f;
                fx.RightSat   = U16(payload, off + 20) / 32767f;
                if (fx.LeftSat  > 1f) fx.LeftSat  = 1f;
                if (fx.RightSat > 1f) fx.RightSat = 1f;
            }
            else if (type == TypeRamp)
            {
                if (len < 20) return false;
                fx.RampStart   = S16(payload, off + 10) / 32767f;
                fx.RampEnd     = S16(payload, off + 12) / 32767f;
                ReadEnvelope(fx, payload, off + 14);
            }
            else // periodic
            {
                if (len < 24) return false;
                fx.Magnitude = S16(payload, off + 10) / 32767f;
                fx.Offset    = S16(payload, off + 12) / 32767f;
                fx.PeriodMs  = U16(payload, off + 14);
                fx.Phase     = U16(payload, off + 16) / 65536f;
                ReadEnvelope(fx, payload, off + 18);
            }

            bool autostart = (typeByte & AutostartBit) != 0;

            int slot = slotByte;
            if (slot == 0 || slot > SlotCount)
            {
                // New effect: the wheel assigns the slot in its reply. Place
                // provisionally at the lowest free slot; AssignSlotFromReply
                // moves it if the wheel picked another.
                //
                // But first, if a condition of this type is ALREADY playing,
                // take its slot instead of opening another. Slots here are
                // freed only by an explicit destroy or reset, and a game that
                // re-creates its effects (which is what losing and regaining
                // the device makes it do, so every plugin off/on does it)
                // hands us a fresh one each time while the old stays marked
                // playing. Evaluate sums them, so the damper grew with every
                // cycle (owner, in game, 2026-09-03). Two live dampers is not
                // something a game asks for; it is us having missed a destroy,
                // and the cost of being wrong is one effect instead of two
                // rather than force multiplied by however many cycles.
                slot = LowestFreeSlot();
                _provisionalSlot = slot;
            }
            else if (slot == _provisionalSlot)
            {
                // The driver addressed our provisional guess directly: the
                // guess was right (or superseded either way); stop waiting
                // for a reply to move it.
                _provisionalSlot = 0;
            }

            var old = _slots[slot];
            fx.Playing    = autostart || (old != null && old.Playing);
            fx.StartTicks = (old != null && old.Playing && fx.Playing) ? old.StartTicks : nowTicks;
            _slots[slot]  = fx;
            ParametricDownloads++;
            RetireOtherCopies(slot);
            Publish();
            return true;
        }

        /// <summary>The wheel's fn2 reply names the slot it assigned
        /// (interrupt-IN, params[0]). Moves the provisional placement.</summary>
        public void AssignSlotFromReply(byte slot)
        {
            int prov = _provisionalSlot;
            if (prov == 0 || slot == 0 || slot > SlotCount || slot == prov) { _provisionalSlot = 0; return; }
            if (_slots[prov] != null && _slots[slot] == null)
            {
                _slots[slot] = _slots[prov];
                _slots[prov] = null;
                Publish();
            }
            _provisionalSlot = 0;
        }

        /// <summary>fn3 SET_EFFECT_STATE: params = [slot, state].</summary>
        public void HandleSetState(byte slot, byte state, long nowTicks)
        {
            if (slot == 0 || slot > SlotCount) return;
            var fx = _slots[slot];
            if (fx == null) return;
            bool play = state == StatePlay;
            if (fx.Playing == play && state != StatePlay) return;
            _slots[slot] = fx.CloneWith(play, play ? nowTicks : fx.StartTicks);
            if (play) RetireOtherCopies(slot);
            Publish();
        }

        /// <summary>fn4 DESTROY_EFFECT: params = [slot].</summary>
        public void HandleDestroy(byte slot)
        {
            if (slot == 0 || slot > SlotCount || _slots[slot] == null) return;
            _slots[slot] = null;
            Publish();
        }

        /// <summary>fn1 RESET_ALL, and the tap's deferred pause reset: every
        /// slot is gone. Parser thread only (see ClearPlayingSnapshot for the
        /// cross-thread half).</summary>
        public void ResetAll()
        {
            for (int i = 0; i <= SlotCount; i++) _slots[i] = null;
            _provisionalSlot = 0;
            _playing = null;
        }

        /// <summary>Cross-thread-safe half of a pause suspend: drop the
        /// playing snapshot NOW (a volatile reference write) so no effect
        /// renders past a pause, while the table itself is retained and
        /// republished on the parser thread once the game drives again (the
        /// tap's deferred flag), mirroring the classic path's
        /// _classicResetRequested pattern.</summary>
        public void ClearPlayingSnapshot() => _playing = null;

        /// <summary>Parser thread: rebuild the playing snapshot from the
        /// retained slot table. The resume half of a pause suspend: native
        /// effects survive a pause (the firmware keeps its slots), so a game
        /// that downloads its conditions once and thereafter only sends
        /// SET_EFFECT_STATE must not lose them to our pause handling (verify
        /// workflow 2026-09-01). Conditions are position/velocity-relative
        /// and per-side saturated, so retention cannot replay a held pull
        /// the way a stale constant could (the issue-#13 class).</summary>
        public void RepublishFromTable() => Publish();

        /// <summary>Parser thread: a constant-force download arrived between
        /// a new-effect parametric download and its slot ack, making the next
        /// ack ambiguous (constants never enter this table, so their acks
        /// would otherwise pass the move-guard and relocate the parametric
        /// effect; verify workflow 2026-09-01). Stop waiting: the effect
        /// stays at its guessed slot and the driver's own re-downloads heal
        /// any drift.</summary>
        public void CancelProvisional() => _provisionalSlot = 0;

        /// <summary>fn8 SET_GLOBAL_GAINS: params = [gain_hi, gain_lo, ...].</summary>
        public void HandleSetGain(ushort gain)
        {
            _globalGain = gain / 65535f;
        }

        // ---------------- evaluate (pump thread) ----------------

        /// <summary>Sum of the playing parametric effects, in fractions of
        /// full scale, positive = toward lower steer. posNorm -1..1 (+1 =
        /// right), velNormPerSec in the same units per second, accel per
        /// second squared. damperGain scales damper terms (the DAMPCAL
        /// number: force fraction per coefficient x unit velocity);
        /// inertiaGain likewise for acceleration. velTermSign (+1/-1) flips
        /// ONLY the velocity-derived terms (damper, friction, inertia); the
        /// spring's sign is pinned by the validated convention. Every effect
        /// family carries its own gain so the test bench can tune them one at
        /// a time. Returns 0 with <paramref name="anyPlaying"/> false when
        /// nothing plays.</summary>
        public float Evaluate(float posNorm, float velNormPerSec, float accel,
                              float damperGain, float inertiaGain,
                              long nowTicks, double ticksPerSecond, out bool anyPlaying,
                              int velTermSign = 1, float springGain = 1f, float frictionGain = 1f,
                              float periodicGain = 1f, float rampGain = 1f)
        {
            var playing = _playing;
            anyPlaying = false;
            if (playing == null) { _lastEvalTicks = nowTicks; return 0f; }

            if (posNorm > 1f) posNorm = 1f; else if (posNorm < -1f) posNorm = -1f;
            float sum = 0f;
            double msPerTick = 1000.0 / ticksPerSecond;
            double dtSec = _lastEvalTicks != 0 ? (nowTicks - _lastEvalTicks) / ticksPerSecond : 0;
            if (dtSec < 0 || dtSec > 0.1) dtSec = 0.001;
            _lastEvalTicks = nowTicks;

            for (int i = 0; i < playing.Length; i++)
            {
                var fx = playing[i];
                double elapsedMs = (nowTicks - fx.StartTicks) * msPerTick;
                if (elapsedMs < fx.DelayMs) continue;
                elapsedMs -= fx.DelayMs;
                if (fx.LengthMs > 0 && elapsedMs > fx.LengthMs) continue;
                anyPlaying = true;

                float term;
                bool condition = true;
                switch (fx.Type)
                {
                    case TypeSpring:
                        term = ConditionTerm(fx, posNorm, springGain);
                        break;
                    case TypeDamper:
                        term = ConditionTerm(fx, velNormPerSec, damperGain) * velTermSign;
                        break;
                    case TypeFriction:
                        term = FrictionTerm(fx, posNorm, velNormPerSec, frictionGain) * velTermSign;
                        break;
                    case TypeInertia:
                        // Velocity carries the frame sign too. InertiaTerm
                        // decides which half of the term pushes ALONG travel
                        // by comparing the term's sign against the velocity's,
                        // so handing it a negated term beside an un-negated
                        // velocity inverted that judgement: on a flipped-frame
                        // rig it faded out the half that resists and kept the
                        // half that sustains, exactly backwards.
                        term = InertiaAsDamping
                            // Damping-shaped: always opposes motion, so it
                            // needs none of the coast/resist machinery.
                            ? ConditionTerm(fx, velNormPerSec, inertiaGain) * velTermSign
                            : InertiaTerm(ConditionTerm(fx, accel, inertiaGain) * velTermSign,
                                          velNormPerSec * velTermSign);
                        break;
                    case TypeRamp:
                    {
                        condition = false;
                        float t = fx.LengthMs > 0 ? (float)(elapsedMs / fx.LengthMs) : 0f;
                        term = (fx.RampStart + (fx.RampEnd - fx.RampStart) * t)
                               * Envelope(fx, elapsedMs) * rampGain;
                        break;
                    }
                    default:   // periodics
                    {
                        condition = false;
                        if (fx.PeriodMs <= 0) { term = 0f; break; }
                        double cycle = elapsedMs / fx.PeriodMs + fx.Phase;
                        cycle -= Math.Floor(cycle);
                        term = (fx.Offset + fx.Magnitude * Wave(fx.Type, cycle)
                                * Envelope(fx, elapsedMs)) * periodicGain;
                        break;
                    }
                }
                // Conditions pass the stability low-pass. Waveforms pass a
                // far gentler one that rounds their discontinuities without
                // touching their shape.
                //
                // A sawtooth's reset is a full-scale step in a single tick, and
                // rendered raw at 1 kHz the wheel is asked to reverse its
                // entire output instantly: the motor answers with an audible
                // click at the peak, which the firmware's own rendering does
                // not produce (owner, 2026-09-03). It was left unfiltered on
                // the reasoning that filtering a square defeats it, which is
                // true of a filter set for conditions and false of one set an
                // order of magnitude higher: at 150 Hz the edge is rounded
                // over about a millisecond while a 4 Hz ramp is untouched.
                sum += condition ? OutputLpf(fx, term, dtSec)
                                 : WaveformSlew(fx, term, dtSec);
            }
            return sum * _globalGain;
        }

        // The shared condition shape: measure (position deviation, velocity,
        // or acceleration), per-side coefficient beyond the deadband,
        // per-side saturation. Positive measure pulls positive (left), which
        // opposes it in the wheel's sign space.
        private static float ConditionTerm(Fx fx, float measure, float gain)
        {
            float dev = measure - fx.Center;
            float db  = fx.Deadband;
            float f;
            if (dev > db)       f = fx.RightCoeff * (dev - db) * gain;
            else if (dev < -db) f = fx.LeftCoeff  * (dev + db) * gain;
            else return 0f;
            float sat = dev > 0f ? fx.RightSat : fx.LeftSat;
            return Clamp(f, sat);
        }

        /// <summary>Whether rendered inertia gives its stored energy back
        /// (a lossless flywheel: push the wheel and it coasts on). The G PRO
        /// firmware does NOT (rig A/B, 2026-09-01: native inertia turned up
        /// "just acts as damping", the wheel stops while ours kept moving),
        /// so the default is the lossy form that matches the wheel. True is
        /// the DirectInput-spec reading, kept for the bench A/B and for
        /// firmware that stores energy properly.</summary>
        public bool InertiaCoasts { get; set; }

        /// <summary>Render the inertia effect against VELOCITY rather than
        /// acceleration, which is what the G PRO's firmware appears to do.
        ///
        /// The owner has now reported it twice, unprompted and months of
        /// tuning apart: native inertia "feels like a whole different effect,
        /// like it's just damping, no inertia". Matching the wheel is the
        /// whole mandate, so when the firmware renders inertia as damping,
        /// rendering a textbook flywheel is the wrong answer however correct
        /// it is on paper.
        ///
        /// It also cures the grain. Acceleration is a SECOND derivative of a
        /// quantized encoder and inertia was the only effect built on one,
        /// which is exactly why it was the grainiest of the four. Velocity is
        /// one derivative cleaner.
        ///
        /// NOTE the gain changes meaning with the mode: force per unit
        /// VELOCITY here, per unit ACCELERATION otherwise, and those differ by
        /// about an order of magnitude on a hand-turned wheel. A gain tuned in
        /// one mode is meaningless in the other.</summary>
        public bool InertiaAsDamping { get; set; } = true;

        // Velocity scale (range/s) over which the sustaining half fades in.
        // A hard gate on sign(velocity) would chatter exactly where the
        // wheel dithers around zero: the start of a push.
        private const float InertiaVelSoftness = 0.05f;

        // Inertia, lossy by default. A virtual mass resists acceleration in
        // both directions: it fights the push, then fights the wheel's own
        // friction on the way down, which is the coast-on the rig felt. The
        // firmware only ever resists, so the half of the term that would
        // push the wheel ALONG its travel is faded out. Positive term pulls
        // toward lower steer, so a term whose sign matches velocity opposes
        // motion; the opposite sign sustains it.
        private float InertiaTerm(float term, float vel)
        {
            if (InertiaCoasts || term == 0f) return term;
            float dir = vel / InertiaVelSoftness;                     // sign, ramped through zero
            if (dir > 1f) dir = 1f; else if (dir < -1f) dir = -1f;
            float sustaining = -dir * (term >= 0f ? 1f : -1f);        // +1 fully along travel
            if (sustaining <= 0f) return term;
            if (sustaining > 1f) sustaining = 1f;
            return term * (1f - sustaining);
        }

        // Friction as a stick-slip lock point (concept from Simucube's open
        // firmware; implementation ours): torque proportional to how far the
        // wheel sits from a lock position that drags along once the distance
        // exceeds FrictionLockDistance, per-side coefficient and saturation.
        // Renders STATIC friction (a still wheel resists starting to move)
        // and cannot chatter at zero velocity the way sign(velocity) models
        // do. The implied position slope is steep, which is exactly why the
        // output low-pass exists (Simucube filters this same term at 200 Hz).
        // The PID friction deadband/center (velocity-domain) are subsumed by
        // the lock-point model and not separately applied.
        private const float FrictionLockDistance = 0.025f;  // of full range
        private const float FrictionDampK        = 0.35f;   // in-zone dissipation

        // The gain is applied to the COULOMB part only, not to the whole
        // term. DirectInput friction is a constant magnitude opposing motion;
        // the velocity term below is ours, added for stability, and native
        // has no counterpart to it. Scaling both meant the calibrated gain
        // was absorbing a viscous component that should never have been in
        // the comparison: the measured gain then matched total force at one
        // speed and was wrong at every other, and the friction phase's fit
        // charged that viscosity to Coulomb (audit D6, 2026-09-02, ~7 % at
        // the default gain and 17 % from a weaker one). Saturation still
        // clamps the sum, because the wheel sees the sum.
        private static float FrictionTerm(Fx fx, float pos, float vel, float gain)
        {
            if (!fx.LockInit) { fx.LockPos = pos; fx.LockInit = true; return 0f; }
            float diff = pos - fx.LockPos;
            if (diff > FrictionLockDistance)
            {
                fx.LockPos = pos - FrictionLockDistance;
                diff = FrictionLockDistance;
            }
            else if (diff < -FrictionLockDistance)
            {
                fx.LockPos = pos + FrictionLockDistance;
                diff = -FrictionLockDistance;
            }
            // Wheel right of the lock point: friction resists rightward
            // motion = pulls left = positive, per the cur sign contract.
            // The pure lock-point is a stiff CONSERVATIVE spring inside its
            // zone and rang on release (rig, 2026-09-01: "friction
            // oscillated once the wheel was released"); real stiction
            // dissipates. The velocity term adds that dissipation (it
            // slightly damper-flavors fast steady slip; acceptable).
            float frac  = diff / FrictionLockDistance;      // -1..1
            float coeff = frac >= 0f ? fx.RightCoeff : fx.LeftCoeff;
            float sat   = frac >= 0f ? fx.RightSat   : fx.LeftSat;
            return Clamp(coeff * (frac * gain + FrictionDampK * vel), sat);
        }

        /// <summary>Cutoff of the per-condition output low-pass, Hz. Every
        /// shipping host-side renderer filters condition OUTPUTS (Simucube
        /// 200 Hz, OpenFFBoard per-effect biquads, mescon's spring damping):
        /// unfiltered, a condition rendered over USB rings on a low-friction
        /// motor. 0 disables (A/B and tests).</summary>
        public float ConditionOutputCutoffHz { get; set; } = 200f;

        /// <summary>How fast a waveform's output may change, in fractions
        /// of full scale per millisecond. 0 disables.
        ///
        /// A LIMIT rather than a filter, because the problem is one sample
        /// wide. A sawtooth reset is a full-scale step in a single tick and a
        /// low-pass able to round that at 1 kHz would have to be slow enough
        /// to dull the whole waveform; a slew limit is transparent everywhere
        /// below its threshold and acts only on the discontinuity. A 250 ms
        /// sawtooth ramps at 0.008 per ms and never touches this; its reset
        /// asks for 2.0 in one tick and gets a few milliseconds instead.
        /// 0.08 reverses full scale in about 25 ms. 0.25 (8 ms) was tried
        /// first and still clicked on the rig: a direct-drive motor answers a
        /// millisecond-scale corner audibly even when the step itself is
        /// bounded.</summary>
        public float WaveformMaxStepPerMs { get; set; } = 0.08f;

        // Rate-limit a waveform's output. Shares the per-effect LPF state
        // slot, which conditions never touch and waveforms never used.
        private float WaveformSlew(Fx fx, float x, double dtSec)
        {
            float rate = WaveformMaxStepPerMs;
            if (rate <= 0f || !fx.LpfInit)
            {
                fx.LpfState = x;
                fx.LpfInit = true;
                return x;
            }
            if (dtSec <= 0) return fx.LpfState;
            float maxStep = (float)(rate * dtSec * 1000.0);
            float d = x - fx.LpfState;
            if (d > maxStep) d = maxStep;
            else if (d < -maxStep) d = -maxStep;
            fx.LpfState += d;
            return fx.LpfState;
        }

        private float OutputLpf(Fx fx, float x, double dtSec, float cutoffHz = -1f)
        {
            float hz = cutoffHz >= 0f ? cutoffHz : ConditionOutputCutoffHz;
            if (hz <= 0f || !fx.LpfInit)
            {
                fx.LpfState = x;
                fx.LpfInit = true;
                return x;
            }
            // Same-timestamp re-evaluate: hold the filtered state rather than
            // stomping it with the raw sample (verify workflow 2026-09-01).
            if (dtSec <= 0) return fx.LpfState;
            float a = (float)(dtSec / (dtSec + 1.0 / (2.0 * Math.PI * hz)));
            fx.LpfState += (x - fx.LpfState) * a;
            return fx.LpfState;
        }

        private static float Clamp(float f, float sat)
            => f > sat ? sat : f < -sat ? -sat : f;

        private static double WaveTriangle(double c)
            => c < 0.25 ? 4 * c : c < 0.75 ? 2 - 4 * c : 4 * c - 4;

        private static float Wave(byte type, double cycle)
        {
            switch (type)
            {
                case TypeSine:         return (float)Math.Sin(2 * Math.PI * cycle);
                case TypeSquare:       return cycle < 0.5 ? 1f : -1f;
                case TypeTriangle:     return (float)WaveTriangle(cycle);
                case TypeSawtoothUp:   return (float)(2 * cycle - 1);
                case TypeSawtoothDown: return (float)(1 - 2 * cycle);
                default:               return 0f;
            }
        }

        // Standard attack/fade envelope over a magnitude (ff.rst semantics):
        // attack_level -> 1 over attack_length, 1 -> fade_level over the
        // final fade_length. Envelopes are INVALID on infinite-duration
        // effects (OpenFFBoard and the PID model agree): an infinite effect
        // has no end to fade toward, and firmware does not attack it either.
        private static float Envelope(Fx fx, double elapsedMs)
        {
            if (fx.LengthMs <= 0) return 1f;
            if (fx.AttackMs > 0 && elapsedMs < fx.AttackMs)
            {
                float t = (float)(elapsedMs / fx.AttackMs);
                return fx.AttackLevel + (1f - fx.AttackLevel) * t;
            }
            if (fx.FadeMs > 0)
            {
                double fadeStart = fx.LengthMs - fx.FadeMs;
                if (elapsedMs > fadeStart)
                {
                    float t = (float)((elapsedMs - fadeStart) / fx.FadeMs);
                    return 1f + (fx.FadeLevel - 1f) * t;
                }
            }
            return 1f;
        }

        private void ReadEnvelope(Fx fx, byte[] p, int off)
        {
            fx.AttackLevel = p[off] / 255f;                    // wire = level >> 7 of 0x7FFF
            fx.AttackMs    = (p[off + 1] << 8) | p[off + 2];
            fx.FadeLevel   = p[off + 3] / 255f;
            fx.FadeMs      = (p[off + 4] << 8) | p[off + 5];
        }

        private static bool IsConditionType(byte t)
            => t == TypeSpring || t == TypeDamper || t == TypeFriction || t == TypeInertia;

        /// <summary>How many stale copies of a condition were retired
        /// because a newer one of the same type started. A steadily climbing
        /// count means destroys are being missed upstream.</summary>
        public int ReplacedStaleConditions => _replacedStaleConditions;
        private int _replacedStaleConditions;

        private int LowestFreeSlot()
        {
            for (int i = 1; i <= SlotCount; i++)
                if (_slots[i] == null) return i;
            return SlotCount;   // pool exhausted: overwrite the last
        }

        private void Publish()
        {
            int n = 0;
            for (int i = 1; i <= SlotCount; i++)
                if (_slots[i] != null && _slots[i].Playing) n++;
            if (n == 0) { _playing = null; return; }
            var arr = new Fx[n];
            int j = 0;
            for (int i = 1; i <= SlotCount; i++)
                if (_slots[i] != null && _slots[i].Playing) arr[j++] = _slots[i];
            _playing = arr;
        }

        /// <summary>Drop any OTHER playing condition of this slot's type.
        ///
        /// One live copy per condition type. Slots are freed only by an
        /// explicit destroy or reset, so a game that re-creates its effects
        /// leaves the old ones playing, and every plugin off/on makes a game
        /// do exactly that: the rendered damper grew and coarsened with each
        /// cycle because Evaluate sums whatever plays (owner, in game,
        /// 2026-09-03).
        ///
        /// The leftovers are REMOVED, not merely hidden from the renderer.
        /// They exist only because we missed their destroy, so they are our
        /// bookkeeping error and not game state. An earlier version shadowed
        /// them instead, meaning a destroy of the live effect revived a stale
        /// one and the wheel kept applying a condition the game had ended
        /// (owner: "if a game destroys an effect, it meant to destroy the
        /// effect altogether and will request it again later"). That is the
        /// held-force failure this project already knows: a force nobody
        /// asked for is worse than no force.
        ///
        /// Conditions only. A game builds rumble from several periodics at
        /// once and the firmware sums those too.
        private void RetireOtherCopies(int keepSlot)
        {
            var kept = _slots[keepSlot];
            if (kept == null || !kept.Playing || !IsConditionType(kept.Type)) return;
            for (int i = 1; i <= SlotCount; i++)
            {
                if (i == keepSlot) continue;
                var other = _slots[i];
                if (other == null || !other.Playing || other.Type != kept.Type) continue;
                _slots[i] = null;
                _replacedStaleConditions++;
            }
        }
ushort U16(byte[] p, int off) => (ushort)((p[off] << 8) | p[off + 1]);
        private static short  S16(byte[] p, int off) => (short)((p[off] << 8) | p[off + 1]);
    }
}
