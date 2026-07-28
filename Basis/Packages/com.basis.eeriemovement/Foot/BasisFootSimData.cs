using Unity.Mathematics;

public struct BasisFootNativeState
{
    public int sideSign;
    public float thighLen, shinLen, legLength;

    // Phase: 0 = Planted, 1 = Stepping
    public int phase;
    public float3 plantedPos;
    public quaternion plantedRot;
    public float3 plantedBodyFwd;   // body forward at plant time — the yaw trigger's reference
    public float3 stepStartPos, stepTargetPos;
    /// <summary>Foot rotation at the instant this step began. The swing blends FROM here, exactly as the
    /// position blends from stepStartPos -- see BasisFootSimulateJob's swing branch for why that matters.</summary>
    public quaternion stepStartRot;
    public float stepTimer, stepDur;
    /// <summary>Floor applied to this step's arc-height strideFrac, frozen at commit alongside stepDur.
    /// A step taken while the body is TURNING travels almost no ground -- a 20 deg re-plant only orbits the
    /// foot ~4 cm -- so the "a short shuffle barely lifts" scaling reads it as a drift correction and collapses
    /// the arc to ~0.46x. But a turn step is a real step and a human clears the floor for it. Frozen (not read
    /// live from the yaw rate) because dynamicHeight must stay CONSTANT across the swing: stepStartPos and
    /// stepTargetPos are both frozen, so a live term would move the foot's peak height mid-flight and pop it.
    /// 0 = no floor = the pre-existing behaviour, which is what the sweep/mocap mirrors leave it at.</summary>
    public float stepArcScale;
    public float plantedTime;       // seconds since this foot landed; gates the double-support window
    /// <summary>How committed THIS step is, 0 = deliberate .. 1 = full speed. Published by the sim job at the
    /// instant the step is requested and consumed by BasisLocalFootDriver.FinalizeStep, which used to re-derive
    /// urgency from sim state -- one fewer mirror to drift. Carries the drift term the job's global urgencyT
    /// cannot see: how far past its trigger this particular foot is.</summary>
    public float stepUrgency;
    /// <summary>Foot rotation frozen at TOUCHDOWN — the dorsiflexed heel-strike pose the foot-flat roll-down
    /// blends FROM, mirroring what stepStartRot does for the swing. Deliberately NOT round-tripped through
    /// BasisFootState: a rebuild (re-engage/teleport) seeds plantedTime to SettledPlantedTime, which is past
    /// the roll-down window, so the exponential branch runs and landRot is never read. Contrast plantedTime,
    /// which DID have to survive the round-trip.</summary>
    public quaternion landRot;

    public float3 idealPos, filteredNormal;
    public float3 currentPos;
    public quaternion currentRot;
    public float3 kneeHint;

    /// <summary>Toe MTP bend, degrees, from the toe surface probe. Positive = dorsiflexion (toes UP, the foot
    /// bending over a rise); negative = plantarflexion. 0 when the toe probe found nothing to conform to, which
    /// includes overhanging an edge -- a toe with no ground under it stays where the animation put it rather than
    /// reaching into a void. Consumed by the FBIK job's toe stage, NOT by the sim job.</summary>
    public float toeBendDeg;
    /// <summary>World medio-lateral axis the toe bends about, built from the body frame rather than the toe bone's
    /// own axes -- a humanoid toe's local axes are rig-dependent, which is the exact trap that silently disabled
    /// the yaw trigger and produced the toes-up foot rotation. Zero if the probe was not run this frame.</summary>
    public float3 toeBendAxis;

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
    public float3 prevBodyFwd;          // last frame's body forward, for yaw-rate
    public float smoothedYawRateDeg;    // body turn rate (deg/s), paces stepping during turns/spins
    /// <summary>Smoothed |d(smoothedVelocity)/dt|, m/s². Drives the acceleration term of step urgency: it is
    /// non-zero at the instant a move STARTS (when speed is still ~0) and at the instant it STOPS (when speed
    /// is falling), which is exactly where a speed-only urgency reads backwards. Scale-invariant — see
    /// k_AccelUrgencyRef.</summary>
    public float smoothedAccelMag;
    public float3 prevRootFwd;          // last frame's PLAYER-ROOT forward; lets the body-fwd filter ride the root
    public bool wasAirborne;            // last frame's airborne flag, so touchdown can be detected as an EDGE
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
    public float stepHeightStrideRefFraction; // stride length (fraction of leg) at which lift reaches max

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

    // Foot bone orientation captured IN THE BODY FRAME at calibration (T-pose):
    //     footAlign = inverse(LookRotation(avatarFwd, avatarUp)) * footBone.rotation
    // A humanoid foot bone's local axes are NOT the body's -- its +Z may run down the shin or out along the
    // toes, entirely rig-dependent. So a LookRotation built from the BODY's axes cannot be assigned to the bone
    // directly; doing that is what came out toes-up and got foot rotation switched off in the first place.
    // Post-multiplying by footAlign re-expresses the bone in whatever frame we want:
    //     footWorldRot = targetFrame * footAlign
    // At rest targetFrame == the rest frame, so this returns EXACTLY the T-pose rotation -- identity by
    // construction, cannot be toes-up -- and it carries the avatar's natural toe-out along for free.
    public quaternion footAlignLeft, footAlignRight;

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
    public float3 hipSway;  // lateral COM shift TOWARD the stance leg, as a world offset (already * body-right)
    public bool airborne;   // ground is out of leg reach; planted feet ride the hips instead of the floor

    // Gait-driven pelvis rotation, as a WORLD delta to pre-multiply onto the hips rotation.
    // Axial rotation (swing-side hip carried forward) + frontal-plane list (swing-side hip dropped).
    // Identity when standing still. Only applied when there is NO hip tracker.
    public quaternion pelvisDelta;
}
