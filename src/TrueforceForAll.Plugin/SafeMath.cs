// Centralized numeric sanitization gate for FFB / audio parameter ingress.
// Rejects NaN / +-Infinity and clamps values to safe ranges so a malformed or
// malicious preset can't push the wheel motor (or audio path) into a damaging
// state. Called from every ApplyXxx site in TrueforcePlugin that pulls user
// numbers off a deserialized preset, and from FiringPatterns.ParseCustom on
// each parsed amplitude.
using System;

namespace TrueforceForAll.Plugin
{
    internal static class SafeMath
    {
        public static float SafeFloat(float v, float min, float max, float fallback)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        public static double SafeDouble(double v, double min, double max, double fallback)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return fallback;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        public static int SafeInt(int v, int min, int max, int fallback)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
