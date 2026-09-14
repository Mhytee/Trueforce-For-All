using System;

namespace TrueforceForAll.Core
{
    /// <summary>The soft lock for the takeover routes (iRacing, and RaceRoom on
    /// its shared-memory route): a wall at the car's steering limit, built from
    /// the sim's own steering position and lock. Named for iRacing, where it
    /// was built first.
    ///
    /// Both sims render their stop as part of the force-feedback output the
    /// takeover replaces, and the steering torque they publish is the physics
    /// torque with no stop in it. So the wall is authored here, in the shape
    /// CSP's custom soft lock uses (the one the AC bridge exports), so all
    /// three games land on the same blend in the plugin: cancel force opposing
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

        /// <summary>RaceRoom: the wheel's position over the car's lock, from the
        /// sim's own numbers. axis is the wheel's position, -1..1 over the
        /// wheel rotation set in the game's controller profile
        /// (steer_wheel_max_rotation, Manual only: 180..1800 degrees); the
        /// plugin feeds the PHYSICAL position from the HID axis, because the
        /// sim's steer_input_raw is normalised to the car's lock and clamps at
        /// 1 there (rig, 2026-09-13), so it cannot say how far past the lock
        /// the wheel is. The car's own
        /// steering wheel rotation is steer_wheel_range_degrees, both full left
        /// to full right, so the car's lock sits at carRotation over
        /// wheelRotation on the axis. steer_lock_degrees is the ROAD wheel's
        /// lock and plays no part: the first cut used it as the steering
        /// wheel's and put the wall a tenth of the way in (rig, 2026-09-13).
        /// NaN when the rotation is Auto or N/A, or either number is unusable:
        /// the caller treats that as no lock, and on Auto the game sets the
        /// wheel's own range to the car's, so the wheel's stop already is the
        /// lock.</summary>
        public static float R3ESteer(float axis, int carRotationDeg, int wheelRotationDeg)
        {
            if (wheelRotationDeg < 180 || wheelRotationDeg > 1800) return float.NaN;
            if (carRotationDeg < 40 || carRotationDeg > 1800) return float.NaN;
            if (float.IsNaN(axis) || float.IsInfinity(axis)) return float.NaN;
            return axis * wheelRotationDeg / carRotationDeg;
        }

        /// <summary>Give a steer magnitude the sign the lock stage expects: the
        /// direction back toward centre in the takeover's authored space, which
        /// is opposite to the physical wheel's position (positive is right on
        /// the HID axis, and a positive authored value pulls right). For sims
        /// whose own steer sign convention is undocumented (RaceRoom). 0 when
        /// the physical position is unknown or centred, which reads as no lock
        /// rather than a guess.</summary>
        public static float SignedByPhysical(float magnitude, float physicalSteer)
        {
            if (float.IsNaN(physicalSteer) || physicalSteer == 0f) return 0f;
            if (float.IsNaN(magnitude) || float.IsInfinity(magnitude)) return 0f;
            float m = Math.Abs(magnitude);
            return physicalSteer > 0f ? -m : m;
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
