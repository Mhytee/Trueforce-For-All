using System;
using System.Collections.Generic;
using System.Linq;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;
using Xunit;

namespace TrueforceForAll.Core.Tests
{
    // The Test buttons' clip path: a recorded telemetry clip replayed into one
    // effect through its real OnTelemetry, on the engine tick (EngineLoop.
    // StartClipTest / EffectClipTest). Two halves:
    //
    //   The shipped clips themselves. Each one must make its effect speak. A
    //   clip that plays as silence is a Test button that looks broken, and
    //   nothing else would notice: the build passes and the wheel says nothing.
    //
    //   The routing contract. The effect under test hears its clip and only
    //   its clip; every other effect keeps hearing the live car; the effect is
    //   handed back clean when the clip ends.
    public class EffectClipTestTests
    {
        private const long TicksPerSec = 1_000_000;   // virtual microsecond clock

        private sealed class RecordingEffect : TelemetryEffect
        {
            public readonly List<TelemetryFrame> Frames = new List<TelemetryFrame>();
            public int StallCalls;

            public override string Name => "Recorder";
            public override bool IsActive => false;
            public override void RenderAdd(float[] buffer, int count) { }
            public override void OnTelemetry(TelemetryFrame f) => Frames.Add(f);
            public override void OnTelemetryStall() => StallCalls++;
        }

        private static EngineLoop NewLoop(ISampleSink sink, params TelemetryEffect[] effects)
        {
            var mixer = new Mixer();
            foreach (var fx in effects) mixer.Add(fx);
            return new EngineLoop(mixer, null, sink, new FrameResampler(TicksPerSec))
            {
                Effects = effects,
            };
        }

        // 100 Hz clip, speed climbing from 200 so its frames can never be
        // mistaken for the 100 km/h live feed the routing tests run beside it.
        private static CsvFrameReader.Capture SyntheticClip(int rows = 51, int gapUs = 10_000)
        {
            var clip = new CsvFrameReader.Capture { SchemaVersion = 2, SourceName = "synthetic", GameName = "test" };
            for (int i = 0; i < rows; i++)
                clip.Rows.Add(new ReplayRow
                {
                    TUs = 5_000_000L + (long)i * gapUs,   // arbitrary origin: the player rebases
                    SessionActive = true,
                    Frame = new TelemetryFrame
                    {
                        Rpms = 4000, MaxRpm = 8000, RedlineRpm = 7600, Throttle01 = 1.0,
                        SpeedKmh = 200 + i, Gear = "3", NumCylinders = 12,
                    },
                });
            return clip;
        }

        private static TelemetryFrame LiveFrame(long atTicks) => new TelemetryFrame
        {
            Rpms = 3000, MaxRpm = 7000, Throttle01 = 0.3, SpeedKmh = 100, Gear = "2",
            CapturedAtTicks = atTicks,
        };

        // ---------------------------------------------------- shipped clips

        // The effects that ship a clip today. A name missing from the assembly
        // means the embed broke (csproj glob, LogicalName), which would quietly
        // send every Test button back to its synthetic shape.
        private static readonly string[] ExpectedClips =
        {
            "AxleSlipEffect", "EnginePulseEffect", "GearShiftEffect", "KerbThumpEffect",
            "LockupJudderEffect", "RevLimiterEffect", "RoadBumpsEffect", "TractionLossEffect",
        };

        [Fact]
        public void ShippedClips_AreEmbedded_AndNamedForRealEffects()
        {
            var shipped = EffectTestClips.EffectTypeNames;
            foreach (string name in ExpectedClips) Assert.Contains(name, shipped);

            foreach (string name in shipped)
            {
                Type type = typeof(TelemetryEffect).Assembly.GetType("TrueforceForAll.Plugin.Effects." + name);
                Assert.True(type != null && typeof(TelemetryEffect).IsAssignableFrom(type),
                    $"TestClips/{name}.csv is not named for an effect class, so no button can reach it");
                Assert.NotNull(EffectTestClips.ForEffectType(name));
            }
        }

        public static IEnumerable<object[]> ShippedClipNames()
            => EffectTestClips.EffectTypeNames.Select(n => new object[] { n });

        [Theory]
        [MemberData(nameof(ShippedClipNames))]
        public void ShippedClip_MakesItsEffectSpeak_ThenHandsItBackSilent(string effectTypeName)
        {
            Type type = typeof(TelemetryEffect).Assembly.GetType("TrueforceForAll.Plugin.Effects." + effectTypeName);
            var effect = (TelemetryEffect)Activator.CreateInstance(type);
            effect.SeedNoise(12345);
            // Where the shipped setting differs from the class default in a way
            // that decides WHETHER the effect fires, test the shipped one.
            if (effect is TractionLossEffect traction) traction.Sensitivity = 0.18f;
            // Disabled on purpose: a Test button must play an effect the user
            // has switched off, and must stop playing it when the test ends.
            effect.Enabled = false;

            var clip = EffectTestClips.ForEffectType(effectTypeName);
            var sink = new CaptureSink();
            var loop = NewLoop(sink, effect);
            var buf = new float[EngineLoop.BatchSamples];

            int totalMs = loop.StartClipTest(effect, clip, nowTicks: 0);
            Assert.True(totalMs > EffectClipTest.TailMs, "clip test did not start");
            Assert.True(effect.IsClipTesting);

            int runMs = totalMs + 200;
            for (int ms = 1; ms <= runMs; ms++) loop.RunOneTick(buf, ms * 1000L, 0f, 0f, 0f);

            float peak = sink.Samples.Max(v => Math.Abs(v));
            Assert.True(peak > 0.02f, $"{effectTypeName}'s clip played as silence (peak {peak:F4})");
            Assert.True(peak <= 1.0f, $"{effectTypeName}'s clip clipped the stream (peak {peak:F3})");

            // The button should answer promptly: something within the first
            // 400 ms, not a long wait on a recording's lead-in.
            float earlyPeak = sink.Samples.Take(400 * 4).Max(v => Math.Abs(v));
            Assert.True(earlyPeak > 0.005f, $"{effectTypeName}'s clip is silent for its first 400 ms");

            Assert.False(effect.IsClipTesting);
            Assert.False(effect.IsActive);
            float tail = sink.Samples.Skip(sink.Samples.Count - 100 * 4).Max(v => Math.Abs(v));
            Assert.Equal(0f, tail);
        }

        [Fact]
        public void RedlineClip_BuzzesOnItsOwnLimiter_AndLeavesThisCarsRedlineAlone()
        {
            // The clip is some other car. This car's redline facts must neither
            // silence the test (a user redline the clip can never rev to, an
            // electric car) nor be overwritten by it: the Car facts panel, the
            // dash and the rev lights all read EffectiveRedlineRpm.
            var limiter = new RevLimiterEffect
            {
                UserGearRedlines = new Dictionary<int, int> { { 0, 12_000 } },
                IsElectric = true,
            };
            limiter.OnTelemetry(new TelemetryFrame { Rpms = 3000, MaxRpm = 13_000, Gear = "2", SpeedKmh = 80 });
            Assert.Equal(12_000, limiter.EffectiveRedlineRpm);

            var sink = new CaptureSink();
            var loop = NewLoop(sink, limiter);
            var buf = new float[EngineLoop.BatchSamples];
            int totalMs = loop.StartClipTest(limiter, EffectTestClips.For(limiter), nowTicks: 0);
            for (int ms = 1; ms <= totalMs + 50; ms++) loop.RunOneTick(buf, ms * 1000L, 0f, 0f, 0f);

            Assert.True(sink.Samples.Max(v => Math.Abs(v)) > 0.02f, "the redline clip played as silence");
            Assert.Equal(12_000, limiter.EffectiveRedlineRpm);
            Assert.False(limiter.IsRedlineGuessed);
            Assert.Equal(12_000, limiter.UserGearRedlines[0]);
        }

        [Fact]
        public void RoadBumpsClip_PlaysTheSurfaceTexture_OnlyWhileItsBoxIsTicked()
        {
            // Owner call 2026-11-11. The clip carries Forza's surface channel
            // and the player passes it through untouched: the effect's own
            // "Surface texture" tick box decides, exactly as it does on track.
            // Ticked, the test is the texture plus the jolt; unticked, the
            // bumps alone. A player that strips the channel, or a gate that
            // stops reading the box during a test, breaks one half of this.
            float[] ticked   = PlayRoadBumpsClip(surfaceEnabled: true);
            float[] unticked = PlayRoadBumpsClip(surfaceEnabled: false);

            // The first 1.5 s is the rough surface, before the jolt.
            double textureOn  = Rms(ticked,   0, 1500 * 4);
            double textureOff = Rms(unticked, 0, 1500 * 4);
            Assert.True(textureOn > 0.015, $"ticked, the surface texture did not play (rms {textureOn:F4})");
            Assert.True(textureOff < textureOn / 3.0,
                $"unticked, the surface texture still played (rms {textureOff:F4} against {textureOn:F4} ticked)");

            // Unticked is still a test: the jolt off the curb is heave.
            float joltPeak = unticked.Max(v => Math.Abs(v));
            Assert.True(joltPeak > 0.02f, $"unticked, the bumps themselves went silent (peak {joltPeak:F4})");
        }

        private static float[] PlayRoadBumpsClip(bool surfaceEnabled)
        {
            // The shipped Road bumps settings (RoadBumpsSettings' defaults).
            var bumps = new RoadBumpsEffect
            {
                Gain = 0.45f, Waveform = Waveform.Triangle, Freq = 61,
                SurfaceGain = 0.70f, SurfaceEnabled = surfaceEnabled,
            };
            bumps.SeedNoise(12345);
            var sink = new CaptureSink();
            var loop = NewLoop(sink, bumps);
            var buf = new float[EngineLoop.BatchSamples];
            int totalMs = loop.StartClipTest(bumps, EffectTestClips.For(bumps), nowTicks: 0);
            for (int ms = 1; ms <= totalMs; ms++) loop.RunOneTick(buf, ms * 1000L, 0f, 0f, 0f);
            return sink.Samples.ToArray();
        }

        private static double Rms(float[] samples, int from, int count)
        {
            int end = Math.Min(samples.Length, from + count);
            double sum = 0;
            for (int i = from; i < end; i++) sum += (double)samples[i] * samples[i];
            return Math.Sqrt(sum / Math.Max(1, end - from));
        }

        // ------------------------------------------------------- implements

        [Fact]
        public void ImplementCapture_PlaysAsAClip_HumWhileTravelling_ThudAtTheSettle()
        {
            // A lower recorded through the real writer and read back, the way
            // an ImplementThudEffect.csv will be made: 200 ms still, 600 ms of
            // travel, settled working. Proves the implement columns survive
            // the capture and the clip path all the way to the effect.
            var clip = RecordLower();

            // Thud alone (hum off): silent until the settle, then the landing.
            float[] thudOnly = PlayImplementClip(clip, humAmp: 0f);
            Assert.Equal(0f, Peak(thudOnly, 0, 780));
            float landing = Peak(thudOnly, 780, 1000);
            Assert.True(landing > 0.1f, $"no thud when the linkage settled (peak {landing:F4})");

            // Hum on: the hydraulics speak while the linkage travels.
            float[] withHum = PlayImplementClip(clip, humAmp: 0.30f);
            Assert.Equal(0f, Peak(withHum, 0, 180));
            float hum = Peak(withHum, 400, 700);
            Assert.True(hum > 0.05f, $"no hydraulic hum while the linkage travelled (peak {hum:F4})");
        }

        [Fact]
        public void ImplementClip_NeverThudsOnTheSwitch_IntoTheClipOrBackToTheGame()
        {
            // Counters are per session. The clip's event count (40) sits above
            // the live game's (2) and the game's (50) above the clip's: compared
            // across the switch, each would read as a fresh event and thud.
            var thud = new ImplementThudEffect { Enabled = true };
            var sink = new CaptureSink();
            var loop = NewLoop(sink, thud);
            var buf = new float[EngineLoop.BatchSamples];

            long now = 0;
            int liveEvent = 2;
            void Run(int ms)
            {
                for (int i = 0; i < ms; i++)
                {
                    now += 1000;
                    if (now % 10_000 == 0) { var live = FarmFrame(now, liveEvent); loop.IngestFrame(in live); }
                    loop.RunOneTick(buf, now, 0f, 0f, 0f);
                }
            }

            var quiet = new CsvFrameReader.Capture();
            for (int i = 0; i < 31; i++)
                quiet.Rows.Add(new ReplayRow { TUs = i * 10_000L, Frame = FarmFrame(0, evt: 40) });

            Run(100);
            int totalMs = loop.StartClipTest(thud, quiet, now);
            liveEvent = 50;                                   // the game moved on meanwhile
            Run(totalMs + 200);
            Assert.False(thud.IsClipTesting);
            Assert.Equal(0f, sink.Samples.Max(v => Math.Abs(v)));

            // Control: the effect is back on the live game and listening.
            int before = sink.Samples.Count;
            liveEvent = 51;
            Run(200);
            float flap = sink.Samples.Skip(before).Max(v => Math.Abs(v));
            Assert.True(flap > 0.05f, $"a real live event no longer thuds (peak {flap:F4})");
        }

        private static TelemetryFrame FarmFrame(long atTicks, int evt) => new TelemetryFrame
        {
            SpeedKmh = 5, Gear = "1",
            ImplementWorking = true, ImplementMoving = false, ImplementSpeed = 0f,
            ImplementEvent = evt, ImplementPhase = 1, ImplementManual = false,
            CapturedAtTicks = atTicks,
        };

        private static CsvFrameReader.Capture RecordLower()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "tfcap_lower_" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                long freq = System.Diagnostics.Stopwatch.Frequency;
                using (var log = new GoldenCaptureLog(path, "Farming Simulator", "test"))
                {
                    for (int i = 0; i <= 120; i++)            // 1.2 s at 100 Hz
                    {
                        bool moving = i >= 20 && i < 80;
                        var f = new TelemetryFrame
                        {
                            SpeedKmh = 8, Gear = "1",
                            ImplementWorking = i >= 20, ImplementMoving = moving,
                            ImplementSpeed = moving ? 0.8f : 0f,
                            ImplementEvent = 0, ImplementPhase = 0, ImplementManual = false,
                            CapturedAtTicks = i * freq / 100,
                        };
                        log.LogRow(in f, true, null);
                    }
                }
                return CsvFrameReader.ReadFile(path);
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch { }
            }
        }

        private static float[] PlayImplementClip(CsvFrameReader.Capture clip, float humAmp)
        {
            var thud = new ImplementThudEffect { Enabled = false, HumAmp = humAmp };
            var sink = new CaptureSink();
            var loop = NewLoop(sink, thud);
            var buf = new float[EngineLoop.BatchSamples];
            int totalMs = loop.StartClipTest(thud, clip, nowTicks: 0);
            for (int ms = 1; ms <= totalMs; ms++) loop.RunOneTick(buf, ms * 1000L, 0f, 0f, 0f);
            return sink.Samples.ToArray();
        }

        // Peak over [fromMs, toMs) of a 4 kHz capture.
        private static float Peak(float[] samples, int fromMs, int toMs)
        {
            float peak = 0f;
            for (int i = fromMs * 4; i < Math.Min(samples.Length, toMs * 4); i++)
                peak = Math.Max(peak, Math.Abs(samples[i]));
            return peak;
        }

        // ---------------------------------------------------------- routing

        [Fact]
        public void ClipTest_FeedsOnlyItsEffect_AndLiveTelemetryResumesAfter()
        {
            var tested = new RecordingEffect();
            var other  = new RecordingEffect();
            var loop = NewLoop(new CaptureSink(), tested, other);
            var buf = new float[EngineLoop.BatchSamples];
            var clip = SyntheticClip();                       // 500 ms
            int totalMs = 500 + EffectClipTest.TailMs;

            long now = 0;
            void Run(int ms)
            {
                for (int i = 0; i < ms; i++)
                {
                    now += 1000;
                    if (now % 10_000 == 0) { var live = LiveFrame(now); loop.IngestFrame(in live); }   // 100 Hz live feed
                    loop.RunOneTick(buf, now, 0f, 0f, 0f);
                }
            }

            Run(100);
            Assert.All(tested.Frames, f => Assert.Equal(100.0, f.SpeedKmh, 3));
            int testedBefore = tested.Frames.Count, otherBefore = other.Frames.Count;
            Assert.True(testedBefore > 0);

            Assert.Equal(totalMs, loop.StartClipTest(tested, clip, now));
            Run(totalMs - 50);                                // inside the clip, then inside its tail

            var duringTest = tested.Frames.Skip(testedBefore).ToList();
            Assert.NotEmpty(duringTest);
            Assert.All(duringTest, f => Assert.InRange(f.SpeedKmh, 199.0, 252.0));   // clip frames only
            Assert.True(duringTest.Last().SpeedKmh > 245.0, "the clip did not play through to its end");
            Assert.Equal(1, tested.StallCalls);               // settled once, when the clip ran out
            Assert.True(tested.IsClipTesting);                // still in its ring-out tail

            // The neighbour never noticed: live frames throughout, no settle.
            var otherDuring = other.Frames.Skip(otherBefore).ToList();
            Assert.NotEmpty(otherDuring);
            Assert.All(otherDuring, f => Assert.Equal(100.0, f.SpeedKmh, 3));
            Assert.Equal(0, other.StallCalls);

            int testedAtTailEnd = tested.Frames.Count;
            Run(200);                                         // past the tail
            Assert.False(tested.IsClipTesting);
            var after = tested.Frames.Skip(testedAtTailEnd).ToList();
            Assert.NotEmpty(after);
            Assert.All(after, f => Assert.Equal(100.0, f.SpeedKmh, 3));
            Assert.Equal(1, tested.StallCalls);
        }

        [Fact]
        public void ClipTest_PlaysWithNoLiveTelemetryAtAll()
        {
            // The first-run case: no game running, nothing in the live ring.
            var tested = new RecordingEffect();
            var loop = NewLoop(new CaptureSink(), tested);
            var buf = new float[EngineLoop.BatchSamples];

            loop.StartClipTest(tested, SyntheticClip(), nowTicks: 0);
            for (int ms = 1; ms <= 500; ms++) loop.RunOneTick(buf, ms * 1000L, 0f, 0f, 0f);

            // 500 Hz effect tick over a 500 ms clip.
            Assert.InRange(tested.Frames.Count, 240, 251);
            // Frames went through the CTM stage, exactly as live ones do.
            Assert.All(tested.Frames, f => Assert.True((f.Caps & SignalGroups.DerivedEvents) != 0));
        }

        [Fact]
        public void ClipTest_NeverPassesOnTheClipsCylinders_AndMapsItsRevsOntoTheLiveCar()
        {
            var tested = new RecordingEffect();
            var loop = NewLoop(new CaptureSink(), tested);
            var buf = new float[EngineLoop.BatchSamples];
            var clip = SyntheticClip();                       // 12 cyl, 4000 of 8000 rpm, redline 7600

            double scale = EffectTestClips.RpmScaleFor(clip, liveMaxRpm: 4000);
            Assert.Equal(0.5, scale, 6);
            loop.StartClipTest(tested, clip, nowTicks: 0, rpmScale: scale);
            for (int ms = 1; ms <= 300; ms++) loop.RunOneTick(buf, ms * 1000L, 0f, 0f, 0f);

            Assert.NotEmpty(tested.Frames);
            Assert.All(tested.Frames, f =>
            {
                Assert.Null(f.NumCylinders);
                Assert.Equal(4000.0, f.MaxRpm, 3);
                Assert.Equal(2000.0, f.Rpms, 0);
                Assert.Equal(3800.0, f.RedlineRpm, 3);
            });
            // The shared clip itself is untouched, ready for the next press.
            Assert.All(clip.Rows, r => Assert.Equal(8000.0, r.Frame.MaxRpm));
            Assert.All(clip.Rows, r => Assert.Equal(12, r.Frame.NumCylinders));
        }

        [Fact]
        public void RpmScale_IsOne_UnlessBothRevRangesAreKnown_AndIsClamped()
        {
            var clip = SyntheticClip();
            Assert.Equal(1.0, EffectTestClips.RpmScaleFor(clip, 0));
            Assert.Equal(1.0, EffectTestClips.RpmScaleFor(clip, double.NaN));
            Assert.Equal(1.0, EffectTestClips.RpmScaleFor(null, 7000));
            Assert.Equal(4.0, EffectTestClips.RpmScaleFor(clip, 80_000));
            Assert.Equal(0.2, EffectTestClips.RpmScaleFor(clip, 500));

            var noRevs = SyntheticClip();
            foreach (var row in noRevs.Rows) row.Frame.MaxRpm = 0;
            Assert.Equal(1.0, EffectTestClips.RpmScaleFor(noRevs, 7000));
        }

        [Fact]
        public void ClipTest_IsAnchoredToItsStart_AndLapsesIfTheLoopWasParked()
        {
            // Pressed while the loop was not ticking (no device, plugin off).
            // When ticking resumes long after, the clip must not spring on the
            // user: it is already over.
            var tested = new RecordingEffect();
            var loop = NewLoop(new CaptureSink(), tested);
            var buf = new float[EngineLoop.BatchSamples];

            loop.StartClipTest(tested, SyntheticClip(), nowTicks: 0);
            Assert.True(tested.IsClipTesting);

            long resumeAt = 60 * TicksPerSec;
            for (int ms = 1; ms <= 10; ms++) loop.RunOneTick(buf, resumeAt + ms * 1000L, 0f, 0f, 0f);

            Assert.Empty(tested.Frames);
            Assert.False(tested.IsClipTesting);
        }

        [Fact]
        public void CancelClipTest_HandsTheEffectBack_WithoutSettlingIt()
        {
            var tested = new RecordingEffect();
            var loop = NewLoop(new CaptureSink(), tested);
            var buf = new float[EngineLoop.BatchSamples];

            loop.StartClipTest(tested, SyntheticClip(), nowTicks: 0);
            long now = 0;
            for (int i = 0; i < 100; i++) loop.RunOneTick(buf, now += 1000, 0f, 0f, 0f);
            int clipFrames = tested.Frames.Count;
            Assert.True(clipFrames > 0);

            loop.CancelClipTest(tested);
            for (int i = 0; i < 20; i++)
            {
                now += 1000;
                var live = LiveFrame(now);
                loop.IngestFrame(in live);
                loop.RunOneTick(buf, now, 0f, 0f, 0f);
            }

            Assert.False(tested.IsClipTesting);
            Assert.Equal(0, tested.StallCalls);   // the caller is taking the effect over, not silencing it
            var after = tested.Frames.Skip(clipFrames).ToList();
            Assert.NotEmpty(after);
            Assert.All(after, f => Assert.Equal(100.0, f.SpeedKmh, 3));
        }

        [Fact]
        public void RestartingAClipTest_ReplacesTheRunningOne()
        {
            var tested = new RecordingEffect();
            var loop = NewLoop(new CaptureSink(), tested);
            var buf = new float[EngineLoop.BatchSamples];
            var clip = SyntheticClip();

            loop.StartClipTest(tested, clip, nowTicks: 0);
            long now = 0;
            for (int i = 0; i < 300; i++) loop.RunOneTick(buf, now += 1000, 0f, 0f, 0f);
            Assert.True(tested.Frames.Last().SpeedKmh > 225.0);

            int before = tested.Frames.Count;
            loop.StartClipTest(tested, clip, now);            // pressed again mid-clip
            for (int i = 0; i < 20; i++) loop.RunOneTick(buf, now += 1000, 0f, 0f, 0f);

            // Back at the top of the clip, and the first run never reached its
            // end to settle the effect.
            Assert.All(tested.Frames.Skip(before), f => Assert.InRange(f.SpeedKmh, 199.0, 203.0));
            Assert.Equal(0, tested.StallCalls);
            Assert.True(tested.IsClipTesting);
        }

        // -------------------------------------------- the synthetic fallback

        [Fact]
        public void SyntheticTest_StillHoldsItsShapeAgainstLiveTelemetry()
        {
            // Effects without a clip, and the stream self-test, still use
            // TestPlay. Its gate moved from IsTesting to IsSyntheticTest; a
            // live frame must still not talk over it.
            var limiter = new RevLimiterEffect();
            Assert.True(limiter.TestPlay() > 0);

            limiter.OnTelemetry(new TelemetryFrame { Rpms = 1000, MaxRpm = 8000, Gear = "2", SpeedKmh = 50 });

            var buf = new float[64];
            limiter.RenderAdd(buf, buf.Length);
            Assert.True(buf.Max(v => Math.Abs(v)) > 0.05f, "a live idle frame silenced the synthetic test");
        }

        [Fact]
        public void StartingAClipTest_ClosesASyntheticTestOnTheSameEffect()
        {
            // Otherwise the synthetic gate would swallow every clip frame.
            var limiter = new RevLimiterEffect { Enabled = false };
            limiter.TestPlay();                               // synthetic buzz, 2 s window

            var sink = new CaptureSink();
            var loop = NewLoop(sink, limiter);
            var buf = new float[EngineLoop.BatchSamples];
            var idle = new CsvFrameReader.Capture();
            for (int i = 0; i < 31; i++)
                idle.Rows.Add(new ReplayRow
                {
                    TUs = i * 10_000L,
                    Frame = new TelemetryFrame { Rpms = 1000, MaxRpm = 8000, Gear = "2", SpeedKmh = 50 },
                });

            loop.StartClipTest(limiter, idle, nowTicks: 0);
            for (int ms = 1; ms <= 250; ms++) loop.RunOneTick(buf, ms * 1000L, 0f, 0f, 0f);

            // The idle clip reached the effect and took the buzz away. (The
            // limiter's 80 ms hold is wall-clock and never armed here, so the
            // amplitude drops on the first clip frame.)
            float late = sink.Samples.Skip(100 * 4).Max(v => Math.Abs(v));
            Assert.Equal(0f, late);
        }
    }
}
