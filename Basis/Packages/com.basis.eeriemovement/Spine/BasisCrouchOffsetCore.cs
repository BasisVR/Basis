using UnityEngine;
namespace Basis.IK
{
    public struct BasisCrouchOffsetInput
    {
        public Vector3 HeadTargetPos;
        public Vector3 HipsPos;
        public Quaternion HipsRot;
        // The hips calibration bind (offsetRotationHips). HipsRot carries it, so HipsRot * forward is the
        // bone's local +Z, not the body's facing -- on a Blender-bound rig that +Z is world-up, so the crouch
        // slid the hips vertically (or, once the up-component was stripped, collapsed and never fired). Cancel
        // it to get the anatomical forward. A degenerate/zero value (uncalibrated, or a caller that leaves it
        // default) means "HipsRot is already anatomical" -- the pre-bind behaviour, bit for bit.
        public Quaternion Bind;
        public Vector3 PlayerUp;
        /// <summary>User gain on the sit-back curve. 1 = the human-median curve, 0 = off.</summary>
        public float Factor;
        /// <summary>T-pose hips->head chain length; the spine sphere the hips ride on.</summary>
        public float RestDist;
        /// <summary>How far the head target sits below the avatar's standing head height, metres, >= 0.</summary>
        public float CrouchDepth;
        /// <summary>Standing head height, metres. Normalizes depth so the curve is avatar-scale invariant.</summary>
        public float StandingHeadHeight;
        /// <summary>1 - sin(trunk flexion) from the postural counterbalance: a waist-fold's pelvis travel is
        /// owned by that term, so the crouch sit-back fades out as the trunk folds.</summary>
        public float Fade;
    }

    public struct BasisCrouchOffsetResult
    {
        public Vector3 HipsPos;  // input hips repositioned for the crouch (== input when not crouching)
        public bool Applied;
        public float SetbackMeters; // commanded posterior slide, after gain/fade/cap
        public float LeanDeg;       // final head->hips chord lean from vertical
    }

    // Stream-free crouch sit-back, shared by the live BasisFullIKConstraintJob and the editor sweep.
    //
    // WHAT A REAL CROUCH DOES (measured, Basis mocap corpus, 69 clips / 20k crouch frames -- standing,
    // feet planted, knees flexed, head below standing height; per-clip medians then cross-clip medians so
    // no subject dominates):
    //   * The pelvis travels BACK the moment the head starts down -- the descent is sat into, hip-first,
    //     not folded straight down with the knees eating the whole drop.
    //   * Hips-behind-head vs head drop (both / standing head height S) saturates like a squat does:
    //       drop 10%S -> setback 0.115*S, 25%S -> 0.20*S, 50%S (full desktop crouch) -> 0.25*S.
    //     Fit: setback/S = 2.57*x / (1 + 8.1*x), x = drop/S - 0.03 (rmse 0.016 vs bucket medians;
    //     a linear model is 3.4x worse -- the sit-back is front-loaded, then plateaus).
    //   * The 3D head-hips distance is nearly CONSTANT while all this happens (0.89-1.00 of standing,
    //     mean ~0.95, even at full depth): the trunk lean comes from the hips riding a sphere around the
    //     head, not from the spine shortening. So the vertical placement here puts the hips ON the
    //     RestDist sphere -- identical to the LockHead length restore at zero setback (continuous with
    //     the standing pose), tilting backward as the setback grows. Measured chord lean at full crouch
    //     (55-60 deg) falls out of that geometry on its own.
    //
    // Without the sphere placement the horizontal slide would ask the spine chain to reach
    // sqrt(RestDist^2 + s^2) -- ~6 cm beyond its length at full crouch -- and the pinned head would
    // stretch the neck. The sit-back and the vertical are one model, not two features.
    public static class BasisCrouchOffsetCore
    {
        const float k_SqrEpsilon = 1e-8f;
        const float k_Epsilon = 1e-5f;

        // Corpus fit (see block comment): setback/S = k_SetbackSlope * x / (1 + k_SetbackSat * x),
        // x = max(0, depth/S - k_DepthDeadzone).
        public const float k_DepthDeadzone = 0.03f;
        public const float k_SetbackSlope = 2.57f;
        public const float k_SetbackSat = 8.1f;
        // Cap the chord lean at sin ~62 deg, the deepest the corpus reaches; a rational Saturate eases into
        // it so a VR user sitting on the real floor holds a deep-squat posture instead of running away.
        public const float k_MaxLeanSin = 0.88f;
        // The vertical takeover blends in over the first few cm of setback so a crouch that begins mid
        // hip-bob eases onto the sphere instead of stepping onto it.
        public const float k_VerticalEngageFrac = 0.04f;

        /// <summary>
        /// The corpus-fitted posterior slide for a given head drop, after gain, fade and the lean cap.
        /// Exposed so the sweep validates the live math instead of mirroring a copy of it.
        /// </summary>
        public static float EvaluateSetback(float crouchDepth, float standingHeadHeight, float factor, float fade, float restDist)
        {
            if (factor <= 0f || fade <= 0f || crouchDepth <= 0f || standingHeadHeight <= k_Epsilon || restDist <= k_Epsilon)
            {
                return 0f;
            }
            float x = crouchDepth / standingHeadHeight - k_DepthDeadzone;
            if (x <= 0f)
            {
                return 0f;
            }
            float setback = k_SetbackSlope * x / (1f + k_SetbackSat * x) * standingHeadHeight;
            setback *= factor * Mathf.Clamp01(fade);
            return BasisTrunkCounterbalanceCore.Saturate(setback, k_MaxLeanSin * restDist);
        }

        public static void Solve(in BasisCrouchOffsetInput i, out BasisCrouchOffsetResult r)
        {
            r = default;
            r.HipsPos = i.HipsPos;

            float setback = EvaluateSetback(i.CrouchDepth, i.StandingHeadHeight, i.Factor, i.Fade, i.RestDist);
            if (setback <= k_Epsilon)
            {
                return;
            }

            Vector3 up = i.PlayerUp.sqrMagnitude < k_SqrEpsilon ? Vector3.up : i.PlayerUp.normalized;

            // Cancel the hips bind (a unit quaternion, so its inverse is its conjugate -- written out to keep
            // the core free of the native Quaternion.Inverse ECall, same discipline as BasisSpineAnatomyCore).
            // A degenerate bind falls back to HipsRot, which is the exact pre-bind behaviour.
            Quaternion hipsAnat = i.HipsRot;
            float bindSq = i.Bind.x * i.Bind.x + i.Bind.y * i.Bind.y + i.Bind.z * i.Bind.z + i.Bind.w * i.Bind.w;
            if (bindSq > 0.5f)
            {
                Quaternion invBind = new Quaternion(-i.Bind.x, -i.Bind.y, -i.Bind.z, i.Bind.w);
                hipsAnat = i.HipsRot * invBind;
            }

            Vector3 forward = hipsAnat * Vector3.forward;
            forward -= up * Vector3.Dot(forward, up);
            if (forward.sqrMagnitude < k_SqrEpsilon)
            {
                return;
            }

            // Total horizontal head->hips offset: whatever is already there (counterbalance shift, gait sway)
            // plus the sit-back, capped so the sphere sqrt stays real and the lean stays in the corpus range.
            Vector3 horizontal = i.HipsPos - i.HeadTargetPos;
            horizontal -= up * Vector3.Dot(horizontal, up);
            horizontal -= forward.normalized * setback;
            float horizMag = horizontal.magnitude;
            float horizCap = k_MaxLeanSin * i.RestDist;
            if (horizMag > horizCap)
            {
                horizontal *= horizCap / horizMag;
                horizMag = horizCap;
            }

            // Hips onto the RestDist sphere around the head: the same distance the LockHead restore holds
            // while standing, so zero setback is exactly the standing placement. Blended in over the first
            // few cm of slide in case the incoming pose was off the sphere (bob mid-step).
            float sphereDrop = Mathf.Sqrt(Mathf.Max(i.RestDist * i.RestDist - horizMag * horizMag, 0f));
            float currentDrop = Vector3.Dot(i.HeadTargetPos - i.HipsPos, up);
            float w = Mathf.Clamp01(setback / (k_VerticalEngageFrac * i.RestDist));
            float drop = Mathf.Lerp(currentDrop, sphereDrop, w);

            r.Applied = true;
            r.SetbackMeters = setback;
            r.LeanDeg = Mathf.Atan2(horizMag, drop) * Mathf.Rad2Deg;
            r.HipsPos = i.HeadTargetPos + horizontal - up * drop;
        }
    }
}
