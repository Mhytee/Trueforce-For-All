using System;
using TrueforceForAll.Core;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The auto-tuner against a simulated wheel with KNOWN firmware
    // constants: if the pulse/decay/frequency identification is right, the
    // recovered gains must land on the ground-truth ratios regardless of
    // the wheel's friction and inertia (they cancel by design).
    public class WheelAutoTunerTests
    {
        // Simulated wheel physics (units: pos -1..1, vel range/s, torque as
        // force fraction -1..1).
        private const double J = 0.02;          // inertia
        private const double Coulomb = 0.05;    // the wheel's own friction torque

        // "Firmware" scales (ground truth the tuner must recover):
        // Damper strong enough to match the rig. The original 0.06 was a
        // guess made when we still believed the damped coast was long; the
        // wheel then showed a coast over in about 0.1 s (2026-09-02), which
        // is roughly thirteen times more damping per unit inertia. A sim that
        // weak takes two thirds of a second to reach a terminal speed and so
        // could never exercise the steady-state damper measurement honestly.
        private const double KfwDamper   = 0.80;   // torque per (coeff x vel)
        private const double KfwSpring   = 2.0;    // torque per (coeff x pos)
        private const double KfwFriction = 0.05;   // torque per coeff, opposing motion
        // Engine-side chain scale (the FfbScale-like attenuation between our
        // commanded cur and delivered torque):
        private const double SChain = 0.8;
        // The engine's own model scales at the initial gains:
        private const double G0Damper = 0.25, G0Spring = 1.0, G0Friction = 1.0;

        private sealed class SimState
        {
            public WheelAutoTuner.PhaseConfig Cfg = new WheelAutoTuner.PhaseConfig();
            public System.Collections.Generic.List<string> Log;
            // The firmware effect can be stopped and restarted for one trial
            // so friction can measure its own baseline alongside the loaded
            // trials instead of borrowing one from another phase.
            public bool NativeEffectOn = true;
            // Strength of the centring spring the tuner has asked the
            // firmware to hold, 0 when none. The firmware renders it in the
            // WHEEL's frame, so it centres correctly no matter which way the
            // simulated chain runs.
            public int    CenterSpringPct;
            public double CenterSpringAt;
            public int    CenterDampPct;
            public double NativeCmd;
        }

        // engineSign models the sign of the plant the RENDERER closes its
        // loop through: a positive cur command driving the measured velocity
        // negative (-1) is the case the renderer's convention was authored
        // for, and the case the rig measured on 2026-09-01, so it is the
        // default here. It is also the only value consistent with the way
        // this sim applies the engine's effects (-deff x vel, always
        // opposing): with +1 the command path and the effect path would
        // disagree about which way the chain points.
        private static WheelAutoTuner RunFullSimulation(out SimState sim, int nativeSign = 1,
                                                        int engineSign = -1,
                                                        double coulombScale = 1.0,
                                                        double velQuantum = 0.0,
                                                        double coulombDriftPerSec = 0.0,
                                                        double effectScale = 1.0)
        {
            var s = new SimState();
            sim = s;
            WheelAutoTuner tuner = null;
            var log = new System.Collections.Generic.List<string>();
            tuner = new WheelAutoTuner(G0Damper, G0Spring, G0Friction,
                cfg => { s.Cfg = cfg; s.NativeEffectOn = true; },
                cmd => s.NativeCmd = cmd,
                log.Add,
                on => s.NativeEffectOn = on,
                (pct, damp, at) =>
                { s.CenterSpringPct = pct; s.CenterDampPct = damp; s.CenterSpringAt = at; return true; });
            s.Log = log;

            double pos = 0.05, vel = 0, t = 0;
            const double dt = 0.001;
            for (int step = 0; step < 400_000 && tuner.Running || tuner.Current == WheelAutoTuner.Phase.Idle; step++)
            {
                if (!tuner.Running && tuner.Current != WheelAutoTuner.Phase.Idle) break;
                if (step >= 400_000) break;

                bool native = s.Cfg.Native;
                string kind = s.Cfg.EffectKind;
                int pct = s.Cfg.EffectPct;
                double coeff = pct / 100.0;

                // Drive torque: native DI constant or the engine's command
                // through the chain. nativeSign models a wheel whose
                // DirectInput direction frame opposes the assumed one (the
                // owner's G PRO): the tuner's DirProbe must detect and
                // compensate it.
                double torque = native ? s.NativeCmd * nativeSign : tuner.EngineCommand * SChain * engineSign;

                // The effect under test. On the ENGINE side it is rendered by
                // the plugin, which only renders it while the tuner says it
                // should be acting: during the pulse and the measurement,
                // never while re-centering. The native side is a firmware
                // effect that stays loaded throughout.
                bool acting = native ? s.NativeEffectOn : tuner.EffectEngaged;
                double keff = 0, deff = 0, feff = 0, springAt = 0;
                if (kind == "DAMPER")
                    deff = effectScale * (native ? coeff * KfwDamper : coeff * G0Damper * SChain);
                else if (kind == "SPRING")
                    keff = effectScale * (native ? coeff * KfwSpring : coeff * G0Spring * SChain);
                else if (kind == "FRICTION")
                    // Engine friction holds at coeff x gain x chain of FULL
                    // scale (the real renderer; the earlier x KfwFriction
                    // made the sim 25x weaker than reality and masked the
                    // friction-phase stall the audit found).
                    feff = effectScale * (native ? coeff * KfwFriction : coeff * G0Friction * SChain);

                if (!acting) { keff = 0; deff = 0; feff = 0; }

                // The centring spring replaces the phase effect while it is
                // held, and it pulls toward centre regardless of which way the
                // chain runs: that is the whole point of asking the wheel to
                // do the centring rather than steering it ourselves.
                if (s.CenterSpringPct > 0)
                {
                    deff = 0; feff = 0;
                    keff = (s.CenterSpringPct / 100.0) * KfwSpring;
                    deff = (s.CenterDampPct / 100.0) * KfwDamper;
                    springAt = s.CenterSpringAt;
                }
                double net = torque - deff * vel - keff * (pos - springAt);
                // The wheel's own friction is allowed to drift over the run.
                // Whatever the physical cause, a baseline measured minutes
                // before the thing it is subtracted from inherits the whole
                // difference, and friction is the one gain built that way.
                double wheelFric = Coulomb * coulombScale * (1 + coulombDriftPerSec * t);
                if (wheelFric < 0) wheelFric = 0;
                double fricTotal = wheelFric + feff;
                if (Math.Abs(vel) < 1e-3 && Math.Abs(net) <= fricTotal)
                {
                    vel = 0;   // stiction holds
                }
                else
                {
                    net -= fricTotal * Math.Sign(vel == 0 ? net : vel);
                    vel += (net / J) * dt;
                }
                pos += vel * dt;
                if (pos > 1) { pos = 1; vel = 0; }
                if (pos < -1) { pos = -1; vel = 0; }

                t += dt;
                // A real encoder reports velocity in steps. It is what makes
                // an ill-conditioned fit misbehave: collinear regressors do
                // not merely fit loosely, they amplify whatever noise is
                // there into the coefficients.
                double reported = velQuantum > 0
                    ? Math.Round(vel / velQuantum) * velQuantum : vel;
                tuner.Sample(pos, reported, t);
            }
            return tuner;
        }

        [Fact]
        public void FullRun_RecoversTheFirmwareScales()
        {
            var tuner = RunFullSimulation(out SimState st);
            Assert.True(tuner.Current == WheelAutoTuner.Phase.Done,
                "aborted: " + tuner.AbortReason + " :: " + string.Join(" | ", st.Log));

            // Force ratio: native pulses deliver 1x, engine pulses SChain
            // -> measured ratio 1/SChain = 1.25.
            Assert.InRange(tuner.MeasuredForceRatio, 1.05, 1.5);

            // Damper: expected gain = KfwDamper / SChain = 0.075.
            // Tight, because a steady-state measurement should be: the coast
            // fit this replaced needed a 0.6..1.5 band just to pass.
            double expectedDamper = KfwDamper / SChain;
            Assert.True(tuner.DamperGain >= expectedDamper * 0.85 && tuner.DamperGain <= expectedDamper * 1.18, $"damper {tuner.DamperGain:F3} vs {expectedDamper:F3}; dragN {tuner.MeasuredDragNative:F4} dragE {tuner.MeasuredDragEngine:F4}; ratio {tuner.MeasuredForceRatio:F3}");

            // Spring: expected gain = KfwSpring / SChain = 2.5 (from the
            // frequency ratio squared; inertia cancels).
            double expectedSpring = KfwSpring / SChain;
            Assert.InRange(tuner.SpringGain, expectedSpring * 0.6, expectedSpring * 1.5);

            // Friction: native holds coeff x KfwFriction, the engine holds
            // coeff x gain x SChain of full scale, so the matching gain is
            // KfwFriction / SChain = 0.0625.
            double expectedFriction = KfwFriction / SChain;
            Assert.True(!double.IsNaN(tuner.FrictionGain), string.Join(" | ", st.Log));
            Assert.InRange(tuner.FrictionGain, expectedFriction * 0.5, expectedFriction * 2.0);

            // The render loop's sign is measured, and on a chain that
            // inverts (the rig's case) it is the CORRECT one: nothing is
            // flagged suspect.
            Assert.True(tuner.RenderSignMeasured);
            Assert.True(tuner.RenderSignCorrect);
            Assert.False(tuner.ResultsSuspect);
        }

        // The genuine fault the probe exists to catch: a chain where a
        // positive stream command drives the measured velocity POSITIVE
        // makes the renderer's +k x velocity positive feedback, so the
        // damper pushes motion along. Gains measured under that are not to
        // be trusted.
        [Fact]
        public void NonInvertingChain_IsFlaggedAsPositiveFeedback()
        {
            var tuner = RunFullSimulation(out _, engineSign: 1);
            Assert.True(tuner.RenderSignMeasured);
            Assert.False(tuner.RenderSignCorrect);
            Assert.True(tuner.ResultsSuspect);
        }

        // The damper gain is a DIFFERENCE of coast rates, and Coulomb
        // friction is a constant torque that is not part of the damper being
        // measured. The old exponential fit folded it into the rate, so the
        // answer moved with which band of speed the trial happened to cover
        // (rig x3, 2026-09-02: 115 % then 67 % spread). The integral fit
        // takes friction out as its own term, so doubling it must not move
        // the recovered damper gain.
        [Fact]
        public void HeavyCoulombFriction_DoesNotSkewTheDamperGain()
        {
            var baseline = RunFullSimulation(out _);
            var heavy    = RunFullSimulation(out _, coulombScale: 2.0);
            Assert.True(heavy.Current == WheelAutoTuner.Phase.Done, $"aborted: {heavy.AbortReason}");

            double expected = KfwDamper / SChain;
            Assert.InRange(heavy.DamperGain, expected * 0.6, expected * 1.5);
            // And the two runs must agree with EACH OTHER, not merely both
            // land somewhere inside a wide band.
            // ~20 % residual, and it is settling time rather than the maths:
            // heavier friction means lower speeds under the same force, so
            // the wheel is a little further from its terminal speed when the
            // window closes, and the flatness check tolerates 10 %. On the
            // wheel this matters far less, because it settles about thirteen
            // times faster relative to the hold than this simulation does.
            // The coast fit this replaced could not be given a bound at all.
            Assert.InRange(heavy.DamperGain / baseline.DamperGain, 0.8, 1.25);
        }

        // Friction is a difference of two measured quantities, so a wheel
        // whose own friction drifts during a run lands that drift squarely in
        // the answer unless the baseline is measured beside the loaded trial
        // rather than phases earlier. The rig showed the failure as a
        // monotone walk across a x5: 0.49, 0.68, 0.74, 0.83, 0.93.
        [Fact]
        public void FrictionIsUnmovedByAWheelThatDriftsDuringTheRun()
        {
            var steady   = RunFullSimulation(out _);
            var drifting = RunFullSimulation(out SimState stD, coulombDriftPerSec: -0.0015);
            Assert.True(drifting.Current == WheelAutoTuner.Phase.Done,
                $"aborted: {drifting.AbortReason} :: {string.Join(" | ", stD.Log)}");
            Assert.False(double.IsNaN(drifting.FrictionGain), "friction did not measure under drift");

            // The drift is roughly a fifth of the wheel's friction across the
            // run. With the baseline taken beside the measurement, it must
            // barely register in the gain.
            Assert.InRange(drifting.FrictionGain / steady.FrictionGain, 0.85, 1.18);
        }

        // A wheel whose effects are half as strong must still yield the same
        // gain: the probe strengths are constants fitted to ONE wheel, and
        // nothing about the answer may depend on them. Measured limit, found
        // by pushing this further: below about half strength the run fails,
        // and it fails on the plateau's TRAVEL budget rather than on anything
        // to do with probe strength. A weakly damped wheel reaches the end of
        // its travel before it reaches a steady speed.
        [Fact]
        public void AWheelWithWeakerEffects_YieldsTheSameGain()
        {
            // A tenth of the firmware strengths: at the stock probe levels
            // this wheel barely responds at all.
            // Both sides equally feeble, so the TRUE gain is unchanged: only
            // the ability to see it at the stock probe strengths is affected.
            var tuner = RunFullSimulation(out _, effectScale: 0.5);
            Assert.True(tuner.Current == WheelAutoTuner.Phase.Done, $"aborted: {tuner.AbortReason}");
            double expected = KfwDamper / SChain;
            Assert.InRange(tuner.DamperGain, expected * 0.7, expected * 1.4);
        }

        // A real wheel reports velocity in steps, and at plateau speeds a few
        // percent IS that quantum. A flatness test stated as a flat
        // percentage therefore rejects good trials at random: on the rig it
        // cost ten rejections for two passes and then killed the phase. The
        // test has to measure the step against the window's own scatter.
        [Fact]
        public void ANoisyVelocitySignal_DoesNotStarveThePlateau()
        {
            var tuner = RunFullSimulation(out _, velQuantum: 0.05);
            Assert.True(tuner.Current == WheelAutoTuner.Phase.Done, $"aborted: {tuner.AbortReason}");
            double expected = KfwDamper / SChain;
            Assert.InRange(tuner.DamperGain, expected * 0.75, expected * 1.3);
        }

        [Fact]
        public void InvertedNativeFrame_IsDetected_AndTheRunStillRecovers()
        {
            var tuner = RunFullSimulation(out _, nativeSign: -1);
            Assert.Equal(WheelAutoTuner.Phase.Done, tuner.Current);
            Assert.True(tuner.NativeDirFlipped);
            // Tight, because a steady-state measurement should be: the coast
            // fit this replaced needed a 0.6..1.5 band just to pass.
            double expectedDamper = KfwDamper / SChain;
            Assert.InRange(tuner.DamperGain, expectedDamper * 0.85, expectedDamper * 1.18);
            double expectedSpring = KfwSpring / SChain;
            Assert.InRange(tuner.SpringGain, expectedSpring * 0.6, expectedSpring * 1.5);
        }

        [Fact]
        public void Runaway_Aborts_AndZeroesTheCommand()
        {
            var tuner = new WheelAutoTuner(0.25, 1, 1, _ => { }, _ => { }, _ => { });
            // Walk it out of Idle (into DirProbe's hand-centering wait).
            for (int i = 0; i < 40; i++) tuner.Sample(0, 0, i * 0.1);
            Assert.True(tuner.Running);
            // Parked near the lock during the hand-center wait: the position
            // guard stands down (the user is being asked to center it; a
            // prior abort may have left the wheel at the lock).
            tuner.Sample(0.97, 0, 100);
            Assert.True(tuner.Running);
            // The velocity guard always fires.
            tuner.Sample(0.5, 12, 100.001);
            Assert.Equal(WheelAutoTuner.Phase.Aborted, tuner.Current);
            Assert.Equal(0, tuner.EngineCommand, 5);
        }

        // Every measurement starts from a stopped wheel (owner, 2026-09-02).
        // A velocity threshold alone passes a wheel that is still drifting,
        // and the old settle then pulsed anyway once its timeout expired,
        // measuring a coast that already had motion in it. Here the wheel
        // creeps back and forth slowly: velocity stays under the old gate the
        // whole time, but the wheel is plainly not still, so no pulse may be
        // issued.
        [Fact]
        public void ADriftingWheelIsNeverPulsed()
        {
            double maxDrive = 0;
            var statuses = new System.Collections.Generic.List<string>();
            var tuner = new WheelAutoTuner(0.25, 1, 1, _ => { },
                cmd => { if (Math.Abs(cmd) > maxDrive) maxDrive = Math.Abs(cmd); },
                statuses.Add);

            // Get to DirProbe on a still wheel, then stop the moment we are
            // there: lingering would legitimately settle and fire a pulse.
            double t0 = 0;
            for (int i = 0; i < 5000 && tuner.Current == WheelAutoTuner.Phase.Idle; i++, t0 += 0.001)
                tuner.Sample(0, 0, t0);
            Assert.Equal(WheelAutoTuner.Phase.DirProbe, tuner.Current);
            maxDrive = 0;

            // Steady creep, which is what an unsettled coast looks like:
            // velocity 0.05 stays under the old 0.10 gate the whole time,
            // but the wheel covers the settle's whole drift allowance every
            // fifth of a second, so it is never still.
            //
            // Watch only the settle window. Centring DOES drive (it walks the
            // wheel to centre, discovering the frame's sign as it goes); what
            // must never happen is a PULSE onto a moving wheel, and settle is
            // where that decision is made.
            double t = t0;
            // One centring sample lands before the handover to settle, and it
            // legitimately drives. Start watching after it.
            tuner.Sample(0, 0.05, t); t += 0.001;
            maxDrive = 0;
            for (int i = 0; i < 8500; i++, t += 0.001)
                tuner.Sample(0.05 * (t - t0), 0.05, t);
            Assert.Equal(0, maxDrive, 6);

            // Carry on past the settle timeout: the trial must be discarded,
            // not pulsed.
            for (int i = 0; i < 3000; i++, t += 0.001)
                tuner.Sample(0.05 * (t - t0), 0.05, t);
            Assert.Contains(statuses, m => m.Contains("would not settle"));
        }

        // The direction frame must come from the wheel's response to the
        // PULSE, not from whatever it does over the three seconds of free
        // response that follow. On the rig a wheel whose frame is inverted
        // read as normal three times running (2026-09-02 08:28, 08:46,
        // 09:06), which pointed the centering drive away from centre and
        // killed the run before it started. Here the post-pulse motion is
        // deliberately larger AND opposite: the pulse must still decide.
        [Fact]
        public void DirectionIsReadFromThePulse_NotTheFreeResponse()
        {
            double drive = 0;
            var msgs = new System.Collections.Generic.List<string>();
            var tuner = new WheelAutoTuner(0.25, 1, 1, _ => { }, cmd => drive = cmd, msgs.Add);
            double t = 0;
            for (int i = 0; i < 5000 && tuner.Current == WheelAutoTuner.Phase.Idle; i++, t += 0.001)
                tuner.Sample(0, 0, t);
            Assert.Equal(WheelAutoTuner.Phase.DirProbe, tuner.Current);

            // Settle (pos 0, so the pulse will push positive) and stop the
            // moment the pulse actually starts.
            for (int i = 0; i < 5000 && drive == 0; i++, t += 0.001) tuner.Sample(0, 0, t);
            Assert.NotEqual(0, drive);
            // Pushed positive, the wheel moves NEGATIVE: an inverted frame.
            double pos = 0;
            for (int i = 0; i < 160; i++, t += 0.001) { pos -= 0.0005; tuner.Sample(pos, -0.5, t); }
            // Then it swings back the other way, further and for far longer.
            for (int i = 0; i < 3100; i++, t += 0.001) { pos += 0.0002; tuner.Sample(pos, 2.0, t); }

            Assert.True(tuner.NativeDirFlipped,
                $"the free response outvoted the push. {string.Join(" | ", msgs)}");
        }

        // The centring spring is rendered by the firmware in the WHEEL's own
        // frame, so it pulls the wheel home whichever way OUR commands run.
        // That is what replaced a hand-driven walk which had to discover the
        // sign for itself, and which overshot and hunted because a
        // safety-capped drive cannot brake a wheel arriving with speed.
        [Fact]
        public void TheCentringSpringWorks_EvenWhenOurCommandsAreBackwards()
        {
            double drive = 0;
            int springPct = 0; double springAt = 0;
            var msgs = new System.Collections.Generic.List<string>();
            var tuner = new WheelAutoTuner(0.25, 1, 1, _ => { }, cmd => drive = cmd, msgs.Add,
                null, (pct, damp, at) => { springPct = pct; springAt = at; return true; });

            double t = 0;
            for (int i = 0; i < 5000 && tuner.Current == WheelAutoTuner.Phase.Idle; i++, t += 0.001)
                tuner.Sample(0, 0, t);
            Assert.Equal(WheelAutoTuner.Phase.DirProbe, tuner.Current);

            // Displaced, and our own commands drive the wheel BACKWARDS.
            double pos = 0.55, vel = 0, closest = 0.55;
            for (int i = 0; i < 40000 && closest > 0.05; i++, t += 0.001)
            {
                double k = (springPct / 100.0) * 2.0;
                vel += (-drive * 6.0 - k * (pos - springAt) - vel * 0.5) * 0.001;
                pos += vel * 0.001;
                if (pos > 1) { pos = 1; vel = 0; }
                if (pos < -1) { pos = -1; vel = 0; }
                tuner.Sample(pos, vel, t);
                closest = Math.Min(closest, Math.Abs(pos));
            }
            Assert.True(closest <= 0.05, $"never centred, closest {closest:F2}");
            Assert.True(springPct > 0, "no centring spring was ever asked for");
        }

        [Fact]
        public void Cancel_TakesEffectOnTheNextSample()
        {
            var tuner = new WheelAutoTuner(0.25, 1, 1, _ => { }, _ => { }, _ => { });
            for (int i = 0; i < 40; i++) tuner.Sample(0, 0, i * 0.1);
            Assert.True(tuner.Running);
            tuner.Cancel("test");
            tuner.Sample(0, 0, 200);
            Assert.Equal(WheelAutoTuner.Phase.Aborted, tuner.Current);
        }
    }
}
