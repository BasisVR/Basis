using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;

namespace UnityEngine.Animations.Rigging
{
    /// <summary>
    /// 3D lookup table for arm bend direction, inspired by HVR-IK's HIKBendLookup.
    /// Maps normalized hand position in chest-relative space to an optimal bend direction.
    /// Uses trilinear interpolation over a pre-computed grid for smooth results.
    /// </summary>
    public struct BasisArmBendLookup
    {
        public const int GridSize = 11; // 11^3 = 1331 entries (lighter than HVR's 21^3)
        public const int GridSizeSq = GridSize * GridSize;
        public const int TotalEntries = GridSize * GridSize * GridSize;

        /// <summary>
        /// Generates a default lookup table with reasonable bend directions.
        /// The grid covers normalized space from -1 to +1 on each axis:
        /// X = left/right (positive = outward from shoulder)
        /// Y = up/down (positive = up)
        /// Z = forward/back (positive = forward)
        /// Values are bend direction vectors (where the elbow should point).
        /// </summary>
        public static Vector3[] GenerateDefaultTable(bool isLeft)
        {
            var table = new Vector3[TotalEntries];
            float step = 2f / (GridSize - 1);

            for (int iz = 0; iz < GridSize; iz++)
            for (int iy = 0; iy < GridSize; iy++)
            for (int ix = 0; ix < GridSize; ix++)
            {
                float x = -1f + ix * step; // outward
                float y = -1f + iy * step; // up
                float z = -1f + iz * step; // forward

                // Default bend direction heuristics (from HVR-IK's multi-factor approach):
                Vector3 bendDir;

                // How much the hand is outward vs inward
                float outwardness = Mathf.Clamp01(isLeft ? -x : x);
                // How much the hand is forward
                float forwardness = Mathf.Clamp01(z);
                // How much the hand is above
                float upness = Mathf.Clamp01(y);

                // Base: elbow bends backward and slightly down
                bendDir = new Vector3(0f, -0.3f, -1f);

                // When hand is forward, elbow goes more downward
                bendDir = Vector3.Lerp(bendDir, new Vector3(0f, -1f, -0.3f), forwardness * 0.6f);

                // When hand is above, elbow goes more backward
                bendDir = Vector3.Lerp(bendDir, new Vector3(0f, -0.5f, -1f), upness * 0.5f);

                // When hand is across body (inward), elbow goes outward and down
                float inwardness = Mathf.Clamp01(isLeft ? x : -x);
                bendDir = Vector3.Lerp(bendDir, new Vector3(isLeft ? -1f : 1f, -0.5f, 0f), inwardness * 0.4f);

                // When hand is behind, elbow goes up
                float behindness = Mathf.Clamp01(-z);
                bendDir = Vector3.Lerp(bendDir, new Vector3(0f, 1f, 0f), behindness * 0.5f);

                // When hand is below, elbow goes backward
                float downness = Mathf.Clamp01(-y);
                bendDir = Vector3.Lerp(bendDir, new Vector3(0f, 0f, -1f), downness * 0.3f);

                int idx = ix + iy * GridSize + iz * GridSizeSq;
                table[idx] = bendDir.normalized;
            }

            return table;
        }

        /// <summary>
        /// Trilinear interpolation lookup. Position should be in normalized space [-1, 1].
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 SampleTrilinear(NativeArray<Vector3> table, Vector3 normalizedPos)
        {
            // Map [-1,1] to [0, GridSize-1]
            float fx = (normalizedPos.x * 0.5f + 0.5f) * (GridSize - 1);
            float fy = (normalizedPos.y * 0.5f + 0.5f) * (GridSize - 1);
            float fz = (normalizedPos.z * 0.5f + 0.5f) * (GridSize - 1);

            // Clamp to valid grid range
            fx = Mathf.Clamp(fx, 0f, GridSize - 1.001f);
            fy = Mathf.Clamp(fy, 0f, GridSize - 1.001f);
            fz = Mathf.Clamp(fz, 0f, GridSize - 1.001f);

            int x0 = (int)fx; int x1 = Mathf.Min(x0 + 1, GridSize - 1);
            int y0 = (int)fy; int y1 = Mathf.Min(y0 + 1, GridSize - 1);
            int z0 = (int)fz; int z1 = Mathf.Min(z0 + 1, GridSize - 1);

            float tx = fx - x0;
            float ty = fy - y0;
            float tz = fz - z0;

            // 8 corner samples
            Vector3 c000 = table[x0 + y0 * GridSize + z0 * GridSizeSq];
            Vector3 c100 = table[x1 + y0 * GridSize + z0 * GridSizeSq];
            Vector3 c010 = table[x0 + y1 * GridSize + z0 * GridSizeSq];
            Vector3 c110 = table[x1 + y1 * GridSize + z0 * GridSizeSq];
            Vector3 c001 = table[x0 + y0 * GridSize + z1 * GridSizeSq];
            Vector3 c101 = table[x1 + y0 * GridSize + z1 * GridSizeSq];
            Vector3 c011 = table[x0 + y1 * GridSize + z1 * GridSizeSq];
            Vector3 c111 = table[x1 + y1 * GridSize + z1 * GridSizeSq];

            // Trilinear interpolation
            Vector3 c00 = Vector3.Lerp(c000, c100, tx);
            Vector3 c10 = Vector3.Lerp(c010, c110, tx);
            Vector3 c01 = Vector3.Lerp(c001, c101, tx);
            Vector3 c11 = Vector3.Lerp(c011, c111, tx);

            Vector3 c0 = Vector3.Lerp(c00, c10, ty);
            Vector3 c1 = Vector3.Lerp(c01, c11, ty);

            return Vector3.Lerp(c0, c1, tz).normalized;
        }
    }
}
