using System.Runtime.CompilerServices;
using Basis.Scripts.Common;
using Unity.Collections;
using UnityEngine;
namespace Basis.IK
{
    /// <summary>
    /// Shoulders, arms and hands: the girdle pre-solve, two-bone arm IK with torso collision, and elbow continuity.
    /// </summary>
    public partial struct BasisEerieMovement
    {
        // Shoulder pre-solve: elevate/protract the girdle off the hand targets before arm IK, so the
        // arms hang off a shoulder that has already moved.
        void SolveShoulderPass(BasisPoseStream stream)
        {
            if (shoulderSolveEnabled)
            {
                SolveShoulder(stream, handleLeftShoulder, enabledLeftShoulder, targetPositionLeftHand, hintPositionLeftHand, hintWeightLeftHand, tposeLeftShoulderLocalDir, tposeLeftShoulderRot, tposeChestRot, tposeShoulderToHandLeft, tposeClavicleLenLeft, tposeShoulderToElbowLeft, true);
                SolveShoulder(stream, handleRightShoulder, enabledRightShoulder, targetPositionRightHand, hintPositionRightHand, hintWeightRightHand, tposeRightShoulderLocalDir, tposeRightShoulderRot, tposeChestRot, tposeShoulderToHandRight, tposeClavicleLenRight, tposeShoulderToElbowRight, false);
            }
            else
            {
                ApplyRotation(stream, enabledLeftShoulder, handleLeftShoulder, targetRotationLeftShoulder, targetOffsetLeftShoulder);
                ApplyRotation(stream, enabledRightShoulder, handleRightShoulder, targetRotationRightShoulder, targetOffsetRightShoulder);
            }
            if (anatShoulderSlide)
            {
                ApplyShoulderSlide(stream);
            }
        }

        // Two-bone arm IK with torso collision and elbow protection, then the swing rate-limit and the
        // twist-bone distribution that both read the pose the IK just produced.
        void SolveArmPass(BasisPoseStream stream)
        {
            // bodyRight (shoulder->shoulder) orients the torso's elliptical collision cross-section;
            // shared by both arms so it is computed once here.
            Vector3 bodyRight = (handleLeftUpperArm.IsValid(stream) && handleRightUpperArm.IsValid(stream))
                ? handleRightUpperArm.GetPosition(stream) - handleLeftUpperArm.GetPosition(stream)
                : Vector3.zero;
            SolveHand(stream, enabledLeftHand, handleLeftUpperArm, handleLeftLowerArm, handleLeftHand, targetPositionLeftHand, targetRotationLeftHand, hintPositionLeftHand, hintRotationLeftHand, hintWeightLeftHand, targetOffsetLeftHand, handleChest, handleNeck, chestRadius, collisionSkin, collisionsEnabled, handRadius, handSkin, protectElbow, collideTrackedElbow, bodyRight, k_SwingLeftElbow);
            SolveHand(stream, enabledRightHand, handleRightUpperArm, handleRightLowerArm, handleRightHand, targetPositionRightHand, targetRotationRightHand, hintPositionRightHand, hintRotationRightHand, hintWeightRightHand, targetOffsetRightHand, handleChest, handleNeck, chestRadius, collisionSkin, collisionsEnabled, handRadius, handSkin, protectElbow, collideTrackedElbow, bodyRight, k_SwingRightElbow);

            // Arm pop continuity: rate-limit the elbow swing so a torso-collision change eases in
            // instead of popping in one frame. Runs before arm twist (which reads the arm pose).
            float swingRate = swingSmoothRateDeg;
            float swingDt = stream.deltaTime;
            if (enabledLeftHand > 0f)
            {
                ApplySwingContinuity(stream, k_SwingLeftElbow, handleLeftUpperArm, handleLeftLowerArm, handleLeftHand, targetPositionLeftHand, swingRate, swingDt);
            }

            if (enabledRightHand > 0f)
            {
                ApplySwingContinuity(stream, k_SwingRightElbow, handleRightUpperArm, handleRightLowerArm, handleRightHand, targetPositionRightHand, swingRate, swingDt);
            }

            // Arm twist distribution: spread wrist/elbow roll along the optional twist bones
            // so the mesh doesn't pinch at the wrist when the hand rotates.
            float lowerTwist = lowerArmTwistFraction;
            float upperTwist = upperArmTwistFraction;
            SolveArmTwist(stream, handleLeftLowerArm, handleLeftHand, handleLeftLowerArmTwist, lowerTwist);
            SolveArmTwist(stream, handleRightLowerArm, handleRightHand, handleRightLowerArmTwist, lowerTwist);
            SolveArmTwist(stream, handleLeftUpperArm, handleLeftLowerArm, handleLeftUpperArmTwist, upperTwist);
            SolveArmTwist(stream, handleRightUpperArm, handleRightLowerArm, handleRightUpperArmTwist, upperTwist);
        }

        void ApplyShoulderSlide(BasisPoseStream stream)
        {
            if (!handleHips.IsValid(stream) || !handleChest.IsValid(stream))
            {
                return;
            }

            Quaternion hipsRot = handleHips.GetRotation(stream);
            Quaternion chestRot = handleChest.GetRotation(stream);
            Quaternion chestLocal = Quaternion.Inverse(hipsRot) * chestRot;
            float chestYaw = BasisTwistSolveCore.SignedTwistAngleDeg(chestLocal, Vector3.up);

            float excess = Mathf.Abs(chestYaw) - shoulderSlideStartDeg;
            if (excess <= 0f)
                return;

            float counterYaw = -Mathf.Sign(chestYaw) * Mathf.Min(excess * shoulderSlideFraction, shoulderSlideMaxDeg);
            ApplyShoulderYaw(stream, handleLeftShoulder, hipsRot, counterYaw);
            ApplyShoulderYaw(stream, handleRightShoulder, hipsRot, counterYaw);
        }
        void ApplyShoulderYaw(BasisPoseStream stream, BasisBoneHandle shoulder, Quaternion hipsRot, float yawDeg)
        {
            if (!shoulder.IsValid(stream))
                return;
            Quaternion delta = hipsRot * Quaternion.AngleAxis(yawDeg, Vector3.up) * Quaternion.Inverse(hipsRot);
            shoulder.SetRotation(stream, delta * shoulder.GetRotation(stream));
        }
        void ApplyArmSwingChestFollow(BasisPoseStream stream)
        {
            float factor = chestArmSwingFactor;
            if (factor <= 0f)
            {
                return;
            }

            if (!handleHips.IsValid(stream) || !handleChest.IsValid(stream))
            {
                return;
            }

            bool leftEnabled = enabledLeftHand > 0f;
            bool rightEnabled = enabledRightHand > 0f;
            if (!leftEnabled && !rightEnabled)
            {
                return;
            }

            Vector3 leftPos = leftEnabled ? targetPositionLeftHand : Vector3.zero;
            Vector3 rightPos = rightEnabled ? targetPositionRightHand : Vector3.zero;
            Vector3 handMid = leftEnabled && rightEnabled ? (leftPos + rightPos) * 0.5f : leftEnabled ? leftPos : rightPos;
            Vector3 hipsPos = handleHips.GetPosition(stream);
            Quaternion hipsAnat = handleHips.GetRotation(stream) * Quaternion.Inverse(offsetRotationHips);
            Quaternion invHipsAnat = Quaternion.Inverse(hipsAnat);
            Vector3 localMid = invHipsAnat * (handMid - hipsPos);

            float forwardDist = Mathf.Max(0.1f, Mathf.Abs(localMid.z));
            float yawDeg = Mathf.Atan2(localMid.x, forwardDist) * Mathf.Rad2Deg * factor;

            Vector3 localMidChest = invHipsAnat * (handMid - handleChest.GetPosition(stream));
            float pitchDeg = Mathf.Atan2(-localMidChest.y, forwardDist) * Mathf.Rad2Deg * factor;

            float maxDeg = chestArmSwingMaxDeg;
            if (maxDeg > 0f)
            {
                yawDeg = Mathf.Clamp(yawDeg, -maxDeg, maxDeg);
                pitchDeg = Mathf.Clamp(pitchDeg, -maxDeg, maxDeg);
            }

            Quaternion local = Quaternion.AngleAxis(yawDeg, Vector3.up) * Quaternion.AngleAxis(pitchDeg, Vector3.right);
            Quaternion deltaWorld = hipsAnat * local * invHipsAnat;

            if (handleUpperChest.IsValid(stream))
            {
                Quaternion chestPart = Quaternion.Slerp(Quaternion.identity, deltaWorld, chestFollowChestShare);
                Quaternion upperPart = Quaternion.Slerp(Quaternion.identity, deltaWorld, 1f - chestFollowChestShare);
                handleChest.SetRotation(stream, chestPart * handleChest.GetRotation(stream));
                handleUpperChest.SetRotation(stream, upperPart * handleUpperChest.GetRotation(stream));
            }
            else
            {
                handleChest.SetRotation(stream, deltaWorld * handleChest.GetRotation(stream));
            }
        }
        void SolveArmTwist(BasisPoseStream stream, BasisBoneHandle parent, BasisBoneHandle child, BasisBoneHandle twist, float fraction)
        {
            if (!twist.IsValid(stream) || fraction <= 0f)
                return;
            if (!parent.IsValid(stream) || !child.IsValid(stream))
                return;

            Vector3 parentPos = parent.GetPosition(stream);
            Vector3 childPos = child.GetPosition(stream);
            float positionFraction = BasisTwistSolveCore.SegmentPositionFraction(parentPos, childPos, twist.GetPosition(stream));

            BasisTwistSolveInput input;
            input.ParentRotation = parent.GetRotation(stream);
            input.ChildRotation = child.GetRotation(stream);
            input.ParentToChild = childPos - parentPos;
            input.Fraction = positionFraction * fraction;

            BasisTwistSolveCore.Solve(input, out BasisTwistSolveResult result);
            if (result.Apply)
            {
                twist.SetRotation(stream, result.TwistWorldRotation);
            }
        }
        public void SolveShoulder(BasisPoseStream stream, BasisBoneHandle shoulderHandle, bool hasShoulderTrackerProp, Vector3 handTargetPosProp, Vector3 hintPosProp, bool hintWeightProp, Vector3 tposeArmDir, Quaternion tposeShoulderRot, Quaternion tposeChestRot, float tposeArmLength, float tposeClavicleLen, float tposeElbowLen, bool isLeft)
        {
            if (!shoulderHandle.IsValid(stream))
            {
                return;
            }

            Quaternion trackerRot = isLeft ? targetRotationLeftShoulder : targetRotationRightShoulder;

            BasisShoulderSolveInput input;
            input.ShoulderPos = shoulderHandle.GetPosition(stream);
            input.HandTargetPos = handTargetPosProp;
            input.ElbowPos = hintPosProp;
            input.HasElbow = hintWeightProp;
            input.HasShoulderTracker = hasShoulderTrackerProp;
            input.ChestRot = handleChest.IsValid(stream) ? handleChest.GetRotation(stream) : Quaternion.identity;
            input.TposeChestRot = tposeChestRot;
            input.TposeShoulderRot = tposeShoulderRot;
            input.TposeArmDirWorld = tposeArmDir;
            input.TposeArmLength = tposeArmLength;
            input.TposeClavicleLength = tposeClavicleLen;
            input.TposeElbowLength = tposeElbowLen;
            input.ShrugEnabled = shoulderShrugEnabled;
            input.ElevationFactor = shoulderElevationFactor;
            input.ProtractionFactor = shoulderProtractionFactor;
            input.CoupleRatio = shoulderCoupleRatio;
            input.MaxShoulderDeg = shoulderMaxDeg;
            input.TrackerFinal = trackerRot * (isLeft ? targetOffsetLeftShoulder : targetOffsetRightShoulder);
            input.IsLeft = isLeft;

            BasisShoulderSolveCore.Solve(input, out BasisShoulderSolveResult result);
            if (result.Apply)
            {
                shoulderHandle.SetRotation(stream, result.ShoulderRotation);
            }
        }
        public void SolveTwoBoneIKArms(BasisPoseStream stream, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, BasisAffineTransform target, BasisAffineTransform hint, bool hintWeight, bool hintIsTracker, Quaternion targetOffset)
        {
            BasisArmSolveInput input = default;
            root.GetPositionAndRotation(stream, out Vector3 shoulderPos, out Quaternion shoulderRot);
            mid.GetPositionAndRotation(stream, out Vector3 elbowPos, out Quaternion elbowRot);
            tip.GetPositionAndRotation(stream, out Vector3 handPos, out Quaternion handRot);
            input.Shoulder = shoulderPos;
            input.Elbow = elbowPos;
            input.Hand = handPos;
            input.RootRotation = shoulderRot;
            input.MidRotation = elbowRot;
            input.TargetPosition = target.translation;
            input.TargetRotation = target.rotation;
            input.HintPosition = hint.translation;
            input.HintWeight = hintWeight;
            input.TargetOffset = targetOffset;
            input.PlayerUp = playerUp;
            input.HintIsTracker = hintIsTracker;
            input.HintMaxStepDeg = float.MaxValue;
            input.TipRotation = handRot;
            input.HintRotation = hintIsTracker ? hint.rotation : default;

            BasisArmSolveCore.Solve(input, out BasisArmSolveResult result);

            mid.SetRotation(stream, result.MidDelta * mid.GetRotation(stream));
            root.SetRotation(stream, result.RootDelta * root.GetRotation(stream));
            root.SetRotation(stream, result.HintDelta * root.GetRotation(stream));
            mid.SetRotation(stream, result.MidPostRoll * mid.GetRotation(stream));
            tip.SetRotation(stream, result.TipRotation);
        }
        /// <summary>
        /// The ARM's body frame, live, from BONE POSITIONS: shoulder line for right, chest->neck for up.
        ///
        /// From POSITIONS, not from the chest bone's ROTATION, and that is the whole reason it transfers. A
        /// bone's local axes are a rig convention, so a frame taken from rotations is fitted to one skeleton and
        /// no other. It also deletes the old frame's entire problem: ArmBendFrame had to strip the chest's YAW
        /// (or head-gaze chest twist swept the lookup and flipped the elbow pole) and then spring-smooth the hips
        /// to stop hip sway wobbling the derived elbow. A position frame has no yaw to strip -- the shoulder line
        /// IS the yaw -- so both the twist-extraction and the hip-frame spring go away.
        /// </summary>
        BasisSwivelFrame BuildArmFrame(BasisPoseStream stream)
        {
            if (!handleLeftUpperArm.IsValid(stream) || !handleRightUpperArm.IsValid(stream)
                || !handleChest.IsValid(stream) || !handleNeck.IsValid(stream))
            {
                return default;   // Valid = false; the caller leaves the arm on the solver's own fallback pole
            }

            return BasisSwivelHintCore.BuildFrame(
                handleLeftUpperArm.GetPosition(stream), handleRightUpperArm.GetPosition(stream),
                handleChest.GetPosition(stream), handleNeck.GetPosition(stream));
        }
        public static void SwingElbowAroundAC(BasisPoseStream stream, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, Vector3 desiredB)
        {
            Vector3 A = root.GetPosition(stream);
            Vector3 C = tip.GetPosition(stream);
            Vector3 B = mid.GetPosition(stream);

            Vector3 AC = C - A;
            float acSqr = Vector3.Dot(AC, AC);
            if (acSqr <= k_SqrEpsilon) return;

            Vector3 n = AC / Mathf.Sqrt(acSqr);
            Vector3 v1 = B - A; v1 -= n * Vector3.Dot(v1, n);
            Vector3 v2 = desiredB - A; v2 -= n * Vector3.Dot(v2, n);

            float v1Sqr = Vector3.Dot(v1, v1);
            float v2Sqr = Vector3.Dot(v2, v2);
            if (v1Sqr <= k_SqrEpsilon || v2Sqr <= k_SqrEpsilon) return;

            v1 /= Mathf.Sqrt(v1Sqr);
            v2 /= Mathf.Sqrt(v2Sqr);

            float dot = Mathf.Clamp(Vector3.Dot(v1, v2), -1f, 1f);
            float ang = Mathf.Acos(dot);
            Vector3 cross = Vector3.Cross(v1, v2);
            float dir = Mathf.Sign(Vector3.Dot(cross, n));
            Quaternion swing = Quaternion.AngleAxis(ang * dir * Mathf.Rad2Deg, n);

            root.SetRotation(stream, swing * root.GetRotation(stream));
        }
        void ApplySwingContinuity(BasisPoseStream stream, int slot, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, Vector3 targetPos, float rateDegPerSec, float dt)
        {
            if (!swingContinuityInit.IsCreated || !root.IsValid(stream) || !mid.IsValid(stream) || !tip.IsValid(stream))
            {
                return;
            }

            Vector3 a = root.GetPosition(stream);
            Vector3 c = tip.GetPosition(stream);
            Vector3 b = mid.GetPosition(stream);

            BasisSwingContinuityState state;
            state.LastDir = swingLastDir[slot];
            state.LastAxis = swingLastAxis[slot];
            state.LastTarget = swingLastTarget[slot];
            state.SmoothState = swingSmoothState[slot];
            state.Seeded = swingContinuityInit[slot] != 0;
            int collided = swingCollided.IsCreated ? swingCollided[slot] : 0;

            BasisSwingContinuityCore.Step(state, a, b, c, targetPos, collided, rateDegPerSec, dt, out BasisSwingContinuityResult r);
            if (!r.Valid)
            {
                return;
            }

            if (r.ApplySwing)
            {
                Quaternion preservedHandRot = tip.GetRotation(stream);
                SwingElbowAroundAC(stream, root, mid, tip, a + r.NewDir);
                tip.SetPosition(stream, c);
                tip.SetRotation(stream, preservedHandRot);
            }

            swingLastDir[slot] = r.State.LastDir;
            swingLastAxis[slot] = r.State.LastAxis;
            swingLastTarget[slot] = r.State.LastTarget;
            swingSmoothState[slot] = r.State.SmoothState;
            swingContinuityInit[slot] = 1;
        }
        public void SolveHand(BasisPoseStream stream, float enabledProp, BasisBoneHandle root, BasisBoneHandle mid, BasisBoneHandle tip, Vector3 targetPosProp, Quaternion targetRotProp, Vector3 hintPosProp, Quaternion hintRotProp, bool hintWeightProp, Quaternion targetOffset, BasisBoneHandle chestStart, BasisBoneHandle chestEnd, float chestRadius, float collisionSkin, bool collisionsEnabled, float handRadius, float handSkin, bool protectElbow, bool collideTrackedElbow, Vector3 bodyRight, int swingSlot)
        {
            // Written `!(w > 0)` so a NaN weight takes the reject branch rather than solving on garbage.
            float weight = enabledProp;
            if (!(weight > 0f))
            {
                return;
            }
            if (!(root.IsValid(stream) && mid.IsValid(stream) && tip.IsValid(stream)))
            {
                return;
            }
            Quaternion origRootRot = root.GetRotation(stream);
            Quaternion origMidRot = mid.GetRotation(stream);
            Quaternion origTipRot = tip.GetRotation(stream);
            // Read inputs
            Vector3 tgtPos = targetPosProp;
            Quaternion tgtRot = targetRotProp;
            Vector3 hintPos = hintPosProp;
            Quaternion hintRot = hintRotProp;
            var target = new BasisAffineTransform(tgtPos, tgtRot);
            var hint = new BasisAffineTransform(hintPos, hintRot);
            bool hasHint = hintWeightProp;
            bool usedModel = false;

            if (!hasHint)
            {
                BasisSwivelFrame frame = BuildArmFrame(stream);

                Vector3 shoulderPos = root.GetPosition(stream);
                float upperLen = (mid.GetPosition(stream) - shoulderPos).magnitude;
                float lowerLen = (tip.GetPosition(stream) - mid.GetPosition(stream)).magnitude;
                float armLen = upperLen + lowerLen;
                bool isLeft = swingSlot == k_SwingLeftElbow;
                if (BasisSwivelHintCore.ArmHint(frame, shoulderPos, tgtPos, armLen, isLeft,
                                                out Vector3 modelHint, out _, useNeuralPole))
                {
                    Vector3 curAxisV = tgtPos - shoulderPos;
                    Vector3 rawBendV = modelHint - shoulderPos;
                    float axLen = curAxisV.magnitude;
                    float rbLen = rawBendV.magnitude;
                    if (axLen > 1e-5f && rbLen > 1e-5f)
                    {
                        Vector3 curAxis = curAxisV / axLen;
                        Vector3 rawBend = rawBendV / rbLen;
                        bool seeded = swingHintInit[swingSlot] != 0;
                        Vector3 cappedBend = seeded
                            ? (Vector3)BasisElbowSwingCapCore.Apply(swingHintBend[swingSlot], swingHintAxis[swingSlot],
                                                                    curAxis, rawBend, BasisElbowSwingCapCore.MaxGain)
                            : rawBend;
                        swingHintBend[swingSlot] = cappedBend;
                        swingHintAxis[swingSlot] = curAxis;
                        Quaternion bodyRot = handleHips.IsValid(stream) ? handleHips.GetRotation(stream) : Quaternion.identity;

                        Vector3 outBend = cappedBend;
                        if (elbowDragEnabled && seeded)
                        {
                            Quaternion bodyDelta = bodyRot * Quaternion.Inverse(swingHintBodyRot[swingSlot]);
                            outBend = (Vector3)BasisElbowDragCore.Apply(swingHintDrag[swingSlot], bodyDelta, curAxis, cappedBend,
                                                                       BasisElbowDragCore.Alpha(elbowDragHz, stream.deltaTime));
                        }
                        swingHintDrag[swingSlot] = outBend;
                        swingHintBodyRot[swingSlot] = bodyRot;
                        swingHintInit[swingSlot] = 1;
                        modelHint = shoulderPos + 0.5f * armLen * outBend;
                    }

                    hint = new BasisAffineTransform(modelHint, hintRot);
                    hasHint = true;
                    usedModel = true;
                }
            }
            if (!usedModel)
            {
                swingHintInit[swingSlot] = 0;
            }
            SolveTwoBoneIKArms(stream, root, mid, tip, target, hint, hasHint, hasHint && !usedModel, targetOffset);
            int collisionState = 0;
            bool doCollisions = collisionsEnabled && chestStart.IsValid(stream) && chestEnd.IsValid(stream);
            bool elbowTrackerForced = hasHint && !usedModel;
            if (doCollisions && protectElbow && (!elbowTrackerForced || collideTrackedElbow))
            {
                BasisElbowProtectInput epi = default;
                epi.Shoulder = root.GetPosition(stream);
                epi.Elbow = mid.GetPosition(stream);
                epi.Hand = tip.GetPosition(stream);
                epi.HasHips = handleHips.IsValid(stream);
                epi.HasSpine = handleSpine.IsValid(stream);
                epi.HipsPos = epi.HasHips ? handleHips.GetPosition(stream) : Vector3.zero;
                epi.SpinePos = epi.HasSpine ? handleSpine.GetPosition(stream) : Vector3.zero;
                epi.ChestPos = chestStart.GetPosition(stream);
                epi.NeckPos = chestEnd.GetPosition(stream);
                epi.ChestRadiusBase = chestRadius;
                epi.CollisionSkin = collisionSkin;
                epi.HandRadius = handRadius;
                epi.HandSkin = handSkin;
                epi.PlayerUp = playerUp;
                epi.BodyRight = bodyRight;

                BasisElbowProtectCore.Solve(epi, out BasisElbowProtectResult epr);
                if (epr.Engaged)
                {
                    tip.GetPositionAndRotation(stream, out Vector3 preservedHandPos, out Quaternion preservedHandRot);
                    SwingElbowAroundAC(stream, root, mid, tip, epr.DesiredElbow);
                    tip.SetPosition(stream, preservedHandPos);
                    tip.SetRotation(stream, preservedHandRot);
                }
                collisionState = epr.CollisionState;
            }

            if (swingCollided.IsCreated)
            {
                swingCollided[swingSlot] = collisionState;
            }

            if (weight < 1f)
            {
                root.SetRotation(stream, Quaternion.Slerp(origRootRot, root.GetRotation(stream), weight));
                mid.SetRotation(stream, Quaternion.Slerp(origMidRot, mid.GetRotation(stream), weight));
                tip.SetRotation(stream, Quaternion.Slerp(origTipRot, tip.GetRotation(stream), weight));
            }
        }
    }
}
