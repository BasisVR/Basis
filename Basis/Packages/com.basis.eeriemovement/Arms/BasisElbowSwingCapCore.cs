using Unity.Burst;
using Unity.Mathematics;

using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// The stateful half of the reach-behind fix: keep the ACCURATE elbow field (which poses the elbow
    /// correctly out-and-back reaching behind) and just CAP how fast its bend may rotate.
    ///
    /// ================================================================================================
    /// WHY THIS EXISTS.
    ///
    /// The no-tracker elbow bend is a unit tangent field on the sphere of hand directions, so by
    /// Poincare-Hopf it carries topologically-required zeros ("cores"). Sweeping the hand through a core
    /// flips the bend ~180 degrees in a fraction of a degree of hand motion -- the reach-behind snap.
    /// (BasisElbowFieldModel's down-and-back core, azimuth ~130, ~20 below level.) A STATELESS field
    /// cannot delete the core without mis-posing the elbow elsewhere: moving it into the torso
    /// (BasisElbowStereoModel) combed the elbow ACROSS THE BODY reaching behind the back -- "not human".
    ///
    /// So do not fight the topology in the field. Keep the field's pose, and bound the RATE the bend may
    /// turn RELATIVE TO THE HAND. Measured over 199,528 real frames, a human elbow tracks its hand at a
    /// bend-rotation / hand-rotation gain of ~1x (median), 4.6x at p99. Anything faster than that is a
    /// core flip, not a human motion. Cap the gain at MaxGain and the flip becomes a fast-but-bounded
    /// sweep at the human's own ceiling instead of a teleport, while everything away from a core -- where
    /// the field already turns slower than the cap -- passes through BIT-IDENTICAL.
    ///
    /// ================================================================================================
    /// WHY A GAIN CAP, AND WHY IT IS SAFE WHERE THE REVERTED "COAST" WAS NOT.
    ///
    ///   * It is a GAIN cap (bend rotation per unit HAND rotation), not a velocity cap. So it is
    ///     FRAMERATE-INDEPENDENT (per hand-step, no dt -- the velocity/rate blends this project tried
    ///     drifted with fps), and it SELF-SCALES: a fast hand earns a proportionally fast elbow, so there
    ///     is NO lag on ordinary reaching, only at a core where the field demands superhuman gain.
    ///
    ///   * It ALWAYS CHASES the field -- it never HOLDS a pole. So a carried bend that is stale or wrong
    ///     (a tracker just dropped, the avatar teleported, the frame hitched) re-acquires the field within
    ///     a few frames on its own. The reverted BasisElbowPoleCoastCore HELD the pole through the core,
    ///     and in a headset that frozen pole degraded everyday arm motion. This cannot: where the cap does
    ///     not bind it returns the field unchanged, and where it does it still moves toward the field.
    ///
    /// ⚠️ NOT VERIFIED IN A HEADSET. The offline harness drives the field maths, not the animation job,
    /// and the live-vs-offline gap (frame timing, tracker<->model handoff, state staleness) is exactly
    /// what sank the coast. Raise MaxGain toward infinity to disable and A/B. See the elbow-field memory.
    /// </summary>
    [BurstCompile]
    public static class BasisElbowSwingCapCore
    {
        /// <summary>Max bend-rotation / hand-rotation gain. Just above the human p99 (4.6x) so ordinary
        /// reaching is never clipped, far below a core flip (100x+). Raise toward infinity to disable.</summary>
        public const float MaxGain = 5f;

        /// <summary>
        /// Cap the bend's swivel about the shoulder->hand axis to <paramref name="maxGain"/> times the axis's
        /// own rotation since last frame. All vectors WORLD space, unit; rawBend and the result are
        /// perpendicular to curAxis.
        ///
        /// prevBend / prevAxis are last frame's CAPPED bend and shoulder->hand axis (the caller's per-arm
        /// state). Away from a core the field turns slower than the cap and rawBend is returned unchanged,
        /// bit-for-bit -- so this is a true no-op on ordinary motion.
        /// </summary>
        public static float3 Apply(float3 prevBend, float3 prevAxis, float3 curAxis, float3 rawBend, float maxGain)
        {
            // Transport prevBend onto the plane perpendicular to curAxis: the axis itself rotates frame to
            // frame (body turns, hand moves) and the bend must follow that for free -- only the residual
            // SWIVEL about the axis is what a core spins, and what this caps.
            float3 tp = prevBend - curAxis * math.dot(prevBend, curAxis);
            float tpLen = math.length(tp);
            if (tpLen < 1e-4f)
            {
                return rawBend;   // degenerate transport (axis flipped ~180) -> just take the field
            }
            tp /= tpLen;

            float3 cross = math.cross(curAxis, tp);              // completes the tangent frame; rawBend = tp*cos+cross*sin
            float ang = math.atan2(math.dot(rawBend, cross), math.dot(rawBend, tp));

            // atan2(|cross|, dot), not acos(dot): acos is ill-conditioned near 1 (a barely-moved hand has
            // dot ~ 1, and float32 acos there loses most of its digits), which would make the cap jitter on
            // slow motion. This form is accurate for every angle.
            float dHand = math.atan2(math.length(math.cross(prevAxis, curAxis)), math.dot(prevAxis, curAxis));
            float cap = maxGain * dHand;
            float capped = math.clamp(ang, -cap, cap);
            if (capped == ang)
            {
                return rawBend;   // cap not binding -> exact field, no drift on ordinary reaching
            }

            float3 outb = tp * math.cos(capped) + cross * math.sin(capped);
            outb = outb - curAxis * math.dot(outb, curAxis);
            return math.normalizesafe(outb, rawBend);
        }
    }
}
