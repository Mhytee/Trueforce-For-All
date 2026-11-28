// One playback of a recorded telemetry clip into ONE effect: what a Test
// button in the settings UI runs. EngineLoop owns these and ticks them on the
// engine thread at the 500 Hz effect tick (see EngineLoop.StartClipTest).
//
// The point is fidelity. The clip's frames take the route a live frame takes:
// enrichment, a FrameResampler (so an 80 Hz Forza clip is predicted up to
// 500 Hz exactly as it is on track), the CTM stage, then the effect's own
// OnTelemetry. Nothing writes the effect's amplitude directly, so the test
// runs the same detection, thresholds and smoothing as a drive, and whatever
// the user has the sliders set to shapes it.
//
// Each test has a private resampler and event deriver. Both hold state, and
// sharing the live ones would let a clip's gear change or kerb edge leak into
// every other effect, and the live car's into the clip.
//
// Two things about the clip's car never reach the effect:
//   - Its cylinder count. The engine layout is the user's (settings, car
//     facts); a clip that overwrote it would leave the next real drive
//     sounding like the clip's car.
//   - Its rev range, when the caller knows the user's. rpmScale maps the
//     clip's revs onto the live car's, so the engine note sits where that
//     car's does and the redline buzz meets that car's redline.

using System;
using System.Collections.Generic;
using TrueforceForAll.Plugin.Effects;

namespace TrueforceForAll.Core
{
    public sealed class EffectClipTest
    {
        /// <summary>How long the effect keeps rendering after the clip's last
        /// frame, so a thud or a decay that began near the end rings out
        /// instead of being chopped (and a disabled effect is still audible
        /// for it).</summary>
        public const int TailMs = 300;

        private readonly TelemetryEffect _effect;
        private readonly IReadOnlyList<ReplayRow> _rows;
        private readonly double _rpmScale;
        private readonly long _startTicks;
        private readonly long _firstUs;
        private readonly long _durationUs;

        private readonly FrameResampler _resampler = new FrameResampler(1_000_000);   // clip time is µs
        private readonly EventDeriver _events = new EventDeriver();
        private int  _cursor;
        private bool _feedEnded;

        /// <param name="startTicks">When playback starts, on the clock the
        /// owning loop is ticked with.</param>
        /// <param name="rpmScale">Multiplier on the clip's RPM, max RPM and
        /// redline (see the header). 1 plays the clip's own car.</param>
        public EffectClipTest(TelemetryEffect effect, CsvFrameReader.Capture clip,
                              long startTicks, double rpmScale = 1.0)
        {
            _effect = effect ?? throw new ArgumentNullException(nameof(effect));
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (clip.Rows.Count < 2) throw new ArgumentException("clip needs at least two rows", nameof(clip));

            _rows       = clip.Rows;
            _startTicks = startTicks;
            _firstUs    = _rows[0].TUs;
            _durationUs = _rows[_rows.Count - 1].TUs - _firstUs;
            _rpmScale   = (rpmScale > 0 && !double.IsNaN(rpmScale) && !double.IsInfinity(rpmScale))
                            ? rpmScale : 1.0;
        }

        public TelemetryEffect Effect => _effect;

        /// <summary>Length of the recorded clip.</summary>
        public int DurationMs => (int)(_durationUs / 1000);

        /// <summary>Clip plus ring-out tail: how long the test occupies the effect.</summary>
        public int TotalMs => DurationMs + TailMs;

        /// <summary>Engine thread, once per effect tick. Returns false once the
        /// test is over (the effect has been handed back to live telemetry).</summary>
        internal bool Tick(long nowTicks, long ticksPerSecond)
        {
            // Whole seconds and remainder separately: the multiply cannot
            // overflow however long the loop was parked before resuming.
            long elapsed = Math.Max(0, nowTicks - _startTicks);
            long nowUs = (elapsed / ticksPerSecond) * 1_000_000L
                       + (elapsed % ticksPerSecond) * 1_000_000L / ticksPerSecond;

            if (!_feedEnded)
            {
                if (nowUs > _durationUs)
                {
                    // The clip ran out, which to the effect is its telemetry
                    // stopping: sustained voices drop, envelopes ring out.
                    _feedEnded = true;
                    _effect.OnTelemetryStall();
                }
                else
                {
                    while (_cursor < _rows.Count && _rows[_cursor].TUs - _firstUs <= nowUs)
                        Ingest(_rows[_cursor++]);

                    if (_resampler.TrySample(nowUs, out var frame))
                    {
                        EngineLoop.ComposeFrame(ref frame, _events);
                        _effect.OnTelemetry(frame);
                    }
                }
            }

            if (nowUs > _durationUs + TailMs * 1000L)
            {
                _effect.EndClipTest();
                return false;
            }
            return true;
        }

        private void Ingest(ReplayRow row)
        {
            TelemetryFrame f = row.Frame;   // struct copy: the shared clip is never written to

            f.NumCylinders     = null;
            f.EngineIsElectric = null;
            if (_rpmScale != 1.0)
            {
                f.Rpms       *= _rpmScale;
                f.MaxRpm     *= _rpmScale;
                f.RedlineRpm *= _rpmScale;
            }

            // Recordings are of enriched frames, so the SimHub overlay has
            // nothing to add; what is left of enrichment is deriving a
            // collision from an accel spike on frames that carry none.
            var noOverlay = default(SimHubOverlay);
            FrameEnricher.Enrich(ref f, sourceIsEnhanced: false, in noOverlay,
                                 suppressRedlineOverlay: true);

            f.CapturedAtTicks = row.TUs - _firstUs;
            _resampler.Ingest(in f);
        }
    }
}
