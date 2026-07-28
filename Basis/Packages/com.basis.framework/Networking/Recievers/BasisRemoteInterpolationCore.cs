using System.Runtime.CompilerServices;
using Unity.Mathematics;

/// <summary>
/// Pure, Burst-friendly interpolation math for remote-avatar playback.
///
/// Replaces the old linear-nlerp + heavy 1€ bone filter with a C1-continuous
/// Catmull-Rom spline through four consecutive network snapshots (p0..p3),
/// evaluated for the segment p1->p2. C1 continuity removes the per-snapshot
/// velocity corner that linear interpolation produced (the source of the
/// end-of-chain wobble), without the group delay / amplitude loss the filter
/// introduced.
///
/// p0 and p3 are the neighbours of the active window; the caller supplies them
/// from the retained previous frame and a peek at the next staged frame. When a
/// neighbour is unavailable (startup / underrun after loss) the caller passes a
/// DUPLICATED endpoint (p0==p1 or p3==p2); the tangents then become one-sided
/// and the curve stays bounded and monotone — no branch or validity flag needed.
///
/// No Unity references beyond Unity.Mathematics, so this is unit-testable off the
/// main thread and reproducible in an offline harness.
/// </summary>
public static class BasisRemoteInterpolationCore
{
    /// <summary>Catmull-Rom position of the p1->p2 segment at t in [0,1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float3 Position(float3 p0, float3 p1, float3 p2, float3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1)
            + (-p0 + p2) * t
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
            + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    /// <summary>
    /// Catmull-Rom rotation of the p1->p2 segment at t in [0,1].
    /// The four quaternions are hemisphere-aligned to p1 first (so the spline
    /// takes the short way and is immune to the codec's sign canonicalization),
    /// interpolated component-wise, then renormalized. Cheaper than a true
    /// SQUAD and visually indistinguishable at network step sizes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static quaternion Rotation(quaternion p0, quaternion p1, quaternion p2, quaternion p3, float t)
    {
        float4 a = p0.value, b = p1.value, c = p2.value, d = p3.value;
        if (math.dot(b, a) < 0f) a = -a;
        if (math.dot(b, c) < 0f) c = -c;
        if (math.dot(b, d) < 0f) d = -d;

        float t2 = t * t, t3 = t2 * t;
        float4 r = 0.5f * ((2f * b)
            + (-a + c) * t
            + (2f * a - 5f * b + 4f * c - d) * t2
            + (-a + 3f * b - 3f * c + d) * t3);

        float len = math.length(r);
        return len > 1e-12f ? new quaternion(r / len) : p1;
    }

    /// <summary>Shortest-path nlerp — the two-point fallback and the reference path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static quaternion NlerpShortest(quaternion a, quaternion b, float t)
    {
        if (math.dot(a.value, b.value) < 0f) b.value = -b.value;
        return math.normalize(math.nlerp(a, b, t));
    }

    /// <summary>
    /// One-pole low-pass smoothing factor for a given cutoff (Hz) and timestep (s):
    /// alpha = dt / (dt + tau), tau = 1/(2π·cutoff). alpha→1 as cutoff→∞ (no smoothing).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float OnePoleAlpha(float cutoffHz, float dt)
    {
        float tau = 1f / (2f * math.PI * math.max(cutoffHz, 0.01f));
        return dt / (dt + math.max(tau, 1e-9f));
    }

    /// <summary>
    /// One shortest-path one-pole low-pass step toward <paramref name="raw"/>. Used to strip the
    /// high-frequency bone-quantization shimmer from the cubic output without reshaping real motion.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static quaternion LowPassStep(quaternion prevFiltered, quaternion raw, float alpha)
    {
        float4 s = raw.value;
        if (math.dot(prevFiltered.value, s) < 0f) s = -s;
        return math.normalize(math.nlerp(prevFiltered, new quaternion(s), alpha));
    }

    /// <summary>
    /// Motion-adaptive low-pass cutoff: cutoff = minCutoff + beta·(angle between the two window
    /// snapshots, radians). When the joint is still the two snapshots are equal, cutoff = minCutoff
    /// (heavy smoothing, hides quantization shimmer); any real motion opens the cutoff so there is no
    /// lag or wobble. The velocity estimate is the CLEAN 20Hz sample motion (prev→target), not the
    /// noisy render-frame delta, so it distinguishes truly-still from moving perfectly — which is why
    /// a very low floor hides the shimmer without smearing slow real motion. No per-bone state.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AdaptiveCutoff(quaternion prevSnapshot, quaternion targetSnapshot, float minCutoffHz, float beta)
    {
        float d = math.min(1f, math.abs(math.dot(prevSnapshot.value, targetSnapshot.value)));
        float moveRad = 2f * math.acos(d);
        return minCutoffHz + beta * moveRad;
    }
}
