using System.Runtime.CompilerServices;
using Basis.Scripts.Common;
using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    public partial struct BasisEerieMovement
    {
        void SolveSpinePass()
        {
            SolveSpine();
            if (plan.lordosis)
            {
                BasisEerieMarkers.SpineLordosis.Begin();
                ApplyCervicalLordosis();
                BasisEerieMarkers.SpineLordosis.End();
            }
        }
        public void SolveSpine()
        {
            BasisEerieMarkers.SpineHipsPlacement.Begin();

            Quaternion chestDesired = targetRotationChest * offsetRotationChest;

            if (plan.prone)
            {
                if (plan.hasHips && plan.hasHead)
                {
                    ApplyProneBodyYaw();
                }
            }
            else
            {
                ResetSpineChainToRest();
                Vector3 headTargetPos = targetPositionHead, hipsTargetPos = targetPositionHips;
                Quaternion headTargetRot = targetRotationHead, hipsTargetRot = targetRotationHips;
                Quaternion offsetHips = offsetRotationHips, hipDesired = hipsTargetRot * offsetHips;
                float restDist = minHeadSpineHeight;
                BasisIKLockMode lockMode = ikLockMode;
                Vector3 up = playerUp;

                switch (lockMode)
                {
                    case BasisIKLockMode.LockHips: break;

                    case BasisIKLockMode.LockHead:
                        {
                            Vector3 headToHips = hipsTargetPos - headTargetPos;
                            float spineLen = headToHips.magnitude;
                            if (spineLen < restDist)
                            {
                                Vector3 spineDir = spineLen > epsilon ? headToHips / spineLen : hipsTargetRot * Vector3.down;
                                hipsTargetPos = headTargetPos + spineDir * restDist;
                            }

                            if (!plan.hipsTracked)
                            {
                                hipsTargetPos = ClampHipsUnderHead(headTargetPos, hipsTargetPos, restDist * HipsUnderHeadMaxLeanFrac, up);
                            }
                        }
                        break;

                    default: hipsTargetPos = AntiContortionist(headTargetPos, headTargetRot, hipsTargetPos, hipsTargetRot, restDist);
                        hipsTargetPos = MitigateSpineBuckling(headTargetPos, hipsTargetRot, hipsTargetPos, restDist, up);
                        float MaxBendDeg = maxBendDeg;
                        hipsTargetPos = EnforceSpineBendLimit(headTargetPos, hipsTargetPos, MaxBendDeg, up);
                        hipsTargetPos = ClampHipsAroundHead(headTargetPos, hipsTargetPos, restDist, minFactor, maxFactor, up);
                        break;
                }
                Vector3 neckCue = ComputeNeckCue(headTargetPos);
                float crouchFade = 1f;
                if (!plan.hipsTracked)
                {
                    BasisTrunkCounterbalanceCore.Solve(hipsTargetPos, neckCue, up, trunkCounterbalance, trunkCounterbalanceMaxSpineFrac * minHeadSpineHeight, out hipsTargetPos, out float flexionFrac, out _);
                    crouchFade = 1f - flexionFrac;
                }
                if (plan.crouchOffset)
                {
                    hipsTargetPos = ApplyCrouchBodyOffset(headTargetPos, hipsTargetPos, hipDesired, up, crouchFade);
                }
                targetPositionHips = hipsTargetPos;
                if (!plan.hipsTracked)
                {
                    BasisHipHingeCore.Solve(neckCue, hipsTargetPos, hipDesired, up, hipHingeStartDeg, hipHingeMaxAddDeg, out hipDesired, out _, out _);
                }

                if (plan.hasHips)
                {
                    poseStream.SetPosition(handleHips, hipsTargetPos);
                    poseStream.SetRotation(handleHips, hipDesired);
                }
            }
            BasisEerieMarkers.SpineHipsPlacement.End();
            if (plan.chestChain)
            {
                BasisEerieMarkers.SpineChainPrep.Begin();

                float Value = maxChestDeltaDeg;
                Quaternion clampedChestRot = chestDesired;
                if (plan.hasNeck)
                {
                    clampedChestRot = ClampRotation(clampedChestRot, poseStream.GetRotation(handleNeck), Value);
                }
                if (plan.hasSpine)
                {
                    clampedChestRot = ClampRotation(clampedChestRot, poseStream.GetRotation(handleSpine), Value);
                }

                poseStream.SetRotation(handleChest, clampedChestRot);

                Vector3 headPos = targetPositionHead;
                Quaternion headRot = targetRotationHead;

                DistributeSpineBend(headPos);
                BiasSpineTowardChest();
                GuardSpineChain();
                BasisEerieMarkers.SpineChainPrep.End();
                BasisEerieMarkers.SpineSequentialIK.Begin();
                SolveSequentialSpineIK(headPos, headRot);
                BasisEerieMarkers.SpineSequentialIK.End();
            }
            else if (plan.headChain)
            {
                Vector3 headPos = targetPositionHead;
                Quaternion headRot = targetRotationHead;

                BasisEerieMarkers.SpineChainPrep.Begin();
                DistributeSpineBend(headPos);
                if (plan.armSwingChestFollow) ApplyArmSwingChestFollow();
                GuardSpineChain();
                BasisEerieMarkers.SpineChainPrep.End();
                BasisEerieMarkers.SpineSequentialIK.Begin();
                SolveSequentialSpineIK(headPos, headRot);
                BasisEerieMarkers.SpineSequentialIK.End();
            }
        }
        void ResetSpineChainToRest()
        {
            if (!plan.hasSpineChain)
            {
                return;
            }
            for (int i = chainHeadToSpine.Length - 1; i >= 0; i--)
            {
                poseStream.ResetToRest(chainHeadToSpine[i]);
            }
        }
        void ApplyProneBodyYaw()
        {
            Vector3 up = playerUp, hipsPos = poseStream.GetPosition(handleHips);
            Vector3 headPos = poseStream.GetPosition(handleHead), bodyFwd = headPos - hipsPos;
            bodyFwd -= up * Vector3.Dot(bodyFwd, up);
            Vector3 desiredFwd = targetRotationHips * Vector3.forward;
            desiredFwd -= up * Vector3.Dot(desiredFwd, up);
            if (bodyFwd.sqrMagnitude < sqrEpsilon || desiredFwd.sqrMagnitude < sqrEpsilon)
            {
                return;
            }

            float deltaYaw = Vector3.SignedAngle(bodyFwd, desiredFwd, up);
            Quaternion swing = Quaternion.AngleAxis(deltaYaw, up);
            Vector3 toTarget = targetPositionHead - headPos;
            toTarget -= up * Vector3.Dot(toTarget, up);
            poseStream.SetPosition(handleHips, headPos + swing * (hipsPos - headPos) + toTarget);
            poseStream.SetRotation(handleHips, swing * poseStream.GetRotation(handleHips));
        }
        public void SolveSequentialSpineIK(Vector3 headTargetPos, Quaternion headTargetRot)
        {
            if (!plan.hasSpineChain)
                return;

            int chainLen = chainHeadToSpine.Length;
            const int tipIdx = 0, firstJoint = 1;
            int lastJoint = chainLen - 2;

            int maxIters = Mathf.Max(1, spineMaxIterations);
            float tolerance = Mathf.Max(0f, spineTolerance), tolSqr = tolerance * tolerance;
            {
                Vector3 rootPos = poseStream.GetPosition(chainHeadToSpine[chainLen - 1]);
                float chainReach = 0f;
                for (int i = 0; i < chainLen - 1; i++)
                {
                    chainReach += (poseStream.GetPosition(chainHeadToSpine[i]) - poseStream.GetPosition(chainHeadToSpine[i + 1])).magnitude;
                }
                Vector3 rootToTarget = headTargetPos - rootPos;
                float targetDist = rootToTarget.magnitude;
                if (targetDist > epsilon && chainReach > epsilon)
                {
                    float compression = chainReach - targetDist, commandedDist;
                    if (compression > 0f)
                    {
                        float band = spineTautBandFrac * chainReach, denom = compression * compression + band * band;
                        commandedDist = denom > 0f ? chainReach - compression * compression * compression / denom : targetDist;
                    }
                    else
                    {
                        commandedDist = chainReach;
                    }
                    headTargetPos = rootPos + rootToTarget * (commandedDist / targetDist);
                }
            }

            Quaternion hipsTwistRot = (plan.hasHips ? poseStream.GetRotation(handleHips) : Quaternion.identity) * Quaternion.Inverse(offsetRotationHips);
            Vector3 ccdUp = hipsTwistRot * Vector3.up;
            if (ccdUp.sqrMagnitude < sqrEpsilon) ccdUp = playerUp;
            float jointSpan = Mathf.Max(1, lastJoint - firstJoint);
            int chestIdx = plan.chestIdx;
            Quaternion finalHeadRot = headTargetRot * offsetRotationHead;

            for (int iter = 0; iter < maxIters; iter++)
            {
                Vector3 tipPos = poseStream.GetPosition(chainHeadToSpine[tipIdx]);
                if ((headTargetPos - tipPos).sqrMagnitude < tolSqr)
                    break;

                for (int i = lastJoint; i >= firstJoint; i--)
                {
                    ReachHeadJoint(i, headTargetPos, firstJoint, chestIdx, jointSpan, ccdUp);
                }
            }

            SolveChestTarget(headTargetPos, firstJoint, lastJoint, chestIdx, jointSpan, ccdUp, tolSqr);

            poseStream.SetRotation(chainHeadToSpine[tipIdx], finalHeadRot);
        }
        void ReachHeadJoint(int i, Vector3 headTargetPos, int firstJoint, int chestIdx, float jointSpan, Vector3 ccdUp)
        {
            const int tipIdx = 0;
            Vector3 jointPos = poseStream.GetPosition(chainHeadToSpine[i]);
            Vector3 curTipPos = poseStream.GetPosition(chainHeadToSpine[tipIdx]), cur = curTipPos - jointPos;
            Vector3 tgt = headTargetPos - jointPos;
            if (cur.sqrMagnitude < sqrEpsilon || tgt.sqrMagnitude < sqrEpsilon)
                return;

            Quaternion delta = BasisQuaternionExt.FromToRotation(cur, tgt);
            float t = (i - firstJoint) / jointSpan, jointTwistKeep = Mathf.Lerp(spineNeckTwistKeep, spineTwistKeep, t);
            float jointSwingScale = 1f - thoracicBendStiffen * (1f - Mathf.Abs(2f * t - 1f));
            delta = BasisTwistSolveCore.ShapeReachStep(delta, ccdUp, jointTwistKeep, jointSwingScale);
            delta = Quaternion.Slerp(Quaternion.identity, delta, spineCCDRelax);
            poseStream.SetRotation(chainHeadToSpine[i], delta * poseStream.GetRotation(chainHeadToSpine[i]));

            if (i == firstJoint)
            {
                ClampNeckCone(i, neckMaxConeDeg);
            }
            else if (i == chestIdx)
            {
                ClampChestCone(i, maxChestDeltaDeg);
            }

            GuardSpineJoint(i);
        }
        void SolveChestTarget(Vector3 headTargetPos, int firstJoint, int lastJoint, int chestBoneIdx, float jointSpan, Vector3 ccdUp, float tolSqr)
        {
            if (!plan.chestTarget)
                return;

            Vector3 chestTargetPos = targetPositionChestRaw;
            Vector3 chestBonePos = poseStream.GetPosition(chainHeadToSpine[chestBoneIdx]);

            if ((chestTargetPos - chestBonePos).sqrMagnitude > (chestPullMaxDist * chestPullMaxDist))
                return;

            float spineT = (lastJoint - firstJoint) / jointSpan;
            float chestTwistKeep = Mathf.Lerp(spineNeckTwistKeep, spineTwistKeep, spineT);
            float spineSwingScale = 1f - thoracicBendStiffen * (1f - Mathf.Abs(2f * spineT - 1f));

            for (int citer = 0; citer < chestIkIterations; citer++)
            {
                Vector3 spinePos = poseStream.GetPosition(chainHeadToSpine[lastJoint]);
                Vector3 chestNow = poseStream.GetPosition(chainHeadToSpine[chestBoneIdx]);

                if ((chestTargetPos - chestNow).sqrMagnitude < tolSqr && (headTargetPos - poseStream.GetPosition(chainHeadToSpine[0])).sqrMagnitude < tolSqr)
                {
                    break;
                }

                Vector3 cCur = chestNow - spinePos, cTgt = chestTargetPos - spinePos;
                if (cCur.sqrMagnitude > sqrEpsilon && cTgt.sqrMagnitude > sqrEpsilon)
                {
                    Quaternion cDelta = BasisQuaternionExt.FromToRotation(cCur, cTgt);
                    cDelta = BasisTwistSolveCore.ShapeReachStep(cDelta, ccdUp, chestTwistKeep, spineSwingScale);

                    cDelta = Quaternion.Slerp(Quaternion.identity, cDelta, spineCCDRelax * chestIkWeight);
                    poseStream.SetRotation(chainHeadToSpine[lastJoint], cDelta * poseStream.GetRotation(chainHeadToSpine[lastJoint]));
                    GuardSpineJoint(lastJoint);
                }

                for (int sweep = 0; sweep < chestIkHeadRestoreSweeps; sweep++)
                {
                    for (int i = lastJoint - 1; i >= firstJoint; i--)
                    {
                        ReachHeadJoint(i, headTargetPos, firstJoint, chestBoneIdx, jointSpan, ccdUp);
                    }
                }
            }
        }
        void GuardSpineJoint(int i)
        {
            if (!plan.spineRom)
            {
                return;
            }

            BasisSpineRestFrame frame = chainSpineRestFrames[i];
            if (!frame.Valid)
            {
                return;
            }

            int parent = i + 1;
            Quaternion parentRot = poseStream.GetRotation(chainHeadToSpine[parent]);
            Quaternion boneRot = poseStream.GetRotation(chainHeadToSpine[i]);
            Quaternion local = BasisSpineAnatomyCore.Conj(parentRot) * boneRot;
            Quaternion clamped = BasisSpineAnatomyCore.Clamp(local, frame, BasisSpineAnatomy.Rom(frame.Segment), out BasisSpineClampInfo info);
            if (!info.Touched)
            {
                return;
            }

            poseStream.SetRotation(chainHeadToSpine[i], parentRot * clamped);
        }
        void GuardSpineChain()
        {
            if (!plan.hasSpineChain)
            {
                return;
            }
            for (int i = 1; i <= chainHeadToSpine.Length - 2; i++)
            {
                GuardSpineJoint(i);
            }
        }
        void ClampNeckCone(int neckIdx, float maxConeDeg)
        {
            Vector3 chestPos = poseStream.GetPosition(chainHeadToSpine[neckIdx + 1]);
            Vector3 neckPos = poseStream.GetPosition(chainHeadToSpine[neckIdx]);
            Vector3 headPos = poseStream.GetPosition(chainHeadToSpine[0]), parentDir = neckPos - chestPos;
            Vector3 boneDir = headPos - neckPos;
            if (parentDir.sqrMagnitude < sqrEpsilon || boneDir.sqrMagnitude < sqrEpsilon)
            {
                return;
            }

            float ang = Vector3.Angle(parentDir, boneDir);
            if (ang <= maxConeDeg)
            {
                return;
            }

            Vector3 axis = Vector3.Cross(boneDir, parentDir);
            if (axis.sqrMagnitude < sqrEpsilon)
            {
                return;
            }

            axis.Normalize();
            Quaternion correction = Quaternion.AngleAxis(ang - maxConeDeg, axis);
            poseStream.SetRotation(chainHeadToSpine[neckIdx], correction * poseStream.GetRotation(chainHeadToSpine[neckIdx]));
        }
        void ClampChestCone(int chestIdx, float maxConeDeg)
        {
            Vector3 spinePos = poseStream.GetPosition(chainHeadToSpine[chestIdx + 1]);
            Vector3 chestPos = poseStream.GetPosition(chainHeadToSpine[chestIdx]);
            Vector3 childPos = poseStream.GetPosition(chainHeadToSpine[chestIdx - 1]), parentDir = chestPos - spinePos;
            Vector3 boneDir = childPos - chestPos;
            if (parentDir.sqrMagnitude < sqrEpsilon || boneDir.sqrMagnitude < sqrEpsilon)
                return;

            float ang = Vector3.Angle(parentDir, boneDir);
            if (ang <= maxConeDeg)
                return;

            Vector3 axis = Vector3.Cross(boneDir, parentDir);
            if (axis.sqrMagnitude < sqrEpsilon)
                return;

            axis.Normalize();
            Quaternion correction = Quaternion.AngleAxis(ang - maxConeDeg, axis);
            poseStream.SetRotation(chainHeadToSpine[chestIdx], correction * poseStream.GetRotation(chainHeadToSpine[chestIdx]));
        }
        void BiasSpineTowardChest()
        {
            if (!plan.hasSpine || !plan.hasChest)
                return;

            Vector3 chestTargetPos = targetPositionChest, spinePos = poseStream.GetPosition(handleSpine);
            Vector3 chestPos = poseStream.GetPosition(handleChest);

            if ((chestTargetPos - chestPos).sqrMagnitude > (chestPullMaxDist * chestPullMaxDist))
                return;

            Vector3 cur = chestPos - spinePos, tgt = chestTargetPos - spinePos;
            if (cur.sqrMagnitude < sqrEpsilon || tgt.sqrMagnitude < sqrEpsilon)
                return;

            Quaternion pull = ClampRotation(BasisQuaternionExt.FromToRotation(cur, tgt), Quaternion.identity, chestPosPullMaxDeg);
            poseStream.SetRotation(handleSpine, pull * poseStream.GetRotation(handleSpine));
        }
        Vector3 ComputeNeckCue(Vector3 headTargetPos)
        {
            return BasisNeckCueCore.Solve(headTargetPos, targetRotationHead * offsetRotationHead, tposeHeadToNeckLocal, playerUp, neckExtensionDamp, neckFlexionDamp);
        }
        public void DistributeSpineBend(Vector3 headTargetPos)
        {
            if (!plan.hasSpineBend)
            {
                return;
            }

            bool hasSpine = plan.hasSpine, hasUpper = plan.hasUpperChest;
            Quaternion hipsRot = poseStream.GetRotation(handleHips);
            Vector3 neckCue = ComputeNeckCue(headTargetPos);
            Vector3 spineCue = Vector3.Lerp(neckCue, headTargetPos, Mathf.Clamp01(spineGazeFollow));
            Quaternion hipsBind = offsetRotationHips;
            BasisSpineBendInput input;
            input.HipsRot = hipsRot;
            input.HipsPos = poseStream.GetPosition(handleHips);
            input.ChestPos = poseStream.GetPosition(handleChest);
            input.SmoothedHead = ApplyChestSpring(spineCue);
            input.HipsBind = hipsBind;
            input.HeadTargetRot = targetRotationHead;
            input.SpineMaxForwardDeg = spineMaxForwardDeg;
            input.SpineMaxBackwardDeg = spineMaxBackwardDeg;
            input.SpineMaxLateralDeg = spineMaxLateralDeg;
            input.SpineBendPitch = spineBendPitch;
            input.SpineBendYaw = spineBendYaw;
            input.SpineBendRoll = spineBendRoll;
            input.UpperBendPitch = upperChestBendPitch;
            input.UpperBendYaw = upperChestBendYaw;
            input.UpperBendRoll = upperChestBendRoll;
            input.AnatDifferentialStiffness = anatDifferentialStiffness;
            input.AnatPelvicTwistRouting = anatPelvicTwistRouting;
            input.SquishBoost = spineSquishBoost;
            input.RestLen = tposeLengthNeckToHips.magnitude;
            input.BendTwistCoupling = bendTwistCoupling;
            input.HasSpine = hasSpine;
            input.HasUpper = hasUpper;

            if (plan.chestTracked)
            {
                input.SpineBendPitch = 0f;
                input.SpineBendRoll = 0f;
                input.UpperBendPitch = 0f;
                input.UpperBendRoll = 0f;
            }

            BasisSpineBendCore.Solve(input, out BasisSpineBendResult r, ref gizmos);
            if (r.EarlyOut)
            {
                return;
            }

            Quaternion hipsAnat = hipsRot * Quaternion.Inverse(hipsBind), invHipsAnat = Quaternion.Inverse(hipsAnat);
            if (r.WriteSpine)
            {
                Quaternion deltaWorld = hipsAnat * BasisSpineBendCore.Compose(r.SpineEuler) * invHipsAnat;
                poseStream.SetRotation(handleSpine, deltaWorld * poseStream.GetRotation(handleSpine));
            }
            if (r.WriteUpper)
            {
                Quaternion deltaWorld = hipsAnat * BasisSpineBendCore.Compose(r.UpperEuler) * invHipsAnat;
                poseStream.SetRotation(handleUpperChest, deltaWorld * poseStream.GetRotation(handleUpperChest));
            }
        }
        Vector3 ApplyChestSpring(Vector3 headTargetPos)
        {
            if (!plan.hasChestSpring)
            {
                return headTargetPos;
            }

            ref BasisChestSpringState spring = ref Ref(chestSpring, 0);
            float hz = chestSpringHz;
            if (hz <= 0f || !spring.Seeded)
            {
                spring.Pos = headTargetPos;
                spring.Vel = Vector3.zero;
                spring.Seeded = true;
                return headTargetPos;
            }

            float dt = poseStream.deltaTime;
            if (dt <= 0f)
                return spring.Pos;

            BasisChestSpringCore.Step(spring.Pos, spring.Vel, headTargetPos, dt, hz, chestSpringDamping, out Vector3 newPos, out Vector3 newVel);

            if (!IsFinite(newPos) || !IsFinite(newVel))
            {
                spring.Pos = headTargetPos;
                spring.Vel = Vector3.zero;
                return headTargetPos;
            }

            spring.Pos = newPos;
            spring.Vel = newVel;
            return newPos;
        }
        static bool IsFinite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        Vector3 ApplyCrouchBodyOffset(Vector3 headTargetPos, Vector3 hipsPos, Quaternion hipsRot, Vector3 playerUpDir, float fade)
        {
            BasisCrouchOffsetInput input;
            input.HeadTargetPos = headTargetPos;
            input.HipsPos = hipsPos;
            input.HipsRot = hipsRot;
            input.Bind = offsetRotationHips;
            input.PlayerUp = playerUpDir;
            input.Factor = moveBodyBackWhenCrouching;
            input.RestDist = minHeadSpineHeight;
            input.CrouchDepth = crouchDepth;
            input.StandingHeadHeight = standingHeadHeight;
            input.Fade = fade;
            BasisCrouchOffsetCore.Solve(input, out BasisCrouchOffsetResult result, ref gizmos);
            return result.HipsPos;
        }
        public void ApplyCervicalLordosis()
        {
            if (!plan.hasNeck)
            {
                return;
            }

            Vector3 referenceUp;
            if (plan.hasChest)
            {
                Vector3 chestToNeck = poseStream.GetPosition(handleNeck) - poseStream.GetPosition(handleChest);
                referenceUp = chestToNeck.sqrMagnitude > sqrEpsilon ? chestToNeck.normalized : poseStream.GetRotation(handleChest) * Vector3.up;
            }
            else
            {
                referenceUp = playerUp;
            }

            BasisCervicalInput input;
            input.BaseDeg = lordosisBaseDeg;
            input.NeckShare = Mathf.Clamp01(lordosisNeckShare);
            input.MaxHeadPitchDeg = lordosisMaxHeadPitchDeg;
            input.ExtremeStartDeg = lordosisExtremeStartDeg;
            input.ExtremeFullDeg = lordosisExtremeFullDeg;
            input.ExtremeRollForwardMaxDeg = lordosisExtremeRollForwardMaxDeg;
            input.ExtremeRollBackwardMaxDeg = lordosisExtremeRollBackwardMaxDeg;
            input.ExtremeHipsHorizontalMax = lordosisExtremeHipsHorizontalMax;
            input.ExtremeChestHorizontalMax = lordosisExtremeChestHorizontalMax;
            input.ExtremeHipsHorizontalLookUp = lordosisExtremeHipsHorizontalLookUp;
            input.ExtremeChestHorizontalLookUp = lordosisExtremeChestHorizontalLookUp;
            input.ExtremeHipsDownMax = lordosisExtremeHipsDownMax;
            input.ExtremeChestDownMax = lordosisExtremeChestDownMax;
            input.ExtremeHipsDownLookUp = lordosisExtremeHipsDownLookUp;
            input.ExtremeChestDownLookUp = lordosisExtremeChestDownLookUp;
            input.PitchGainDeg = Mathf.Max(0f, lordosisPitchGainDeg);
            input.ReferenceUp = referenceUp;
            input.HeadTargetRot = targetRotationHead;
            input.HasUpperChest = plan.hasUpperChest;

            BasisCervicalSolveCore.Solve(input, out BasisCervicalResult result, ref gizmos);
            if (result.EarlyOut)
            {
                if (plan.hasHead)
                {
                    poseStream.SetPosition(handleHead, targetPositionHead);
                    poseStream.SetRotation(handleHead, result.HeadRotClamped * offsetRotationHead);
                }
                return;
            }

            Vector3 shoulderRight = plan.hasBodyRight ? poseStream.GetPosition(handleRightUpperArm) - poseStream.GetPosition(handleLeftUpperArm) : Vector3.zero;
            bool hasShoulderRight = shoulderRight.sqrMagnitude > sqrEpsilon;
            if (hasShoulderRight)
            {
                shoulderRight.Normalize();
            }

            if (plan.hasChestRef && result.BhDeg != 0f)
            {
                Quaternion bhRot = poseStream.GetRotation(plan.chestRef);
                Vector3 bhAxis = hasShoulderRight ? shoulderRight : bhRot * Vector3.right;
                poseStream.SetRotation(plan.chestRef, Quaternion.AngleAxis(result.BhDeg, bhAxis) * bhRot);
            }

            if (result.HasExtreme)
            {
                Quaternion refRot = plan.hasHips ? poseStream.GetRotation(handleHips) * Quaternion.Inverse(offsetRotationHips) : (plan.hasChest ? poseStream.GetRotation(handleChest) : Quaternion.identity);
                Vector3 refForward = refRot * Vector3.forward, refDown = -(refRot * Vector3.up);

                if (plan.hasHips)
                {
                    Vector3 hipsOffset = refForward * result.HipsForwardAmount + refDown * result.HipsDownAmount;
                    poseStream.SetPosition(handleHips, poseStream.GetPosition(handleHips) + hipsOffset);
                }

                if (plan.hasChest)
                {
                    Vector3 chestOffset = refForward * result.ChestForwardAmount + refDown * result.ChestDownAmount;
                    poseStream.SetPosition(handleChest, poseStream.GetPosition(handleChest) + chestOffset);
                }
            }
            float extraNeckDeg = Mathf.Clamp01(neckGazeFollow) * neckGazeFollowMaxDeg * result.LookDownFrac;
            float totalNeckDeg = result.NeckDeg + extraNeckDeg;
            if (totalNeckDeg != 0f)
            {
                Quaternion neckRotCurrent = poseStream.GetRotation(handleNeck);
                Vector3 neckAxis = hasShoulderRight ? shoulderRight : neckRotCurrent * Vector3.right;
                poseStream.SetRotation(handleNeck, Quaternion.AngleAxis(totalNeckDeg, neckAxis) * neckRotCurrent);
            }

            if (plan.hasHead)
            {
                poseStream.SetPosition(handleHead, targetPositionHead);
                poseStream.SetRotation(handleHead, result.HeadRotClamped * offsetRotationHead);
            }
        }
        public static Vector3 ClampHipsAroundHead(Vector3 headPos, Vector3 hipsPos, float restDistance, float minFactor, float maxFactor, Vector3 playerUp)
        {
            Vector3 headToHips = hipsPos - headPos;
            float dist = headToHips.magnitude, minD = restDistance * minFactor, maxD = restDistance * maxFactor;
            if (dist < epsilon)
            {
                return headPos - minD * playerUp;
            }

            Vector3 dir = headToHips / dist;
            float upDot = Vector3.Dot(dir, playerUp);
            if (upDot > 0f)
            {
                Vector3 horiz = dir - playerUp * upDot;
                dir = horiz.sqrMagnitude > sqrEpsilon ? horiz.normalized : -playerUp;
            }

            return headPos + dir * Mathf.Clamp(dist, minD, maxD);
        }
        const float HipsUnderHeadMaxLeanFrac = 1.0f;
        public static Vector3 ClampHipsUnderHead(Vector3 headPos, Vector3 hipsPos, float maxHorizontal, Vector3 playerUp)
        {
            if (maxHorizontal <= 0f)
            {
                return hipsPos;
            }

            Vector3 up = playerUp;
            Vector3 diff = hipsPos - headPos, lateral = diff - up * Vector3.Dot(diff, up);
            float lateralLen = lateral.magnitude;
            if (lateralLen <= maxHorizontal || lateralLen < epsilon)
            {
                return hipsPos;
            }

            return hipsPos - lateral * (1f - maxHorizontal / lateralLen);
        }
        public static Vector3 EnforceSpineBendLimit(Vector3 headPos, Vector3 hipsPos, float maxBendDeg, Vector3 playerUp)
        {
            if (maxBendDeg <= 0f)
            {
                return hipsPos;
            }

            Vector3 diff = hipsPos - headPos;
            if (diff.sqrMagnitude < minMag)
            {
                return hipsPos;
            }

            Vector3 up = playerUp;
            float down = Vector3.Dot(diff, -up);
            Vector3 lateral = diff + up * down;
            float lateralLen = lateral.magnitude, coneTan = Mathf.Tan(Mathf.Min(maxBendDeg, 89.9f) * Mathf.Deg2Rad);
            float minDown = lateralLen / Mathf.Max(coneTan, minMag);
            if (down >= minDown)
            {
                return hipsPos;
            }

            return headPos - up * minDown + lateral;
        }
        public static Vector3 AntiContortionist(Vector3 headPos, Quaternion headRot, Vector3 hipsPos, Quaternion hipsRot, float restDistance)
        {
            Vector3 headFwd = headRot * Vector3.forward, hipsFwd = hipsRot * Vector3.forward;
            float facingSimilarity = Vector3.Dot(headFwd, hipsFwd);
            float minDistFactor = Mathf.Lerp(0.2f, 0.85f, Mathf.Clamp01((facingSimilarity + 1f) * 0.5f));
            float minDist = restDistance * minDistFactor;
            Vector3 diff = hipsPos - headPos;
            float currentDist = diff.magnitude;

            if (currentDist < minDist && currentDist > epsilon)
            {
                return headPos + diff * (minDist / currentDist);
            }
            return hipsPos;
        }
        public static Vector3 MitigateSpineBuckling(Vector3 headPos, Quaternion hipsRot, Vector3 hipsPos, float restDistance, Vector3 playerUp)
        {
            Vector3 diff = hipsPos - headPos;
            float currentDist = diff.magnitude;

            if (currentDist >= restDistance || currentDist < epsilon)
                return hipsPos;

            Vector3 hipsUp = hipsRot * Vector3.up, spineDir = (headPos - hipsPos).normalized;
            float tension = Mathf.Clamp01(Vector3.Dot(hipsUp, spineDir));
            float compression = 1f - (currentDist / restDistance);
            float pushAmount = compression * tension * restDistance * 0.5f;
            return hipsPos - playerUp * pushAmount;
        }
    }
}
