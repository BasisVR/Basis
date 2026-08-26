using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    public static class BasisArmSolveCore
    {
        const float epsilon = 1e-5f, sqrEpsilon = 1e-8f;
        public const float MinElbowAngleDeg = 23f, MaxElbowAngleDeg = 170f, WristRollComfortDeg = 80f;
        public const float WristRollRampStartDeg = 55f, WristRollMaxReliefDeg = 70f, TrackerRollHandBlend = 0.5f;
        public const float TrackerForearmRollMaxDeg = 120f, WristKeepFrac = 0.15f, WristKeepMaxDeg = 15f;
        public const float TrackerPoleAnchorFrac = 0.05f, TrackerPoleTrustFrac = 0.12f;
        const float wristWrapFadeStartDeg = 155f, wristWrapFadeEndDeg = 178f;
        /// <summary>Convenience overload for callers with nothing to draw into.</summary>
        public static void Solve(in BasisArmSolveInput i, out BasisArmSolveResult r)
        {
            BasisIKGizmoRecorder gizmos = default;
            Solve(i, out r, ref gizmos);
        }
        public static void Solve(in BasisArmSolveInput i, out BasisArmSolveResult r, ref BasisIKGizmoRecorder gizmos)
        {
            r = default;

            r.MidPostRoll = Quaternion.identity;
            Quaternion hintRotation = i.HasHintRotation ? i.HintRotation : default;

            Vector3 aPosition = i.Shoulder, bPosition = i.Elbow, cPosition = i.Hand;
            Quaternion rootRot = i.RootRotation, midRot = i.MidRotation;
            Vector3 tPosition = i.TargetPosition;
            Quaternion tRotation = i.TargetRotation * i.TargetOffset;
            Vector3 ab = bPosition - aPosition, bc = cPosition - bPosition, ac = cPosition - aPosition;
            float abLen = ab.magnitude, bcLen = bc.magnitude, totalLen = abLen + bcLen;
            Vector3 atCorrected = tPosition - aPosition;
            float acLen = ac.magnitude, oldAbcAngle = BasisIKMath.TriangleAngle(acLen, abLen, bcLen);
            float atCorrectedLen = atCorrected.magnitude;
            float newAbcAngle = BasisIKMath.TriangleAngle(atCorrectedLen, abLen, bcLen);

            newAbcAngle = Mathf.Clamp(newAbcAngle, MinElbowAngleDeg * Mathf.Deg2Rad, MaxElbowAngleDeg * Mathf.Deg2Rad);

            byte axisSource = 0;
            Vector3 axis = Vector3.Cross(ab, bc);
            if (axis.sqrMagnitude < sqrEpsilon)
            {
                Vector3 straightArm = ac.sqrMagnitude > sqrEpsilon ? ac : bc;
                if (straightArm.sqrMagnitude > sqrEpsilon)
                {
                    Vector3 saN = straightArm.normalized, downPole = -i.PlayerUp - saN * Vector3.Dot(-i.PlayerUp, saN);
                    axis = Vector3.Cross(downPole, bc);
                    axisSource = 4;
                }

                if (axis.sqrMagnitude < sqrEpsilon)
                {
                    axis = i.HintWeight ? Vector3.Cross(i.HintPosition - aPosition, bc) : Vector3.zero;
                    axisSource = 1;
                }
                if (axis.sqrMagnitude < sqrEpsilon)
                {
                    axis = Vector3.Cross(atCorrected, bc);
                    axisSource = 2;
                }

                if (axis.sqrMagnitude < sqrEpsilon)
                {
                    axis = i.PlayerUp;
                    axisSource = 3;
                }
            }
            axis = axis.normalized;

            float a = 0.5f * (oldAbcAngle - newAbcAngle), sin = Mathf.Sin(a), cos = Mathf.Cos(a);
            Quaternion deltaR = new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, cos);

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
            float hintFade = 0f, swivelUsedRad = 0f, hintProjMag = 0f, armProjMag = 0f, poleCondW = 1f;
            if (i.HintWeight)
            {
                float acSqrMag = ac.sqrMagnitude;
                if (acSqrMag > 0f)
                {
                    ab = bPosition - aPosition;
                    ac = cPosition - aPosition;

                    Vector3 acNorm = ac / Mathf.Sqrt(acSqrMag), ah = i.HintPosition - aPosition;
                    Vector3 abProj = ab - acNorm * Vector3.Dot(ab, acNorm);
                    Vector3 ahProj = ah - acNorm * Vector3.Dot(ah, acNorm), elbowDir = abProj;
                    hintProjMag = ahProj.magnitude;
                    armProjMag = abProj.magnitude;

                    hintFade = 1f;

                    Vector3 anchorCarriedRaw = Vector3.zero, anchorCarried = Vector3.zero;
                    bool hasAnchorCarried = false, poleMeasurable = ahProj.sqrMagnitude > sqrEpsilon;
                    if (i.HintIsTracker && totalLen > epsilon)
                    {
                        poleCondW = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01( (hintProjMag / totalLen - TrackerPoleAnchorFrac) / (TrackerPoleTrustFrac - TrackerPoleAnchorFrac)));

                        if (i.HasPrevPole)
                        {
                            Quaternion carryRot = Quaternion.identity;
                            if (IsValidRotation(i.PrevHintRotation) && IsValidRotation(hintRotation))
                            {
                                carryRot = hintRotation * Quaternion.Inverse(i.PrevHintRotation);
                            }

                            anchorCarriedRaw = carryRot * i.PrevPoleDir;
                            anchorCarried = anchorCarriedRaw - acNorm * Vector3.Dot(anchorCarriedRaw, acNorm);
                            hasAnchorCarried = anchorCarried.sqrMagnitude > sqrEpsilon;
                        }

                        if (poleMeasurable && (poleCondW >= 1f || !i.HasPrevPole))
                        {
                            r.PoleDirUsed = ahProj / hintProjMag;
                            r.PoleRotUsed = hintRotation;
                            r.PoleAnchorValid = true;
                        }
                        else if (i.HasPrevPole)
                        {
                            r.PoleDirUsed = i.PrevPoleDir;
                            r.PoleRotUsed = i.PrevHintRotation;
                            r.PoleAnchorValid = true;
                            if (poleMeasurable && poleCondW > 0f && hasAnchorCarried)
                            {
                                float ease = BasisIKMath.SignedAngleRad(anchorCarried, ahProj, acNorm) * poleCondW;
                                r.PoleDirUsed = (BasisIKMath.AngleAxisRad(ease, acNorm) * anchorCarriedRaw).normalized;
                                r.PoleRotUsed = hintRotation;
                            }
                        }
                    }

                    if (poleMeasurable && elbowDir.sqrMagnitude > sqrEpsilon)
                    {
                        float poleSwivel = BasisIKMath.SignedAngleRad(elbowDir, ahProj, acNorm);
                        if (i.HintIsTracker && i.HasPrevPole && poleCondW < 1f && hasAnchorCarried)
                        {
                            float anchorSwivel = BasisIKMath.SignedAngleRad(elbowDir, anchorCarried, acNorm);
                            float dSwivel = Mathf.DeltaAngle(anchorSwivel * Mathf.Rad2Deg, poleSwivel * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                            poleSwivel = anchorSwivel + poleCondW * dSwivel;
                        }

                        swivelUsedRad = poleSwivel * hintFade;
                        float swivel = swivelUsedRad, maxStep = i.HintMaxStepDeg * Mathf.Deg2Rad;
                        if (swivel > maxStep) swivel = maxStep;
                        else if (swivel < -maxStep) swivel = -maxStep;
                        swivelUsedRad = swivel;

                        hintR = BasisIKMath.AngleAxisRad(swivel, acNorm);

                        rootRot = hintR * rootRot;
                        bPosition = aPosition + hintR * (bPosition - aPosition);
                        cPosition = aPosition + hintR * (cPosition - aPosition);
                        midRot = hintR * midRot;
                        hintApplied = true;
                    }
                }
            }
            r.PoleConditioning = poleCondW;

            if (i.HintWeight && !i.HintIsTracker)
            {
                float poleCond = totalLen > epsilon ? hintProjMag / totalLen : 1f;
                float collapse = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((poleCond - 0.15f) / 0.15f));
                Vector3 acStab = cPosition - aPosition;
                if (collapse > 0f && acStab.sqrMagnitude > sqrEpsilon)
                {
                    Vector3 acStabN = acStab.normalized;
                    Vector3 downPole = -i.PlayerUp - acStabN * Vector3.Dot(-i.PlayerUp, acStabN);
                    Vector3 elbowPole = (bPosition - aPosition) - acStabN * Vector3.Dot(bPosition - aPosition, acStabN);
                    if (downPole.sqrMagnitude > sqrEpsilon && elbowPole.sqrMagnitude > sqrEpsilon)
                    {
                        float stabSwivel = BasisIKMath.SignedAngleRad(elbowPole, downPole, acStabN) * collapse;
                        float budget = i.HintMaxStepDeg * Mathf.Deg2Rad - Mathf.Abs(swivelUsedRad);
                        if (!(budget > 0f)) budget = 0f;
                        if (stabSwivel > budget) stabSwivel = budget;
                        else if (stabSwivel < -budget) stabSwivel = -budget;

                        Quaternion stab = BasisIKMath.AngleAxisRad(stabSwivel, acStabN);
                        rootRot = stab * rootRot;
                        bPosition = aPosition + stab * (bPosition - aPosition);
                        cPosition = aPosition + stab * (cPosition - aPosition);
                        midRot = stab * midRot;
                        hintR = stab * hintR;
                    }
                }
            }

            float tipRotSqr = i.TipRotation.x * i.TipRotation.x + i.TipRotation.y * i.TipRotation.y + i.TipRotation.z * i.TipRotation.z + i.TipRotation.w * i.TipRotation.w;
            if (tipRotSqr > 0.5f && !i.HintIsTracker)
            {
                Vector3 fore = cPosition - bPosition, acRelief = cPosition - aPosition;
                if (fore.sqrMagnitude > sqrEpsilon && acRelief.sqrMagnitude > sqrEpsilon)
                {
                    Quaternion neutral = midRot * Quaternion.Inverse(i.MidRotation) * i.TipRotation;
                    float twistRad = BasisIKMath.TwistAngleRad(tRotation * Quaternion.Inverse(neutral), fore.normalized);
                    r.WristTwistDeg = twistRad * Mathf.Rad2Deg;

                    float rollAbs = Mathf.Abs(twistRad), rampStart = WristRollRampStartDeg * Mathf.Deg2Rad;
                    float band = WristRollComfortDeg * Mathf.Deg2Rad, relief;
                    if (rollAbs <= rampStart)
                    {
                        relief = 0f;
                    }
                    else if (rollAbs <= band)
                    {
                        float t = rollAbs - rampStart;
                        relief = t * t / (2f * (band - rampStart));
                    }
                    else
                    {
                        relief = 0.5f * (band - rampStart) + (rollAbs - band);
                    }

                    float reliefCap = WristRollMaxReliefDeg * Mathf.Deg2Rad;
                    if (relief > reliefCap) relief = reliefCap;
                    relief *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01( (rollAbs * Mathf.Rad2Deg - wristWrapFadeStartDeg) / (wristWrapFadeEndDeg - wristWrapFadeStartDeg)));

                    if (relief > 0f)
                    {
                        float reliefSigned = twistRad < 0f ? -relief : relief;
                        Quaternion reliefR = BasisIKMath.AngleAxisRad(reliefSigned, acRelief.normalized);

                        rootRot = reliefR * rootRot;
                        bPosition = aPosition + reliefR * (bPosition - aPosition);
                        cPosition = aPosition + reliefR * (cPosition - aPosition);
                        midRot = reliefR * midRot;
                        hintR = reliefR * hintR;
                        r.WristReliefDeg = reliefSigned * Mathf.Rad2Deg;
                    }
                }
            }

            Vector3 guardUp = i.TorsoUp.sqrMagnitude > sqrEpsilon ? i.TorsoUp : i.PlayerUp;
            float guardSwivel = BasisElbowAnatomyCore.GuardSwivelRad(aPosition, bPosition, cPosition, guardUp, totalLen, i.ElbowLateralOut, i.PrevGuardSide, out int guardSideUsed);
            r.GuardSideUsed = guardSideUsed;
            if (guardSwivel != 0f)
            {
                Vector3 acGuard = cPosition - aPosition;
                if (acGuard.sqrMagnitude > sqrEpsilon)
                {
                    Vector3 acGuardN = acGuard.normalized;
                    Quaternion guard = BasisIKMath.AngleAxisRad(guardSwivel, acGuardN);

                    rootRot = guard * rootRot;
                    bPosition = aPosition + guard * (bPosition - aPosition);
                    cPosition = aPosition + guard * (cPosition - aPosition);
                    midRot = guard * midRot;
                    hintR = guard * hintR;
                }
            }

            float hintRotSqr = hintRotation.x * hintRotation.x + hintRotation.y * hintRotation.y + hintRotation.z * hintRotation.z + hintRotation.w * hintRotation.w;
            {
                Vector3 foreRoll = cPosition - bPosition;
                if (foreRoll.sqrMagnitude > sqrEpsilon)
                {
                    Vector3 foreRollN = foreRoll.normalized;
                    float handDemand = 0f;
                    bool handDemandValid = tipRotSqr > 0.5f;
                    if (handDemandValid)
                    {
                        Quaternion neutral = midRot * Quaternion.Inverse(i.MidRotation) * i.TipRotation;
                        handDemand = BasisIKMath.TwistAngleRad(tRotation * Quaternion.Inverse(neutral), foreRollN);
                    }

                    float roll = 0f;
                    bool rollLive = false;
                    if (i.HintIsTracker && hintRotSqr > 0.5f)
                    {
                        float trackerRoll = BasisIKMath.TwistAngleRad(hintRotation * Quaternion.Inverse(midRot), foreRollN);

                        roll = trackerRoll;
                        rollLive = true;
                        if (handDemandValid)
                        {
                            r.WristTwistDeg = handDemand * Mathf.Rad2Deg;

                            float d = handDemand - trackerRoll;
                            if (d > Mathf.PI) d -= 2f * Mathf.PI;
                            else if (d < -Mathf.PI) d += 2f * Mathf.PI;
                            float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01( (Mathf.Abs(d) * Mathf.Rad2Deg - wristWrapFadeStartDeg) / (wristWrapFadeEndDeg - wristWrapFadeStartDeg)));
                            roll = trackerRoll + TrackerRollHandBlend * d * fade;
                        }
                    }

                    if (handDemandValid && i.ForearmFollowWeight > 0f)
                    {
                        float resid = handDemand - roll;
                        if (resid > Mathf.PI) resid -= 2f * Mathf.PI;
                        else if (resid < -Mathf.PI) resid += 2f * Mathf.PI;

                        float residAbs = Mathf.Abs(resid);
                        float keep = Mathf.Min(WristKeepFrac * residAbs, WristKeepMaxDeg * Mathf.Deg2Rad);
                        float seamBasisDeg = Mathf.Abs(r.WristTwistDeg), residAbsDeg = residAbs * Mathf.Rad2Deg;
                        if (residAbsDeg > seamBasisDeg) seamBasisDeg = residAbsDeg;
                        float seam = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01( (seamBasisDeg - wristWrapFadeStartDeg) / (wristWrapFadeEndDeg - wristWrapFadeStartDeg)));
                        float w = i.ForearmFollowWeight < 1f ? i.ForearmFollowWeight : 1f;
                        float topUp = (residAbs - keep) * seam * w;
                        roll += resid < 0f ? -topUp : topUp;
                        rollLive = true;
                    }

                    if (rollLive)
                    {
                        float rollAbs = Mathf.Abs(roll), rollCap = TrackerForearmRollMaxDeg * Mathf.Deg2Rad;
                        if (rollAbs > rollCap) rollAbs = rollCap;
                        if (rollAbs > 1e-6f)
                        {
                            float rollSigned = roll < 0f ? -rollAbs : rollAbs;
                            r.MidPostRoll = BasisIKMath.AngleAxisRad(rollSigned, foreRollN);
                            midRot = r.MidPostRoll * midRot;
                            r.ForearmRollDeg = rollSigned * Mathf.Rad2Deg;
                        }
                    }

                    if (handDemandValid)
                    {
                        float residOut = handDemand - r.ForearmRollDeg * Mathf.Deg2Rad;
                        if (residOut > Mathf.PI) residOut -= 2f * Mathf.PI;
                        else if (residOut < -Mathf.PI) residOut += 2f * Mathf.PI;
                        r.WristResidualDeg = residOut * Mathf.Rad2Deg;
                    }
                }
            }

            // cache old rotations in our temporary Z forward Y up space
            var cachedRootRotation = Quaternion.LookRotation(i.Elbow - i.Shoulder, i.Hand - i.Shoulder);
            cachedRootRotation *= Quaternion.Euler(0f, 0f, 90f);
            var cachedMidRotation = Quaternion.LookRotation(i.Hand - i.Elbow, i.Shoulder - i.Hand);
            cachedMidRotation *= Quaternion.Euler(0f, 0f, 90f);

            // Solve triangle in temporary Z forward Y up space
            var rootRotation = Quaternion.LookRotation(i.TargetPosition - i.Shoulder, i.HintPosition - i.Shoulder);
            rootRotation *= Quaternion.Euler(-a * Mathf.Rad2Deg, 0f, 0f);
            var projectedElbow = i.Shoulder + rootRotation * Vector3.forward * ab.magnitude;
            var midRotation = Quaternion.LookRotation(i.TargetPosition - projectedElbow, rootRotation * Vector3.left);

            // Get custom space deltas
            rootDelta = rootRotation * Quaternion.Inverse(cachedRootRotation);
            var midDelta = midRotation * Quaternion.Inverse(cachedMidRotation);

            gizmos.Segment(i.Shoulder, i.Shoulder + cachedRootRotation * Vector3.forward, Color.blue);
            gizmos.Segment(i.Shoulder, i.Shoulder + cachedRootRotation * Vector3.up, Color.green);
            gizmos.Segment(i.Shoulder, i.Shoulder + rootRotation * Vector3.forward, Color.blue);
            gizmos.Segment(i.Shoulder, i.Shoulder + rootRotation * Vector3.up, Color.green);

            // Apply deltas to original bone rotations
            r.RootRotation = rootDelta * i.RootRotation;
            r.MidRotation = midDelta * i.MidRotation;

            r.MidDelta = deltaR;
            r.RootDelta = rootDelta;
            r.HintDelta = hintR;
            r.TipRotation = tRotation;
            r.HintApplied = hintApplied;

            r.ElbowSolved = bPosition;
            r.HandSolved = cPosition;
            r.RootRotationSolved = rootRot;
            r.MidRotationSolved = midRot;

            r.UpperLength = abLen;
            r.LowerLength = bcLen;
            r.TargetDistance = atCorrectedLen;
            r.ReachRatio = (totalLen > epsilon) ? atCorrectedLen / totalLen : 0f;
            r.ElbowAngleDeg = BasisIKMath.AngleDeg(aPosition - bPosition, cPosition - bPosition);
            r.HintFade = hintFade;
            r.HintProjMag = hintProjMag;
            r.ArmProjMag = armProjMag;
            r.AxisSource = axisSource;
            r.HandError = (cPosition - tPosition).magnitude;
        }
        static bool IsValidRotation(Quaternion q) => (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w) > 0.5f;
    }
    public static class BasisArmBendLookup
    {
        public const int GridSize = 11, GridSizeSq = GridSize * GridSize, TotalEntries = GridSize * GridSize * GridSize;
        public static Vector3[] GenerateDefaultTable()
        {
            var table = new Vector3[TotalEntries];
            float step = 2f / (GridSize - 1);

            for (int iz = 0; iz < GridSize; iz++)
            for (int iy = 0; iy < GridSize; iy++)
            for (int ix = 0; ix < GridSize; ix++)
            {
                float x = -1f + ix * step, y = -1f + iy * step, z = -1f + iz * step;
                Vector3 bendDir;
                float forwardness = Mathf.Clamp01(z), upness = Mathf.Clamp01(y);

                bendDir = new Vector3(0f, -0.3f, -1f);

                bendDir = Vector3.Lerp(bendDir, new Vector3(0f, -1f, -0.3f), forwardness * 0.6f);

                bendDir = Vector3.Lerp(bendDir, new Vector3(0.7f, -0.8f, -0.2f), upness * 0.5f);

                float inwardness = Mathf.Clamp01(-x);
                bendDir = Vector3.Lerp(bendDir, new Vector3(1f, -0.5f, 0f), inwardness * 0.4f);

                float behindness = Mathf.Clamp01(-z);
                bendDir = Vector3.Lerp(bendDir, new Vector3(0.4f, -0.75f, -0.55f), behindness * 0.7f);

                float downness = Mathf.Clamp01(-y);
                bendDir = Vector3.Lerp(bendDir, new Vector3(0f, 0f, -1f), downness * 0.3f);

                int idx = ix + iy * GridSize + iz * GridSizeSq;
                table[idx] = bendDir.normalized;
            }

            return table;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float ClampToGrid(float v) => v > 0f ? (v < GridSize - 1.001f ? v : GridSize - 1.001f) : 0f;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 SampleTrilinear(NativeArray<Vector3> table, Vector3 normalizedPos)
        {
            float fx = ClampToGrid((normalizedPos.x * 0.5f + 0.5f) * (GridSize - 1));
            float fy = ClampToGrid((normalizedPos.y * 0.5f + 0.5f) * (GridSize - 1));
            float fz = ClampToGrid((normalizedPos.z * 0.5f + 0.5f) * (GridSize - 1));

            int x0 = (int)fx; int x1 = Mathf.Min(x0 + 1, GridSize - 1);
            int y0 = (int)fy; int y1 = Mathf.Min(y0 + 1, GridSize - 1);
            int z0 = (int)fz; int z1 = Mathf.Min(z0 + 1, GridSize - 1);

            float tx = fx - x0, ty = fy - y0, tz = fz - z0;
            Vector3 c000 = table[x0 + y0 * GridSize + z0 * GridSizeSq];
            Vector3 c100 = table[x1 + y0 * GridSize + z0 * GridSizeSq];
            Vector3 c010 = table[x0 + y1 * GridSize + z0 * GridSizeSq];
            Vector3 c110 = table[x1 + y1 * GridSize + z0 * GridSizeSq];
            Vector3 c001 = table[x0 + y0 * GridSize + z1 * GridSizeSq];
            Vector3 c101 = table[x1 + y0 * GridSize + z1 * GridSizeSq];
            Vector3 c011 = table[x0 + y1 * GridSize + z1 * GridSizeSq];
            Vector3 c111 = table[x1 + y1 * GridSize + z1 * GridSizeSq], c00 = Vector3.Lerp(c000, c100, tx);
            Vector3 c10 = Vector3.Lerp(c010, c110, tx), c01 = Vector3.Lerp(c001, c101, tx);
            Vector3 c11 = Vector3.Lerp(c011, c111, tx), c0 = Vector3.Lerp(c00, c10, ty);
            Vector3 c1 = Vector3.Lerp(c01, c11, ty);

            return Vector3.Lerp(c0, c1, tz).normalized;
        }
    }
}
