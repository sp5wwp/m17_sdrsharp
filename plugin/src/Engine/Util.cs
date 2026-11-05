// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System.Diagnostics;

namespace SDRSharp.M17
{
    /// <summary>Small helpers that behave the same on every .NET version.</summary>
    public static class Util
    {
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>Monotonic milliseconds.</summary>
        public static long Milliseconds => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
    }
}
