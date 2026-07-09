// The engine's render tick, extracted from TrueforcePlugin.ProducerLoop in
// phase 0c of docs/haptic-engine-plan.md. One RunOneTick = one ducking
// arbitration + one 4-sample mixer render + silence floor + push to the sink.
//
// In production the plugin's producer thread calls RunOneTick in a loop and
// the sink is TrueforceDevice.PushFloats, whose ring back-pressure paces the
// loop to exactly 1 kHz (4 samples/ms at the 4 kHz stream rate). In the
// harness the sink is a capture buffer and the loop is driven by a for-loop
// on virtual time — same code path, deterministic.
//
// Phase 1 grows this class into the real engine clock (frame queue, jitter
// buffer, resampler, 500 Hz effect ticking); phase 0c is the move-only home.

using System;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Core
{
    /// <summary>Where rendered samples go. TrueforceDevice in production
    /// (adapter in the plugin), a capture buffer in the harness.</summary>
    public interface ISampleSink
    {
        /// <summary>Consume <paramref name="count"/> samples. May block (the
        /// device ring's back-pressure is what paces the production loop).</summary>
        void Push(float[] samples, int count);
    }

    public sealed class EngineLoop
    {
        /// <summary>Samples per tick. 4 samples at the 4 kHz stream rate =
        /// one 1 ms device packet per tick.</summary>
        public const int BatchSamples = 4;

        /// <summary>Effects tick every Nth engine tick: 1 kHz loop / 2 =
        /// 500 Hz effect updates (the spec's 400 Hz floor, and it divides the
        /// device rate exactly).</summary>
        public const int EffectTickDivisor = 2;

        // Float-space silence floor. Samples with |v| < this are zeroed so the
        // u16 conversion produces exactly 0x8000 — TrueforceDevice's silence
        // detection requires exact-center samples to choose the keepalive
        // packet shape. ~3e-4 corresponds to ±10 LSB out of 32767, well below
        // any perceptible content but above floating-point noise.
        public const float SilenceFloor = 3e-4f;

        private readonly Mixer _mixer;
        private readonly DuckingController _ducking;   // may be null (bare rigs)
        private readonly ISampleSink _sink;
        private readonly FrameResampler _resampler;    // may be null (legacy rigs)
        private readonly EventDeriver _events = new EventDeriver();
        private long _tick;

        /// <summary>The effects ticked at 500 Hz with resampled frames.
        /// Assigned by the host after construction (the plugin builds effects
        /// after its settings load). Null/empty = no effect ticking (phase-0c
        /// behavior, where the host fans OnTelemetry out itself).</summary>
        public TelemetryEffect[] Effects;

        public EngineLoop(Mixer mixer, DuckingController ducking, ISampleSink sink,
                          FrameResampler resampler = null)
        {
            _mixer     = mixer ?? throw new ArgumentNullException(nameof(mixer));
            _ducking   = ducking;
            _sink      = sink ?? throw new ArgumentNullException(nameof(sink));
            _resampler = resampler;
        }

        /// <summary>Source thread: hand a (already enriched) frame to the
        /// engine. Replaces the host's direct OnTelemetry fan-out: effects now
        /// see fixed-rate resampled frames on the engine thread, which also
        /// puts Tick and RenderAdd on the SAME thread.</summary>
        public void IngestFrame(in TelemetryFrame frame) => _resampler?.Ingest(in frame);

        /// <summary>Diagnostics: the resampler, if this loop owns one.</summary>
        public FrameResampler Resampler => _resampler;

        // Stall settle handshake. The host's watchdog thread detects the
        // telemetry stall, but the settle itself (resampler reset + the
        // OnTelemetryStall fan-out) must run on THIS thread, at a tick
        // boundary: settling from the watchdog thread can interleave with an
        // in-flight effect tick that already sampled the held pre-stall
        // frame, and an OnTelemetry(heldFrame) landing after
        // OnTelemetryStall re-latches the sustained amplitude with nothing
        // left to silence it (the ring is empty afterwards, so no further
        // ticks arrive).
        private volatile bool _stallSettleRequested;

        /// <summary>Any thread: telemetry went stale; reset the resampler and
        /// silence sustained effects at the top of the next engine tick.</summary>
        public void RequestStallSettle() => _stallSettleRequested = true;

        /// <summary>One engine tick: [every 2nd tick: resample → effects tick]
        /// → ducking → render → silence floor → push. <paramref name="nowTicks"/>
        /// is the clock in the resampler's tick units (Stopwatch in production,
        /// the fixture's virtual clock in the harness). <paramref name="buf"/>
        /// is caller-owned scratch (≥ BatchSamples). Exceptions from effects /
        /// ducking / render are contained (batch pushed as silence, caller owns
        /// logging); sink exceptions propagate — a dead device is the
        /// production loop's shutdown signal.</summary>
        public void RunOneTick(float[] buf, long nowTicks,
                               float duckDepth, float duckAttackMs, float duckReleaseMs,
                               Action<Exception> onRenderError = null)
        {
            try
            {
                _tick++;

                // Consume a pending stall settle BEFORE sampling: same thread
                // as the effect ticks, so no stale in-flight frame can land
                // after the silencing (see RequestStallSettle).
                if (_stallSettleRequested)
                {
                    _stallSettleRequested = false;
                    _resampler?.Reset();
                    var settleFx = Effects;
                    if (settleFx != null)
                    {
                        for (int i = 0; i < settleFx.Length; i++)
                        {
                            try { settleFx[i].OnTelemetryStall(); }
                            catch { /* one bad effect must not starve the rest */ }
                        }
                    }
                }

                if (_resampler != null && (_tick % EffectTickDivisor) == 0)
                {
                    var fx = Effects;
                    if (fx != null && _resampler.TrySample(nowTicks, out var frame))
                    {
                        // Phase 2 CTM stage: caps + axle rollups + edge events
                        // on the RESAMPLED frame, so effects see them at the
                        // fixed 500 Hz rate the deriver's hysteresis assumes.
                        // Caps first (Native from field presence), then each
                        // deriving stage adds its own Derived bit — effects
                        // trust Events/rollups only when the bit says the
                        // stage actually ran.
                        frame.Caps = SignalCaps.InferNative(in frame);
                        CtmComposer.Compose(ref frame);
                        frame.Events = _events.Derive(in frame);
                        frame.Caps |= SignalGroups.DerivedEvents;
                        for (int i = 0; i < fx.Length; i++)
                        {
                            try { fx[i].OnTelemetry(frame); }
                            catch { /* one bad effect must not starve the rest */ }
                        }
                    }
                }

                _ducking?.Update(duckDepth, duckAttackMs, duckReleaseMs);
                _mixer.Render(buf, BatchSamples);
                for (int i = 0; i < BatchSamples; i++)
                {
                    float v = buf[i];
                    if (v < SilenceFloor && v > -SilenceFloor) buf[i] = 0f;
                }
            }
            catch (Exception ex)
            {
                onRenderError?.Invoke(ex);
                Array.Clear(buf, 0, BatchSamples);
            }

            _sink.Push(buf, BatchSamples);
        }
    }
}
