using UnityEngine;
namespace Basis.IK
{
    public static class BasisEeriePlanner
    {
        public const float stationaryDelaySeconds = 0.15f, footIKBlendInSpeed = 20f, footIKBlendOutSpeed = 15f;
        public const bool locomotionFootIK = false, footRotationFromSim = true;
        public static void Bind(ref BasisEerieMovement job)
        {
            ref BasisEeriePlan p = ref job.plan;
            p.hasHips = job.handleHips.IsBound;
            p.hasSpine = job.handleSpine.IsBound;
            p.hasChest = job.handleChest.IsBound;
            p.hasUpperChest = job.handleUpperChest.IsBound;
            p.hasNeck = job.handleNeck.IsBound;
            p.hasHead = job.handleHead.IsBound;
            int chainLen = job.chainHeadToSpine.IsCreated ? job.chainHeadToSpine.Length : 0;
            p.hasSpineChain = chainLen >= 3;
            for (int i = 0; i < chainLen; i++)
            {
                if (!job.chainHeadToSpine[i].IsBound)
                {
                    p.hasSpineChain = false;
                }
            }
            p.chestIdx = p.hasSpineChain ? job.chainChestIdx : -1;
            p.hasChestJoint = p.hasSpineChain && chainLen > 3 && p.chestIdx >= 1 && p.chestIdx < chainLen - 2;
            p.hasSpineRestFrames = p.hasSpineChain && job.chainSpineRestFrames.IsCreated && job.chainSpineRestFrames.Length >= chainLen;
            p.hasSpineBend = p.hasHips && p.hasChest && (p.hasSpine || p.hasUpperChest);
            p.hasBodyRight = job.handleLeftUpperArm.IsBound && job.handleRightUpperArm.IsBound;
            p.torsoFrom = p.hasChest ? job.handleChest : p.hasSpine ? job.handleSpine : job.handleHips;
            p.torsoTo = p.hasNeck ? job.handleNeck : job.handleHead;
            p.hasTorso = p.torsoFrom.IsBound && p.torsoTo.IsBound;
            p.legFrameTo = p.hasChest ? job.handleChest : p.hasSpine ? job.handleSpine : p.hasNeck ? job.handleNeck : job.handleHead;
            p.hasLegFrame = job.handleLeftUpperLeg.IsBound && job.handleRightUpperLeg.IsBound && p.hasHips && p.legFrameTo.IsBound;
            p.chestRef = p.hasUpperChest ? job.handleUpperChest : job.handleChest;
            p.hasChestRef = p.chestRef.IsBound;
            p.hasLeftShoulder = job.handleLeftShoulder.IsBound;
            p.hasRightShoulder = job.handleRightShoulder.IsBound;
            p.hasLeftToe = job.handleLeftToe.IsBound;
            p.hasRightToe = job.handleRightToe.IsBound;
            p.hasChestSpring = job.chestSpring.IsCreated && job.chestSpring.Length >= 1;
            p.hasSwingState = job.swingContinuity.IsCreated && job.swingContinuity.Length >= BasisEerieMovement.swingCount;
            p.hasArmState = job.armState.IsCreated && job.armState.Length >= BasisEerieMovement.swingCount;
            p.hasLegState = job.legState.IsCreated && job.legState.Length >= 2;
            p.hasLegDiagnostics = job.legDiagnostics.IsCreated && job.legDiagnostics.Length >= 2;
            p.boundSlots = 0;
            for (int i = 0; i < BasisEerieMovement.Count; i++)
            {
                if (job.SlotHandle(i).IsBound)
                {
                    p.boundSlots |= 1u << i;
                }
            }
            p.leftArm.has = job.handleLeftUpperArm.IsBound && job.handleLeftLowerArm.IsBound && job.handleLeftHand.IsBound;
            p.leftArm.hasUpperTwist = job.handleLeftUpperArmTwist.IsBound && job.handleLeftUpperArm.IsBound && job.handleLeftLowerArm.IsBound;
            p.leftArm.hasLowerTwist = job.handleLeftLowerArmTwist.IsBound && job.handleLeftLowerArm.IsBound && job.handleLeftHand.IsBound;
            p.rightArm.has = job.handleRightUpperArm.IsBound && job.handleRightLowerArm.IsBound && job.handleRightHand.IsBound;
            p.rightArm.hasUpperTwist = job.handleRightUpperArmTwist.IsBound && job.handleRightUpperArm.IsBound && job.handleRightLowerArm.IsBound;
            p.rightArm.hasLowerTwist = job.handleRightLowerArmTwist.IsBound && job.handleRightLowerArm.IsBound && job.handleRightHand.IsBound;
            p.leftLeg.has = job.handleLeftUpperLeg.IsBound && job.handleLeftLowerLeg.IsBound && job.handleLeftFoot.IsBound;
            p.rightLeg.has = job.handleRightUpperLeg.IsBound && job.handleRightLowerLeg.IsBound && job.handleRightFoot.IsBound;
        }
        public static void FootIK(ref BasisEerieFrameFacts facts, ref float stationaryTimer, ref float leftBlend, ref float rightBlend, out bool simulate, out bool reengage)
        {
            stationaryTimer = facts.moving ? 0f : stationaryTimer + facts.deltaTime;
            bool ready = facts.footSimReady && (locomotionFootIK || stationaryTimer >= stationaryDelaySeconds) && facts.footIKSetting && !facts.prone && facts.upright;
            bool leftWant = ready && !facts.leftLegTracked, rightWant = ready && !facts.rightLegTracked;
            if (facts.leftLegTracked) leftBlend = 0f;
            if (facts.rightLegTracked) rightBlend = 0f;
            float leftPrev = leftBlend, rightPrev = rightBlend;
            leftBlend = Mathf.MoveTowards(leftBlend, leftWant ? 1f : 0f, (leftWant ? footIKBlendInSpeed : footIKBlendOutSpeed) * facts.deltaTime);
            rightBlend = Mathf.MoveTowards(rightBlend, rightWant ? 1f : 0f, (rightWant ? footIKBlendInSpeed : footIKBlendOutSpeed) * facts.deltaTime);
            facts.leftFootSim = leftBlend;
            facts.rightFootSim = rightBlend;
            reengage = facts.footSimReady && ((leftPrev < 0.001f && leftBlend >= 0.001f) || (rightPrev < 0.001f && rightBlend >= 0.001f));
            simulate = facts.footSimReady && (!facts.leftLegTracked || !facts.rightLegTracked);
        }
        public static void Frame(ref BasisEerieMovement job, in BasisEerieFrameFacts facts)
        {
            ref BasisEeriePlan p = ref job.plan;
            p.hipsTracked = facts.hipsTracked;
            p.chestTracked = facts.chestTracked;
            p.prone = facts.prone && !facts.hipsTracked;
            p.chestChain = facts.chestTracked && p.hasChest;
            p.headChain = !p.chestChain && p.hasHead;
            p.crouchOffset = !facts.chestTracked && !facts.hipsTracked && !facts.seated && !facts.prone;
            p.gaitPelvis = !facts.hipsTracked && facts.footSimReady && Mathf.Min(facts.leftFootSim, facts.rightFootSim) > 0.001f;
            p.lordosis = job.anatCervicalLordosis && p.hasNeck;
            p.spineRom = job.spineAnatomicalRom && p.hasSpineRestFrames;
            p.chestTarget = job.chestIkTarget && p.hasChestJoint && facts.chestTracked;
            p.shoulderSlide = job.anatShoulderSlide && p.hasHips && p.hasChest;
            p.leftShoulderTracked = facts.leftShoulderTracked;
            p.rightShoulderTracked = facts.rightShoulderTracked;
            p.leftShoulder = Shoulder(p.hasLeftShoulder, job.shoulderSolveEnabled, facts.leftShoulderTracked);
            p.rightShoulder = Shoulder(p.hasRightShoulder, job.shoulderSolveEnabled, facts.rightShoulderTracked);
            p.leftToeTracked = facts.leftToeTracked;
            p.rightToeTracked = facts.rightToeTracked;
            Arm(ref p.leftArm, ref job, facts.leftHandWeight, facts.leftElbowTracked, facts.leftElbowRoll);
            Arm(ref p.rightArm, ref job, facts.rightHandWeight, facts.rightElbowTracked, facts.rightElbowRoll);
            p.armSwingChestFollow = job.chestArmSwingFactor > 0f && p.hasHips && p.hasChest && (p.leftArm.weight > 0f || p.rightArm.weight > 0f);
            Leg(ref p.leftLeg, ref job, in facts, true);
            Leg(ref p.rightLeg, ref job, in facts, false);
            p.leftToeDriven = p.hasLeftToe && facts.leftToeTracked;
            p.rightToeDriven = p.hasRightToe && facts.rightToeTracked;
            p.leftToeSurface = p.hasLeftToe && !facts.leftToeTracked && p.leftLeg.target == BasisEerieSource.Sim && facts.leftToeBend;
            p.rightToeSurface = p.hasRightToe && !facts.rightToeTracked && p.rightLeg.target == BasisEerieSource.Sim && facts.rightToeBend;
            job.playerUp = Up(job.playerUp);
            job.offsetRotationHips = Unit(job.offsetRotationHips);
            job.offsetRotationHead = Unit(job.offsetRotationHead);
            job.offsetRotationChest = Unit(job.offsetRotationChest);
            job.offsetRotationLeftFoot = Unit(job.offsetRotationLeftFoot);
            job.offsetRotationRightFoot = Unit(job.offsetRotationRightFoot);
            job.offsetRotationLeftToe = Unit(job.offsetRotationLeftToe);
            job.offsetRotationRightToe = Unit(job.offsetRotationRightToe);
            job.offsetRotationLeftShoulder = Unit(job.offsetRotationLeftShoulder);
            job.offsetRotationRightShoulder = Unit(job.offsetRotationRightShoulder);
            job.offsetRotationLeftHand = Unit(job.offsetRotationLeftHand);
            job.offsetRotationRightHand = Unit(job.offsetRotationRightHand);
        }
        public static bool KneeAssistWanted(in BasisEerieFrameFacts facts, bool isLeft) => isLeft ? facts.leftFootTracked && !facts.leftKneeTracked : facts.rightFootTracked && !facts.rightKneeTracked;
        static BasisEerieShoulderMode Shoulder(bool has, bool solve, bool tracked) => !has ? BasisEerieShoulderMode.None : solve ? BasisEerieShoulderMode.Solve : tracked ? BasisEerieShoulderMode.Tracker : BasisEerieShoulderMode.None;
        static void Arm(ref BasisEerieArmPlan arm, ref BasisEerieMovement job, float weight, bool elbowTracked, bool roll)
        {
            arm.weight = weight;
            arm.solve = weight > 0f && arm.has;
            arm.trackerHint = elbowTracked;
            arm.hintRoll = elbowTracked && roll;
            arm.upperTwist = arm.hasUpperTwist && job.upperArmTwistFraction > 0f;
            arm.lowerTwist = arm.hasLowerTwist && job.lowerArmTwistFraction > 0f;
            arm.elbowProtect = job.collisionsEnabled && job.protectElbow && job.plan.hasChest && job.plan.hasNeck && (!elbowTracked || job.collideTrackedElbow);
            arm.elbowDrag = job.elbowDragEnabled;
            arm.poleAnchor = elbowTracked && job.plan.hasArmState;
        }
        static void Leg(ref BasisEerieLegPlan leg, ref BasisEerieMovement job, in BasisEerieFrameFacts facts, bool isLeft)
        {
            bool tracked = isLeft ? facts.leftLegTracked : facts.rightLegTracked, kneeTracked = isLeft ? facts.leftKneeTracked : facts.rightKneeTracked;
            bool simRotation = isLeft ? facts.leftSimFootRotation : facts.rightSimFootRotation, roll = isLeft ? facts.leftKneeRoll : facts.rightKneeRoll;
            bool bendNormal = isLeft ? facts.leftKneeBendNormal : facts.rightKneeBendNormal;
            float sim = isLeft ? facts.leftFootSim : facts.rightFootSim, assist = isLeft ? facts.leftKneeAssist : facts.rightKneeAssist;
            bool simActive = facts.footSimReady && sim > 0.001f;
            leg.tracked = tracked;
            leg.kneeTracked = kneeTracked;
            leg.target = tracked ? BasisEerieSource.Tracker : simActive ? BasisEerieSource.Sim : BasisEerieSource.None;
            leg.weight = tracked ? 1f : simActive ? sim : 0f;
            leg.solve = leg.weight > 0f && leg.has;
            leg.preserveTip = leg.target == BasisEerieSource.Sim && !simRotation;
            leg.hint = kneeTracked ? BasisEerieSource.Tracker : simActive ? BasisEerieSource.Sim : assist > 0f ? BasisEerieSource.Assist : BasisEerieSource.None;
            leg.hintWeight = kneeTracked ? 1f : simActive ? sim : assist > 0f ? assist : 0f;
            leg.modelHint = job.plan.hasLegFrame && (leg.hint == BasisEerieSource.None || leg.hint == BasisEerieSource.Sim);
            leg.hintRoll = kneeTracked && roll;
            leg.trackerBendNormal = facts.trackerBendNormal && kneeTracked && bendNormal;
            leg.swivel = job.legSwivelSmoothing && job.plan.hasLegState && job.plan.hasHips;
            leg.swivelTracked = kneeTracked || tracked;
        }
        static Quaternion Unit(Quaternion q) => q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > BasisEerieMovement.sqrEpsilon ? q : Quaternion.identity;
        static Vector3 Up(Vector3 up)
        {
            float m = up.sqrMagnitude;
            if (m < BasisEerieMovement.sqrEpsilon)
            {
                return Vector3.up;
            }
            return Mathf.Abs(m - 1f) > 1e-6f ? up / Mathf.Sqrt(m) : up;
        }
    }
}
