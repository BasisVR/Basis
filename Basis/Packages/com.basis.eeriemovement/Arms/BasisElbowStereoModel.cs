using Unity.Burst;
using Unity.Mathematics;

using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// Where the elbow goes for a user with NO elbow tracker -- the successor to
    /// <see cref="BasisElbowFieldModel"/>, built to KILL THE REACH-BEHIND SNAP.
    ///
    /// ================================================================================================
    /// WHY THE PREVIOUS MODEL SNAPPED WHEN YOU REACHED BEHIND, AND WHY NO REFIT COULD FIX IT.
    ///
    /// BasisElbowFieldModel predicted the elbow's POSITION and projected it onto the reachable circle.
    /// That projected bend is a UNIT TANGENT VECTOR on the sphere of hand directions, and by the
    /// HAIRY BALL / POINCARE-HOPF theorem every continuous such field has zeros of total index 2 --
    /// TWO cores per reach shell, somewhere. A global polynomial field splits that into two separate
    /// index-1 zeros, and at least one of them always landed in REACHABLE workspace: the down-and-back
    /// core at azimuth ~130 deg, ~20 deg below level, which is exactly "hand out and behind the hip,
    /// arm bent" -- reaching behind. Sweeping the hand through it rotated the elbow hint up to 169 deg
    /// in a single frame. Measured, on the real solve chain.
    ///
    /// It was proven (5 ways, on 199,528 frames of real arm motion) that NO stateless refit removes it:
    /// affine, quadratic, lever-floor-constrained, and on-axis-anchored fits all just RELOCATED the
    /// reachable core, sometimes making it sharper. The zeros are topological, not a tuning bug.
    ///
    /// ================================================================================================
    /// THE FIX: ONE index-2 zero, PLACED IN THE TORSO.
    ///
    /// The escape is not to have TWO index-1 zeros (which cannot both hide -- the never-reached region
    /// is a single connected cap, so it contains no antipodal pair, and a two-pole field always leaves
    /// one pole in the open). It is to have ONE index-2 zero, and put it where the hand physically
    /// cannot go.
    ///
    /// A stereographic projection gives exactly that: the constant field d/dzeta on the plane, pulled
    /// back to the sphere, is a smooth unit tangent field whose ONLY zero is the projection pole, and
    /// that zero has index 2. Put the pole at <see cref="k_Zero"/> -- straight ACROSS the body (the hand
    /// would have to pass through the torso to point there, at any reach) -- and the field is zero-free
    /// over the ENTIRE reachable workspace. No core to sweep through. No snap.
    ///
    /// The base field is then ROTATED per-direction by theta(d) to match where real elbows actually go.
    /// A rotation in the tangent plane cannot create or move a zero (it preserves magnitude), so however
    /// theta wiggles, the single zero stays pinned in the torso. This is the reference the old
    /// swivel-ANGLE model never had: its reference vanished when the arm hung down (29.7% of frames);
    /// this one vanishes only inside the chest.
    ///
    /// MEASURED, 199,528 frames, both arms, vs the shipped BasisElbowFieldModel:
    ///
    ///                                     shipped field     THIS model
    ///   reach-behind snap (worst deg/frame)   33.2 deg        0.3 deg
    ///   worst reachable elbow gain            ~106 x          7.0 x     (a human tracks 0.5-1.5x)
    ///   elbow error vs real humans            ~9.1 %arm       7.7 %arm  (better in every region)
    ///   topological zeros in reach            2 (reachable)   1 (in the torso, 32 deg from any pose)
    ///
    /// FITTED on the harness's own dumped features (the same one-pipeline discipline as the field model:
    /// the Python fit and this C# eval share the mirrored body frame bit-for-bit, so a handedness/mirror
    /// slip cannot silently poison it). DO NOT HAND-EDIT THE CONSTANTS. Re-fit and re-generate; the
    /// scaffold is scratchpad/elbow (stereo + validation against this file).
    ///
    /// ⚠️ NOT VERIFIED IN A HEADSET. The offline harness drives the solve maths, not the animation job.
    /// The base field's FEEL and its interaction with BasisElbowAnatomyCore (the guard fires ~2.3% of
    /// reachable poses here vs 0.6% for the old field -- more often, but structurally free) are exactly
    /// what an offline harness cannot see. A/B it against the old field (BasisElbowFieldModel.UseStereo)
    /// before trusting it. See the elbow-field memory for the coast that was clean offline and worse live.
    /// </summary>
    [BurstCompile]
    public static class BasisElbowStereoModel
    {
        /// <summary>The single index-2 zero, in the mirrored body frame (+x OUT, +y UP, +z FWD). It points
        /// straight ACROSS the body (deep -x), so a hand can only point this way by passing through the
        /// torso -- unreachable at every reach. 32 deg from the nearest real hand pose in the corpus.
        /// Everything reachable is zero-free because of where this sits.</summary>
        static readonly float3 k_Zero = new float3(-0.97339949f, +0.20492621f, -0.10246310f);

        /// <summary>An orthonormal pair spanning the plane perpendicular to k_Zero -- the stereographic
        /// chart's axes. Precomputed (cross(k_Zero, up), then cross) so the runtime does no basis build.</summary>
        static readonly float3 k_ChartA = new float3(+0.10468478f, +0.00000000f, -0.99450545f);
        static readonly float3 k_ChartB = new float3(-0.20380023f, -0.97877743f, -0.02145266f);

        /// <summary>theta(d), the rotation from the base field to the real elbow, fitted as
        /// cos/sin = dot(features, coef) with features [1, u*sc, v*sc, d.k_Zero] (sc = 1/(1+u^2+v^2)).
        /// Linear in the chart coords keeps theta smooth, which keeps the elbow gain low -- a wigglier
        /// theta measured worse (higher gain) for negligible accuracy.</summary>
        static readonly float4 k_ThetaCos = new float4(+0.60690088f, -0.14357171f, +0.68042736f, +0.07441551f);
        static readonly float4 k_ThetaSin = new float4(-0.38487204f, +0.42321954f, +0.78253400f, +0.25439812f);

        /// <summary>
        /// The bend direction the two-bone solver wants: a UNIT vector PERPENDICULAR to the shoulder->hand
        /// axis, so it lies on the elbow's reachable circle by construction (the solver needs no fade or
        /// pole guard to drag it back). Depends only on the hand DIRECTION, not the reach.
        ///
        /// `conditioning` comes back as the base field's magnitude before it is normalised -- a purely
        /// geometric lever that is >= ~0.55 everywhere reachable and only collapses at k_Zero, in the
        /// torso. It is reported for the tests and gizmos, exactly like the field model's conditioning.
        /// </summary>
        public static float3 BendDirection(float3 tipLocal, out float conditioning)
        {
            float3 d = math.normalizesafe(tipLocal, new float3(0f, -1f, 0f));

            // --- stereographic base field: single index-2 zero at k_Zero, unit tangent elsewhere ---
            float ds = math.dot(d, k_Zero);
            // 1 - ds vanishes only AT k_Zero (unreachable); keep it off zero so nothing divides by 0.
            float den = 1f - ds;
            den = math.abs(den) < 1e-6f ? (den < 0f ? -1e-6f : 1e-6f) : den;

            float u = math.dot(d, k_ChartA) / den;
            float v = math.dot(d, k_ChartB) / den;
            float r2 = u * u + v * v;
            float W = r2 + 1f;

            // T = d/du of the inverse stereographic map = the constant chart field, pulled back.
            float3 Dv = 2f * u * k_ChartA + 2f * v * k_ChartB + (r2 - 1f) * k_Zero;
            float3 T = ((2f * k_ChartA + 2f * u * k_Zero) * W - Dv * (2f * u)) / (W * W);
            T = T - d * math.dot(T, d);                       // clean any numerical along-axis leak
            conditioning = math.length(T);

            float3 baseDir = math.normalizesafe(T, new float3(0f, 0f, -1f));

            // --- rotate the base field by the fitted theta(d) to land on the real elbow. A tangent-plane
            // rotation preserves magnitude, so it CANNOT create or move the single zero: still zero-free
            // over the whole reachable workspace, whatever theta does. ---
            float sc = 1f / (1f + r2);
            float4 f = new float4(1f, u * sc, v * sc, ds);
            float ct = math.dot(f, k_ThetaCos);
            float st = math.dot(f, k_ThetaSin);
            float theta = math.atan2(st, ct);

            float3 cross = math.cross(d, baseDir);            // completes the tangent frame at d
            float3 bend = baseDir * math.cos(theta) + cross * math.sin(theta);
            bend = bend - d * math.dot(bend, d);
            return math.normalizesafe(bend, baseDir);
        }
    }
}
