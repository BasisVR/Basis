using UnityEngine;
namespace Basis.IK
{
    public struct BasisHipHingeInput
    {
        public Vector3 HeadPos;
        public Vector3 HipsPos;
        public Quaternion HipsRot;
        public Vector3 PlayerUp;
        public float StartDeg;
        public float MaxAddDeg;
    }

    public struct BasisHipHingeResult
    {
        public Quaternion HipsRot;   // input rotation, pitched forward by AddDeg when Applied
        public bool Applied;
        public float LeanDeg;        // forward lean of head over hips (NaN when not computed)
        public float AddDeg;         // pelvis pitch added this solve
    }

    // Stream-free port of BasisFullIKConstraintJob.ApplyHipHinge. When the forward lean past StartDeg
    // grows, the pelvis pitches forward so the spine does not swallow the whole reach -- this IS the
    // lumbopelvic rhythm. Change the hinge math HERE so the job and the offline sweep stay in lock-step.
    //
    // ================================================================================================
    // THE CURVE IS MEASURED, NOT A HEURISTIC. Fitted to the pelvis anterior tilt of real humans across the
    // full bend range (CMU corpus, 35,081 frames, `scratchpad/hinge_fit.py`), tilt vs head-over-hips lean:
    //
    //     lean     real pelvis tilt (median)      old model (start 30, x0.5, cap 15)
    //     30-45          ~3 deg                          3.8 deg
    //     45-60          10.4                            11.2
    //     60-75          25.7                            15.0  (capped)
    //     75-90          42.3                            15.0  (capped)
    //     90-115         53.1                             0.0  (switched OFF -- head below hips)
    //
    // The real pelvis STAYS put through the early bend (the lumbar spine leads), then rotates NEARLY 1:1
    // with additional lean once you are past ~45 deg (the hips take over), easing toward a hamstring limit
    // around 50 deg. The old linear-half-capped-at-15 under-tilted deep bends by 3-4x and, worst of all,
    // switched OFF entirely once the head dropped to hip level -- exactly the toe-touch pose where a real
    // pelvis is most tilted. That missing fold is torso the spine and neck had to absorb: the "tortoise
    // neck" / stiff bend-over.
    //
    // So: `PelvisFollowSlope = 1.0` past StartDeg (the pelvis follows the lean one-for-one), eased into
    // MaxAddDeg with the same rational saturation the joint guards use (slope-1 handover, no pop), and the
    // whole thing runs through lean > 90 (head below hips) instead of bailing. StartDeg is the depth at
    // which the pelvis starts to take over (~45); MaxAddDeg is the hamstring-limited cap (~50).
    //
    // ⚠️ Gated to NO hip tracker in the job (BasisFullBodyIK.SolveSpine): a tracked pelvis is the user's own
    // and must not be synthetically pitched -- see BasisHipHingeCoreTests + [[feedback_hip_tilt_stabilization_removed]].
    // ================================================================================================
    public static class BasisHipHingeCore
    {
        const float k_Epsilon = 1e-5f;
        const float k_SqrEpsilon = 1e-8f;

        /// <summary>
        /// How fast the pelvis follows additional lean once past StartDeg. Measured ~0.9-1.1 in the deep
        /// phase of a real bend; 1.0 = "the pelvis takes essentially all of it", which is what the corpus
        /// shows and is a clean anatomical statement.
        /// </summary>
        public const float PelvisFollowSlope = 1.0f;

        public static void Solve(in BasisHipHingeInput i, out BasisHipHingeResult r)
        {
            r = default;
            r.HipsRot = i.HipsRot;
            r.LeanDeg = float.NaN;

            if (i.MaxAddDeg <= 0f)
            {
                return;
            }

            Vector3 hipsToHead = i.HeadPos - i.HipsPos;
            float upDot = Vector3.Dot(hipsToHead, i.PlayerUp);
            Vector3 horizontal = hipsToHead - i.PlayerUp * upDot;
            float horizMag = horizontal.magnitude;
            // NB: NO `upDot <= 0` bail. atan2 carries the lean smoothly past 90 deg (head below the hips), and
            // the hinge axis is well defined for any non-zero horizontal -- so a deep toe-touch, where the
            // pelvis tilt is greatest, is exactly where the old guard used to switch the hinge off.
            if (horizMag < k_Epsilon)
            {
                return;
            }

            float leanDeg = Mathf.Atan2(horizMag, upDot) * Mathf.Rad2Deg;
            r.LeanDeg = leanDeg;
            if (leanDeg <= i.StartDeg)
            {
                return;
            }

            float excess = (leanDeg - i.StartDeg) * PelvisFollowSlope;
            float addDeg = Saturate(excess, i.MaxAddDeg);

            Vector3 hingeAxis = Vector3.Cross(i.PlayerUp, horizontal / horizMag);
            if (hingeAxis.sqrMagnitude < k_SqrEpsilon)
            {
                return;
            }

            hingeAxis.Normalize();
            r.AddDeg = addDeg;
            r.Applied = true;
            r.HipsRot = Quaternion.AngleAxis(addDeg, hingeAxis) * i.HipsRot;
        }

        /// <summary>
        /// Rational saturation toward `cap`: linear until 80% of it, then eased so it approaches but never
        /// reaches `cap` -- slope 1 at the handover, no derivative step, so the pelvis rounds into its limit
        /// instead of hitting a wall. Same shape as BasisSpineAnatomyCore/BasisElbowAnatomyCore.
        /// </summary>
        public static float Saturate(float x, float cap)
        {
            if (!(cap > k_Epsilon))
            {
                return 0f;
            }
            float soft = cap * 0.8f;
            if (!(x > soft))
            {
                return x < 0f ? 0f : x;
            }
            float m = cap - soft;
            float e = x - soft;
            return soft + m * e / (m + e);
        }
    }
}
