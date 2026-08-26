using UnityEngine;
namespace Basis.IK
{
    public static class BasisElbowProtectCore
    {
        const float epsilon = 1e-5f, clearMargin = 0.003f;
        const int swivelSteps = 48, clearRefineSteps = 12, maxRefineSteps = 12;
        const float swingPreferenceRatio = 0.0125f, contactMarginRatio = 0.25f, chestDepthRatio = 0.68f;
        const float authorityFadeStart = 0.95f, authorityFadeEnd = 0.995f;
        struct Frame
        {
            public Vector3 ShoulderPos, AcDir, CurrentDir, ElbowCenter, BodyLat, BodyFwd;
            public float ElbowRadius, UpperArmR, ChestR, SpineR, HipsR, ThetaOut, SwingPreference;
        }
        /// <summary>Convenience overload for callers with nothing to draw into.</summary>
        public static void Solve(in BasisElbowProtectInput i, out BasisElbowProtectResult r)
        {
            BasisIKGizmoRecorder gizmos = default;
            Solve(i, out r, ref gizmos);
        }
        public static void Solve(in BasisElbowProtectInput i, out BasisElbowProtectResult r, ref BasisIKGizmoRecorder gizmos)
        {
            r = default;
            r.DesiredElbow = i.Elbow;
            r.SideDot = float.NaN;

            Vector3 shoulderPos = i.Shoulder, elbowPos = i.Elbow, handPos = i.Hand, acAxis = handPos - shoulderPos;
            float acSqr = Vector3.Dot(acAxis, acAxis);
            if (acSqr <= epsilon * epsilon)
            {
                return;
            }

            Frame f = default;
            f.ShoulderPos = shoulderPos;
            f.AcDir = acAxis / Mathf.Sqrt(acSqr);
            Vector3 toElbow = elbowPos - shoulderPos;
            f.ElbowCenter = shoulderPos + f.AcDir * Vector3.Dot(toElbow, f.AcDir);
            f.ElbowRadius = (elbowPos - f.ElbowCenter).magnitude;
            r.ElbowCenter = f.ElbowCenter;
            r.ElbowRadius = f.ElbowRadius;
            if (f.ElbowRadius <= epsilon)
            {
                return;
            }

            f.UpperArmR = Mathf.Max(0f, (i.HandRadius + i.HandSkin) * 1.2f);
            float chestRBase = i.ChestRadiusBase, skin = i.CollisionSkin;

            f.ChestR = Mathf.Max(0f, chestRBase + skin);
            f.SpineR = Mathf.Max(0f, chestRBase * 0.8f + skin);
            f.HipsR = Mathf.Max(0f, chestRBase * 1.4f + skin);

            Vector3 upN = i.PlayerUp.sqrMagnitude > epsilon * epsilon ? i.PlayerUp.normalized : Vector3.up;
            Vector3 bodyLat = i.BodyRight - upN * Vector3.Dot(i.BodyRight, upN);
            if (bodyLat.sqrMagnitude <= epsilon * epsilon)
            {
                Vector3 chestClosest = BasisEerieMovement.ClosestPointOnSegment(shoulderPos, i.ChestPos, i.NeckPos);
                Vector3 off = shoulderPos - chestClosest;
                bodyLat = off - upN * Vector3.Dot(off, upN);
            }
            Vector3 bodyFwd = Vector3.zero;
            float bodyLatLen = bodyLat.magnitude;
            if (bodyLatLen > epsilon)
            {
                bodyLat /= bodyLatLen;
                Vector3 fwd = Vector3.Cross(bodyLat, upN);
                float fLen = fwd.magnitude;
                bodyFwd = fLen > epsilon ? fwd / fLen : Vector3.zero;
            }
            else
            {
                bodyLat = Vector3.zero;
            }
            f.BodyLat = bodyLat;
            f.BodyFwd = bodyFwd;

            float contactMargin = f.ChestR * contactMarginRatio;
            f.SwingPreference = f.ChestR * swingPreferenceRatio;

            float natClear = MinTorsoClearance(i, f, elbowPos), worstPen = natClear < 0f ? -natClear : 0f;
            r.WorstPenetration = worstPen;
            if (natClear >= contactMargin)
            {
                return;
            }

            Vector3 shoulderClosest = BasisEerieMovement.ClosestPointOnSegment(shoulderPos, i.ChestPos, i.NeckPos);
            Vector3 shoulderOut = shoulderPos - shoulderClosest;
            Vector3 shoulderPerp = shoulderOut - f.AcDir * Vector3.Dot(shoulderOut, f.AcDir);
            float shoulderPerpSqr = shoulderPerp.sqrMagnitude;
            if (shoulderPerpSqr <= epsilon * epsilon)
            {
                return;
            }
            Vector3 outDir = shoulderPerp / Mathf.Sqrt(shoulderPerpSqr);

            f.CurrentDir = (elbowPos - f.ElbowCenter) / f.ElbowRadius;
            r.SideDot = Vector3.Dot(f.CurrentDir, outDir);

            f.ThetaOut = Mathf.Atan2(Vector3.Dot(Vector3.Cross(f.CurrentDir, outDir), f.AcDir), Vector3.Dot(f.CurrentDir, outDir)) * Mathf.Rad2Deg;
            float firstClearT = -1f, lastBlockedT = 0f, bestClear = float.NegativeInfinity;
            int bestClearK = 0;
            for (int k = 0; k <= swivelSteps; k++)
            {
                float t = (float)k / swivelSteps, c = SwivelClearance(i, f, t);
                if (firstClearT < 0f)
                {
                    if (c >= clearMargin)
                    {
                        firstClearT = t;
                    }
                    else
                    {
                        lastBlockedT = t;
                    }
                }
                float s = c - f.SwingPreference * t;
                if (s > bestClear)
                {
                    bestClear = s;
                    bestClearK = k;
                }
            }
            float bestClearT = (float)bestClearK / swivelSteps;
            bool cleared = firstClearT >= 0f;
            if (cleared)
            {
                if (firstClearT > 0f)
                {
                    float lo = lastBlockedT, hi = firstClearT;
                    for (int b = 0; b < clearRefineSteps; b++)
                    {
                        float mid = 0.5f * (lo + hi), c = SwivelClearance(i, f, mid);
                        if (c >= clearMargin)
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid;
                        }
                    }
                    firstClearT = hi;
                }
            }
            else
            {
                float lo = (float)Mathf.Max(0, bestClearK - 1) / swivelSteps;
                float hi = (float)Mathf.Min(swivelSteps, bestClearK + 1) / swivelSteps;
                bestClearT = RefineClearanceMax(i, f, lo, hi);
            }

            float chosenT = cleared ? firstClearT : bestClearT;
            float flipCommit = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Mathf.Abs(f.ThetaOut) - 100f) / 80f));
            chosenT += (1f - chosenT) * flipCommit;

            float totalLen = (elbowPos - shoulderPos).magnitude + (handPos - elbowPos).magnitude;
            float reach = totalLen > epsilon ? Mathf.Sqrt(acSqr) / totalLen : 1f;
            float authority = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((reach - authorityFadeStart) / (authorityFadeEnd - authorityFadeStart)));
            chosenT *= authority;

            float approach = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(natClear / contactMargin));
            chosenT *= approach;

            Vector3 dir = Quaternion.AngleAxis(f.ThetaOut * chosenT, f.AcDir) * f.CurrentDir;

            r.DesiredElbow = f.ElbowCenter + dir * f.ElbowRadius;
            r.SwingAngleDeg = Mathf.Abs(f.ThetaOut * chosenT);
            r.BlendUsed = chosenT;

            r.CollisionState = worstPen <= epsilon ? 0 : (cleared ? 1 : 2);
            r.ResidualClearance = MinTorsoClearance(i, f, r.DesiredElbow);
            r.Engaged = true;
        }
        static float RefineClearanceMax(in BasisElbowProtectInput i, in Frame f, float lo, float hi)
        {
            const float invPhi = 0.6180339887f;
            float a = lo, b = hi;
            float t1 = b - invPhi * (b - a), t2 = a + invPhi * (b - a);
            float f1 = SwivelClearance(i, f, t1) - f.SwingPreference * t1;
            float f2 = SwivelClearance(i, f, t2) - f.SwingPreference * t2;
            for (int n = 0; n < maxRefineSteps; n++)
            {
                if (f1 < f2)
                {
                    a = t1; t1 = t2; f1 = f2;
                    t2 = a + invPhi * (b - a);
                    f2 = SwivelClearance(i, f, t2) - f.SwingPreference * t2;
                }
                else
                {
                    b = t2; t2 = t1; f2 = f1;
                    t1 = b - invPhi * (b - a);
                    f1 = SwivelClearance(i, f, t1) - f.SwingPreference * t1;
                }
            }
            return 0.5f * (a + b);
        }
        static float SwivelClearance(in BasisElbowProtectInput i, in Frame f, float t)
        {
            Vector3 d = Quaternion.AngleAxis(f.ThetaOut * t, f.AcDir) * f.CurrentDir;
            return MinTorsoClearance(i, f, f.ElbowCenter + d * f.ElbowRadius);
        }
        static float MinTorsoClearance(in BasisElbowProtectInput i, in Frame f, Vector3 elbowPos)
        {
            float worst = float.PositiveInfinity;
            if (i.HasHips && i.HasSpine)
            {
                worst = Mathf.Min(worst, SegmentClearance(f.ShoulderPos, elbowPos, f.UpperArmR, i.HipsPos, i.SpinePos, f.HipsR, f.SpineR, f.BodyLat, f.BodyFwd));
            }
            if (i.HasSpine)
            {
                worst = Mathf.Min(worst, SegmentClearance(f.ShoulderPos, elbowPos, f.UpperArmR, i.SpinePos, i.ChestPos, f.SpineR, f.ChestR, f.BodyLat, f.BodyFwd));
            }
            worst = Mathf.Min(worst, SegmentClearance(f.ShoulderPos, elbowPos, f.UpperArmR, i.ChestPos, i.NeckPos, f.ChestR, f.ChestR, f.BodyLat, f.BodyFwd));
            return worst;
        }
        static float SegmentClearance(Vector3 p1, Vector3 q1, float r1, Vector3 p2, Vector3 q2, float latR0, float latR1, Vector3 bodyLat, Vector3 bodyFwd)
        {
            BasisEerieMovement.SegmentSegmentClosestPoints(p1, q1, p2, q2, out _, out float segT, out Vector3 c1, out Vector3 c2);
            Vector3 sep = c1 - c2;
            float sepLen = sep.magnitude, latR = latR0 + (latR1 - latR0) * segT, rEff = latR;
            float apR = latR * chestDepthRatio;
            Vector3 axis = q2 - p2;
            float axisSqr = axis.sqrMagnitude;
            if (apR > epsilon && sepLen > epsilon && axisSqr > epsilon * epsilon && bodyLat.sqrMagnitude > epsilon * epsilon && bodyFwd.sqrMagnitude > epsilon * epsilon)
            {
                Vector3 axisN = axis / Mathf.Sqrt(axisSqr), sepPerp = sep - axisN * Vector3.Dot(sep, axisN);
                float sepPerpLen = sepPerp.magnitude;
                if (sepPerpLen > epsilon)
                {
                    Vector3 sepDir = sepPerp / sepPerpLen;
                    float cu = Vector3.Dot(sepDir, bodyLat), cw = Vector3.Dot(sepDir, bodyFwd);
                    float denom = (cu * cu) / (latR * latR) + (cw * cw) / (apR * apR);
                    if (denom > epsilon)
                    {
                        rEff = 1f / Mathf.Sqrt(denom);
                    }
                }
            }
            return sepLen - (r1 + rEff);
        }
    }
}
