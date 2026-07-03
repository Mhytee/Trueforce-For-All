// Headless replay rig: the production pipeline with the wheel and SimHub cut
// off at their seams. ReplayFrameSource stands in for the live telemetry
// source (virtual time), CaptureSink stands in for TrueforceDevice. Everything
// in between — FrameEnricher, the effects, DuckingController, EngineLoop — is
// the exact code the plugin runs. One engine tick per fixture millisecond,
// matching the production producer's ring-paced ~1 kHz.

using System;
using System.Collections.Generic;
using TrueforceForAll.Core;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Core.Tests
{
    /// <summary>Sink that captures the rendered 4 kHz stream plus a per-tick
    /// trace of the ducking state for envelope assertions.</summary>
    public sealed class CaptureSink : ISampleSink
    {
        public readonly List<float> Samples = new List<float>(1 << 18);
        public void Push(float[] samples, int count)
        {
            for (int i = 0; i < count; i++) Samples.Add(samples[i]);
        }
    }

    public sealed class HeadlessRig
    {
        public readonly EnginePulseEffect  EnginePulse  = new EnginePulseEffect();
        public readonly RoadBumpsEffect    RoadBumps    = new RoadBumpsEffect();
        public readonly TractionLossEffect TractionLoss = new TractionLossEffect();
        public readonly GearShiftEffect    GearShift    = new GearShiftEffect();
        public readonly AbsClickEffect     AbsClick     = new AbsClickEffect();
        public readonly PitLimiterEffect   PitLimiter   = new PitLimiterEffect();
        public readonly DrsEffect          Drs          = new DrsEffect();
        public readonly CollisionEffect    Collision    = new CollisionEffect();
        public readonly RevLimiterEffect   RevLimiter   = new RevLimiterEffect();
        public readonly AirborneEffect     Airborne     = new AirborneEffect();

        public readonly Mixer             Mixer   = new Mixer();
        public readonly DuckingController Ducking = new DuckingController();
        public readonly CaptureSink       Sink    = new CaptureSink();
        public readonly EngineLoop        Loop;

        public readonly TelemetryEffect[] Effects;

        // Per-tick duck traces (one entry per engine tick).
        public readonly List<float> DuckL0Trace = new List<float>(1 << 16);

        public float DuckDepth     = 0.5f;
        public float DuckAttackMs  = 5.0f;
        public float DuckReleaseMs = 80.0f;

        public HeadlessRig()
        {
            Effects = new TelemetryEffect[]
            {
                EnginePulse, RoadBumps, TractionLoss, GearShift, AbsClick,
                PitLimiter, Drs, Collision, RevLimiter, Airborne,
            };
            foreach (var fx in Effects) Mixer.Add(fx);

            Ducking.EnginePulse  = EnginePulse;
            Ducking.RoadBumps    = RoadBumps;
            Ducking.TractionLoss = TractionLoss;
            Ducking.GearShift    = GearShift;
            Ducking.AbsClick     = AbsClick;
            Ducking.PitLimiter   = PitLimiter;
            Ducking.Drs          = Drs;
            Ducking.Collision    = Collision;
            Ducking.RevLimiter   = RevLimiter;
            Ducking.Airborne     = Airborne;

            Loop = new EngineLoop(Mixer, Ducking, Sink);
        }

        /// <summary>Replay a capture end to end. For every fixture row: emit it
        /// (enrich + effect fan-out, mirroring DispatchFrame) then run one
        /// engine tick per elapsed fixture millisecond until the next row.
        /// Returns total engine ticks run.</summary>
        public long Run(CsvFrameReader.Capture capture)
        {
            var source = new ReplayFrameSource(capture, virtualTime: true);
            source.OnFrame = f =>
            {
                // Headless overlay: the fixture frame already carries the
                // SimHub-side fields (the recording plugin overlaid them), so
                // the overlay snapshot echoes the frame — Enrich becomes a
                // no-op on those fields and still derives collisions, exactly
                // as a live run whose caches are in sync.
                var overlay = new SimHubOverlay
                {
                    MaxRpm           = f.MaxRpm,
                    AbsActive        = f.AbsActive,
                    RedlineRpm       = f.RedlineRpm,
                    PitLimiterActive = f.PitLimiterActive,
                    DrsActive        = f.DrsActive,
                };
                FrameEnricher.Enrich(ref f, sourceIsEnhanced: true, overlay,
                                     suppressRedlineOverlay: false);
                for (int i = 0; i < Effects.Length; i++)
                {
                    try { Effects[i].OnTelemetry(f); } catch { }
                }
            };

            long ticks = 0;
            var buf = new float[EngineLoop.BatchSamples];
            long lastUs = capture.Rows.Count > 0 ? capture.Rows[0].TUs : 0;

            while (source.StepOne())
            {
                long nowUs = source.VirtualNowUs;
                long dtMs  = Math.Max(1, (nowUs - lastUs) / 1000);
                lastUs = nowUs;
                for (long t = 0; t < dtMs; t++)
                {
                    Loop.RunOneTick(buf, DuckDepth, DuckAttackMs, DuckReleaseMs);
                    DuckL0Trace.Add(Ducking.DuckL0);
                    ticks++;
                }
            }
            return ticks;
        }
    }
}
