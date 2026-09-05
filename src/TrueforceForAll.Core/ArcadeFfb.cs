// The arcade force-feedback intermediate representation, and the per-game
// decoders that produce it.
//
// WHY AN IR AT ALL. Every emulated arcade game encodes its force command
// differently: the command byte that means "constant force" in Initial D means
// something else in the next cabinet, so there is no shared opcode table to
// write. What IS shared is the layer underneath. FFBArcadePlugin, which is the
// reference for every one of these protocols, decodes each game into the same
// six calls (Constant, Spring, Friction, Damper, Sine, Rumble), and those map
// cleanly onto what our wheel can render. So the six calls are the contract, a
// decoder per game is the variable part, and adding a game is adding one small
// class rather than touching the reader, the renderer or the plugin.
//
// UNITS AND SIGNS, once, here, so no decoder has to decide them again:
//   Strength   0..1 of full device force. Constant is SIGNED, and positive means
//              the wheel is pushed RIGHT.
//
//              That sentence contradicts HidppEffectEngine, which states the
//              opposite for the same scalar channel, and the contradiction was
//              live for a while: nothing reconciled the two, so which one held
//              was a matter of reading rather than measuring.
//
//              Settled on the rig (owner, G PRO, Initial D 8): what reaches the
//              wheel is correct as authored here, PROVIDED the tap's sign
//              reconciliation is not applied on top. It was being applied,
//              because arcade shares the pass-through path, and the cabinet's
//              centring spring pushed the wrong way under the same Invert
//              setting that is right in every other game. The device now skips
//              that inversion for an authored arcade force (see
//              TrueforceDevice.FfbBypassInvert), which is what makes the line
//              above true end to end.
//
//              What that test did NOT settle is the absolute convention at ep3,
//              only that no inversion belongs between here and there. Do not
//              quote it as having measured which way ep3 counts.
//   PeriodMs   milliseconds. The reference hands this to SDL_HapticPeriodic.period.
//   Rumble     the reference's rumble is a gamepad-motor call, not a wheel
//              effect. It is carried here for completeness and because a decoder
//              should record what the game actually asked for, but the renderer
//              is free to ignore it: on a Trueforce wheel that texture is what
//              the ep3 stream already carries.
//
// The direction convention is the single most error-prone thing in this port.
// The reference names its constants by what GENERATES the force, so
// DIRECTION_FROM_LEFT makes the wheel go RIGHT. Decoders here must resolve that
// and emit a signed strength, never a named direction.

using System;
using System.Collections.Generic;

namespace TrueforceForAll.Core
{
    public enum ArcadeTriggerKind
    {
        None = 0,
        Constant,
        Spring,
        Friction,
        Damper,
        Sine,
        Triangle,
        SawtoothUp,
        SawtoothDown,
        Inertia,
        Ramp,
        Rumble,
    }

    /// <summary>One call in the arcade trigger vocabulary.</summary>
    public struct ArcadeTrigger
    {
        public ArcadeTriggerKind Kind;
        /// <summary>0..1 of full force. SIGNED for Constant, where positive
        /// pushes the wheel right.</summary>
        public double Strength;
        /// <summary>Milliseconds, periodic effects only.</summary>
        public int PeriodMs;
        /// <summary>Rumble only: the two motor levels the reference would drive.</summary>
        public double RumbleLow, RumbleHigh;

        // Condition parameters, as the device would have received them. Carried in
        // full rather than reduced to one strength, because these are where a
        // user's own tuning ends up: min and max force, the alternative-FFB
        // windows and the doubling options all land in the coefficients and
        // saturations, and flattening them would quietly discard the work.
        // Defaults describe the simple symmetric case the direct decoders build.
        public double CoeffLeft, CoeffRight;   // -1..1
        public double SatLeft, SatRight;       // 0..1
        public double Deadband;                // 0..1, half width
        public double Center;                  // -1..1
        public double Offset;                  // periodic offset, -1..1
        public double Phase;                   // periodic phase, 0..1 of a cycle

        public bool Present => Kind != ArcadeTriggerKind.None;

        public static ArcadeTrigger Constant(double signedStrength)
            => new ArcadeTrigger { Kind = ArcadeTriggerKind.Constant, Strength = signedStrength };

        public static ArcadeTrigger Spring(double strength)
            => Condition(ArcadeTriggerKind.Spring, strength);

        /// <summary>A symmetric condition: equal coefficients, full saturation, no
        /// deadband, centred. What a decoder that only knows one strength builds.</summary>
        public static ArcadeTrigger Condition(ArcadeTriggerKind kind, double strength)
            => new ArcadeTrigger
            {
                Kind = kind,
                Strength = strength,
                CoeffLeft = strength,
                CoeffRight = strength,
                SatLeft = 1.0,
                SatRight = 1.0,
            };

        /// <summary>A condition with every parameter given, for a source that has
        /// the device-level values and must not round them off.</summary>
        public static ArcadeTrigger ConditionFull(ArcadeTriggerKind kind,
            double coeffLeft, double coeffRight, double satLeft, double satRight,
            double deadband, double center)
            => new ArcadeTrigger
            {
                Kind = kind,
                Strength = Math.Max(Math.Abs(coeffLeft), Math.Abs(coeffRight)),
                CoeffLeft = coeffLeft,
                CoeffRight = coeffRight,
                SatLeft = satLeft,
                SatRight = satRight,
                Deadband = deadband,
                Center = center,
            };

        public static ArcadeTrigger Friction(double strength)
            => Condition(ArcadeTriggerKind.Friction, strength);

        public static ArcadeTrigger Damper(double strength)
            => Condition(ArcadeTriggerKind.Damper, strength);

        public static ArcadeTrigger Sine(double strength, int periodMs)
            => new ArcadeTrigger { Kind = ArcadeTriggerKind.Sine, Strength = strength, PeriodMs = periodMs };

        public static ArcadeTrigger Periodic(ArcadeTriggerKind kind, double magnitude,
                                             int periodMs, double offset, double phase)
            => new ArcadeTrigger
            {
                Kind = kind,
                Strength = magnitude,
                PeriodMs = periodMs,
                Offset = offset,
                Phase = phase,
            };

        public static ArcadeTrigger Rumble(double low, double high)
            => new ArcadeTrigger { Kind = ArcadeTriggerKind.Rumble, RumbleLow = low, RumbleHigh = high };
    }

    /// <summary>Everything one decoded arcade command asks for. Immutable and
    /// reference-swapped rather than mutated, so the reader thread can publish it
    /// to the pump thread without a lock and without tearing, the same way the
    /// effect engine publishes its slot table.
    ///
    /// One slot per trigger kind rather than a list: a game commands at most one
    /// of each per frame, fixed fields need no allocation to inspect, and the
    /// renderer wants to ask "is there a spring right now" rather than to scan.</summary>
    public sealed class ArcadeFfbState
    {
        public static readonly ArcadeFfbState Empty = new ArcadeFfbState();

        public ArcadeTrigger Constant;
        public ArcadeTrigger Spring;
        public ArcadeTrigger Friction;
        public ArcadeTrigger Damper;
        public ArcadeTrigger Periodic;
        public ArcadeTrigger Inertia;
        public ArcadeTrigger Ramp;
        public ArcadeTrigger Rumble;

        public void Set(ArcadeTrigger t)
        {
            switch (t.Kind)
            {
                case ArcadeTriggerKind.Constant: Constant = t; break;
                case ArcadeTriggerKind.Spring:   Spring   = t; break;
                case ArcadeTriggerKind.Friction: Friction = t; break;
                case ArcadeTriggerKind.Damper:   Damper   = t; break;
                case ArcadeTriggerKind.Inertia:  Inertia  = t; break;
                // The four periodic shapes share one channel: a game builds a
                // texture from one waveform at a time, and the renderer has one
                // periodic slot to put it in.
                case ArcadeTriggerKind.Sine:
                case ArcadeTriggerKind.Triangle:
                case ArcadeTriggerKind.SawtoothUp:
                case ArcadeTriggerKind.SawtoothDown: Periodic = t; break;
                case ArcadeTriggerKind.Ramp:     Ramp     = t; break;
                case ArcadeTriggerKind.Rumble:   Rumble   = t; break;
            }
        }

        public bool AnyForceShaped
            => Constant.Present || Spring.Present || Friction.Present
            || Damper.Present || Periodic.Present || Inertia.Present || Ramp.Present;

        /// <summary>Value equality over what the renderer acts on, so the reader
        /// can publish only on a genuine change. Re-publishing an identical state
        /// would make the renderer re-download its effects, and a download resets
        /// the engine's per-effect filter state.</summary>
        public bool SameAs(ArcadeFfbState o)
        {
            if (o == null) return false;
            return Same(Constant, o.Constant) && Same(Spring, o.Spring)
                && Same(Friction, o.Friction) && Same(Damper, o.Damper)
                && Same(Periodic, o.Periodic) && Same(Inertia, o.Inertia)
                && Same(Ramp, o.Ramp) && Same(Rumble, o.Rumble);
        }

        private static bool Same(ArcadeTrigger a, ArcadeTrigger b)
            => a.Kind == b.Kind
            && a.PeriodMs == b.PeriodMs
            && Near(a.Strength, b.Strength)
            && Near(a.CoeffLeft, b.CoeffLeft) && Near(a.CoeffRight, b.CoeffRight)
            && Near(a.SatLeft, b.SatLeft) && Near(a.SatRight, b.SatRight)
            && Near(a.Deadband, b.Deadband) && Near(a.Center, b.Center)
            && Near(a.Offset, b.Offset) && Near(a.Phase, b.Phase)
            && Near(a.RumbleLow, b.RumbleLow) && Near(a.RumbleHigh, b.RumbleHigh);

        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
    }

    /// <summary>What an arcade force source publishes, whichever way it got the
    /// data. Two exist: one reads TeknoParrot's cabinet IO block and decodes the
    /// protocol itself, the other reads already-decoded effect calls out of a
    /// shared block published by a build of FFBArcadePlugin. The plugin's force
    /// provider and effect renderer consult this rather than either concrete
    /// class, so adding a third way in later touches neither.</summary>
    public interface IArcadeForceSource
    {
        /// <summary>The scalar constant force, if fresher than maxAgeMs. Same
        /// contract as the wire tap and the AC source so the provider chain can
        /// consult any of them interchangeably.</summary>
        short? TryGetFreshFfbTarget(int maxAgeMs);

        void ClearLastFfbTarget();

        /// <summary>Push tuning into a source that is already running. Without
        /// this the values are read once when the source is built, so a slider
        /// would do nothing until the game was restarted, which reads as a dead
        /// control rather than as a setting that needs a relaunch.</summary>
        void ApplyTuning(double forceScale, bool invertDirection, bool acceptDamper);

        /// <summary>The effect-shaped part (spring, damper, friction, periodic),
        /// if fresher than maxAgeMs, else null. A new object is published only on
        /// a genuine change, so callers can compare by reference.</summary>
        ArcadeFfbState TryGetFreshState(int maxAgeMs);
    }

    /// <summary>Decodes one game's force command out of TeknoParrot's cabinet IO
    /// block. Implementations are stateless where the game allows it; a game that
    /// genuinely carries state between commands may hold it, in which case the
    /// instance belongs to one session and Reset must clear it.</summary>
    public interface IArcadeFfbDecoder
    {
        string Name { get; }

        /// <summary>FFBArcadePlugin's game id, which is how these protocols are
        /// identified everywhere else. 0 for a decoder that is not tied to one.</summary>
        int GameId { get; }

        /// <summary>Which int-sized slot of the block carries the command: 2 for
        /// most games, 6 for a handful.</summary>
        int SlotIndex { get; }

        /// <summary>Decode one command. Returns false when the command is not a
        /// real one, which is a distinct answer from "a command asking for no
        /// force": every one of these protocols leaves idle values in the slot,
        /// and the guards that reject them are load-bearing.
        ///
        /// The whole block is passed as well as the extracted DWORD because not
        /// every game confines itself to one slot.</summary>
        bool TryDecode(byte[] block, uint dword, ArcadeFfbState into);

        /// <summary>Drop any state held between commands. Called when the game
        /// goes away or the reader stops.</summary>
        void Reset();
    }

    /// <summary>The per-game decoder registry. Adding a game is adding one class
    /// and one line here.</summary>
    public static class ArcadeDecoders
    {
        private static readonly Dictionary<int, IArcadeFfbDecoder> _byId
            = new Dictionary<int, IArcadeFfbDecoder>();

        static ArcadeDecoders()
        {
            // Initial D 6, 7 and 8 share one protocol. The 7 and 8 decode files
            // in the reference are byte-identical apart from the class name, and
            // 6 differs only in the order its branches are written, which cannot
            // matter because the command values are mutually exclusive.
            Register(new InitialD8Decoder(18, "Initial D Arcade Stage 8"));
            Register(new InitialD8Decoder(17, "Initial D Arcade Stage 7"));
            Register(new InitialD8Decoder(8,  "Initial D Arcade Stage 6 AA"));

            Register(new SegaRally3Decoder());
            Register(new FordRacingDecoder());
            Register(new ArcticThunderDecoder());

            // The four-slot family. Dead Heat is WMMT3 with exactly one branch
            // changed, and Dead Heat Riders moves the spring to another slot with
            // a different divisor.
            Register(new WmmtFamilyDecoder(61, "Wangan Midnight Maximum Tune 3 / 3DX+", 6, 63.0, false));
            Register(new WmmtFamilyDecoder(62, "Dead Heat", 6, 63.0, true));
            Register(new WmmtFamilyDecoder(63, "Dead Heat Riders", 2, 500.0, true));

            // DELIBERATELY ABSENT, so the reason is recorded rather than rediscovered:
            //   Pokken Tournament (19) commands no wheel force at all. It is a stick
            //     cabinet with two vibration motors and nothing to render as torque.
            //   Storm Racer G (54) and D1GP (55) read the block for steering torque
            //     but take their surface and shake effects from game memory at
            //     hardcoded addresses. The torque half is implementable; the scale
            //     depends on an ini value whose default we could not establish, so
            //     writing it would be guessing at how strong the game meant to be.
            //   Every other arcade title FFBArcadePlugin supports reads game memory
            //     rather than this block, and is out of reach of this path entirely.
        }

        public static void Register(IArcadeFfbDecoder d)
        {
            if (d == null) return;
            _byId[d.GameId] = d;
        }

        /// <summary>The decoder for a game id, or null when we have not written
        /// one. Null is the honest answer: guessing another game's protocol would
        /// put invented force on the wheel.</summary>
        public static IArcadeFfbDecoder ForGameId(int gameId)
        {
            IArcadeFfbDecoder d;
            return _byId.TryGetValue(gameId, out d) ? d : null;
        }

        public static IEnumerable<IArcadeFfbDecoder> All => _byId.Values;
    }

    /// <summary>Initial D Arcade Stage 8 (FFBArcadePlugin game id 18).
    ///
    /// Command DWORD: byte[2] = command, byte[0] = magnitude, byte[1] = period or
    /// direction depending on the command.
    ///
    /// Three things here are counter-intuitive and all three come from the
    /// reference decode rather than from reading the bytes sensibly:
    ///   - Command 0x00 is a FLAG, not a level. It fires only on magnitude 0x01
    ///     and means a full-strength spring. Reading a level out of that byte
    ///     gives 1/127 of full scale, which is silence.
    ///   - The two constant-force directions do not share a magnitude scale. The
    ///     one that pushes right uses (128 - magnitude), so a LOW byte there is a
    ///     STRONG force. Treating it as a sign flip makes the wheel pull harder
    ///     one way than the other.
    ///   - The periodic's period byte is scaled: 0..127 maps onto 0..120 ms.
    /// </summary>
    public sealed class InitialD8Decoder : IArcadeFfbDecoder
    {
        public const byte CmdSpring         = 0x00;
        public const byte CmdConstant       = 0x04;
        public const byte CmdRumbleSine     = 0x05;
        public const byte CmdSpringVariable = 0x06;

        public InitialD8Decoder(int gameId, string name)
        {
            GameId = gameId;
            Name = name;
        }

        public string Name { get; private set; }
        public int GameId { get; private set; }
        public int SlotIndex => 2;
        public void Reset() { }

        public bool TryDecode(byte[] block, uint dword, ArcadeFfbState into)
        {
            if (into == null) return false;
            byte mag    = (byte)(dword & 0xff);
            byte param  = (byte)((dword >> 8) & 0xff);
            byte opcode = (byte)((dword >> 16) & 0xff);

            switch (opcode)
            {
                case CmdSpring:
                    if (mag != 0x01) return false;
                    into.Set(ArcadeTrigger.Spring(1.0));
                    return true;

                case CmdSpringVariable:
                    if (mag == 0 || mag >= 0x80) return false;
                    into.Set(ArcadeTrigger.Spring(mag / 127.0));
                    return true;

                case CmdConstant:
                {
                    if (mag == 0 || mag >= 0x80) return false;
                    double f;
                    if (param == 0x00)
                    {
                        // Reference passes DIRECTION_FROM_LEFT here, and a
                        // from-left direction moves the wheel RIGHT.
                        f = (128 - mag) / 127.0;
                        into.Set(ArcadeTrigger.Constant(f));
                        into.Set(ArcadeTrigger.Rumble(f, 0));
                    }
                    else if (param == 0x01)
                    {
                        f = mag / 127.0;
                        into.Set(ArcadeTrigger.Constant(-f));
                        into.Set(ArcadeTrigger.Rumble(0, f));
                    }
                    else return false;
                    return true;
                }

                case CmdRumbleSine:
                {
                    if (mag == 0 || param == 0) return false;
                    double f = mag / 127.0;
                    int periodMs = (int)Math.Round(param / 127.0 * 120.0);
                    into.Set(ArcadeTrigger.Sine(f, periodMs));
                    into.Set(ArcadeTrigger.Rumble(f, f));
                    return true;
                }

                default:
                    return false;
            }
        }
    }
}
