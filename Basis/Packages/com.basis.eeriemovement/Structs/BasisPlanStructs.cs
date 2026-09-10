namespace Basis.IK
{
    public struct BasisEerieFrameFacts
    {
        public bool hipsTracked, chestTracked, prone, seated, upright, moving, footIKSetting, trackerBendNormal, footSimReady;
        public bool leftLegTracked, rightLegTracked, leftFootTracked, rightFootTracked, leftKneeTracked, rightKneeTracked;
        public bool leftElbowTracked, rightElbowTracked, leftShoulderTracked, rightShoulderTracked, leftToeTracked, rightToeTracked;
        public bool leftSimFootRotation, rightSimFootRotation, leftToeBend, rightToeBend, leftElbowRoll, rightElbowRoll, leftKneeRoll, rightKneeRoll;
        public bool leftKneeBendNormal, rightKneeBendNormal;
        public float deltaTime, leftHandWeight, rightHandWeight, leftFootSim, rightFootSim, leftKneeAssist, rightKneeAssist;
    }
    public struct BasisEerieArmPlan
    {
        public bool has, hasUpperTwist, hasLowerTwist, solve, trackerHint, hintRoll, upperTwist, lowerTwist, elbowProtect;
        public float weight;
    }
    public struct BasisEerieLegPlan
    {
        public bool has, solve, tracked, kneeTracked, preserveTip, modelHint, hintRoll, trackerBendNormal, swivel, swivelTracked;
        public float weight, hintWeight;
        public BasisEerieSource target, hint;
    }
    public struct BasisEeriePlan
    {
        public bool hasHips, hasSpine, hasChest, hasUpperChest, hasNeck, hasHead, hasSpineChain, hasChestJoint, hasSpineRestFrames, hasSpineBend;
        public bool hasBodyRight, hasTorso, hasLegFrame, hasChestRef, hasLeftShoulder, hasRightShoulder, hasLeftToe, hasRightToe;
        public bool hasChestSpring, hasArmState, hasLegState, hasLegDiagnostics;
        public int chestIdx;
        public uint boundSlots;
        public BasisBoneHandle torsoFrom, torsoTo, legFrameTo, chestRef;
        public bool prone, hipsTracked, chestTracked, chestChain, headChain, crouchOffset, gaitPelvis, lordosis, spineRom, chestTarget;
        public bool armSwingChestFollow, leftShoulderTracked, rightShoulderTracked, leftToeTracked, rightToeTracked, leftToeDriven, rightToeDriven;
        public bool leftToeSurface, rightToeSurface;
        public BasisEerieShoulderMode leftShoulder, rightShoulder;
        public BasisEerieArmPlan leftArm, rightArm;
        public BasisEerieLegPlan leftLeg, rightLeg;
    }
}
