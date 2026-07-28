using UnityEngine;
namespace Basis.IK
{
    public struct BasisCervicalInput
    {
        public float BaseDeg;
        public float NeckShare;
        public float MaxHeadPitchDeg;
        public float ExtremeStartDeg;
        public float ExtremeFullDeg;
        public float ExtremeRollForwardMaxDeg;
        public float ExtremeRollBackwardMaxDeg;
        public float ExtremeHipsHorizontalMax;
        public float ExtremeChestHorizontalMax;
        public float ExtremeHipsDownMax;
        public float ExtremeChestDownMax;
        public float ExtremeHipsDownLookUp;
        public float ExtremeChestDownLookUp;
        public float PitchGainDeg;

        public Vector3 ReferenceUp;
        public Quaternion HeadTargetRot;
        public bool HasUpperChest;
    }

    public struct BasisCervicalResult
    {
        public bool EarlyOut;
        public Quaternion HeadRotClamped;   // gaze rotation after pitch clamp; wrapper applies * headOffset

        public float BhDeg;                 // bend angle for upperChest/chest (around its local right)
        public float NeckDeg;               // neck bend angle
        public bool HasExtreme;             // extremeFrac > 0
        public float HipsForwardAmount;     // along refForward
        public float HipsDownAmount;        // along refDown
        public float ChestForwardAmount;
        public float ChestDownAmount;

        // diagnostics
        public float HeadPitchInputDeg;
        public float HeadPitchClampedDeg;
        public float LordosisDeg;
        public float UpperChestLordosisDeg;
        public float ExtremeFrac;
        public float ExtremeRollDeg;
        public float PitchAbsDeg;
        public float SignedPitch;
        public float LookUpFrac;
        public float LookDownFrac;
    }

    // Stream-free port of BasisFullIKConstraintJob.ApplyCervicalLordosis. Computes the scalar
    // lordosis response + the clamped gaze rotation; the wrapper reads handles fresh and applies
    // the bend/offset/neck/head writes in the original order (preserving upperChest->neck coupling).
    public static class BasisCervicalSolveCore
    {
        const float k_SqrEpsilon = 1e-8f;

        public static void Solve(in BasisCervicalInput i, out BasisCervicalResult r)
        {
            r = default;

            // The pitch clamp is measured against ReferenceUp (the chest up), NOT world up. It used to build
            // the horizontal plane out of world XZ and the yaw axis out of Vector3.up, while everything below
            // this block already used ReferenceUp -- so the core disagreed with itself the moment the torso
            // was not upright. Bent over or prone, the clamp fired against the wrong horizon and dragged the
            // head off the HMD. Identical to the old code when ReferenceUp is world up.
            Vector3 up = i.ReferenceUp.sqrMagnitude > k_SqrEpsilon ? i.ReferenceUp.normalized : Vector3.up;

            Quaternion headRot = i.HeadTargetRot;
            Vector3 hf = headRot * Vector3.forward;
            float upComp = Vector3.Dot(hf, up);
            Vector3 horiz = hf - up * upComp;
            float horizMag = horiz.magnitude;
            float pitchDeg = (horizMag > 1e-6f) ? Mathf.Atan2(-upComp, horizMag) * Mathf.Rad2Deg : (upComp < 0f ? 90f : -90f);
            float clampedDeg = Mathf.Clamp(pitchDeg, -i.MaxHeadPitchDeg, i.MaxHeadPitchDeg);
            if (clampedDeg != pitchDeg)
            {
                Vector3 yawForward;
                if (horizMag > 1e-6f)
                {
                    yawForward = horiz / horizMag;
                }
                else
                {
                    // Gaze is along the torso axis, so head-forward gives no yaw. Head-up is orthogonal to
                    // head-forward by construction, so it always survives the projection -- use it, and stay
                    // in the body frame rather than falling back to a world axis.
                    Vector3 alt = headRot * Vector3.up;
                    Vector3 altH = alt - up * Vector3.Dot(alt, up);
                    yawForward = altH.sqrMagnitude > k_SqrEpsilon ? altH.normalized : Vector3.Cross(up, Vector3.right).normalized;
                }
                Vector3 yawRight = Vector3.Cross(up, yawForward);
                Quaternion correction = Quaternion.AngleAxis(-(pitchDeg - clampedDeg), yawRight);
                headRot = correction * headRot;
            }

            Vector3 headForward = headRot * Vector3.forward;
            float pitchSigned = Vector3.Dot(headForward, up);
            float lookUpFrac = Mathf.Clamp01(pitchSigned);
            float lookDownFrac = Mathf.Clamp01(-pitchSigned);

            float pitchAbsDeg = Mathf.Asin(Mathf.Min(Mathf.Abs(pitchSigned), 1f)) * Mathf.Rad2Deg;
            float extremeFrac = Mathf.Clamp01((pitchAbsDeg - i.ExtremeStartDeg) / Mathf.Max(1e-3f, i.ExtremeFullDeg - i.ExtremeStartDeg));

            float signedPitch = lookDownFrac - lookUpFrac;
            float signedBalance = -signedPitch;

            // Smooth |signedPitch| so the base-lordosis term is differentiable at level gaze;
            // a hard abs() kinks the neck-bend rate ~4x right at the most common head pose.
            float smoothAbsPitch = Mathf.Sqrt(signedPitch * signedPitch + 0.0225f) - 0.15f;
            float lordosisDeg = i.BaseDeg * (1f - smoothAbsPitch) + i.PitchGainDeg * signedPitch;

            bool hasUpperChest = i.HasUpperChest;
            float neckDeg = hasUpperChest ? lordosisDeg * i.NeckShare : lordosisDeg;
            float upperChestLordosisDeg = hasUpperChest ? lordosisDeg * (1f - i.NeckShare) : 0f;
            float extremeRollMag = signedPitch >= 0f ? i.ExtremeRollForwardMaxDeg : i.ExtremeRollBackwardMaxDeg;
            float extremeRollDeg = extremeFrac * signedPitch * extremeRollMag;

            r.HeadRotClamped = headRot;
            r.HeadPitchInputDeg = pitchDeg;
            r.HeadPitchClampedDeg = clampedDeg;
            r.LordosisDeg = lordosisDeg;
            r.UpperChestLordosisDeg = upperChestLordosisDeg;
            r.NeckDeg = neckDeg;
            r.ExtremeFrac = extremeFrac;
            r.ExtremeRollDeg = extremeRollDeg;
            r.PitchAbsDeg = pitchAbsDeg;
            r.SignedPitch = signedPitch;
            r.LookUpFrac = lookUpFrac;
            r.LookDownFrac = lookDownFrac;

            if (Mathf.Abs(lordosisDeg) < 0.01f && extremeFrac <= 0f)
            {
                r.EarlyOut = true;
                return;
            }

            r.BhDeg = upperChestLordosisDeg + extremeRollDeg;

            if (extremeFrac > 0f)
            {
                r.HasExtreme = true;
                float horizCoeff = extremeFrac * signedBalance;
                float hipsDown = extremeFrac * (lookDownFrac * i.ExtremeHipsDownMax + lookUpFrac * i.ExtremeHipsDownLookUp);
                float chestDown = extremeFrac * (lookDownFrac * i.ExtremeChestDownMax + lookUpFrac * i.ExtremeChestDownLookUp);
                r.HipsForwardAmount = horizCoeff * i.ExtremeHipsHorizontalMax;
                r.HipsDownAmount = hipsDown;
                r.ChestForwardAmount = horizCoeff * i.ExtremeChestHorizontalMax;
                r.ChestDownAmount = chestDown;
            }
        }
    }
}
