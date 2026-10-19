using UnityEngine;
namespace Basis.IK
{
    public static class BasisSpineBendCore
    {
        const float sqrEpsilon = 1e-8f, epsilon = 1e-5f, bendDeadbandDeg = 3f, bendDeadbandWidthDeg = 7f;
        const float twistFadeFullHoriz = 0.342f, twistFadeZeroHoriz = 0.174f;
        public static void Solve(in BasisSpineBendInput i, out BasisSpineBendResult r)
        {
            r = default;

            Quaternion invHips = Quaternion.Inverse(i.HipsRot), hipsSpace = i.HipsBind * invHips;
            Vector3 localChestDir = hipsSpace * (i.ChestPos - i.HipsPos);
            Vector3 localTargetDir = hipsSpace * (i.SmoothedHead - i.HipsPos);
            if (localChestDir.sqrMagnitude < sqrEpsilon || localTargetDir.sqrMagnitude < sqrEpsilon)
            {
                r.EarlyOut = true;
                return;
            }

            Vector3 chestDirN = localChestDir.normalized, targetDirN = localTargetDir.normalized;
            Vector3 bendCross = Vector3.Cross(chestDirN, targetDirN);
            float bendDot = Mathf.Clamp(Vector3.Dot(chestDirN, targetDirN), -1f, 1f);
            float bendAngleDeg = Mathf.Atan2(bendCross.magnitude, bendDot) * Mathf.Rad2Deg;
            Vector3 bendAxisScaled = bendCross.sqrMagnitude > sqrEpsilon ? bendCross.normalized * bendAngleDeg : Vector3.zero;
            float bendPitchDeg = bendAxisScaled.x, bendRollDeg = bendAxisScaled.z;
            Vector3 bendEuler = new Vector3(bendPitchDeg, 0f, bendRollDeg);
            Quaternion headRotLocal = hipsSpace * i.HeadTargetRot;
            Vector3 headFwdLocal = headRotLocal * Vector3.forward;
            float horizMagSq = headFwdLocal.x * headFwdLocal.x + headFwdLocal.z * headFwdLocal.z;
            float twistY = (horizMagSq < sqrEpsilon) ? 0f : Mathf.Atan2(headFwdLocal.x, headFwdLocal.z) * Mathf.Rad2Deg;
            float twistFadeT = Mathf.Clamp01((Mathf.Sqrt(horizMagSq) - twistFadeZeroHoriz) / (twistFadeFullHoriz - twistFadeZeroHoriz));
            twistY *= Mathf.SmoothStep(0f, 1f, twistFadeT);

            float maxFwd = Mathf.Max(0f, i.SpineMaxForwardDeg), maxBack = Mathf.Max(0f, i.SpineMaxBackwardDeg);
            float maxLat = Mathf.Max(0f, i.SpineMaxLateralDeg);
            float squishMult = ComputeSquishMultiplier(i.SmoothedHead - i.HipsPos, i.RestLen, i.SquishBoost);
            float bendMag = Mathf.Sqrt(bendEuler.x * bendEuler.x + bendEuler.z * bendEuler.z);
            float bendT = Mathf.Clamp01((bendMag - bendDeadbandDeg) / bendDeadbandWidthDeg);
            float bendGate = Mathf.SmoothStep(0f, 1f, bendT), spinePitchEff = Mathf.Clamp01(i.SpineBendPitch);
            float spineYawEff = Mathf.Clamp01(i.SpineBendYaw), spineRollEff = Mathf.Clamp01(i.SpineBendRoll);
            float upperPitchEff = Mathf.Clamp01(i.UpperBendPitch), upperYawEff = Mathf.Clamp01(i.UpperBendYaw);
            float upperRollEff = Mathf.Clamp01(i.UpperBendRoll);
            if (i.AnatDifferentialStiffness)
            {
                spineYawEff *= 0.4f;
                upperYawEff = Mathf.Clamp01(upperYawEff * 1.5f);
            }
            if (i.AnatPelvicTwistRouting)
            {
                float total = spineYawEff + upperYawEff;
                spineYawEff = total * 0.25f;
                upperYawEff = total * 0.75f;
            }

            if (i.HasSpine)
            {
                Vector3 e = new Vector3( bendEuler.x * spinePitchEff * squishMult * bendGate, twistY * spineYawEff * squishMult, bendEuler.z * spineRollEff * squishMult * bendGate );
                e.y += i.BendTwistCoupling * e.z;
                r.SpineEuler = ClampAsymmetric(e, maxFwd, maxBack, maxLat);
                r.WriteSpine = true;
            }
            if (i.HasUpper)
            {
                Vector3 e = new Vector3( bendEuler.x * upperPitchEff * squishMult * bendGate, twistY * upperYawEff * squishMult, bendEuler.z * upperRollEff * squishMult * bendGate );
                e.y += i.BendTwistCoupling * e.z;
                r.UpperEuler = ClampAsymmetric(e, maxFwd, maxBack, maxLat);
                r.WriteUpper = true;
            }

            r.BendPitchDeg = bendPitchDeg;
            r.TwistY = twistY;
            r.SquishMult = squishMult;
            r.BendGate = bendGate;
            r.SpineYawEff = spineYawEff;
            r.UpperYawEff = upperYawEff;
        }
        public static Quaternion Compose(Vector3 e)
        {
            Quaternion yaw = BasisSpineAnatomyCore.AxisAngle(e.y, Vector3.up);
            float swingDeg = Mathf.Sqrt(e.x * e.x + e.z * e.z);
            if (swingDeg <= epsilon)
            {
                return yaw;
            }
            Quaternion swing = BasisSpineAnatomyCore.AxisAngle(swingDeg, new Vector3(e.x / swingDeg, 0f, e.z / swingDeg));
            return yaw * swing;
        }
        public static float ComputeSquishMultiplier(Vector3 hipsToHead, float restLen, float squishBoost)
        {
            float boost = Mathf.Clamp(squishBoost, 0f, 2f);
            if (boost <= 0f)
            {
                return 1f;
            }
            if (restLen < epsilon)
            {
                return 1f;
            }
            float currentMag = hipsToHead.magnitude, squish = currentMag / restLen;
            float t = Mathf.Clamp01((squish - 0.7f) / 0.6f);
            return Mathf.Lerp(1f + boost, Mathf.Max(0f, 1f - boost), t);
        }
        static Vector3 ClampAsymmetric(Vector3 e, float maxFwd, float maxBack, float maxLat)
        {
            if (e.x > 0f) e.x = Mathf.Min(e.x, maxFwd);
            else e.x = Mathf.Max(e.x, -maxBack);
            e.y = Mathf.Clamp(e.y, -maxLat, maxLat);
            e.z = Mathf.Clamp(e.z, -maxLat, maxLat);
            return e;
        }
    }
    public static class BasisCrouchOffsetCore
    {
        const float sqrEpsilon = 1e-8f, epsilon = 1e-5f;
        // Head drop as a fraction of standing head height: the lean starts at StartDepth and is fully applied by FullDepth.
        public const float StartDepth = 0.1f, FullDepth = 0.5f;
        // Max horizontal hip setback as a fraction of the head-to-hips distance.
        public const float MaxSetback = 0.6f;
        // Fraction of MaxSetback where the soft cap starts easing in. Lower is a gentler approach to the limit.
        public const float SoftCapKnee = 0.6f;
        // How far the pelvis tips forward (degrees) at full crouch lean.
        public const float HipTiltDeg = 15f;
        public static void Solve(in BasisCrouchOffsetInput i, out BasisCrouchOffsetResult r)
        {
            r = default;
            r.HipsPos = i.HipsPos;
            r.HipsRot = i.HipsRot;
            if (i.Factor <= 0f || i.StandingHeadHeight <= epsilon || i.RestDist <= epsilon)
            {
                return;
            }

            // 0 above StartDepth, eases in to 1 at FullDepth.
            float t = BasisArmSolveCore.Smoothstep(StartDepth, FullDepth, i.CrouchDepth / i.StandingHeadHeight) * Mathf.Clamp01(i.Fade);
            if (t <= 0f)
            {
                return;
            }
            float setback = t * MaxSetback * i.RestDist * i.Factor;

            Vector3 up = i.PlayerUp.sqrMagnitude < sqrEpsilon ? Vector3.up : i.PlayerUp.normalized;
            Quaternion hipsAnat = i.HipsRot;
            float bindSq = i.Bind.x * i.Bind.x + i.Bind.y * i.Bind.y + i.Bind.z * i.Bind.z + i.Bind.w * i.Bind.w;
            if (bindSq > 0.5f)
            {
                Quaternion invBind = new Quaternion(-i.Bind.x, -i.Bind.y, -i.Bind.z, i.Bind.w);
                hipsAnat = i.HipsRot * invBind;
            }

            Vector3 forward = hipsAnat * Vector3.forward;
            forward -= up * Vector3.Dot(forward, up);
            if (forward.sqrMagnitude < sqrEpsilon)
            {
                return;
            }

            // Push the hips back, then keep them on the spine-length sphere under the head so the spine doesn't stretch.
            Vector3 horizontal = Vector3.ProjectOnPlane(i.HipsPos - i.HeadTargetPos, up) - forward.normalized * setback;
            // Soft cap: passes through below the knee, then eases into the max instead of stopping dead.
            float horizMag = horizontal.magnitude, horizCap = MaxSetback * i.RestDist, knee = horizCap * SoftCapKnee;
            if (horizMag > knee)
            {
                float soft = horizCap - knee, capped = knee + soft * (1f - Mathf.Exp(-(horizMag - knee) / soft));
                horizontal *= capped / horizMag;
                horizMag = capped;
            }
            float sphereDrop = Mathf.Sqrt(Mathf.Max(i.RestDist * i.RestDist - horizMag * horizMag, 0f));
            float drop = Mathf.Lerp(Vector3.Dot(i.HeadTargetPos - i.HipsPos, up), sphereDrop, t);

            r.Applied = true;
            r.SetbackMeters = setback;
            r.LeanDeg = Mathf.Atan2(horizMag, drop) * Mathf.Rad2Deg;
            r.HipsPos = i.HeadTargetPos + horizontal - up * drop;

            // Tip the pelvis forward a little with the lean so the spine isn't doing all the bending.
            Vector3 right = Vector3.Cross(up, forward.normalized);
            r.HipsRot = Quaternion.AngleAxis(t * HipTiltDeg * i.Factor, right) * i.HipsRot;
        }
    }
}
