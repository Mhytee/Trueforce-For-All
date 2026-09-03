using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The DirectInput effect renderer behind the FFB tap: the Windows
    // runtime downloads game effects to the wheel as the mainline hidpp_ff
    // slot dialect on 0x8123, the firmware ignores them all while an ep3
    // stream is live, and this engine re-renders the parametric ones.
    //
    // Wire bytes are from the owner's G PRO trace (usb-trace.pcap,
    // 2026-08-31): AC parked, damper coefficient 0.748, and from
    // docs/di-condition-engine.md.
    //
    // Sign contract (matches the stationary spring and the classic spring
    // emulation, both hardware-validated): positive force pulls toward
    // LOWER steer; positive position/velocity = rightward. So a damper
    // under rightward motion and a spring right of center both come out
    // POSITIVE.
    public class HidppEffectEngineTests
    {
        // Test clock: ticks are milliseconds (ticksPerSecond = 1000).
        private const double TicksPerSec = 1000.0;

        private static float Eval(HidppEffectEngine e, float pos, float vel, float acc,
                                  long nowMs, out bool playing,
                                  float damperGain = 1f, float inertiaGain = 1f)
            => e.Evaluate(pos, vel, acc, damperGain, inertiaGain, nowMs, TicksPerSec, out playing);

        // Build a HID++ fn2 DOWNLOAD_EFFECT report: [rid][ff][feat][fn|sw]
        // [slot][type][len:2][delay:2][block...]
        private static byte[] Download(byte slot, byte type, ushort lenMs, ushort delayMs, params byte[] block)
        {
            var p = new byte[64];
            p[0] = 0x12; p[1] = 0xff; p[2] = 0x0e; p[3] = 0x2f;
            p[4] = slot; p[5] = type;
            p[6] = (byte)(lenMs >> 8); p[7] = (byte)lenMs;
            p[8] = (byte)(delayMs >> 8); p[9] = (byte)delayMs;
            Array.Copy(block, 0, p, 10, block.Length);
            return p;
        }

        private static byte[] ConditionBlock(ushort leftSat, short leftCoeff, ushort deadband,
                                             short center, short rightCoeff, ushort rightSat)
            => new byte[]
            {
                (byte)(leftSat >> 8), (byte)leftSat,
                (byte)((ushort)leftCoeff >> 8), (byte)leftCoeff,
                (byte)(deadband >> 8), (byte)deadband,
                (byte)((ushort)center >> 8), (byte)center,
                (byte)((ushort)rightCoeff >> 8), (byte)rightCoeff,
                (byte)(rightSat >> 8), (byte)rightSat,
            };

        // The trace's damper download, byte for byte:
        // 12 ff 0e 2f 02 87 00 00 00 00 7f ff 5f b0 00 00 00 00 5f b0 7f ff
        private static readonly byte[] AcDamper =
        {
            0x12, 0xff, 0x0e, 0x2f, 0x02, 0x87, 0x00, 0x00, 0x00, 0x00,
            0x7f, 0xff, 0x5f, 0xb0, 0x00, 0x00, 0x00, 0x00, 0x5f, 0xb0, 0x7f, 0xff, 0x00, 0x00,
        };

        [Fact]
        public void AcDamper_TraceBytes_DecodeAndPlay()
        {
            var e = new HidppEffectEngine();
            Assert.True(e.HandleDownload(AcDamper, 0, AcDamper.Length, 0));
            Assert.True(e.AnyPlaying);
            Assert.True(e.AnyDamperPlayingAt(10, TicksPerSec));
            Assert.Equal(1, e.ParametricDownloads);
        }

        [Fact]
        public void Damper_OpposesMotion_SignContract()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f };   // raw math
            e.HandleDownload(AcDamper, 0, AcDamper.Length, 0);
            // Moving right at 1 range/s: pull left (positive), coeff 0.748.
            float f = Eval(e, 0f, 1f, 0f, 10, out bool playing);
            Assert.True(playing);
            Assert.InRange(f, 0.72f, 0.78f);
            // Moving left: pull right (negative), symmetric.
            Assert.InRange(Eval(e, 0f, -1f, 0f, 10, out _), -0.78f, -0.72f);
            // Still wheel: a damper commands nothing.
            Assert.Equal(0f, Eval(e, 0.5f, 0f, 0f, 10, out _), 3);
        }

        [Fact]
        public void ConstantType_IsRefused_ScalarPathOwnsIt()
        {
            var e = new HidppEffectEngine();
            var dl = Download(1, 0x80 /* constant + autostart */, 0, 0, 0x7f, 0xff);
            Assert.False(e.HandleDownload(dl, 0, dl.Length, 0));
            Assert.False(e.AnyPlaying);
        }

        [Fact]
        public void Spring_PullsTowardCenter_WithDeadbandAndSaturation()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f };   // raw math
            // coeff 0.5 both sides, deadband 0.1 half-width, center 0, sat 0.3.
            var dl = Download(0, (byte)(HidppEffectEngine.TypeSpring | 0x80), 0, 0,
                ConditionBlock(leftSat: 0x2666, leftCoeff: 0x4000, deadband: 0x0ccc,
                               center: 0, rightCoeff: 0x4000, rightSat: 0x2666));
            Assert.True(e.HandleDownload(dl, 0, dl.Length, 0));
            // Right of the band: pulls left. dev 0.5 - 0.1 = 0.4 x 0.5 = 0.2.
            Assert.InRange(Eval(e, 0.5f, 0f, 0f, 10, out _), 0.18f, 0.22f);
            // Left of the band mirrors.
            Assert.InRange(Eval(e, -0.5f, 0f, 0f, 10, out _), -0.22f, -0.18f);
            // Inside the deadband: zero (but the effect is playing).
            Assert.Equal(0f, Eval(e, 0.05f, 0f, 0f, 10, out bool playing), 3);
            Assert.True(playing);
            // Far out: saturation clamps at 0.3.
            Assert.InRange(Eval(e, 1f, 0f, 0f, 10, out _), 0.28f, 0.31f);
        }

        [Fact]
        public void NoAutostart_WaitsForPlay_ThenStopsAndDestroys()
        {
            var e = new HidppEffectEngine();
            var dl = Download(3, HidppEffectEngine.TypeDamper, 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));
            e.HandleDownload(dl, 0, dl.Length, 0);
            Assert.False(e.AnyPlaying);
            e.HandleSetState(3, HidppEffectEngine.StatePlay, 0);
            Assert.True(e.AnyPlaying);
            e.HandleSetState(3, HidppEffectEngine.StateStop, 5);
            Assert.False(e.AnyPlaying);
            e.HandleSetState(3, HidppEffectEngine.StatePlay, 10);
            Assert.True(e.AnyPlaying);
            e.HandleDestroy(3);
            Assert.False(e.AnyPlaying);
        }

        [Fact]
        public void ResetAll_ClearsEverything()
        {
            var e = new HidppEffectEngine();
            e.HandleDownload(AcDamper, 0, AcDamper.Length, 0);
            Assert.True(e.AnyPlaying);
            e.ResetAll();
            Assert.False(e.AnyPlaying);
            Assert.Equal(0f, Eval(e, 0f, 1f, 0f, 10, out bool playing), 4);
            Assert.False(playing);
        }

        [Fact]
        public void GlobalGain_ScalesTheSum()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f };   // raw math
            e.HandleDownload(AcDamper, 0, AcDamper.Length, 0);
            float full = Eval(e, 0f, 1f, 0f, 10, out _);
            e.HandleSetGain(0x7fff);   // 50 %
            float half = Eval(e, 0f, 1f, 0f, 10, out _);
            Assert.InRange(half / full, 0.48f, 0.52f);
        }

        [Fact]
        public void FiniteDuration_ExpiresWithoutAStop()
        {
            var e = new HidppEffectEngine();
            var dl = Download(0, (byte)(HidppEffectEngine.TypeDamper | 0x80), lenMs: 100, delayMs: 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));
            e.HandleDownload(dl, 0, dl.Length, nowTicks: 0);
            Assert.NotEqual(0f, Eval(e, 0f, 1f, 0f, 50, out bool p1));
            Assert.True(p1);
            Eval(e, 0f, 1f, 0f, 250, out bool p2);
            Assert.False(p2);
        }

        [Fact]
        public void Delay_HoldsTheStart()
        {
            var e = new HidppEffectEngine();
            var dl = Download(0, (byte)(HidppEffectEngine.TypeDamper | 0x80), 0, delayMs: 100,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));
            e.HandleDownload(dl, 0, dl.Length, 0);
            Eval(e, 0f, 1f, 0f, 50, out bool before);
            Assert.False(before);
            Eval(e, 0f, 1f, 0f, 150, out bool after);
            Assert.True(after);
        }

        [Fact]
        public void PeriodicSine_RendersTheWave()
        {
            var e = new HidppEffectEngine { WaveformMaxStepPerMs = 0f };
            // magnitude 0.5, no offset, period 1000 ms, phase 0.
            var dl = Download(0, (byte)(HidppEffectEngine.TypeSine | 0x80), 0, 0,
                0x40, 0x00,   // magnitude 0.5
                0x00, 0x00,   // offset 0
                0x03, 0xe8,   // period 1000 ms
                0x00, 0x00,   // phase 0
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00);   // no envelope
            e.HandleDownload(dl, 0, dl.Length, 0);
            // Quarter period: sin = 1 -> 0.5.
            Assert.InRange(Eval(e, 0f, 0f, 0f, 250, out _), 0.48f, 0.52f);
            // Half period: sin = 0.
            Assert.InRange(Eval(e, 0f, 0f, 0f, 500, out _), -0.02f, 0.02f);
            // Three quarters: -0.5.
            Assert.InRange(Eval(e, 0f, 0f, 0f, 750, out _), -0.52f, -0.48f);
        }

        // Each effect family carries its own render gain so the test bench
        // can tune one at a time (rig 2026-09-01: the bench's single slider
        // showed the same number for every effect). Damper and inertia are
        // separate numbers even though they start equal.
        [Fact]
        public void PeriodicGain_ScalesWaveformsOnly()
        {
            var e = new HidppEffectEngine { WaveformMaxStepPerMs = 0f };
            var dl = Download(0, (byte)(HidppEffectEngine.TypeSine | 0x80), 0, 0,
                0x40, 0x00, 0x00, 0x00, 0x03, 0xe8, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
            e.HandleDownload(dl, 0, dl.Length, 0);
            // Quarter period at half gain: 0.5 x 0.5.
            float half = e.Evaluate(0f, 0f, 0f, 1f, 1f, 250, TicksPerSec, out _,
                                    velTermSign: 1, springGain: 1f, frictionGain: 1f,
                                    periodicGain: 0.5f);
            Assert.InRange(half, 0.24f, 0.26f);
            // The condition gains do not touch a waveform.
            float condGains = e.Evaluate(0f, 0f, 0f, 0f, 0f, 1250, TicksPerSec, out _,
                                         velTermSign: 1, springGain: 0f, frictionGain: 0f);
            Assert.InRange(condGains, 0.48f, 0.52f);
        }

        [Fact]
        public void RampGain_ScalesTheRamp()
        {
            var e = new HidppEffectEngine { WaveformMaxStepPerMs = 0f };
            // Ramp 0 -> 1 over 1000 ms, no envelope.
            var dl = Download(0, (byte)(HidppEffectEngine.TypeRamp | 0x80), 1000, 0,
                0x00, 0x00,   // start 0
                0x7f, 0xff,   // end 1.0
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
            e.HandleDownload(dl, 0, dl.Length, 0);
            float full = e.Evaluate(0f, 0f, 0f, 1f, 1f, 500, TicksPerSec, out _);
            Assert.InRange(full, 0.48f, 0.52f);
            float quarter = e.Evaluate(0f, 0f, 0f, 1f, 1f, 501, TicksPerSec, out _,
                                       velTermSign: 1, springGain: 1f, frictionGain: 1f,
                                       periodicGain: 1f, rampGain: 0.5f);
            Assert.InRange(quarter, 0.24f, 0.26f);
        }

        [Fact]
        public void InertiaGain_IsIndependentOfTheDamperGain()
        {
            // Acceleration mode: the two are separate quantities there. In
            // damping mode both read velocity, so keeping them apart is a
            // question of gains rather than of inputs.
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f, InertiaAsDamping = false };
            var damper = Download(1, (byte)(HidppEffectEngine.TypeDamper | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));   // coeff 0.5
            var inertia = Download(2, (byte)(HidppEffectEngine.TypeInertia | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));
            e.HandleDownload(damper, 0, damper.Length, 0);
            e.HandleDownload(inertia, 0, inertia.Length, 0);
            // Velocity 1, acceleration 0: only the damper contributes.
            Assert.InRange(Eval(e, 0f, 1f, 0f, 10, out _, damperGain: 1f, inertiaGain: 0f),
                           0.49f, 0.51f);
            // Acceleration 1, velocity 0: only inertia, and its own gain
            // scales it while the damper's is zero.
            Assert.InRange(Eval(e, 0f, 0f, 1f, 11, out _, damperGain: 0f, inertiaGain: 1f),
                           0.49f, 0.51f);
        }

        // Inertia has two readings and the bench A/Bs them. Lossless (the
        // DirectInput spec) stores energy and hands it back while the wheel
        // slows, so it coasts on; lossy (what the G PRO firmware does, rig
        // A/B 2026-09-01) only ever resists. They differ ONLY while the
        // wheel is decelerating.
        [Fact]
        public void Inertia_LossyFormNeverPushesAlongTravel()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f, InertiaCoasts = false, InertiaAsDamping = false };
            var dl = Download(0, (byte)(HidppEffectEngine.TypeInertia | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));   // coeff 0.5
            e.HandleDownload(dl, 0, dl.Length, 0);

            // Speeding up rightward (vel +, accel +): resists, so a positive
            // term that pulls toward lower steer survives untouched.
            float resisting = Eval(e, 0f, 1f, 1f, 10, out _, inertiaGain: 1f);
            Assert.InRange(resisting, 0.49f, 0.51f);

            // Slowing down while still moving rightward (vel +, accel -):
            // the lossless form would sustain the motion. Lossy must not.
            float sustaining = Eval(e, 0f, 1f, -1f, 11, out _, inertiaGain: 1f);
            Assert.InRange(sustaining, -0.02f, 0.02f);
        }

        [Fact]
        public void Inertia_LosslessFormCoastsTheWheelOn()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f, InertiaCoasts = true, InertiaAsDamping = false };
            var dl = Download(0, (byte)(HidppEffectEngine.TypeInertia | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));
            e.HandleDownload(dl, 0, dl.Length, 0);
            // Same decelerating state: the spec reading hands the energy back
            // as a torque along travel (negative = toward higher steer).
            float sustaining = Eval(e, 0f, 1f, -1f, 10, out _, inertiaGain: 1f);
            Assert.InRange(sustaining, -0.51f, -0.49f);
        }

        [Fact]
        public void Inertia_LossyFormFadesSmoothlyThroughZeroVelocity()
        {
            // A hard gate on sign(velocity) would chatter where the wheel
            // dithers around zero: the start of a push.
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f, InertiaCoasts = false, InertiaAsDamping = false };
            var dl = Download(0, (byte)(HidppEffectEngine.TypeInertia | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));
            e.HandleDownload(dl, 0, dl.Length, 0);
            float prev = float.NaN;
            for (int i = 0; i <= 20; i++)
            {
                float vel = -0.05f + i * 0.005f;      // sweeps through zero
                float now = Eval(e, 0f, vel, -1f, 10 + i, out _, inertiaGain: 1f);
                if (!float.IsNaN(prev))
                    Assert.True(Math.Abs(now - prev) < 0.10f,
                        $"inertia stepped {Math.Abs(now - prev):F3} at vel {vel:F3}");
                prev = now;
            }
        }

        // The frame sign has to reach the velocity InertiaTerm judges
        // against, not just the term it scales. With only the term negated,
        // a flipped-frame rig faded out the resisting half and kept the
        // sustaining half: inertia that pushes motion along instead of
        // opposing it.
        [Fact]
        public void Inertia_LossyFormStillResists_OnAFlippedFrame()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f, InertiaCoasts = false, InertiaAsDamping = false };
            var dl = Download(0, (byte)(HidppEffectEngine.TypeInertia | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));   // coeff 0.5
            e.HandleDownload(dl, 0, dl.Length, 0);

            // Speeding up: the term must survive, whichever way the frame runs.
            float normal = e.Evaluate(0f, 1f, 1f, 1f, 1f, 10, TicksPerSec, out _, velTermSign: 1);
            float flipped = e.Evaluate(0f, 1f, 1f, 1f, 1f, 11, TicksPerSec, out _, velTermSign: -1);
            Assert.InRange(Math.Abs(normal), 0.49f, 0.51f);
            Assert.InRange(Math.Abs(flipped), 0.49f, 0.51f);
            Assert.True(normal * flipped < 0, "the frame sign must reach the output");

            // Slowing down: the lossy form must stay silent on BOTH frames.
            Assert.InRange(e.Evaluate(0f, 1f, -1f, 1f, 1f, 12, TicksPerSec, out _, velTermSign: 1),
                           -0.02f, 0.02f);
            Assert.InRange(e.Evaluate(0f, 1f, -1f, 1f, 1f, 13, TicksPerSec, out _, velTermSign: -1),
                           -0.02f, 0.02f);
        }

        // The calibrated gain must scale the CONSTANT part of friction and
        // leave the stability term alone: native DirectInput friction has no
        // velocity component, so a gain that scales one would be matching
        // total force at a single speed and missing everywhere else.
        [Fact]
        public void FrictionGain_ScalesCoulombOnly_NotTheStabilityTerm()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f };
            var dl = Download(0, (byte)(HidppEffectEngine.TypeFriction | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x2000, 0, 0, 0x2000, 0x7fff));   // coeff 0.25
            e.HandleDownload(dl, 0, dl.Length, 0);

            // Coefficient and gains chosen to stay clear of saturation: the
            // clamp applies to the SUM, so a clipped term would mask the
            // thing being tested.
            // Slide well past the lock distance so frac saturates at 1, then
            // read the term at a standstill and at speed.
            Eval(e, 0f, 0f, 0f, 10, out _);
            Eval(e, 0.5f, 0f, 0f, 11, out _);

            float g1still = e.Evaluate(0.5f, 0f, 0f, 1f, 1f, 12, TicksPerSec, out _,
                                       velTermSign: 1, springGain: 1f, frictionGain: 1f);
            float g2still = e.Evaluate(0.5f, 0f, 0f, 1f, 1f, 13, TicksPerSec, out _,
                                       velTermSign: 1, springGain: 1f, frictionGain: 1.5f);
            // Standing still there is only Coulomb, so the gain is the whole story.
            Assert.InRange(g2still / g1still, 1.45f, 1.55f);

            // Moving, the stability term is present and must NOT have doubled.
            float g1move = e.Evaluate(0.5f, 1f, 0f, 1f, 1f, 14, TicksPerSec, out _,
                                      velTermSign: 1, springGain: 1f, frictionGain: 1f);
            float g2move = e.Evaluate(0.5f, 1f, 0f, 1f, 1f, 15, TicksPerSec, out _,
                                      velTermSign: 1, springGain: 1f, frictionGain: 1.5f);
            float coulombDelta = g2still - g1still;
            // The whole difference between the two gains is Coulomb: the
            // velocity term contributes identically at both gains.
            Assert.InRange(g2move - g1move, coulombDelta * 0.95f, coulombDelta * 1.05f);
        }

        // The wheel's own firmware renders inertia as damping (owner, by
        // feel, twice), so that is the default: matching the wheel is the
        // point, and it also drops the grain that came of building an effect
        // on a second derivative.
        [Fact]
        public void InertiaRendersAsDampingByDefault()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f };
            var dl = Download(0, (byte)(HidppEffectEngine.TypeInertia | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));   // coeff 0.5
            e.HandleDownload(dl, 0, dl.Length, 0);

            // Opposes VELOCITY, and does not care about acceleration at all.
            Assert.InRange(Eval(e, 0f, 1f, 0f, 10, out _, inertiaGain: 1f), 0.49f, 0.51f);
            Assert.InRange(Eval(e, 0f, -1f, 0f, 11, out _, inertiaGain: 1f), -0.51f, -0.49f);
            Assert.Equal(0f, Eval(e, 0f, 0f, 5f, 12, out _, inertiaGain: 1f), 3);
        }

        // A sawtooth resets by a full-scale step in a single tick. Rendered
        // raw at 1 kHz that asks the motor to reverse its whole output
        // instantly, and it answers with a click at the peak that the
        // firmware's own rendering does not make (owner, 2026-09-03). The
        // waveform filter exists to round that edge, and it must round it
        // without flattening the ramp that leads up to it.
        [Fact]
        public void ASawtoothResetIsRounded_ButTheRampIsNot()
        {
            var e = new HidppEffectEngine();       // default waveform filter
            // Magnitude 1.0, period 100 ms, no offset or envelope.
            var dl = Download(0, (byte)(HidppEffectEngine.TypeSawtoothUp | 0x80), 0, 0,
                0x7f, 0xff, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
            e.HandleDownload(dl, 0, dl.Length, 0);

            // Walk a full cycle a millisecond at a time and find the largest
            // single-tick jump anywhere in it.
            float prev = Eval(e, 0f, 0f, 0f, 1, out _);
            float biggestStep = 0f;
            for (long ms = 2; ms <= 200; ms++)
            {
                float now = Eval(e, 0f, 0f, 0f, ms, out _);
                biggestStep = Math.Max(biggestStep, Math.Abs(now - prev));
                prev = now;
            }
            // Unfiltered the reset is a step of 2.0 (from +1 to -1). Rounded
            // over about a millisecond it is a fraction of that.
            Assert.True(biggestStep < 0.5f,
                $"the sawtooth reset still steps {biggestStep:F2} in one tick");

            // And the ramp itself still gets where it is going: sampled over
            // most of a cycle it must still sweep nearly the full range.
            var raw = new HidppEffectEngine { WaveformMaxStepPerMs = 0f };
            raw.HandleDownload(dl, 0, dl.Length, 0);
            float lo = 1f, hi = -1f;
            for (long ms = 210; ms <= 300; ms++)
            {
                float v = Eval(e, 0f, 0f, 0f, ms, out _);
                lo = Math.Min(lo, v); hi = Math.Max(hi, v);
            }
            Assert.True(hi - lo > 1.5f, $"the ramp was flattened to {hi - lo:F2}");
        }

        // Slots are freed only by an explicit destroy, so a game that
        // re-creates its effects leaves the old ones playing. Every plugin
        // off/on makes a game do that, and summing the leftovers grew the
        // rendered damper with each cycle (owner, in game, 2026-09-03). One
        // live copy per condition type, newest wins.
        [Fact]
        public void OnlyTheNewestCopyOfAConditionSurvives()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f };
            var block = ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff);   // coeff 0.5

            // The game creates a damper, then re-creates it twice at fresh
            // slots without ever destroying the first two.
            for (byte slot = 1; slot <= 3; slot++)
            {
                var dl = Download(slot, (byte)(HidppEffectEngine.TypeDamper | 0x80), 0, 0, block);
                e.HandleDownload(dl, 0, dl.Length, slot);
            }

            // One damper's worth of force, not three.
            Assert.InRange(Eval(e, 0f, 1f, 0f, 10, out bool playing), 0.49f, 0.51f);
            Assert.True(playing);

            // Destroying it leaves NOTHING. The older copies were our own
            // missed destroys, not game state: a game that ends an effect
            // wants it ended and will ask again if it wants it back. Reviving
            // a stale copy would keep a force on the wheel that the game had
            // explicitly stopped.
            e.HandleDestroy(3);
            Assert.Equal(0f, Eval(e, 0f, 1f, 0f, 11, out bool stillPlaying), 3);
            Assert.False(stillPlaying);

            // Periodics are NOT deduped: a game builds rumble from several at
            // once and the firmware sums them too.
            var p = new HidppEffectEngine { WaveformMaxStepPerMs = 0f };
            for (byte slot = 1; slot <= 2; slot++)
            {
                var dl = Download(slot, (byte)(HidppEffectEngine.TypeSquare | 0x80), 0, 0,
                    0x40, 0x00, 0x00, 0x00, 0x03, 0xe8, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
                p.HandleDownload(dl, 0, dl.Length, 0);
            }
            // Two squares at magnitude 0.5 sum to 1.0.
            Assert.InRange(Math.Abs(Eval(p, 0f, 0f, 0f, 100, out _)), 0.9f, 1.1f);
        }

        [Fact]
        public void Friction_StickSlipLockPoint()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f };   // raw math
            var dl = Download(0, (byte)(HidppEffectEngine.TypeFriction | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x2000, 0, 0, 0x2000, 0x7fff));   // coeff 0.25
            e.HandleDownload(dl, 0, dl.Length, 0);
            // (Lock distance 0.025; still velocity, so the in-zone
            // dissipation term contributes nothing here.)
            // First sample seeds the lock point: no force out of nowhere.
            Assert.Equal(0f, Eval(e, 0f, 0f, 0f, 10, out _), 3);
            // Half the lock distance right of the lock: half the coefficient,
            // pulling left (positive).
            Assert.InRange(Eval(e, 0.0125f, 0f, 0f, 11, out _), 0.115f, 0.135f);
            // Far past the distance: the lock drags along, force saturates at
            // the full coefficient (static friction while pushed).
            Assert.InRange(Eval(e, 0.1f, 0f, 0f, 12, out _), 0.24f, 0.26f);
            // Reversing direction flips the force within one lock distance:
            // kinetic friction opposes the NEW direction, no chatter step.
            Assert.InRange(Eval(e, 0.03f, 0f, 0f, 13, out _), -0.26f, -0.24f);
            // Easing back rightward across the dragged lock point (now at
            // 0.055): a proportional opposing torque, positive again, with no
            // discontinuity. A sign(velocity) model would chatter here; the
            // lock point renders the static-friction transition smoothly.
            float back = Eval(e, 0.0675f, 0f, 0f, 14, out _);
            Assert.InRange(back, 0.115f, 0.135f);
        }

        [Fact]
        public void ConditionOutput_LowPass_SmoothsASuddenStep()
        {
            var e = new HidppEffectEngine();   // default 200 Hz cutoff
            e.HandleDownload(AcDamper, 0, AcDamper.Length, 0);
            // First evaluate seeds the filter (pass-through), still wheel.
            Assert.Equal(0f, Eval(e, 0f, 0f, 0f, 10, out _), 3);
            // 1 ms later the velocity steps to full: the filtered output must
            // land clearly below the raw 0.748 term but above zero.
            float filtered = Eval(e, 0f, 1f, 0f, 11, out _);
            Assert.InRange(filtered, 0.15f, 0.60f);
            // And converge upward on the next millisecond.
            float next = Eval(e, 0f, 1f, 0f, 12, out _);
            Assert.True(next > filtered);
        }

        [Fact]
        public void Envelope_IsInvalid_OnInfiniteDurationEffects()
        {
            var e = new HidppEffectEngine();
            // Infinite sine with a 500 ms attack from level 0: the attack must
            // be IGNORED (PID/OpenFFBoard semantics), so a quarter period in,
            // the wave is at full magnitude.
            var dl = Download(0, (byte)(HidppEffectEngine.TypeSine | 0x80), 0, 0,
                0x40, 0x00,   // magnitude 0.5
                0x00, 0x00,   // offset 0
                0x03, 0xe8,   // period 1000 ms
                0x00, 0x00,   // phase 0
                0x00, 0x01, 0xf4, 0x00, 0x00, 0x00);   // attackLevel 0, attackMs 500
            e.HandleDownload(dl, 0, dl.Length, 0);
            Assert.InRange(Eval(e, 0f, 0f, 0f, 250, out _), 0.48f, 0.52f);
        }

        [Fact]
        public void VelTermSign_FlipsDamper_NeverTheSpring()
        {
            var e = new HidppEffectEngine { ConditionOutputCutoffHz = 0f };
            var spring = Download(1, (byte)(HidppEffectEngine.TypeSpring | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));   // coeff 0.5
            var damper = Download(2, (byte)(HidppEffectEngine.TypeDamper | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));   // coeff 0.5
            e.HandleDownload(spring, 0, spring.Length, 0);
            e.HandleDownload(damper, 0, damper.Length, 0);
            // pos 0.5 -> spring +0.25; vel 1 -> damper +0.5.
            float normal = e.Evaluate(0.5f, 1f, 0f, 1f, 1f, 10, TicksPerSec, out _, velTermSign: 1);
            Assert.InRange(normal, 0.72f, 0.78f);
            // Flipped: damper -0.5, spring STAYS +0.25 (an anti-damper fix
            // must not create an anti-spring).
            float flipped = e.Evaluate(0.5f, 1f, 0f, 1f, 1f, 11, TicksPerSec, out _, velTermSign: -1);
            Assert.InRange(flipped, -0.28f, -0.22f);
        }

        [Fact]
        public void ExpiredEffect_IsNotLive_ForTheGates()
        {
            var e = new HidppEffectEngine();
            var dl = Download(0, (byte)(HidppEffectEngine.TypeDamper | 0x80), lenMs: 100, delayMs: 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));
            e.HandleDownload(dl, 0, dl.Length, 0);
            Assert.True(e.AnyDamperPlayingAt(50, TicksPerSec));
            Assert.True(e.AnyPlayingAt(50, TicksPerSec));
            // Past its duration, with no stop/destroy ever arriving, the
            // gates must read dead (an expired decoded damper used to pin the
            // CSP fallback damper off; audit 2026-09-01).
            Assert.False(e.AnyDamperPlayingAt(250, TicksPerSec));
            Assert.False(e.AnyPlayingAt(250, TicksPerSec));
        }

        [Fact]
        public void ProvisionalSlot_MovesOnWheelReply()
        {
            var e = new HidppEffectEngine();
            // New effect: slot byte 0. Engine places it provisionally.
            var dl = Download(0, (byte)(HidppEffectEngine.TypeDamper | 0x80), 0, 0,
                ConditionBlock(0x7fff, 0x4000, 0, 0, 0x4000, 0x7fff));
            e.HandleDownload(dl, 0, dl.Length, 0);
            Assert.True(e.AnyPlaying);
            // The wheel says it landed in slot 5.
            e.AssignSlotFromReply(5);
            // State addressed to slot 5 must now find it.
            e.HandleSetState(5, HidppEffectEngine.StateStop, 10);
            Assert.False(e.AnyPlaying);
        }

        [Fact]
        public void Redownload_ToSameSlot_KeepsPlayingWithNewParams()
        {
            var e = new HidppEffectEngine();
            e.HandleDownload(AcDamper, 0, AcDamper.Length, 0);
            float before = Eval(e, 0f, 1f, 0f, 10, out _);
            // AC re-sends the damper with a lower coefficient (rolling).
            var weaker = Download(2, 0x87, 0, 0,
                ConditionBlock(0x7fff, 0x0800, 0, 0, 0x0800, 0x7fff));   // coeff 0.0625
            e.HandleDownload(weaker, 0, weaker.Length, 20);
            float after = Eval(e, 0f, 1f, 0f, 30, out bool playing);
            Assert.True(playing);
            Assert.True(after < before * 0.2f);
        }

        [Fact]
        public void UnknownType_IsCountedAndIgnored()
        {
            var e = new HidppEffectEngine();
            var dl = Download(0, 0x8f, 0, 0, 0x11, 0x22);
            Assert.False(e.HandleDownload(dl, 0, dl.Length, 0));
            Assert.False(e.AnyPlaying);
            Assert.Equal(1, e.UnknownTypeDownloads);
        }
    }
}
