using System;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Sub-quantization deadband for the local avatar uplink. Byte-identical suppression
    /// (<see cref="BasisAvatarIdleSuppression"/>) almost never fires in VR because sensor noise
    /// crosses quantization steps every frame; these primitives let the sender compare RAW
    /// (pre-quantization) values against the values of the last frame actually sent and drop the
    /// frame when every field moved less than a visibility threshold.
    ///
    /// Comparing against the last SENT frame (not the previous frame) bounds the standing error to
    /// the threshold itself: slow drift accumulates until it exceeds the deadband and then sends.
    /// Thresholds are chosen at or below the wire's own High-quality quantization step, so a
    /// suppressed change could barely have been expressed on the wire in the first place — and the
    /// noise this suppresses is exactly the shimmer receivers otherwise have to low-pass away.
    ///
    /// Pure C# so BasisServerTests can cover it; quaternion dots run in double because the
    /// tightest thresholds (cos of hundredths of a degree) sit inside float epsilon of 1.0.
    /// </summary>
    public static class BasisAvatarDeadband
    {
        /// <summary>Bone-rotation deadband. ~2.5× the 12-bit High quantization step (0.04°).</summary>
        public const float BoneAngleDegrees = 0.10f;
        /// <summary>Body/hips orientation deadband — long lever arm, kept tighter.</summary>
        public const float RootAngleDegrees = 0.05f;
        /// <summary>Hips world-position deadband per axis (metres).</summary>
        public const float PositionMeters = 0.002f;
        /// <summary>Hips local-delta deadband per axis (metres); wire step is ~30 µm.</summary>
        public const float HipsDeltaMeters = 0.0015f;
        /// <summary>End-effector target position deadband per axis (metres).</summary>
        public const float EffectorPositionMeters = 0.002f;
        /// <summary>Avatar scale deadband (posit16 wire; scale is normally bit-stable).</summary>
        public const float ScaleUnits = 1e-4f;

        /// <summary>|dot| threshold equivalent to an angle deadband: cos(angle/2).</summary>
        public static double MinAbsDotForAngleDegrees(float degrees)
            => Math.Cos(degrees * (Math.PI / 180.0) * 0.5);

        /// <summary>
        /// True when every quaternion pair (flattened xyzw, length = multiple of 4) is within the
        /// angle expressed by <paramref name="minAbsDot"/>. Sign-insensitive (double cover).
        /// </summary>
        public static bool QuatsWithin(ReadOnlySpan<float> current, ReadOnlySpan<float> lastSent, double minAbsDot)
        {
            if (current.Length != lastSent.Length || (current.Length & 3) != 0) return false;
            for (int i = 0; i < current.Length; i += 4)
            {
                double dot = (double)current[i] * lastSent[i]
                           + (double)current[i + 1] * lastSent[i + 1]
                           + (double)current[i + 2] * lastSent[i + 2]
                           + (double)current[i + 3] * lastSent[i + 3];
                // !(>=) rather than (<) so a NaN dot returns false (forces send), matching ValuesWithin.
                if (!(Math.Abs(dot) >= minAbsDot)) return false;
            }
            return true;
        }

        /// <summary>
        /// True when every scalar delta |current − lastSent| is at most <paramref name="maxAbsDelta"/>.
        /// Non-finite values never pass (a NaN must not wedge the suppressor).
        /// </summary>
        public static bool ValuesWithin(ReadOnlySpan<float> current, ReadOnlySpan<float> lastSent, float maxAbsDelta)
        {
            if (current.Length != lastSent.Length) return false;
            for (int i = 0; i < current.Length; i++)
            {
                float d = current[i] - lastSent[i];
                if (!(d <= maxAbsDelta && d >= -maxAbsDelta)) return false;
            }
            return true;
        }
    }
}
