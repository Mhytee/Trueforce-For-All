using System;

namespace TrueforceForAll.Core
{
    /// <summary>The soft lock for the iRacing reshape: a wall at the car's
    /// steering limit, built from the sim's own steering angle and range.
    ///
    /// iRacing renders its stop as part of its force-feedback output, which is
    /// off on the reshape path, and the steering torque it publishes is the
    /// physics torque with no stop in it. So the wall is authored here, in the
    /// shape CSP's custom soft lock uses (the one the AC bridge exports), so
    /// both games land on the same blend in the plugin: cancel force opposing
    /// the steer, then lerp toward a target by an amount.
    ///
    /// Inputs are normalised to the CAR's lock: steer is the wheel angle over
    /// the per-side limit, so 1 is the car's full lock and beyond it is past.
    /// The band, padding and speed terms are CSP's defaults ([CUSTOM_SOFT_LOCK]
    /// PADDING 10, SHIFT_PADDING 0.8, SPEED_FACTOR 1).</summary>
    public static class IRacingSoftLock
    {
        /// <summary>Width of the band over which the lock ramps in, in degrees
        /// of wheel angle. Converted to the car's normalised steer per call.</summary>
        public const float PaddingDeg = 10f;
        /// <summary>How far the band slides outward past the car's limit: 0
        /// centres it on the limit, 1 puts the whole band beyond it.</summary>
        public const float ShiftPadding = 0.8f;
        /// <summary>Weight of the steer-speed term in the target.</summary>
        public const float SpeedFactor = 1f;

        /// <summary>Compute the blend inputs. amount is 0 when nowhere near the
        /// limit. steerSpeed is d(steer)/dt per second; lockDeg is the car's
        /// per-side lock in degrees (half of SteeringWheelAngleMax); strength is
        /// the force at the wall as a share of full scale, 0..1.
        ///
        /// target carries the sign of steer. In the reshape's authored sign
        /// space that is the direction back toward centre (see the plugin's
        /// TryComputeIRacingSoftLock for the derivation).</summary>
        public static void Compute(float steer, float steerSpeed, float lockDeg, float strength,
                                   out float amount, out float target)
        {
            amount = 0f;
            target = 0f;
            if (!(lockDeg > 1f)) return;
            if (float.IsNaN(steer) || float.IsInfinity(steer)) return;

            float pad = PaddingDeg / lockDeg;
            float mag = Math.Abs(steer);
            // The band straddles the limit and slides outward with the shift.
            float a = mag - 1f - ShiftPadding * pad;
            amount = InvLerpSat(a, -pad, pad);
            if (amount <= 0f) { amount = 0f; return; }

            if (float.IsNaN(strength) || strength < 0f) strength = 0f;
            else if (strength > 1f) strength = 1f;
            // The speed term pushes harder while the wheel is still moving into
            // the wall and eases as it comes back out. It fades over the next
            // band width past the limit, so a wheel held hard against the stop
            // settles on strength alone.
            float speedAware = InvLerpSat(mag - 1f, pad * 2f, 0f);
            if (float.IsNaN(steerSpeed) || float.IsInfinity(steerSpeed)) steerSpeed = 0f;
            float t = Math.Sign(steer) * strength + steerSpeed * speedAware * SpeedFactor;
            if (t > 1f) t = 1f; else if (t < -1f) t = -1f;
            target = t;
        }

        /// <summary>CSP's math.lerpInvSat: where x sits between a and b, clamped
        /// to 0..1. a may exceed b, which reverses the ramp.</summary>
        public static float InvLerpSat(float x, float a, float b)
        {
            float t = (x - a) / (b - a);
            return t < 0f ? 0f : (t > 1f ? 1f : t);
        }
    }
}
