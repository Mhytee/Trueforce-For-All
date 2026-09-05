// Turns a stream of audio peak readings into a bar fill of 0..1 that actually
// uses the bar.
//
// This is the third attempt and the first one measured against real audio, so
// the two that failed are worth recording. Both anchored the scale to something
// fixed and let the program fall where it may:
//
//   1. Bar spans a fixed window below 0 dBFS. Music through the owner's rig
//      peaks at -9 dB, so a 45 dB window put the whole track between levels 3
//      and 5 of ten. Read as "frozen at a level". Narrowing the window does not
//      help: it slides the same narrow band down the strip, and a quieter
//      source (-26 dB, measured on the same rig minutes later) went fully dark.
//   2. Bar spans a fixed window below a ceiling that follows the loudest recent
//      peak. Worse, and instructively so: with an instant attack the ceiling IS
//      the current sample whenever the signal reaches a new local maximum,
//      which for music is most of the time, so the bar pinned at full 93% of
//      the capture. Slowing the ceiling's fall did not fix it (45% at 1 dB/s);
//      the meter was measuring the signal against itself.
//
// The measurements underneath that: two captures from the same machine minutes
// apart spanned 13 dB and 3.6 dB of program range. No fixed window suits both,
// which is the whole problem in one sentence.
//
// So both ends move. The bar is stretched between the quietest and loudest the
// program has been over the last few seconds, which fills the strip whatever
// the material and whatever the system volume.

using System;

namespace TrueforceForAll.Core
{
    public sealed class AudioLevelEnvelope
    {
        /// <summary>Below this, a reading is silence: it lights nothing and
        /// teaches the envelope nothing.
        ///
        /// An absolute gate is not optional in a design where both ends of the
        /// scale move, and this is the failure that proved it. Windows endpoints
        /// do not necessarily report a true zero when nothing is playing: the
        /// owner's Voicemeeter virtual device returns 2.328e-10, which is
        /// -192.7 dBFS, on every single sample forever. Fifteen seconds of that
        /// filled the window with one constant value, the envelope scaled itself
        /// onto the noise floor as if it were the program, and the whole strip
        /// lit up in a silent room.
        ///
        /// -70 dBFS is about three ten-thousandths of full scale: inaudible on
        /// any normal setup, and comfortably above the idle floor of a real
        /// sound card as well as a virtual one.</summary>
        public const double SilenceDb = -70.0;

        /// <summary>How narrow a program the bar will still stretch across its
        /// full length, in decibels. Below this the strip stops opening up, so
        /// a held note reads as a steady bar rather than as its own noise
        /// amplified to full scale.
        ///
        /// Fixed rather than a setting: it was a "Contrast" slider for one
        /// afternoon and the owner's verdict was that it only ever wanted to be
        /// at maximum, which is this value. A control with one useful position
        /// is a control that should not exist.</summary>
        public const int MinSpanDb = 3;

        /// <summary>How far back "recently" reaches, and the one real trade-off
        /// left in here. A quieter track cannot be recognised until the louder
        /// one has aged out of the window, so this IS the time the strip spends
        /// looking wrong after a song change. Shorten it too far and a quiet
        /// passage within one song ages the loud part out and gets rescaled up,
        /// which is the second failed design in miniature.
        ///
        /// Measured both ways, dropping into a track 18 dB quieter and running a
        /// ten second quiet bridge inside one:
        ///
        ///     window   settles after   bridge wrongly rescaled
        ///       3 s        4.9 s                12%
        ///       5 s        6.9 s                 8%
        ///       8 s        8.9 s                 4%
        ///      15 s       17.0 s                 0%
        ///
        /// Settling costs about the window plus two seconds, so there is little
        /// left to win below five and the bridge artefact grows quickly. 15 was
        /// the first guess and the owner reported the wait; 5 is the pick.</summary>
        public const int WindowSeconds = 5;

        // One bucket per second, holding that second's loudest and quietest
        // reading. A ring of extremes rather than a decaying envelope, because a
        // decay with an instant attack is exactly what failed above: it lets the
        // current sample define its own reference.
        private readonly double[] _hi = new double[WindowSeconds];
        private readonly double[] _lo = new double[WindowSeconds];
        private bool[] _has = new bool[WindowSeconds];
        private double _accHi, _accLo;
        private bool _accHas;
        private double _bucketSec;

        public AudioLevelEnvelope() { Reset(); }

        public void Reset()
        {
            for (int i = 0; i < WindowSeconds; i++) _has[i] = false;
            _accHas = false;
            _bucketSec = 0;
        }

        /// <summary>Feed one linear peak reading (0..1) and the time since the
        /// last one, and get where the bar should sit, 0..1.</summary>
        public double Push(double peak, double elapsedSec)
        {
            // Advance the ring on the real clock, whether or not this reading
            // counts, so silence ages the window out instead of freezing it.
            if (elapsedSec > 0 && elapsedSec < 5.0)
            {
                _bucketSec += elapsedSec;
                if (_bucketSec >= 1.0)
                {
                    _bucketSec = 0;
                    for (int i = 0; i < WindowSeconds - 1; i++)
                    {
                        _hi[i] = _hi[i + 1];
                        _lo[i] = _lo[i + 1];
                        _has[i] = _has[i + 1];
                    }
                    _hi[WindowSeconds - 1] = _accHi;
                    _lo[WindowSeconds - 1] = _accLo;
                    _has[WindowSeconds - 1] = _accHas;
                    _accHas = false;
                }
            }

            if (!(peak > 0)) return 0;
            double db = 20.0 * Math.Log10(peak);
            if (db > 0) db = 0;
            if (db < SilenceDb) return 0;

            if (!_accHas) { _accHi = _accLo = db; _accHas = true; }
            else
            {
                if (db > _accHi) _accHi = db;
                if (db < _accLo) _accLo = db;
            }

            double hi = db, lo = db;
            for (int i = 0; i < WindowSeconds; i++)
            {
                if (!_has[i]) continue;
                if (_hi[i] > hi) hi = _hi[i];
                if (_lo[i] < lo) lo = _lo[i];
            }

            // A program narrower than the floor is CENTRED in it rather than
            // hung from the top. Both keep the division safe; centring also
            // means a steady tone sits mid-strip and moves either way around
            // itself, where hanging it from the top made a held note read as
            // full scale, which is the same wrong answer the noise floor used
            // to give.
            double span = hi - lo;
            if (span < MinSpanDb)
            {
                double mid = (hi + lo) * 0.5;
                lo = mid - MinSpanDb * 0.5;
                hi = mid + MinSpanDb * 0.5;
            }

            double v = (db - lo) / (hi - lo);
            return v <= 0 ? 0 : v >= 1 ? 1 : v;
        }
    }
}
