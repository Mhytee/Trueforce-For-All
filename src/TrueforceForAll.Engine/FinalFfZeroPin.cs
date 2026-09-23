// Assetto Corsa's finalFF is the game's post-gain force output. With the
// in-game force feedback gain at 0 it reads exactly zero forever, and a
// plugin that re-injects it would silently mute the wheel. Real driving
// force is never exactly zero for long: road noise and self-aligning torque
// keep it moving whenever the car is moving. So "the car has been driving
// and the value has been exactly zero the whole time" is the signature of a
// zeroed gain (or a sim that is not driving this wheel), and the caller
// hands the force back to the wire tap until a non-zero value appears.
//
// Pure: time and the clock rate are passed in, so it is unit-tested directly.

using System;

namespace TrueforceForAll.Core
{
    public sealed class FinalFfZeroPin
    {
        /// <summary>Below this the car is not driving hard enough to expect
        /// force: a parked or creeping car can legitimately read zero.</summary>
        public const float DrivingSpeedKmh = 20f;

        /// <summary>Exact-zero force while driving must last this long
        /// before the value is called pinned. Long enough to ride out a
        /// straight with the wheel dead centre.</summary>
        public const double PinAfterSeconds = 3.0;

        private const float ZeroEps = 1e-4f;

        private long _zeroRunStartTicks;   // 0 = no run in progress
        private bool _pinned;

        public bool IsPinned => _pinned;

        /// <summary>Feed one physics tick. Returns the pinned state after it.</summary>
        public bool Note(float finalFf, float speedKmh, long nowTicks, long ticksPerSecond)
        {
            bool zero = Math.Abs(finalFf) < ZeroEps;
            bool driving = speedKmh > DrivingSpeedKmh;

            if (!zero)
            {
                _zeroRunStartTicks = 0;
                _pinned = false;
                return false;
            }
            if (!driving)
            {
                // A zero at rest says nothing either way: keep the current
                // verdict, but do not let a stop count toward a pin.
                _zeroRunStartTicks = 0;
                return _pinned;
            }
            if (_zeroRunStartTicks == 0)
            {
                _zeroRunStartTicks = nowTicks == 0 ? 1 : nowTicks;
                return _pinned;
            }
            double runSec = (nowTicks - _zeroRunStartTicks) / (double)ticksPerSecond;
            if (runSec >= PinAfterSeconds) _pinned = true;
            return _pinned;
        }

        public void Reset()
        {
            _zeroRunStartTicks = 0;
            _pinned = false;
        }
    }
}
