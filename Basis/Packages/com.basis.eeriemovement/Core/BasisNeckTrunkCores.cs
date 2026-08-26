using UnityEngine;
namespace Basis.IK
{
    public static class BasisCervicalSolveCore
    {
        const float sqrEpsilon = 1e-8f;
        /// <summary>Convenience overload for callers with nothing to draw into.</summary>
        public static void Solve(in BasisCervicalInput i, out BasisCervicalResult r)
        {
            BasisIKGizmoRecorder gizmos = default;
            Solve(i, out r, ref gizmos);
        }
        public static void Solve(in BasisCervicalInput i, out BasisCervicalResult r, ref BasisIKGizmoRecorder gizmos)
        {
            r = default;

            Vector3 up = i.ReferenceUp.sqrMagnitude > sqrEpsilon ? i.ReferenceUp.normalized : Vector3.up;
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
                    Vector3 alt = headRot * Vector3.up, altH = alt - up * Vector3.Dot(alt, up);
                    yawForward = altH.sqrMagnitude > sqrEpsilon ? altH.normalized : Vector3.Cross(up, Vector3.right).normalized;
                }
                Vector3 yawRight = Vector3.Cross(up, yawForward);
                Quaternion correction = Quaternion.AngleAxis(-(pitchDeg - clampedDeg), yawRight);
                headRot = correction * headRot;
            }

            Vector3 headForward = headRot * Vector3.forward;
            float pitchSigned = Vector3.Dot(headForward, up), lookUpFrac = Mathf.Clamp01(pitchSigned);
            float lookDownFrac = Mathf.Clamp01(-pitchSigned);
            float pitchAbsDeg = Mathf.Asin(Mathf.Min(Mathf.Abs(pitchSigned), 1f)) * Mathf.Rad2Deg;
            float extremeFrac = Mathf.Clamp01((pitchAbsDeg - i.ExtremeStartDeg) / Mathf.Max(1e-3f, i.ExtremeFullDeg - i.ExtremeStartDeg));
            float signedPitch = lookDownFrac - lookUpFrac, signedBalance = -signedPitch;
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
                float hipsHoriz = signedPitch >= 0f ? i.ExtremeHipsHorizontalMax : i.ExtremeHipsHorizontalLookUp;
                float chestHoriz = signedPitch >= 0f ? i.ExtremeChestHorizontalMax : i.ExtremeChestHorizontalLookUp;
                r.HipsForwardAmount = horizCoeff * hipsHoriz;
                r.HipsDownAmount = hipsDown;
                r.ChestForwardAmount = horizCoeff * chestHoriz;
                r.ChestDownAmount = chestDown;
            }
        }
    }
    public static class BasisNeckCueCore
    {
        const float sqrEpsilon = 1e-8f, epsilon = 1e-5f;
        public const float DefaultExtensionDamp = 0.65f;
        public static Vector3 Solve(Vector3 headTargetPos, Quaternion headWorldRot, Vector3 tposeHeadToNeckLocal, Vector3 playerUp, float extensionDamp)
        {
            return Solve(headTargetPos, headWorldRot, tposeHeadToNeckLocal, playerUp, extensionDamp, 0f);
        }
        public static Vector3 Solve(Vector3 headTargetPos, Quaternion headWorldRot, Vector3 tposeHeadToNeckLocal, Vector3 playerUp, float extensionDamp, float flexionDamp)
        {
            Vector3 lever = headWorldRot * tposeHeadToNeckLocal;

            if (lever.sqrMagnitude < sqrEpsilon)
            {
                return headTargetPos + lever;
            }

            Vector3 up = playerUp.sqrMagnitude < sqrEpsilon ? Vector3.up : playerUp.normalized;
            Vector3 gaze = headWorldRot * Vector3.forward;
            float upComp = Vector3.Dot(gaze, up);
            bool isExtension = upComp > 0f;
            float damp = Mathf.Clamp01(isExtension ? extensionDamp : flexionDamp);
            if (damp <= 0f)
            {
                return headTargetPos + lever;
            }

            Vector3 horiz = gaze - up * upComp;
            float horizMag = horiz.magnitude, pitchDeg = Mathf.Atan2(upComp, horizMag) * Mathf.Rad2Deg;
            Vector3 forwardAzimuth;
            if (horizMag > epsilon)
            {
                forwardAzimuth = horiz / horizMag;
            }
            else
            {

                Vector3 alt = headWorldRot * Vector3.up, altH = alt - up * Vector3.Dot(alt, up);
                if (altH.sqrMagnitude < sqrEpsilon)
                {
                    return headTargetPos + lever;
                }
                forwardAzimuth = isExtension ? -altH.normalized : altH.normalized;
            }

            Vector3 axis = Vector3.Cross(up, forwardAzimuth);
            if (axis.sqrMagnitude < sqrEpsilon)
            {
                return headTargetPos + lever;
            }
            axis.Normalize();

            Quaternion undo = BasisSpineAnatomyCore.AxisAngle(damp * pitchDeg, axis);
            return headTargetPos + undo * lever;
        }
    }
    public static class BasisTrunkCounterbalanceCore
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f;
        public const float DerivedGain = 0.38f;
        public static bool Solve(Vector3 hipsPos, Vector3 neckCue, Vector3 playerUp, float gain, float maxShift, out Vector3 newHipsPos, out float flexionFrac, out float shiftMeters)
        {
            newHipsPos = hipsPos;
            flexionFrac = 0f;
            shiftMeters = 0f;

            if (gain <= 0f || maxShift <= 0f)
            {
                return false;
            }

            Vector3 up = playerUp.sqrMagnitude < sqrEpsilon ? Vector3.up : playerUp.normalized;
            Vector3 trunk = neckCue - hipsPos;
            float len = trunk.magnitude;
            if (len < epsilon)
            {
                return false;
            }

            Vector3 horizontal = trunk - up * Vector3.Dot(trunk, up);
            float horizMag = horizontal.magnitude;

            flexionFrac = Mathf.Clamp01(horizMag / len);
            if (horizMag < epsilon)
            {
                return false;
            }

            float shift = Saturate(gain * horizMag, maxShift);
            if (shift <= 0f)
            {
                return false;
            }

            shiftMeters = shift;
            newHipsPos = hipsPos - (horizontal / horizMag) * shift;
            return true;
        }
        public static float Saturate(float x, float cap)
        {
            if (!(cap > epsilon))
            {
                return 0f;
            }
            float soft = cap * 0.8f;
            if (!(x > soft))
            {
                return x < 0f ? 0f : x;
            }
            float m = cap - soft, e = x - soft;
            return soft + m * e / (m + e);
        }
    }
}
