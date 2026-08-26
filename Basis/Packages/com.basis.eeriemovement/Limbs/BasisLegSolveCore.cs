using UnityEngine;
namespace Basis.IK
{
    public static class BasisLegSolveCore
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f, poleColinearSin = 0.5f;
        public const float MinKneeInteriorDeg = 20f, MaxKneeInteriorDeg = 176f, KneeAnteriorSoftDeg = 85f;
        public const float KneeAnteriorHardDeg = 89.5f, KneeAnteriorTaperStartDeg = 160f, TrackerShinRollMaxDeg = 45f;
        public static float ClampKneeSwivelDeg(float swivelDeg, float softDeg, float hardDeg)
        {
            float wrapped = swivelDeg - 360f * Mathf.Floor((swivelDeg + 180f) / 360f);
            float mag = wrapped < 0f ? -wrapped : wrapped;

            if (!(mag >= 0f))
            {
                return 0f;
            }

            if (!(mag > softDeg))
            {
                return wrapped;
            }

            float maxExcess = hardDeg - softDeg;
            if (!(maxExcess > 0f))
            {
                return wrapped < 0f ? -softDeg : softDeg;
            }

            float excess = mag - softDeg, compressed = softDeg + maxExcess * excess / (maxExcess + excess);
            float taperSpan = 180f - KneeAnteriorTaperStartDeg;
            if (mag > KneeAnteriorTaperStartDeg && taperSpan > 0f)
            {
                float u = (mag - KneeAnteriorTaperStartDeg) / taperSpan;
                if (u > 1f) u = 1f;
                compressed *= 1f - u * u * (3f - 2f * u);
            }

            return wrapped < 0f ? -compressed : compressed;
        }
        /// <summary>Convenience overload for callers with nothing to draw into.</summary>
        public static void Solve(in BasisLegSolveInput i, out BasisLegSolveResult r)
        {
            BasisIKGizmoRecorder gizmos = default;
            Solve(i, out r, ref gizmos);
        }
        public static void Solve(in BasisLegSolveInput i, out BasisLegSolveResult r, ref BasisIKGizmoRecorder gizmos)
        {
            r = default;

            r.MidPostRoll = Quaternion.identity;
            Quaternion hintRotation = i.HasHintRotation ? i.HintRotation : default;

            Vector3 aPosition = i.Root, bPosition = i.Mid, cPosition = i.Tip;
            Quaternion rootRot = i.RootRotation, midRot = i.MidRotation;
            Vector3 tPosition = i.TargetPosition;
            Quaternion tRotation = i.TargetRotation * i.TargetOffset;
            float hintWeight = i.HintWeight;
            bool hasHint = hintWeight > 0f;
            Vector3 ab = bPosition - aPosition, bc = cPosition - bPosition, ac = cPosition - aPosition;
            float abLen = ab.magnitude, bcLen = bc.magnitude, acLen = ac.magnitude, maxReach = abLen + bcLen;
            float oldAbcAngle = BasisIKMath.TriangleAngle(acLen, abLen, bcLen);
            Vector3 atCorrected = tPosition - aPosition;
            float atCorrectedLen = atCorrected.magnitude, minFlexReach = MinFlexionReach(abLen, bcLen);
            if (atCorrectedLen < minFlexReach) atCorrectedLen = minFlexReach;

            float maxExtReach = MaxExtensionReach(abLen, bcLen);
            if (atCorrectedLen > maxExtReach) atCorrectedLen = maxExtReach;

            float newAbcAngle = BasisIKMath.TriangleAngle(atCorrectedLen, abLen, bcLen);
            byte axisSource = 0;
            Vector3 bendAxis = Vector3.Cross(ab, bc);
            if (bendAxis.sqrMagnitude < sqrEpsilon)
            {
                if (hasHint)
                {
                    bendAxis = Vector3.Cross(i.HintPosition - aPosition, bc);
                    axisSource = 1;
                }

                if (bendAxis.sqrMagnitude < sqrEpsilon)
                {
                    bendAxis = Vector3.Cross(atCorrected, bc);
                    axisSource = 2;
                }

                if (bendAxis.sqrMagnitude < sqrEpsilon)
                {
                    Vector3 bcN = bcLen > epsilon ? bc / bcLen : Vector3.zero;
                    bendAxis = i.BendNormal - bcN * Vector3.Dot(i.BendNormal, bcN);
                    axisSource = 3;

                    if (bendAxis.sqrMagnitude < sqrEpsilon)
                    {
                        bendAxis = i.BendNormal;
                    }
                }
            }

            bendAxis = Vector3.Normalize(bendAxis);

            float half = 0.5f * (oldAbcAngle - newAbcAngle), sinHalf = Mathf.Sin(half), cosHalf = Mathf.Cos(half);
            Quaternion deltaR = new Quaternion(bendAxis.x * sinHalf, bendAxis.y * sinHalf, bendAxis.z * sinHalf, cosHalf);

            midRot = deltaR * midRot;
            cPosition = bPosition + deltaR * (cPosition - bPosition);
            ac = cPosition - aPosition;

            Quaternion rootDelta = Quaternion.identity;
            if (atCorrectedLen > epsilon)
            {
                rootDelta = BasisQuaternionExt.FromToRotation(ac, atCorrected);
                rootRot = rootDelta * rootRot;
                bPosition = aPosition + rootDelta * (bPosition - aPosition);
                cPosition = aPosition + rootDelta * (cPosition - aPosition);
                midRot = rootDelta * midRot;
            }

            Quaternion hintR = Quaternion.identity;
            bool hintApplied = false;
            Vector3 acFinal = cPosition - aPosition;
            float acFinalSqr = acFinal.sqrMagnitude;
            if (acFinalSqr > sqrEpsilon)
            {
                Vector3 acNorm = acFinal / Mathf.Sqrt(acFinalSqr);
                Vector3 kneeDir = Vector3.Cross(acNorm, rootDelta * bendAxis);
                kneeDir -= acNorm * Vector3.Dot(kneeDir, acNorm);

                Vector3 bendPole = Vector3.Cross(acNorm, i.BendNormal);
                bendPole -= acNorm * Vector3.Dot(bendPole, acNorm);
                bool hasBendPole = bendPole.sqrMagnitude > sqrEpsilon;
                Vector3 anteriorPole = bendPole;
                bool hasAnteriorPole = hasBendPole;
                if (i.AnteriorNormal.sqrMagnitude > sqrEpsilon)
                {
                    Vector3 ap = Vector3.Cross(acNorm, i.AnteriorNormal);
                    ap -= acNorm * Vector3.Dot(ap, acNorm);
                    if (ap.sqrMagnitude > sqrEpsilon)
                    {
                        anteriorPole = ap;
                        hasAnteriorPole = true;
                    }
                }

                Vector3 pole = bendPole;
                if (hasHint)
                {
                    Vector3 ah = i.HintPosition - aPosition, ahProj = ah - acNorm * Vector3.Dot(ah, acNorm);
                    float ahLen = ah.magnitude;

                    if (ahProj.sqrMagnitude > sqrEpsilon)
                    {
                        pole = ahProj;
                    }

                    pole = GuardPoleAnterior(pole, anteriorPole, acNorm, hasAnteriorPole, ref axisSource);

                    float poleSin = ahLen > epsilon ? ahProj.magnitude / ahLen : 0f;
                    if (poleSin < poleColinearSin && hasBendPole && pole.sqrMagnitude > sqrEpsilon)
                    {
                        float blend = 1f - poleSin / poleColinearSin;
                        pole = Vector3.Slerp(pole.normalized, bendPole.normalized, blend);
                        axisSource = 4;
                    }

                    if (i.HintDistrust > 0f && hasBendPole && pole.sqrMagnitude > sqrEpsilon)
                    {
                        pole = Vector3.Slerp(pole.normalized, bendPole.normalized, Mathf.Clamp01(i.HintDistrust));
                        axisSource = 5;
                    }
                }

                pole -= acNorm * Vector3.Dot(pole, acNorm);

                pole = GuardPoleAnterior(pole, anteriorPole, acNorm, hasAnteriorPole, ref axisSource);

                float weight = hasHint ? hintWeight : 1f;

                if (weight > 0f && kneeDir.sqrMagnitude > sqrEpsilon && pole.sqrMagnitude > sqrEpsilon)
                {
                    float swivel = ScaleSwivel(BasisIKMath.SignedAngleRad(kneeDir, pole, acNorm), weight);
                    hintR = BasisIKMath.AngleAxisRad(swivel, acNorm);

                    rootRot = hintR * rootRot;
                    bPosition = aPosition + hintR * (bPosition - aPosition);
                    cPosition = aPosition + hintR * (cPosition - aPosition);
                    midRot = hintR * midRot;
                    hintApplied = hasHint;
                }
            }

            float hintRotSqr = hintRotation.x * hintRotation.x + hintRotation.y * hintRotation.y + hintRotation.z * hintRotation.z + hintRotation.w * hintRotation.w;
            if (i.HintIsTracker && hintRotSqr > 0.5f)
            {
                Vector3 shinRoll = cPosition - bPosition;
                if (shinRoll.sqrMagnitude > sqrEpsilon)
                {
                    Vector3 shinRollN = shinRoll.normalized;
                    float roll = BasisIKMath.TwistAngleRad(hintRotation * Quaternion.Inverse(midRot), shinRollN);
                    float rollAbs = Mathf.Abs(roll), rollCap = TrackerShinRollMaxDeg * Mathf.Deg2Rad;
                    if (rollAbs > rollCap) rollAbs = rollCap;
                    if (rollAbs > 1e-6f)
                    {
                        float rollSigned = roll < 0f ? -rollAbs : rollAbs;
                        r.MidPostRoll = BasisIKMath.AngleAxisRad(rollSigned, shinRollN);
                        midRot = r.MidPostRoll * midRot;
                        r.ShinRollDeg = rollSigned * Mathf.Rad2Deg;
                    }
                }
            }

            r.MidDelta = deltaR;
            r.RootDelta = rootDelta;
            r.HintDelta = hintR;
            r.TipRotation = tRotation;
            r.HintApplied = hintApplied;

            r.KneeSolved = bPosition;
            r.FootSolved = cPosition;
            r.RootRotationSolved = rootRot;
            r.MidRotationSolved = midRot;

            r.UpperLength = abLen;
            r.LowerLength = bcLen;
            r.TargetDistance = atCorrectedLen;
            r.ReachRatio = (maxReach > epsilon) ? atCorrectedLen / maxReach : 0f;
            r.KneeAngleDeg = BasisIKMath.AngleDeg(aPosition - bPosition, cPosition - bPosition);
            r.AxisSource = axisSource;
            r.FootError = (cPosition - tPosition).magnitude;
        }
        static Vector3 GuardPoleAnterior(Vector3 pole, Vector3 anteriorPole, Vector3 acNorm, bool hasAnteriorPole, ref byte axisSource)
        {
            if (!hasAnteriorPole || !(pole.sqrMagnitude > sqrEpsilon))
            {
                return pole;
            }

            Vector3 anterior = anteriorPole.normalized;
            float poleDeg = BasisIKMath.SignedAngleRad(anterior, pole, acNorm) * Mathf.Rad2Deg;
            float guardedDeg = ClampKneeSwivelDeg(poleDeg, KneeAnteriorSoftDeg, KneeAnteriorHardDeg);

            if (guardedDeg != poleDeg)
            {
                pole = BasisIKMath.AngleAxisRad(guardedDeg * Mathf.Deg2Rad, acNorm) * anterior;
                axisSource = 5;
            }

            return pole;
        }
        static float ScaleSwivel(float radians, float weight)
        {
            if (weight >= 1f)
            {
                return radians;
            }

            if (!(weight > 0f))
            {
                return 0f;
            }

            return 2f * Mathf.Atan(weight * Mathf.Tan(0.5f * radians));
        }
        static float MinFlexionReach(float upper, float lower)
        {
            float c = Mathf.Cos(MinKneeInteriorDeg * Mathf.Deg2Rad);
            float d2 = upper * upper + lower * lower - 2f * upper * lower * c;
            return d2 > 0f ? Mathf.Sqrt(d2) : 0f;
        }
        static float MaxExtensionReach(float upper, float lower)
        {
            float c = Mathf.Cos(MaxKneeInteriorDeg * Mathf.Deg2Rad);
            float d2 = upper * upper + lower * lower - 2f * upper * lower * c;
            return d2 > 0f ? Mathf.Sqrt(d2) : 0f;
        }
    }
    public static class BasisKneeForwardCore
    {
        public const float DefaultUprightCoupling = 1.0f, FollowFadeStartDeg = 120f, MaxFollowDeg = 60f;
        public const float LegUprightFadeStartDot = 0.25f, LegUprightFadeFullDot = 0.55f, RefCondSinFadeStart = 0.15f;
        public const float RefCondSinFadeFull = 0.35f;
        const float epsilon = 1e-5f, sqrEpsilon = 1e-10f;
        /// <summary>Convenience overload for callers with nothing to draw into.</summary>
        public static void Solve(in BasisKneeForwardInput i, out BasisKneeForwardResult r)
        {
            BasisIKGizmoRecorder gizmos = default;
            Solve(i, out r, ref gizmos);
        }
        public static void Solve(in BasisKneeForwardInput i, out BasisKneeForwardResult r, ref BasisIKGizmoRecorder gizmos)
        {
            r = default;

            Vector3 hipToFoot = i.FootPosition - i.HipPosition;
            float axisSqr = hipToFoot.sqrMagnitude, radius = i.UpperLength > epsilon ? i.UpperLength : 0.4f;
            Vector3 mid = (i.HipPosition + i.FootPosition) * 0.5f;

            if (axisSqr < sqrEpsilon)
            {
                r.BendDir = i.BodyForwardDir.sqrMagnitude > sqrEpsilon ? i.BodyForwardDir.normalized : Vector3.forward;
                r.KneeHint = mid + r.BendDir * radius;
                r.HintWeight = 0f;
                return;
            }
            Vector3 axis = hipToFoot / Mathf.Sqrt(axisSqr);
            Vector3 up = i.PlayerUp.sqrMagnitude > sqrEpsilon ? i.PlayerUp.normalized : Vector3.up;
            Vector3 bodyPerp = Vector3.ProjectOnPlane(i.BodyForwardDir, axis);
            if (bodyPerp.sqrMagnitude < sqrEpsilon)
            {
                Vector3 fallback = Vector3.ProjectOnPlane(up, axis);
                if (fallback.sqrMagnitude < sqrEpsilon)
                {
                    fallback = Vector3.Cross(axis, Vector3.right);
                }
                if (fallback.sqrMagnitude < sqrEpsilon)
                {
                    fallback = Vector3.Cross(axis, Vector3.up);
                }
                r.BendDir = fallback.normalized;
                r.KneeHint = mid + r.BendDir * radius;
                r.HintWeight = 0f;
                return;
            }
            Vector3 bodyPerpN = bodyPerp.normalized;
            float fwdMag = i.BodyForwardDir.magnitude;
            float refConditioning = Smoothstep(RefCondSinFadeStart, RefCondSinFadeFull, fwdMag > epsilon ? Mathf.Sqrt(bodyPerp.sqrMagnitude) / fwdMag : 0f);
            float legVertical01 = Smoothstep(LegUprightFadeStartDot, LegUprightFadeFullDot, Mathf.Abs(Vector3.Dot(axis, up)));
            r.Upright01 = legVertical01;

            float strength = BasisIKMath.Saturate(i.Strength);
            Vector3 footPerp = Vector3.ProjectOnPlane(i.FootForwardDir, axis), bendDir;
            float followDeg;
            if (footPerp.sqrMagnitude < sqrEpsilon || legVertical01 <= 0f)
            {
                bendDir = bodyPerpN;
                followDeg = 0f;
            }
            else
            {
                Vector3 footPerpN = footPerp.normalized;
                float signedDeg = Vector3.SignedAngle(bodyPerpN, footPerpN, axis);
                float rawAngle = signedDeg < 0f ? -signedDeg : signedDeg;

                followDeg = Mathf.Min(BasisIKMath.Saturate(i.Coupling) * legVertical01 * refConditioning * rawAngle, MaxFollowDeg);

                if (rawAngle > FollowFadeStartDeg)
                {
                    float u = (rawAngle - FollowFadeStartDeg) / (180f - FollowFadeStartDeg);
                    if (u > 1f) u = 1f;
                    followDeg *= 1f - u * u * (3f - 2f * u);
                }

                bendDir = Quaternion.AngleAxis(signedDeg < 0f ? -followDeg : followDeg, axis) * bodyPerpN;
                bendDir = Vector3.ProjectOnPlane(bendDir, axis);
                bendDir = bendDir.sqrMagnitude > sqrEpsilon ? bendDir.normalized : bodyPerpN;
            }

            r.BendDir = bendDir;
            r.FollowDeg = followDeg;
            r.KneeHint = mid + bendDir * radius;
            r.HintWeight = strength * refConditioning;
        }
        static float Smoothstep(float a, float b, float v)
        {
            float t = Mathf.Approximately(a, b) ? (v >= b ? 1f : 0f) : BasisIKMath.Saturate((v - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
