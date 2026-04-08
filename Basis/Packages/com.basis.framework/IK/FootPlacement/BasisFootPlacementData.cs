using Unity.Mathematics;

public struct BasisFootNativeState
{
    public int sideSign;
    public float thighLen, shinLen, legLength;

    // Phase: 0 = Planted, 1 = Stepping
    public int phase;
    public float3 plantedPos;
    public quaternion plantedRot;
    public float3 stepStartPos, stepTargetPos;
    public quaternion stepTargetRot;
    public float stepTimer, stepDur;

    public float3 idealPos, filteredNormal;
    public float3 currentPos;
    public quaternion currentRot;
    public float3 kneeHint;

    // Step trigger output (read by main thread after job)
    public bool wantsStep;
    public float3 predictedTargetXZ;
}

public struct BasisFootSimState
{
    public float3 prevHeadPos;
    public float prevHeadYaw;
    public float3 smoothedVelocity;
    public float3 smoothedBodyFwd;
    public float3 smoothedBodyRight;
}

public struct BasisFootSimInput
{
    public float dt;
    public float3 headPos;
    public float3 hipsPos;
    public quaternion hipsRot;
    public quaternion chestRot;
    public quaternion headRot;
    public float3 avatarForward;
    public float3 avatarRight;
    public bool hasChest;
    public bool groundHit;
    public float3 groundPoint;
    public float splayWhenCrouched;
    public float3 playerUp;
}

public struct BasisFootSimParams
{
    // Prediction
    public float predictionFactor;
    public float velocityBiasFactor;
    public float leadOffsetFactor;
    public float maxVelocityOffsetFraction;
    public float maxPredictionFraction;

    // Smoothing
    public float plantedLerpSpeed;
    public float rotationLerpSpeed;
    public float velocitySmoothAccel;
    public float velocitySmoothDecel;
    public float bodyFwdRateMoving;
    public float bodyFwdRateStationary;
    public float kneeHintLerpSpeed;

    // Foot rotation limits
    public float maxFootTiltDegrees;
    public float maxFootYawDegrees;

    // Step arc
    public float stepArcLiftExp;
    public float stepArcDropExp;
    public float stepHeightMinFraction;

    // Idle / Turn
    public float idleSpeedThreshold;
    public float idleBoostFraction;
    public float maxPlantedYawDegrees;

    // Side enforcement
    public float idealSideEnforceFraction;
    public float stepTargetSideFraction;
    public float footSideEnforceFraction;

    // Vertical correction
    public float maxVerticalDriftFraction;

    // Knee
    public float kneeForwardPushFraction;
    public float kneeMinSideFraction;

    // Body forward weights
    public float bodyFwdHipsWeight;
    public float bodyFwdChestWeight;
    public float bodyFwdHeadWeight;

    // Hip bob
    public float hipBobFraction;

    // Calibrated measurements
    public float stanceWidth;
    public float hipToFoot;
    public float leftLegLen, rightLegLen;
    public float leftThighLen, leftShinLen;
    public float rightThighLen, rightShinLen;
    public float footLength;
    public float ankleHeight;

    // Derived step parameters
    public float stepTriggerDist;
    public float strideScale;
    public float stepHeightCalc;
    public float stepDurSlow;
    public float stepDurFast;
    public float raySphereRadius;
    public float footHeightOffset;
    public float fastSpeedRef;
    public float rayCastRange;
}

public struct BasisFootSimOutput
{
    public float hipBob;
}
