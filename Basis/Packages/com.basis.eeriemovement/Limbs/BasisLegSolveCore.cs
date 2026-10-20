using UnityEngine;
namespace Basis.IK
{
    public static class BasisLegSolveCore
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f, restBendAgreeCos = 0.94f;
        public const float MinKneeInteriorDeg = 20f, MaxKneeInteriorDeg = 176f, KneeAnteriorSoftDeg = 85f;
        public const float KneeAnteriorHardDeg = 89.5f, KneeAnteriorTaperStartDeg = 160f, TrackerShinRollMaxDeg = 45f;
        public const float KneeHintForward = 0.5f;
        public const float KneeHintFootFlatTrust = 0.3f;
        public const float KneeTrackerHintBias = 0.06f;
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
        public static void Solve(in BasisLegSolveInput i, out BasisLegSolveResult r)
        {
            r = default;

            r.MidPostRoll = Quaternion.identity;
            Quaternion hintRotation = i.HasHintRotation ? i.HintRotation : default;

            Vector3 aPosition = i.Root, bPosition = i.Mid, cPosition = i.Tip;
            Quaternion rootRot = i.RootRotation, midRot = i.MidRotation;
            Vector3 tPosition = i.TargetPosition;
            Quaternion tRotation = i.TargetRotation * i.TargetOffset;
            Vector3 ab = bPosition - aPosition, bc = cPosition - bPosition, ac = cPosition - aPosition;
            float abLen = ab.magnitude, bcLen = bc.magnitude, acLen = ac.magnitude, maxReach = abLen + bcLen;

            // If we don't have a knee hint (active knee tracker) then we make our own and position it accordingly.
            Vector3 hintPosition = i.HintPosition;
            float hintWeight = i.HintWeight;
            if (!i.HintIsTracker && i.HasBodyFrame)
            {
                // Push direction is the foot's forward flattened onto the ground, so the knee points the way the foot does.
                // When the foot points mostly up/down the flattened forward gets unreliable, so blend toward the hips forward.
                Vector3 flatFoot = Vector3.ProjectOnPlane(i.FootForward, i.Up);
                float flatLen = flatFoot.magnitude;
                Vector3 pushDir = i.HipsForward;
                if (flatLen > epsilon)
                {
                    float footTrust = Mathf.Clamp01(flatLen / KneeHintFootFlatTrust);
                    pushDir = Vector3.Slerp(i.HipsForward, flatFoot / flatLen, footTrust);
                }

                // Start halfway between the hip and the foot target, then push it out in front of the knee.
                hintPosition = (aPosition + tPosition) * 0.5f + pushDir * (maxReach * KneeHintForward);
                hintWeight = 1f;
            }

            // Near full extension a tracked knee sits on the hip/foot line, so the direction of its offset is mostly noise and the leg twists around.
            // Push the hint a fixed distance along the bend the tracker's rotation implies: the offset then never shrinks below the push, so small
            // noise can't flip it, and once the knee bends the real offset outgrows the push and takes over on its own. Same as a tracked elbow.
            if (i.HintIsTracker && hintWeight > 0f && TrackerBendDir(i, tPosition - aPosition, out Vector3 trackerBendDir))
            {
                hintPosition += trackerBendDir * (KneeTrackerHintBias * maxReach);
            }

            bool hasHint = hintWeight > 0f;
            float oldAbcAngle = BasisIKMath.TriangleAngle(acLen, abLen, bcLen);
            Vector3 atCorrected = tPosition - aPosition;
            float atCorrectedLen = atCorrected.magnitude, minFlexReach = MinFlexionReach(abLen, bcLen);
            if (atCorrectedLen < minFlexReach) atCorrectedLen = minFlexReach;

            float maxExtReach = MaxExtensionReach(abLen, bcLen);
            if (atCorrectedLen > maxExtReach) atCorrectedLen = maxExtReach;

            float newAbcAngle = BasisIKMath.TriangleAngle(atCorrectedLen, abLen, bcLen);
            byte axisSource = 0;
            Vector3 restAxis = Vector3.Cross(ab, bc), bendAxis = restAxis;
            Vector3 abN = abLen > epsilon ? ab / abLen : Vector3.zero, anatAxis = i.BendNormal - abN * Vector3.Dot(i.BendNormal, abN);
            bool restBend = restAxis.sqrMagnitude >= sqrEpsilon, anatomical = abLen > epsilon && anatAxis.sqrMagnitude > sqrEpsilon && (!restBend || Vector3.Dot(restAxis.normalized, anatAxis.normalized) < restBendAgreeCos);
            if (anatomical)
            {
                bendAxis = anatAxis;
                axisSource = 3;
            }
            else if (!restBend)
            {
                if (hasHint)
                {
                    bendAxis = Vector3.Cross(hintPosition - aPosition, bc);
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
            if (anatomical)
            {
                deltaR = BasisIKMath.AngleAxisRad(Mathf.PI - newAbcAngle, bendAxis);
                if (restBend) deltaR = deltaR * BasisIKMath.AngleAxisRad(oldAbcAngle - Mathf.PI, Vector3.Normalize(restAxis));
            }

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
                    Vector3 ah = hintPosition - aPosition, ahProj = ah - acNorm * Vector3.Dot(ah, acNorm);
                    if (ahProj.sqrMagnitude > sqrEpsilon)
                    {
                        pole = ahProj;
                    }
                }

                pole -= acNorm * Vector3.Dot(pole, acNorm);

                pole = GuardPoleAnterior(pole, anteriorPole, acNorm, hasAnteriorPole, ref axisSource);
                if (pole.sqrMagnitude > sqrEpsilon) r.PoleDir = pole.normalized;

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

            r.HintPosition = hintPosition;
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
        // Knee direction (perpendicular to the hip/foot axis) implied by the knee tracker's rotation, else by the hips.
        // HintRotation is the tracker mapped onto the lower leg bone, so carrying the rest-pose hinge (BendNormal, hips-right) through it gives the live hinge.
        static bool TrackerBendDir(in BasisLegSolveInput i, Vector3 axis, out Vector3 dir)
        {
            dir = default;
            float axisSqr = axis.sqrMagnitude;
            if (axisSqr < sqrEpsilon)
            {
                return false;
            }
            axis /= Mathf.Sqrt(axisSqr);
            Vector3 hinge = i.HasHintRotation ? i.HintRotation * (Quaternion.Inverse(i.MidRotation) * i.BendNormal) : i.BendNormal;
            dir = Vector3.Cross(axis, hinge);
            dir -= axis * Vector3.Dot(dir, axis);
            float dirSqr = dir.sqrMagnitude;
            if (dirSqr < sqrEpsilon)
            {
                return false;
            }
            dir /= Mathf.Sqrt(dirSqr);
            return true;
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
}
