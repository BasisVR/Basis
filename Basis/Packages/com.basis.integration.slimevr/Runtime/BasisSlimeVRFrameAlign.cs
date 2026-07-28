#if BASIS_FRAMEWORK_EXISTS
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Integration.SlimeVR
{
    /// <summary>
    /// Solves the rigid transform from SlimeVR's coordinate frame into Basis/Unity playspace, from a
    /// set of point correspondences (the same physical trackers seen in both frames). Both spaces are
    /// gravity-aligned and metric, so the only real degrees of freedom are a handedness flip
    /// (SlimeVR is right-handed, Unity left-handed), a yaw about up, a translation, and a uniform
    /// scale (which absorbs Basis's DeviceScale when Basis positions are read in scaled space). That
    /// makes the fit a closed-form 2D-yaw similarity — no SVD, no iterative solver — and it reports an
    /// RMS residual so the caller can refuse to trust a poor alignment.
    /// </summary>
    public static class BasisSlimeVRFrameAlign
    {
        public readonly struct FrameTransform
        {
            public readonly float Scale;
            public readonly Quaternion Yaw;
            public readonly Vector3 Translation;
            public readonly float ResidualMeters;
            public readonly int SampleCount;
            public readonly bool Valid;

            public FrameTransform(float scale, Quaternion yaw, Vector3 translation, float residualMeters, int sampleCount, bool valid)
            {
                Scale = scale;
                Yaw = yaw;
                Translation = translation;
                ResidualMeters = residualMeters;
                SampleCount = sampleCount;
                Valid = valid;
            }

            public static FrameTransform Invalid(int sampleCount) =>
                new FrameTransform(1f, Quaternion.identity, Vector3.zero, float.PositiveInfinity, sampleCount, false);

            /// <summary>Maps a handedness-flipped SlimeVR point (see <see cref="FlipHandedness"/>) into Basis space.</summary>
            public Vector3 Apply(Vector3 flippedSlimeVrPoint) => Scale * (Yaw * flippedSlimeVrPoint) + Translation;
        }

        /// <summary>
        /// SlimeVR (right-handed, Y up) to Unity (left-handed, Y up): negate Z. Any residual horizontal
        /// axis convention is absorbed by the free yaw the solve recovers, so this single flip is
        /// sufficient for every gravity-aligned SlimeVR orientation.
        /// </summary>
        public static Vector3 FlipHandedness(Vector3 slimeVrPoint) => new Vector3(slimeVrPoint.x, slimeVrPoint.y, -slimeVrPoint.z);

        public static Vector3 FlipHandedness(SlimeVRVec3 p) => new Vector3(p.X, p.Y, -p.Z);

        /// <summary>
        /// The rotation partner of the negate-Z position flip. For the coordinate reflection
        /// C = diag(1,1,-1), a rotation R maps to C·R·C, whose quaternion is (-x, -y, z, w). Derived,
        /// not guessed; and any SlimeVR horizontal-axis convention difference reduces to a constant yaw
        /// that the HMD anchor removes and the relative calibration offset absorbs — so only the
        /// handedness (which this fixes) actually has to be right.
        /// </summary>
        public static Quaternion FlipHandedness(SlimeVRQuat q) => new Quaternion(-q.X, -q.Y, q.Z, q.W);

        /// <summary>
        /// A one-anchor SlimeVR→Basis placement built from the HMD seen in both frames. Both spaces are
        /// gravity-aligned, so the HMD's position and yaw fully determine the transform (no other shared
        /// tracker required — which is what lets the "force" mode work when Basis has no other trackers).
        /// </summary>
        public readonly struct HmdAnchor
        {
            public readonly Vector3 SlimeHmdFlipped;
            public readonly Vector3 BasisHmdPosition;
            public readonly Quaternion Yaw;
            public readonly bool Valid;

            public HmdAnchor(Vector3 slimeHmdFlipped, Vector3 basisHmdPosition, Quaternion yaw, bool valid)
            {
                SlimeHmdFlipped = slimeHmdFlipped;
                BasisHmdPosition = basisHmdPosition;
                Yaw = yaw;
                Valid = valid;
            }

            public Vector3 ApplyPosition(Vector3 flippedSlimePoint) => BasisHmdPosition + Yaw * (flippedSlimePoint - SlimeHmdFlipped);
            public Quaternion ApplyRotation(Quaternion unitySlimeRot) => Yaw * unitySlimeRot;
        }

        /// <summary>
        /// Builds the HMD anchor from the HMD pose in each frame (SlimeVR raw, Basis Unity). Yaw is the
        /// horizontal heading difference between the two HMD forwards; pitch/roll are shared (gravity).
        /// </summary>
        public static HmdAnchor BuildHmdAnchor(Vector3 basisHmdPos, Quaternion basisHmdRot, SlimeVRVec3 slimeHmdPos, SlimeVRQuat slimeHmdRot)
        {
            Vector3 slimeFlipped = FlipHandedness(slimeHmdPos);
            Quaternion slimeUnityRot = FlipHandedness(slimeHmdRot);

            float basisYaw = HorizontalYawDegrees(basisHmdRot);
            float slimeYaw = HorizontalYawDegrees(slimeUnityRot);
            if (float.IsNaN(basisYaw) || float.IsNaN(slimeYaw))
            {
                return new HmdAnchor(slimeFlipped, basisHmdPos, Quaternion.identity, false);
            }
            Quaternion yaw = Quaternion.AngleAxis(basisYaw - slimeYaw, Vector3.up);
            return new HmdAnchor(slimeFlipped, basisHmdPos, yaw, true);
        }

        private static float HorizontalYawDegrees(Quaternion rot)
        {
            Vector3 fwd = rot * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f)
            {
                // Looking near-vertical: fall back to the projected up vector for a stable heading.
                fwd = rot * Vector3.up;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-6f)
                {
                    return float.NaN;
                }
            }
            return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Closed-form yaw+scale+translation fit of <paramref name="source"/> onto <paramref name="target"/>.
        /// Source points must already be handedness-flipped. Returns an invalid transform (infinite
        /// residual) when there are fewer than three correspondences or the geometry is degenerate.
        /// </summary>
        public static FrameTransform Solve(IReadOnlyList<Vector3> source, IReadOnlyList<Vector3> target)
        {
            int n = Mathf.Min(source.Count, target.Count);
            if (n < 3)
            {
                return FrameTransform.Invalid(n);
            }

            Vector3 cs = Vector3.zero, ct = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                cs += source[i];
                ct += target[i];
            }
            cs /= n;
            ct /= n;

            // Optimal yaw about up from the 2D (x,z) Procrustes: theta = atan2(sum a x b, sum a . b).
            float cross = 0f, dot = 0f, sourceSpread = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = source[i] - cs;
                Vector3 b = target[i] - ct;
                cross += a.x * b.z - a.z * b.x;
                dot += a.x * b.x + a.z * b.z;
                sourceSpread += a.sqrMagnitude;
            }
            if (sourceSpread < 1e-6f)
            {
                return FrameTransform.Invalid(n); // coincident source points — no orientation to recover
            }

            float theta = Mathf.Atan2(cross, dot) * Mathf.Rad2Deg;

            // The 2D formula's sign convention may not match Quaternion.AngleAxis about up; pick whichever
            // of +/-theta actually fits better rather than hand-deriving the handedness of the yaw.
            FrameTransform best = FrameTransform.Invalid(n);
            for (int s = 0; s < 2; s++)
            {
                Quaternion yaw = Quaternion.AngleAxis(s == 0 ? theta : -theta, Vector3.up);
                float num = 0f;
                for (int i = 0; i < n; i++)
                {
                    Vector3 a = source[i] - cs;
                    Vector3 b = target[i] - ct;
                    num += Vector3.Dot(yaw * a, b);
                }
                float scale = num / sourceSpread;
                if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 1e-3f || scale > 100f)
                {
                    continue;
                }
                Vector3 translation = ct - scale * (yaw * cs);

                float sqSum = 0f;
                for (int i = 0; i < n; i++)
                {
                    Vector3 mapped = scale * (yaw * (source[i] - cs));
                    sqSum += (mapped - (target[i] - ct)).sqrMagnitude;
                }
                float residual = Mathf.Sqrt(sqSum / n);
                if (residual < best.ResidualMeters)
                {
                    best = new FrameTransform(scale, yaw, translation, residual, n, true);
                }
            }
            return best;
        }
    }
}
#endif
