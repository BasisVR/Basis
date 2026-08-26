using UnityEngine;
namespace Basis.IK
{
    public static class BasisShoulderSolveCore
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-10f, reachEngageThreshold = 0.7f, setStartDeg = 8f;
        const float setFullDeg = 95f, trackerRefine = 0.35f, depressionShare = 0.25f, depressionBand = 0.12f;
        const float shrugHangStart = 0.75f, shrugHangFull = 0.92f, shrugRiseStartFrac = 0.02f;
        const float shrugRiseFullFrac = 0.065f, shrugBendFadeStartFrac = 0.09f, shrugBendFadeEndFrac = 0.14f;
        const float shrugMaxDeg = 44f;
        /// <summary>Convenience overload for callers with nothing to draw into.</summary>
        public static void Solve(in BasisShoulderSolveInput i, out BasisShoulderSolveResult r)
        {
            BasisIKGizmoRecorder gizmos = default;
            Solve(i, out r, ref gizmos);
        }
        public static void Solve(in BasisShoulderSolveInput i, out BasisShoulderSolveResult r, ref BasisIKGizmoRecorder gizmos)
        {
            r = default;

            float armLen = Mathf.Max(i.TposeArmLength, epsilon);
            Vector3 driver = i.HasElbow ? i.ElbowPos : i.HandTargetPos, armVec = driver - i.ShoulderPos;
            Vector3 handVec = i.HandTargetPos - i.ShoulderPos;
            if (armVec.sqrMagnitude < sqrEpsilon || i.TposeArmDirWorld.sqrMagnitude < sqrEpsilon)
            {
                r.Apply = false;
                return;
            }

            Quaternion invChest = Quaternion.Inverse(i.ChestRot);
            Vector3 restDirL = (Quaternion.Inverse(i.TposeChestRot) * i.TposeArmDirWorld).normalized;
            Vector3 armDirL = (invChest * armVec).normalized;
            Quaternion swing = BasisQuaternionExt.FromToRotation(restDirL, armDirL);
            Vector3 rv = QuatToRotationVector(swing);
            float swingDeg = rv.magnitude * Mathf.Rad2Deg, rawReach = handVec.magnitude / (armLen * 1.1f);
            float reachFade = i.HasElbow ? 1f : ReachEngage(Mathf.Clamp01(rawReach));
            float setting = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(setStartDeg, setFullDeg, swingDeg));
            float engage = setting * reachFade, couple = i.CoupleRatio * engage, raise = armDirL.y - restDirL.y;
            float elevGain = Mathf.Lerp(depressionShare, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-depressionBand, depressionBand, raise)));
            Vector3 girdleRv = new Vector3(rv.x * i.ElevationFactor * elevGain, rv.y * i.ProtractionFactor, rv.z * i.ElevationFactor * elevGain) * couple;
            float shrugRad = 0f, bindLen = i.HasElbow ? i.TposeElbowLength : i.TposeArmLength;
            if (i.ShrugEnabled && bindLen > epsilon)
            {
                float clav = Mathf.Clamp(i.TposeClavicleLength, 0f, bindLen * 0.45f);
                float seg = Mathf.Max(bindLen - clav, epsilon);
                Vector3 segVecL = armDirL * armVec.magnitude - restDirL * clav;
                float hangDot = segVecL.sqrMagnitude > sqrEpsilon ? -segVecL.normalized.y : 0f;
                if (hangDot > shrugHangStart)
                {
                    float hang = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(shrugHangStart, shrugHangFull, hangDot));
                    float cr = Vector3.Dot(restDirL, armDirL);
                    float expected = clav * cr + Mathf.Sqrt(clav * clav * (cr * cr - 1f) + seg * seg);
                    float deficitFrac = (expected - armVec.magnitude) / armLen;
                    float riseIn = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(shrugRiseStartFrac, shrugRiseFullFrac, deficitFrac));
                    float bendOut = i.HasElbow ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(shrugBendFadeStartFrac, shrugBendFadeEndFrac, deficitFrac));
                    shrugRad = shrugMaxDeg * Mathf.Deg2Rad * hang * riseIn * (1f - bendOut) * Mathf.Max(i.ElevationFactor, 0f);
                    girdleRv.z += i.IsLeft ? -shrugRad : shrugRad;
                }
            }

            girdleRv -= Vector3.Dot(girdleRv, armDirL) * armDirL;

            float maxRad = Mathf.Max(i.MaxShoulderDeg, 0f) * Mathf.Deg2Rad, mag = girdleRv.magnitude;
            if (maxRad > 0f && mag > maxRad && mag > epsilon)
            {
                girdleRv *= maxRad / mag;
            }

            Quaternion girdleL = BasisQuaternionExt.NormalizeSafe(RotationVectorToQuat(girdleRv));
            Quaternion shoulderRestLocal = Quaternion.Inverse(i.TposeChestRot) * i.TposeShoulderRot;
            Quaternion anatomical = i.ChestRot * girdleL * shoulderRestLocal, result;
            if (i.HasShoulderTracker)
            {
                Quaternion deltaWorld = i.ChestRot * girdleL * invChest;
                result = Quaternion.Slerp(i.TrackerFinal, deltaWorld * i.TrackerFinal, trackerRefine * engage);
            }
            else
            {
                result = anatomical;
            }

            float elevRad = Mathf.Sqrt(girdleRv.x * girdleRv.x + girdleRv.z * girdleRv.z);
            float twistLeak = girdleRv.sqrMagnitude > epsilon ? Vector3.Dot(girdleRv, armDirL) : 0f;

            r.Apply = true;
            r.ShoulderRotation = result;
            r.ReachRatio = reachFade;
            r.Elevation = elevRad * Mathf.Rad2Deg;
            r.Protraction = Mathf.Abs(girdleRv.y) * Mathf.Rad2Deg;
            r.CrossBodyContrib = CrossBody(armDirL, restDirL, i.IsLeft) * Mathf.Abs(girdleRv.y) * Mathf.Rad2Deg;
            r.ComputedWeight = engage;
            r.SwingAngleDeg = swingDeg;
            r.AppliedAngleDeg = Quaternion.Angle(Quaternion.identity, girdleL);
            r.TwistLeakDeg = Mathf.Abs(twistLeak) * Mathf.Rad2Deg;
            r.DriverIsElbow = i.HasElbow;
            r.ShrugDeg = shrugRad * Mathf.Rad2Deg;
        }
        static float ReachEngage(float reach)
        {
            if (reach < reachEngageThreshold)
            {
                return 0f;
            }
            float t = (reach - reachEngageThreshold) / (1f - reachEngageThreshold);
            return t * t;
        }
        static float CrossBody(Vector3 armDirL, Vector3 restDirL, bool isLeft)
        {
            float side = isLeft ? -armDirL.x : armDirL.x;
            return Mathf.Clamp01(-side);
        }
        static Quaternion RotationVectorToQuat(Vector3 rv)
        {
            float ang = rv.magnitude;
            if (ang < epsilon)
            {
                return Quaternion.identity;
            }
            return Quaternion.AngleAxis(ang * Mathf.Rad2Deg, rv / ang);
        }
        static Vector3 QuatToRotationVector(Quaternion q)
        {
            q.ToAngleAxis(out float deg, out Vector3 axis);
            if (float.IsNaN(axis.x) || float.IsInfinity(axis.x) || axis.sqrMagnitude < epsilon)
            {
                return Vector3.zero;
            }
            if (deg > 180f)
            {
                deg -= 360f;
            }
            return axis.normalized * (deg * Mathf.Deg2Rad);
        }
    }
}
