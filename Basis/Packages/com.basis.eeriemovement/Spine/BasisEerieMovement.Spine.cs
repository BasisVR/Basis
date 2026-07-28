using System.Runtime.CompilerServices;
using Basis.Scripts.Common;
using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// Spine, hips and neck: the CCD chain, the pre-bend distribution and every postural modifier that acts on the torso.
    /// </summary>
    public partial struct BasisEerieMovement
    {
        // Hips + the chest/neck/head chain, then the anatomy modifiers that act on the spine after it.
        void SolveSpinePass(BasisPoseStream stream)
        {
            SolveSpine(stream);
            if (anatCervicalLordosis)
            {
                ApplyCervicalLordosis(stream);
            }
        }

        public void SolveSpine(BasisPoseStream stream)
        {
            if (!enabledSpineIK)
            {
                return;
            }
            // ---- Read targets ----
            Vector3 headTargetPos = targetPositionHead;
            Vector3 hipsTargetPos = targetPositionHips;

            Quaternion headTargetRot = targetRotationHead;
            Quaternion hipsTargetRot = targetRotationHips;
            Quaternion offsetHips = offsetRotationHips;
            Quaternion chestTargetRot = targetRotationChest;

            Quaternion hipDesired = hipsTargetRot * offsetHips;
            Quaternion chestDesired = chestTargetRot * targetOffsetChest;

            float restDist = minHeadSpineHeight;
            BasisIKLockMode lockMode = ikLockMode;
            Vector3 up = playerUp;

            // Lock mode determines how hips position relates to head position:
            // LockHips: Hips are the anchor; apply hips directly, no head-relative clamping.
            // LockHead: Head is the anchor; hips ride at rest spine length along the spine's own axis.
            // LockBoth: Both independently positioned; spine must accommodate (original behavior).
            switch (lockMode)
            {
                case BasisIKLockMode.LockHips: // hips are authoritative, skip head-relative clamping
                    break;

                case BasisIKLockMode.LockHead: // head is the anchor; the spine may not compress below its rest length, allow stretching further
                    {
                        Vector3 headToHips = hipsTargetPos - headTargetPos;
                        float spineLen = headToHips.magnitude;
                        if (spineLen < restDist)
                        {
                            Vector3 spineDir = spineLen > k_Epsilon ? headToHips / spineLen : hipsTargetRot * Vector3.down;
                            hipsTargetPos = headTargetPos + spineDir * restDist;
                        }
                    }
                    break;

                default: // LockBoth - original behavior: clamp hips relative to head
                    hipsTargetPos = AntiContortionist(headTargetPos, headTargetRot, hipsTargetPos, hipsTargetRot, restDist);
                    hipsTargetPos = MitigateSpineBuckling(headTargetPos, hipsTargetRot, hipsTargetPos, restDist, up);
                    float MaxBendDeg = maxBendDeg;
                    hipsTargetPos = EnforceSpineBendLimit(headTargetPos, hipsTargetPos, MaxBendDeg, up);
                    hipsTargetPos = ClampHipsAroundHead(headTargetPos, hipsTargetPos, restDist, minFactor, maxFactor, up);
                    break;
            }
            Vector3 neckCue = ComputeNeckCue(headTargetPos);
            float crouchFade = 1f;
            if (!hasHipsTracker)
            {
                hipsTargetPos = ApplyTrunkCounterbalance(neckCue, hipsTargetPos, up, out float flexionFrac);
                crouchFade = 1f - flexionFrac;
            }
            hipsTargetPos = ApplyCrouchBodyOffset(stream, headTargetPos, hipsTargetPos, hipDesired, up, crouchFade);
            targetPositionHips = hipsTargetPos;
            if (!hasHipsTracker)
            {
                hipDesired = ApplyHipHinge(stream, neckCue, hipsTargetPos, hipDesired, up);
            }

            // Apply hips driver if valid
            if (handleHips.IsValid(stream))
            {
                handleHips.SetPosition(stream, hipsTargetPos);
                handleHips.SetRotation(stream, hipDesired);
            }
            if (hasChestTracker && handleChest.IsValid(stream))
            {
                // Neck rotation produced by your spine IK pass – we keep this
                Quaternion neckRot = handleNeck.IsValid(stream) ? handleNeck.GetRotation(stream) : Quaternion.identity;

                // Spine as an extra reference if available (nice stabiliser)
                Quaternion spineRot = handleSpine.IsValid(stream) ? handleSpine.GetRotation(stream) : neckRot;

                float Value = maxChestDeltaDeg;
                // Clamp relative to neck and spine
                Quaternion clampedChestRot = ClampRotation(chestDesired, neckRot, Value);
                clampedChestRot = ClampRotation(clampedChestRot, spineRot, Value);

                handleChest.SetRotation(stream, clampedChestRot);

                Vector3 headPos = targetPositionHead;
                Quaternion headRot = targetRotationHead;

                DistributeSpineBend(stream, headPos);
                BiasSpineTowardChest(stream);
                GuardSpineChain(stream);
                SolveSequentialSpineIK(stream, headPos, headRot);
            }
            else if (handleChest.IsValid(stream) && handleNeck.IsValid(stream) && handleHead.IsValid(stream))
            {
                Vector3 headPos = targetPositionHead;
                Quaternion headRot = targetRotationHead;

                DistributeSpineBend(stream, headPos);
                ApplyArmSwingChestFollow(stream);
                GuardSpineChain(stream);
                SolveSequentialSpineIK(stream, headPos, headRot);
            }
        }
        public void SolveSequentialSpineIK(BasisPoseStream stream, Vector3 headTargetPos, Quaternion headTargetRot)
        {
            if (!chainHeadToSpine.IsCreated || chainHeadToSpine.Length < 3)
                return;

            int chainLen = chainHeadToSpine.Length;
            const int tipIdx = 0;
            const int firstJoint = 1;
            int lastJoint = chainLen - 2;

            for (int i = 0; i < chainLen; i++)
            {
                if (!chainHeadToSpine[i].IsValid(stream))
                    return;
            }

            int maxIters = Mathf.Max(1, spineMaxIterations);
            float tolerance = Mathf.Max(0f, spineTolerance);
            float tolSqr = tolerance * tolerance;
            {
                Vector3 rootPos = chainHeadToSpine[chainLen - 1].GetPosition(stream);
                float chainReach = 0f;
                for (int i = 0; i < chainLen - 1; i++)
                {
                    chainReach += (chainHeadToSpine[i].GetPosition(stream) - chainHeadToSpine[i + 1].GetPosition(stream)).magnitude;
                }
                Vector3 rootToTarget = headTargetPos - rootPos;
                float targetDist = rootToTarget.magnitude;
                if (targetDist > k_Epsilon && chainReach > k_Epsilon)
                {
                    float compression = chainReach - targetDist;
                    float commandedDist;
                    if (compression > 0f)
                    {
                        float band = spineTautBandFrac * chainReach;
                        commandedDist = chainReach - compression * compression * compression / (compression * compression + band * band);
                    }
                    else
                    {
                        commandedDist = chainReach;
                    }
                    headTargetPos = rootPos + rootToTarget * (commandedDist / targetDist);
                }
            }

            float ccdRelax = spineCCDRelax;
            float lumbarTwistKeep = spineTwistKeep;
            float cervicalTwistKeep = spineNeckTwistKeep;
            // Body-relative twist axis (hips-up), NOT world-up: vertical standing, horizontal lying down, so
            // the relax strips the same anatomical axial-twist DOF in any orientation. Falls back to playerUp.
            Quaternion hipsTwistRot = handleHips.IsValid(stream) ? handleHips.GetRotation(stream) : Quaternion.identity;
            Vector3 ccdUp = hipsTwistRot * Vector3.up;
            if (ccdUp.sqrMagnitude < k_SqrEpsilon) ccdUp = playerUp;
            float jointSpan = Mathf.Max(1, lastJoint - firstJoint);
            float neckCone = neckMaxConeDeg;
            float chestCone = maxChestDeltaDeg;
            Quaternion finalHeadRot = headTargetRot * targetOffsetHead;

            for (int iter = 0; iter < maxIters; iter++)
            {
                Vector3 tipPos = chainHeadToSpine[tipIdx].GetPosition(stream);
                if ((headTargetPos - tipPos).sqrMagnitude < tolSqr)
                    break;

                // Walk from root-side (spine) toward tip-side (neck) so the longer-lever joints
                // take the bigger swing first; later passes through the loop fine-tune with the
                // shorter levers.
                for (int i = lastJoint; i >= firstJoint; i--)
                {
                    ReachHeadJoint(stream, i, headTargetPos, firstJoint, chainLen, jointSpan,
                        cervicalTwistKeep, lumbarTwistKeep, ccdUp, ccdRelax, neckCone, chestCone);
                }
            }

            // ==========================================================================================
            // PHASE B -- THE CHEST AS A SECONDARY IK TARGET. The loop above placed the HEAD (primary,
            // welded to the HMD); the chest position fell out of it as a free FK consequence. Now pull the
            // chest bone onto its own target and RESTORE the head with the joints above the chest, which
            // have spare DOF. The head is never traded for the chest. Bit-identical to head-only above when
            // the chest target is off (weight 0). See SolveChestTarget.
            // ==========================================================================================
            SolveChestTarget(stream, headTargetPos, firstJoint, lastJoint, chainLen, jointSpan,
                cervicalTwistKeep, lumbarTwistKeep, ccdUp, ccdRelax, neckCone, chestCone);

            chainHeadToSpine[tipIdx].SetRotation(stream, finalHeadRot);
        }
        // One CCD step aiming the head tip from joint `i` -- the exact body of the Phase A loop, extracted so
        // Phase B's head-restore reuses it verbatim (a copy would drift). Shapes the reach (twist graded root
        // -> tip, mid-thoracic stiffened), relaxes, applies the cones, then the anatomy guard LAST.
        void ReachHeadJoint(BasisPoseStream stream, int i, Vector3 headTargetPos, int firstJoint, int chainLen,
            float jointSpan, float cervicalTwistKeep, float lumbarTwistKeep, Vector3 ccdUp, float ccdRelax,
            float neckCone, float chestCone)
        {
            const int tipIdx = 0;
            Vector3 jointPos = chainHeadToSpine[i].GetPosition(stream);
            Vector3 curTipPos = chainHeadToSpine[tipIdx].GetPosition(stream);

            Vector3 cur = curTipPos - jointPos;
            Vector3 tgt = headTargetPos - jointPos;
            if (cur.sqrMagnitude < k_SqrEpsilon || tgt.sqrMagnitude < k_SqrEpsilon)
                return;

            Quaternion delta = BasisQuaternionExt.FromToRotation(cur, tgt);
            float t = (i - firstJoint) / jointSpan;
            float jointTwistKeep = Mathf.Lerp(cervicalTwistKeep, lumbarTwistKeep, t);
            float jointSwingScale = 1f - thoracicBendStiffen * (1f - Mathf.Abs(2f * t - 1f));
            delta = BasisTwistSolveCore.ShapeReachStep(delta, ccdUp, jointTwistKeep, jointSwingScale);
            delta = Quaternion.Slerp(Quaternion.identity, delta, ccdRelax);
            chainHeadToSpine[i].SetRotation(stream, delta * chainHeadToSpine[i].GetRotation(stream));

            if (i == firstJoint)
            {
                ClampNeckCone(stream, i, neckCone);
            }
            else if (chainLen >= 5 && i == chainLen - 3)
            {
                ClampChestCone(stream, i, chestCone);
            }

            // LAST, so it sees the outcome of every other constraint on this joint, not just the
            // CCD's own step. The cones above are reach heuristics; this is anatomy.
            GuardSpineJoint(stream, i);
        }
        void SolveChestTarget(BasisPoseStream stream, Vector3 headTargetPos, int firstJoint, int lastJoint,
            int chainLen, float jointSpan, float cervicalTwistKeep, float lumbarTwistKeep, Vector3 ccdUp,
            float ccdRelax, float neckCone, float chestCone)
        {
            // Off (toggle false -> weight 0): return before touching a single bone, so the head-only solve
            // above is the whole story, bit for bit. This is the "same usability" guarantee.
            if (!chestIkTarget)
                return;

            int chestBoneIdx = chainLen - 3;   // the Chest bone
            // Need a real Spine joint below the chest to move it, and real upper joints to restore the head.
            if (chestBoneIdx < firstJoint || lastJoint <= firstJoint || lastJoint <= chestBoneIdx)
                return;

            // THE RAW chest, not the head-hint-biased targetPositionChest -- pinning to the biased one dragged
            // the torso ~8cm up and leaned the body in desktop / no-tracker mode.
            Vector3 chestTargetPos = targetPositionChestRaw;
            Vector3 chestBonePos = chainHeadToSpine[chestBoneIdx].GetPosition(stream);
            // A chest target that is wildly far from the FK chest is a glitching tracker or an unset target;
            // chasing it would wreck the torso. Fall back to the head-only chest. Same guard the old
            // BiasSpineTowardChest used, and the anatomy guard below bounds whatever does get through.
            if ((chestTargetPos - chestBonePos).sqrMagnitude > (chestPullMaxDist * chestPullMaxDist))
                return;

            // The Spine is the root end of the chain, so its shaping params are those of index lastJoint.
            float spineT = (lastJoint - firstJoint) / jointSpan;
            float spineTwistKeep = Mathf.Lerp(cervicalTwistKeep, lumbarTwistKeep, spineT);
            float spineSwingScale = 1f - thoracicBendStiffen * (1f - Mathf.Abs(2f * spineT - 1f));

            for (int citer = 0; citer < chestIkIterations; citer++)
            {
                // 1) rotate the Spine so the Chest bone slides toward its target.
                Vector3 spinePos = chainHeadToSpine[lastJoint].GetPosition(stream);
                Vector3 cCur = chainHeadToSpine[chestBoneIdx].GetPosition(stream) - spinePos;
                Vector3 cTgt = chestTargetPos - spinePos;
                if (cCur.sqrMagnitude > k_SqrEpsilon && cTgt.sqrMagnitude > k_SqrEpsilon)
                {
                    Quaternion cDelta = BasisQuaternionExt.FromToRotation(cCur, cTgt);
                    cDelta = BasisTwistSolveCore.ShapeReachStep(cDelta, ccdUp, spineTwistKeep, spineSwingScale);
                    // Relax x weight: a gentler chest pull lets the head-restore keep pace, which is exactly
                    // why the moderate weight preserves the head where a full pull loosened it.
                    cDelta = Quaternion.Slerp(Quaternion.identity, cDelta, ccdRelax * chestIkWeight);
                    chainHeadToSpine[lastJoint].SetRotation(stream, cDelta * chainHeadToSpine[lastJoint].GetRotation(stream));
                    GuardSpineJoint(stream, lastJoint);
                }

                // 2) restore the head with the UPPER joints only (chest and above -- never the Spine, which
                // now owns the chest). They have far more DOF than the head needs, so the head returns to
                // target without disturbing the chest the Spine just placed.
                for (int sweep = 0; sweep < chestIkHeadRestoreSweeps; sweep++)
                {
                    for (int i = lastJoint - 1; i >= firstJoint; i--)
                    {
                        ReachHeadJoint(stream, i, headTargetPos, firstJoint, chainLen, jointSpan,
                            cervicalTwistKeep, lumbarTwistKeep, ccdUp, ccdRelax, neckCone, chestCone);
                    }
                }
            }
        }
        // ==============================================================================================
        // THE ANATOMICAL ENVELOPE. Pulls one spine joint back inside the range of motion its real vertebrae
        // have. See BasisSpineAnatomyCore for the measurements and BasisSpineAnatomy for the table.
        //
        // WHY IT LIVES INSIDE THE CCD LOOP. The CCD is what actually places the head, and before this it
        // rotated the spine, chest and upperChest with NO per-joint limit whatsoever -- its only constraints
        // were a cone on the neck and a cone on the chest. So a limit applied BEFORE the CCD is a suggestion
        // the CCD is free to ignore, which is exactly what happened to BasisSpineBendCore.ClampAsymmetric.
        // And a limit applied AFTER the CCD would drag the head off the HMD, which is not negotiable.
        //
        // Applied per-joint INSIDE the loop, the residual simply redistributes onto the other vertebrae on
        // the next sweep -- which is what a real spine does when you ask one segment for more than it has.
        // The head still converges, because the CCD still gets the last word on it.
        //
        // The chain runs head -> hips, so joint `i`'s PARENT is `i + 1`.
        // ==============================================================================================
        void GuardSpineJoint(BasisPoseStream stream, int i)
        {
            if (!spineAnatomicalRom)
            {
                return;
            }
            if (!chainSpineRestFrames.IsCreated || i < 0 || i >= chainSpineRestFrames.Length)
            {
                return;
            }

            BasisSpineRestFrame frame = chainSpineRestFrames[i];
            if (!frame.Valid)
            {
                return;   // the head and the hips: commanded, not solved. Never guarded.
            }

            int parent = i + 1;
            if (parent >= chainHeadToSpine.Length || !chainHeadToSpine[parent].IsValid(stream) || !chainHeadToSpine[i].IsValid(stream))
            {
                return;
            }

            Quaternion parentRot = chainHeadToSpine[parent].GetRotation(stream);
            Quaternion boneRot = chainHeadToSpine[i].GetRotation(stream);
            Quaternion local = BasisSpineAnatomyCore.Conj(parentRot) * boneRot;

            Quaternion clamped = BasisSpineAnatomyCore.Clamp(local, frame, chainSpineRoms[i], out BasisSpineClampInfo info);
            if (!info.Touched)
            {
                return;   // legal pose: the bone is not written at all, so it cannot be perturbed.
            }

            chainHeadToSpine[i].SetRotation(stream, parentRot * clamped);
        }
        // A full sweep of the envelope over every solved vertebra. Run right after DistributeSpineBend so
        // the CCD starts from a legal spine -- the CCD breaks out early when the head is already on target,
        // and on those frames it would otherwise never look at the pre-bend's output at all.
        void GuardSpineChain(BasisPoseStream stream)
        {
            if (!chainHeadToSpine.IsCreated || chainHeadToSpine.Length < 3)
            {
                return;
            }
            for (int i = 1; i <= chainHeadToSpine.Length - 2; i++)
            {
                GuardSpineJoint(stream, i);
            }
        }
        // Constrains the neck (chain index neckIdx) to within maxConeDeg of the chest→neck
        // direction. Enforced in-loop so chest/spine take the slack on the next CCD sweep.
        void ClampNeckCone(BasisPoseStream stream, int neckIdx, float maxConeDeg)
        {
            Vector3 chestPos = chainHeadToSpine[neckIdx + 1].GetPosition(stream);
            Vector3 neckPos = chainHeadToSpine[neckIdx].GetPosition(stream);
            Vector3 headPos = chainHeadToSpine[0].GetPosition(stream);

            Vector3 parentDir = neckPos - chestPos;
            Vector3 boneDir = headPos - neckPos;
            if (parentDir.sqrMagnitude < k_SqrEpsilon || boneDir.sqrMagnitude < k_SqrEpsilon)
            {
                return;
            }

            float ang = Vector3.Angle(parentDir, boneDir);
            if (ang <= maxConeDeg)
            {
                return;
            }

            Vector3 axis = Vector3.Cross(boneDir, parentDir);
            if (axis.sqrMagnitude < k_SqrEpsilon)
            {
                return;
            }

            axis.Normalize();
            Quaternion correction = Quaternion.AngleAxis(ang - maxConeDeg, axis);
            chainHeadToSpine[neckIdx].SetRotation(stream, correction * chainHeadToSpine[neckIdx].GetRotation(stream));
        }
        void ClampChestCone(BasisPoseStream stream, int chestIdx, float maxConeDeg)
        {
            Vector3 spinePos = chainHeadToSpine[chestIdx + 1].GetPosition(stream);
            Vector3 chestPos = chainHeadToSpine[chestIdx].GetPosition(stream);
            Vector3 childPos = chainHeadToSpine[chestIdx - 1].GetPosition(stream);

            Vector3 parentDir = chestPos - spinePos;
            Vector3 boneDir = childPos - chestPos;
            if (parentDir.sqrMagnitude < k_SqrEpsilon || boneDir.sqrMagnitude < k_SqrEpsilon)
                return;

            float ang = Vector3.Angle(parentDir, boneDir);
            if (ang <= maxConeDeg)
                return;

            Vector3 axis = Vector3.Cross(boneDir, parentDir);
            if (axis.sqrMagnitude < k_SqrEpsilon)
                return;

            axis.Normalize();
            Quaternion correction = Quaternion.AngleAxis(ang - maxConeDeg, axis);
            chainHeadToSpine[chestIdx].SetRotation(stream, correction * chainHeadToSpine[chestIdx].GetRotation(stream));
        }
        void BiasSpineTowardChest(BasisPoseStream stream)
        {
            if (!handleSpine.IsValid(stream) || !handleChest.IsValid(stream))
                return;

            Vector3 chestTargetPos = targetPositionChest;
            Vector3 spinePos = handleSpine.GetPosition(stream);
            Vector3 chestPos = handleChest.GetPosition(stream);

            if ((chestTargetPos - chestPos).sqrMagnitude > (chestPullMaxDist * chestPullMaxDist))
                return;

            Vector3 cur = chestPos - spinePos;
            Vector3 tgt = chestTargetPos - spinePos;
            if (cur.sqrMagnitude < k_SqrEpsilon || tgt.sqrMagnitude < k_SqrEpsilon)
                return;

            Quaternion pull = ClampRotation(BasisQuaternionExt.FromToRotation(cur, tgt), Quaternion.identity, chestPosPullMaxDeg);
            handleSpine.SetRotation(stream, pull * handleSpine.GetRotation(stream));
        }
        // Pre-distributes the hips→head bend onto spine and upperChest in hips-local space, split
        // into independent pitch / yaw / roll contributions so anisotropic human ranges of motion
        // can be respected (lumbar twists very little, cervical twists a lot, forward bend ≫ back).
        // Pipeline: (chest spring smooths target) → (decompose bend into pitch/roll, twist into yaw)
        //   → (per-axis weight) → (asymmetric clamp) → (apply as hips-local delta).
        // The chest→neck→head two-bone solve afterwards handles whatever residual reach remains.
        // The neck, estimated RIGIDLY off the head target, and therefore EXACTLY invariant to a gaze: if the
        // head orbits the neck by Q then Q's two lever arms cancel algebraically (written out in full inside
        // DistributeSpineBend). Every consumer that wants to know where the TORSO is must read this and not
        // headTargetPos -- the HMD sits forward of the neck pivot, so the raw head target reports a lean the
        // moment you look down. Shared by the spine bend, the postural counterbalance and the hip hinge so
        // the three cannot drift apart.
        Vector3 ComputeNeckCue(Vector3 headTargetPos)
        {
            return headTargetPos + (targetRotationHead * targetOffsetHead) * tposeHeadToNeckLocal;
        }
        // Wrapper for BasisTrunkCounterbalanceCore: the pelvis travels back as the trunk folds forward, so the
        // bend happens at the hip instead of the torso folding down into itself. The cap scales with the
        // avatar's own spine (minHeadSpineHeight is the T-pose hips->head chain), so it is avatar-relative
        // rather than a fixed number of metres. Gating (no hip tracker) is the caller's, as with ApplyHipHinge.
        Vector3 ApplyTrunkCounterbalance(Vector3 neckCue, Vector3 hipsPos, Vector3 playerUp, out float flexionFrac)
        {
            BasisTrunkCounterbalanceInput input;
            input.HipsPos = hipsPos;
            input.NeckCue = neckCue;
            input.PlayerUp = playerUp;
            input.Gain = trunkCounterbalance;
            input.MaxShift = trunkCounterbalanceMaxSpineFrac * minHeadSpineHeight;
            BasisTrunkCounterbalanceCore.Solve(input, out BasisTrunkCounterbalanceResult result);
            flexionFrac = result.FlexionFrac;
            return result.HipsPos;
        }
        public void DistributeSpineBend(BasisPoseStream stream, Vector3 headTargetPos)
        {
            if (!handleHips.IsValid(stream) || !handleChest.IsValid(stream))
            {
                return;
            }

            bool hasSpine = handleSpine.IsValid(stream);
            bool hasUpper = handleUpperChest.IsValid(stream);
            if (!hasSpine && !hasUpper)
            {
                return;
            }

            Quaternion hipsRot = handleHips.GetRotation(stream);

            // ==========================================================================================
            // THE SPINE IS CUED OFF THE NECK, NOT THE HEAD. This is the fix for "looking down forces chest
            // to rotate".
            //
            // BasisSpineBendCore bends the spine by the angle between hips->chest and hips->CUE. Hand it the
            // HEAD and you have handed it a point that is not on the spine at all -- the head sits on the END
            // of the neck and ORBITS it when you nod. So a user who gazes down without moving their torso by
            // one millimetre still swings the head target forward and down, the hips->head vector tips over,
            // and the solver bends the spine to a lean that never happened. Measured on a T-posed adult with
            // the torso held byte-identical: a 45 deg glance down invents 4.4 deg of chest pitch, 60 deg
            // invents 8.4 deg, 75 deg invents 10.4 deg. (BasisSpineGazeContaminationTests.)
            //
            // The neck, estimated RIGIDLY off the head, is exactly invariant to that nod. Write it out: if
            // the head orbits the neck by Q, then
            //     estimatedNeck = (neck + Q*(head-neck)) + (Q*headRot) * inv(headRot)*(neck-head)
            //                   = neck + Q*(head-neck) + Q*(neck-head)
            //                   = neck
            // -- the two lever arms cancel, algebraically, for ANY Q. Not damped, not faded, not clamped:
            // CANCELLED. A gaze cannot move this cue, so it cannot bend the spine, so there is nothing left
            // to tune. BasisSpineGazeContaminationTests pins it at exactly zero.
            //
            // A real human's chest pitches -0.05 deg per degree of gaze -- i.e. not at all -- so zero is not
            // an approximation of the right answer here, it IS the right answer.
            //
            // It also disarms a SECOND bug for free. ComputeSquishMultiplier amplifies the spine's rotation
            // as hips->cue COMPRESSES (x1.42 at 25% compression), and gazing down was shortening hips->HEAD
            // -- so the phantom bend was being multiplied by a phantom squish. The neck does not move on a
            // gaze, so neither does the squish. RestLen moves to hips->NECK to match: the spine spans the
            // spine, and the head was never part of it.
            // ==========================================================================================
            Vector3 neckCue = ComputeNeckCue(headTargetPos);

            // A LITTLE REAL SPINE. neckCue is invariant to a pure gaze (the head orbits the neck by Q, the
            // rigid re-attachment un-orbits it -- that is the look-down-stability fix, chest pitch 0.000 deg
            // on any gaze). But that reads as a rigid mannequin under a swiveling head on desktop. Blend the
            // cue a fraction back toward the ACTUAL head: on a look-down the head has orbited forward+down, so
            // the cue tips that way and the chest folds a touch. 0 = rigid, 1 = the full (phantom) follow. A
            // real chest does NOT fold on gaze (corpus: -0.05 deg/deg), so this is a deliberate desktop-feel
            // knob, small by default, and it costs nothing with a chest tracker (the pitch weight is zeroed).
            Vector3 spineCue = Vector3.Lerp(neckCue, headTargetPos, Mathf.Clamp01(spineGazeFollow));

            Quaternion hipsBind = offsetRotationHips;

            BasisSpineBendInput input;
            input.HipsRot = hipsRot;
            input.HipsPos = handleHips.GetPosition(stream);
            input.ChestPos = handleChest.GetPosition(stream);
            input.SmoothedHead = ApplyChestSpring(stream, spineCue);
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
            input.RestLen = tposeLengthNeckToHips.magnitude;   // the spine spans hips->NECK; the head was never part of it
            input.BendTwistCoupling = bendTwistCoupling;
            input.HasSpine = hasSpine;
            input.HasUpper = hasUpper;

            // A tracked chest already measures torso lean, so the head-position-derived forward/lateral
            // pre-bend is redundant -- and looking down swings the HMD forward of the neck, which it
            // misreads as a lean and hunches the chest forward (the squish boost compounds it). Drop the
            // lean (pitch/roll) and let the tracked chest + the spine chain own it; keep the facing twist.
            if (hasChestTracker)
            {
                input.SpineBendPitch = 0f;
                input.SpineBendRoll = 0f;
                input.UpperBendPitch = 0f;
                input.UpperBendRoll = 0f;
            }

            BasisSpineBendCore.Solve(input, out BasisSpineBendResult r);
            if (r.EarlyOut)
            {
                return;
            }

            // Apply the delta in the SAME bind-cancelled frame the core measured it in (hipsRot * inv(bind)),
            // not the raw hips-bone frame. On an identity bind this is hipsRot exactly, so it is bit-identical
            // for the usual rigs; on a rig bound rolled/axis-swapped it stops the anatomically-framed bend from
            // being re-applied about the bone's rolled axes (which leaned the chest sideways by 10-14 deg).
            Quaternion hipsAnat = hipsRot * Quaternion.Inverse(hipsBind);
            Quaternion invHipsAnat = Quaternion.Inverse(hipsAnat);
            if (r.WriteSpine)
            {
                Quaternion deltaWorld = hipsAnat * Quaternion.Euler(r.SpineEuler) * invHipsAnat;
                handleSpine.SetRotation(stream, deltaWorld * handleSpine.GetRotation(stream));
            }
            if (r.WriteUpper)
            {
                Quaternion deltaWorld = hipsAnat * Quaternion.Euler(r.UpperEuler) * invHipsAnat;
                handleUpperChest.SetRotation(stream, deltaWorld * handleUpperChest.GetRotation(stream));
            }
        }
        // Critically-damped spring on the head target consumed by DistributeSpineBend. Lets the
        // body lag slightly behind quick head moves without affecting the head bone itself.
        // Uses implicit Euler so it stays stable at high Hz / low fps where explicit Euler blows
        // up (omega * dt > 1 → divergent oscillation → NaN → corrupted quaternions downstream).
        Vector3 ApplyChestSpring(BasisPoseStream stream, Vector3 headTargetPos)
        {
            if (!chestSpringState.IsCreated || !chestSpringInit.IsCreated)
            {
                return headTargetPos;
            }

            float hz = chestSpringHz;
            if (hz <= 0f)
            {
                chestSpringState[0] = headTargetPos;
                chestSpringState[1] = Vector3.zero;
                chestSpringInit[0] = 1;
                return headTargetPos;
            }
            if (chestSpringInit[0] == 0)
            {
                chestSpringState[0] = headTargetPos;
                chestSpringState[1] = Vector3.zero;
                chestSpringInit[0] = 1;
                return headTargetPos;
            }

            float dt = stream.deltaTime;
            if (dt <= 0f)
                return chestSpringState[0];

            BasisChestSpringCore.Step(chestSpringState[0], chestSpringState[1], headTargetPos, dt, hz,
                chestSpringDamping, out Vector3 newPos, out Vector3 newVel);

            // Defensive: if upstream input has produced a NaN, re-seed instead of poisoning the rig.
            if (!IsFinite(newPos) || !IsFinite(newVel))
            {
                chestSpringState[0] = headTargetPos;
                chestSpringState[1] = Vector3.zero;
                return headTargetPos;
            }

            chestSpringState[0] = newPos;
            chestSpringState[1] = newVel;
            return newPos;
        }
        static bool IsFinite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        // Pelvis tilts forward to share the lean past the threshold. Without this, a deep forward
        // reach makes the spine swallow the entire bend and everything above the hips folds.
        Quaternion ApplyHipHinge(BasisPoseStream stream, Vector3 headPos, Vector3 hipsPos, Quaternion hipsRot, Vector3 playerUp)
        {
            BasisHipHingeInput input;
            input.HeadPos = headPos;
            input.HipsPos = hipsPos;
            input.HipsRot = hipsRot;
            input.PlayerUp = playerUp;
            input.StartDeg = hipHingeStartDeg;
            input.MaxAddDeg = hipHingeMaxAddDeg;
            BasisHipHingeCore.Solve(input, out BasisHipHingeResult result);
            return result.HipsRot;
        }
        // `fade` is 1 - sin(trunk flexion) from the postural counterbalance. This term reads head HEIGHT, so
        // it cannot tell a squat from a waist-fold and would double-count the pelvis travel the counterbalance
        // has already applied; fading it out as the trunk folds lets each own the posture it describes -- the
        // crouch sit-back for a squat with an upright trunk, the counterbalance for a bend.
        Vector3 ApplyCrouchBodyOffset(BasisPoseStream stream, Vector3 headTargetPos, Vector3 hipsPos, Quaternion hipsRot, Vector3 playerUpDir, float fade)
        {
            if (hasChestTracker || hasHipsTracker)
            {
                return hipsPos;
            }

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
            BasisCrouchOffsetCore.Solve(input, out BasisCrouchOffsetResult result);
            return result.HipsPos;
        }
        public void ApplyCervicalLordosis(BasisPoseStream stream)
        {
            if (!handleNeck.IsValid(stream))
            {
                return;
            }

            Vector3 referenceUp;
            if (handleChest.IsValid(stream))
            {
                Vector3 chestToNeck = handleNeck.GetPosition(stream) - handleChest.GetPosition(stream);
                referenceUp = chestToNeck.sqrMagnitude > k_SqrEpsilon
                    ? chestToNeck.normalized
                    : handleChest.GetRotation(stream) * Vector3.up;
            }
            else
            {
                Vector3 up = playerUp;
                referenceUp = up.sqrMagnitude < k_SqrEpsilon ? Vector3.up : up.normalized;
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
            input.ExtremeHipsDownMax = lordosisExtremeHipsDownMax;
            input.ExtremeChestDownMax = lordosisExtremeChestDownMax;
            input.ExtremeHipsDownLookUp = lordosisExtremeHipsDownLookUp;
            input.ExtremeChestDownLookUp = lordosisExtremeChestDownLookUp;
            input.PitchGainDeg = Mathf.Max(0f, lordosisPitchGainDeg);
            input.ReferenceUp = referenceUp;
            input.HeadTargetRot = targetRotationHead;
            input.HasUpperChest = handleUpperChest.IsValid(stream);

            BasisCervicalSolveCore.Solve(input, out BasisCervicalResult result);
            if (result.EarlyOut)
            {
                if (handleHead.IsValid(stream))
                {
                    handleHead.SetPosition(stream, targetPositionHead);
                    handleHead.SetRotation(stream, result.HeadRotClamped * targetOffsetHead);
                }
                return;
            }

            Vector3 shoulderRight = (handleLeftUpperArm.IsValid(stream) && handleRightUpperArm.IsValid(stream))
                ? handleRightUpperArm.GetPosition(stream) - handleLeftUpperArm.GetPosition(stream)
                : Vector3.zero;
            bool hasShoulderRight = shoulderRight.sqrMagnitude > k_SqrEpsilon;
            if (hasShoulderRight)
            {
                shoulderRight.Normalize();
            }

            BasisBoneHandle bendHandle = input.HasUpperChest ? handleUpperChest : handleChest;
            if (bendHandle.IsValid(stream) && result.BhDeg != 0f)
            {
                Quaternion bhRot = bendHandle.GetRotation(stream);
                Vector3 bhAxis = hasShoulderRight ? shoulderRight : bhRot * Vector3.right;
                bendHandle.SetRotation(stream, Quaternion.AngleAxis(result.BhDeg, bhAxis) * bhRot);
            }

            if (result.HasExtreme)
            {
                Quaternion refRot = handleHips.IsValid(stream)
                    ? handleHips.GetRotation(stream) * Quaternion.Inverse(offsetRotationHips)
                    : (handleChest.IsValid(stream) ? handleChest.GetRotation(stream) : Quaternion.identity);
                Vector3 refForward = refRot * Vector3.forward;
                Vector3 refDown = -(refRot * Vector3.up);

                if (handleHips.IsValid(stream))
                {
                    Vector3 hipsOffset = refForward * result.HipsForwardAmount + refDown * result.HipsDownAmount;
                    handleHips.SetPosition(stream, handleHips.GetPosition(stream) + hipsOffset);
                }

                if (handleChest.IsValid(stream))
                {
                    Vector3 chestOffset = refForward * result.ChestForwardAmount + refDown * result.ChestDownAmount;
                    handleChest.SetPosition(stream, handleChest.GetPosition(stream) + chestOffset);
                }
            }
            float extraNeckDeg = Mathf.Clamp01(neckGazeFollow) * neckGazeFollowMaxDeg * result.LookDownFrac;
            float totalNeckDeg = result.NeckDeg + extraNeckDeg;
            if (totalNeckDeg != 0f)
            {
                Quaternion neckRotCurrent = handleNeck.GetRotation(stream);
                Vector3 neckAxis = hasShoulderRight ? shoulderRight : neckRotCurrent * Vector3.right;
                handleNeck.SetRotation(stream, Quaternion.AngleAxis(totalNeckDeg, neckAxis) * neckRotCurrent);
            }

            if (handleHead.IsValid(stream))
            {
                handleHead.SetPosition(stream, targetPositionHead);
                handleHead.SetRotation(stream, result.HeadRotClamped * targetOffsetHead);
            }
        }
        public static Vector3 ClampHipsAroundHead(Vector3 headPos, Vector3 hipsPos, float restDistance, float minFactor, float maxFactor, Vector3 playerUp)
        {
            Vector3 headToHips = hipsPos - headPos;
            float dist = headToHips.magnitude;
            float minD = restDistance * minFactor;
            float maxD = restDistance * maxFactor;
            if (dist < k_Epsilon)
            {
                return headPos - minD * playerUp; // degenerate: place the hips straight below the head
            }

            Vector3 dir = headToHips / dist;
            float upDot = Vector3.Dot(dir, playerUp);
            if (upDot > 0f)
            {
                Vector3 horiz = dir - playerUp * upDot;
                dir = horiz.sqrMagnitude > k_SqrEpsilon ? horiz.normalized : -playerUp;
            }

            return headPos + dir * Mathf.Clamp(dist, minD, maxD);
        }
        public static Vector3 EnforceSpineBendLimit(Vector3 headPos, Vector3 hipsPos, float maxBendDeg, Vector3 playerUp)
        {
            if (maxBendDeg <= 0f)
            {
                return hipsPos;
            }

            Vector3 diff = hipsPos - headPos;
            if (diff.sqrMagnitude < k_MinMag)
            {
                return hipsPos;
            }

            Vector3 up = playerUp;

            // Decompose head→hips into a downward drop (along -up) and a horizontal lean.
            float down = Vector3.Dot(diff, -up);  // signed: hips are below the head when > 0
            Vector3 lateral = diff + up * down;   // diff minus the (-up * down) vertical part
            float lateralLen = lateral.magnitude;
            float coneTan = Mathf.Tan(Mathf.Min(maxBendDeg, 89.9f) * Mathf.Deg2Rad);
            float minDown = lateralLen / Mathf.Max(coneTan, k_MinMag);
            if (down >= minDown)
            {
                return hipsPos;
            }

            return headPos - up * minDown + lateral;
        }
        public static Vector3 AntiContortionist(Vector3 headPos, Quaternion headRot, Vector3 hipsPos, Quaternion hipsRot, float restDistance)
        {
            Vector3 headFwd = headRot * Vector3.forward;
            Vector3 hipsFwd = hipsRot * Vector3.forward;
            float facingSimilarity = Vector3.Dot(headFwd, hipsFwd);

            float minDistFactor = Mathf.Lerp(0.2f, 0.85f, Mathf.Clamp01((facingSimilarity + 1f) * 0.5f));
            float minDist = restDistance * minDistFactor;

            Vector3 diff = hipsPos - headPos;
            float currentDist = diff.magnitude;

            if (currentDist < minDist && currentDist > k_Epsilon)
            {
                return headPos + diff * (minDist / currentDist);
            }
            return hipsPos;
        }
        public static Vector3 MitigateSpineBuckling(Vector3 headPos, Quaternion hipsRot, Vector3 hipsPos, float restDistance, Vector3 playerUp)
        {
            Vector3 diff = hipsPos - headPos;
            float currentDist = diff.magnitude;

            if (currentDist >= restDistance || currentDist < k_Epsilon)
                return hipsPos;

            Vector3 hipsUp = hipsRot * Vector3.up;
            Vector3 spineDir = (headPos - hipsPos).normalized;

            float tension = Mathf.Clamp01(Vector3.Dot(hipsUp, spineDir));
            float compression = 1f - (currentDist / restDistance);

            float pushAmount = compression * tension * restDistance * 0.5f;
            return hipsPos - playerUp * pushAmount;
        }
    }
}
