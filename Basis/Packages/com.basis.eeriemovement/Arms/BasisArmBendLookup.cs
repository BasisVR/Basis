using System.Runtime.CompilerServices;
using Unity.Collections;

using UnityEngine;
namespace Basis.IK
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
        public static Vector3[] GenerateDefaultTable()
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
                // How much the hand is forward
                float forwardness = Mathf.Clamp01(z);
                // How much the hand is above
                float upness = Mathf.Clamp01(y);

                // Base: elbow bends backward and slightly down
                bendDir = new Vector3(0f, -0.3f, -1f);

                // When hand is forward, elbow goes more downward
                bendDir = Vector3.Lerp(bendDir, new Vector3(0f, -1f, -0.3f), forwardness * 0.6f);

                // When hand is above, the elbow goes down and OUT (not straight back). A purely-backward
                // bend runs nearly parallel to the shoulder->hand axis for an up-forward reach, which
                // collapses the elbow pole (the swivel becomes unconstrained and the elbow flips UP).
                // Down-and-out keeps the pole perpendicular to the arm, so the elbow follows it -- the
                // natural high-reach pose (elbow flares to the side) instead of winging up/in front.
                bendDir = Vector3.Lerp(bendDir, new Vector3(0.7f, -0.8f, -0.2f), upness * 0.5f);

                // When hand is across body (inward), elbow goes outward and down
                float inwardness = Mathf.Clamp01(-x);
                bendDir = Vector3.Lerp(bendDir, new Vector3(1f, -0.5f, 0f), inwardness * 0.4f);

                // When hand is behind, the elbow stays down and swings outward/back (never up, which chicken-wings)
                float behindness = Mathf.Clamp01(-z);
                bendDir = Vector3.Lerp(bendDir, new Vector3(0.4f, -0.75f, -0.55f), behindness * 0.7f);

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
        /// <remarks>
        /// This is inside a Burst job, so an out-of-range index is not an exception you catch — it aborts the
        /// process. It therefore must not trust its caller. <see cref="Mathf.Clamp"/> did: it reads
        /// <c>if (v &lt; min) v = min; else if (v &gt; max) v = max;</c>, and a NaN fails BOTH comparisons, so it
        /// passed straight through to <c>(int)NaN</c> == int.MinValue and indexed the table at -2147483648.
        /// Anything non-finite now lands on the grid origin instead: a slightly wrong elbow for one frame beats
        /// killing the editor.
        /// </remarks>
        // Ordered so that NaN fails BOTH tests and falls out at 0. Phrased the natural way round (v < 0 ? 0 :
        // v > hi ? hi : v) a NaN takes neither branch and escapes the clamp entirely -- which is exactly how
        // Mathf.Clamp let one through into (int)NaN == int.MinValue.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float ClampToGrid(float v) =>
            v > 0f ? (v < GridSize - 1.001f ? v : GridSize - 1.001f) : 0f;


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 SampleTrilinear(NativeArray<Vector3> table, Vector3 normalizedPos)
        {
            // Map [-1,1] to [0, GridSize-1]
            float fx = ClampToGrid((normalizedPos.x * 0.5f + 0.5f) * (GridSize - 1));
            float fy = ClampToGrid((normalizedPos.y * 0.5f + 0.5f) * (GridSize - 1));
            float fz = ClampToGrid((normalizedPos.z * 0.5f + 0.5f) * (GridSize - 1));

            int x0 = (int)fx; int x1 = Mathf.Min(x0 + 1, GridSize - 1);
            int y0 = (int)fy; int y1 = Mathf.Min(y0 + 1, GridSize - 1);
            int z0 = (int)fz; int z1 = Mathf.Min(z0 + 1, GridSize - 1);

            // ⚠ MEASURED, NOT A GUESS: fading these weights does NOT fix the elbow buzz.
            //
            // Raw-fraction trilinear is C0 but not C1 -- the gradient jumps at every cell boundary -- so the
            // obvious theory was that a hand sweeping across cells steps the bend direction's derivative, and
            // a step in the elbow's velocity is exactly what buzz is. Perlin's quintic fade
            // (t*t*t*(t*(t*6-15)+10), C2, node values preserved exactly) was tried here and measured against
            // the 20-clip CMU corpus: worst-case elbow jitter moved 1.360% -> 1.359% of arm length. Nothing.
            // Jerk ratio and pop count did not move at all.
            //
            // The C1 discontinuity is real but it is NOT the dominant term: the grid is 11^3 over the whole
            // workspace, so a reach crosses only a handful of cells, and the resulting step is small next to
            // whatever is actually generating the noise. Left as raw fractions rather than shipping a change
            // that cannot be justified with data. If the real source is fixed and this then becomes the
            // largest remaining term, re-test it and the fade is three multiply-adds away.
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
