using System.Runtime.CompilerServices;
using Unity.Collections;
namespace UnityEngine.Animations.Rigging
{
    /// <summary>
    /// Full-body pass: Head + Legs + Hips + Dual Driven TR + Dual TwoBoneIK Hands (with chest/hand capsule & elbow protection).
    /// All driven via a single job.
    /// </summary>
    [System.Serializable]
    public struct BasisFullBodyData : IAnimationJobData, IBasisFullBodyData
    {
        public const int Count = 22;

        // Live target positions (Vector3) pushed every frame from the manager.
        [SyncSceneToStream, SerializeField]
        public Vector3
            TargetPosition0, TargetPosition1, TargetPosition2, TargetPosition3, TargetPosition4,
            TargetPosition5, TargetPosition6, TargetPosition7, TargetPosition8, TargetPosition9,
            TargetPosition10, TargetPosition11, TargetPosition12, TargetPosition13, TargetPosition14,
            TargetPosition15, TargetPosition16, TargetPosition17, TargetPosition18, TargetPosition19,
            TargetPosition20, TargetPosition54;

        // Live target rotations (Quaternion) — stored as Quaternion on the component; bound as Vector4 by the job.
        [SyncSceneToStream, SerializeField]
        public Quaternion
            TargetRotation0, TargetRotation1, TargetRotation2, TargetRotation3, TargetRotation4,
            TargetRotation5, TargetRotation6, TargetRotation7, TargetRotation8, TargetRotation9,
            TargetRotation10, TargetRotation11, TargetRotation12, TargetRotation13, TargetRotation14,
            TargetRotation15, TargetRotation16, TargetRotation17, TargetRotation18, TargetRotation19,
            TargetRotation20, TargetRotation54;

        // Calibration offsets (applied on top of target each frame) — final = target * offset
        [SyncSceneToStream, SerializeField]
        public Quaternion
            OffsetRotation0, OffsetRotation1, OffsetRotation2, OffsetRotation3, OffsetRotation4,
            OffsetRotation5, OffsetRotation6, OffsetRotation7, OffsetRotation8, OffsetRotation9,
            OffsetRotation10, OffsetRotation11, OffsetRotation12, OffsetRotation13, OffsetRotation14,
            OffsetRotation15, OffsetRotation16, OffsetRotation17, OffsetRotation18, OffsetRotation19,
            OffsetRotation20, OffsetRotation54;

        // Per-slot enable/weights (0..1). Allows toggling bones independently within a single job.
        [SyncSceneToStream, SerializeField]
        public bool
            Weight0, Weight1, Weight2, Weight3, Weight4,
            Weight5, Weight6, Weight7, Weight8, Weight9,
            Weight10, Weight11, Weight12, Weight13, Weight14,
            Weight15, Weight16, Weight17, Weight18, Weight19,
            Weight20, Weight54;

        // Property name helpers for binding
        public string GetTargetPositionVector3Property(int index) => index switch
        {
            0 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition0)),
            1 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition1)),
            2 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition2)),
            3 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition3)),
            4 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition4)),
            5 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition5)),
            6 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition6)),
            7 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition7)),
            8 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition8)),
            9 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition9)),
            10 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition10)),
            11 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition11)),
            12 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition12)),
            13 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition13)),
            14 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition14)),
            15 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition15)),
            16 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition16)),
            17 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition17)),
            18 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition18)),
            19 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition19)),
            20 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition20)),
            54 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition54)),
            _ => string.Empty
        };

        public string GetTargetRotationVector4Property(int index) => index switch
        {
            0 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation0)),
            1 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation1)),
            2 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation2)),
            3 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation3)),
            4 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation4)),
            5 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation5)),
            6 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation6)),
            7 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation7)),
            8 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation8)),
            9 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation9)),
            10 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation10)),
            11 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation11)),
            12 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation12)),
            13 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation13)),
            14 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation14)),
            15 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation15)),
            16 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation16)),
            17 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation17)),
            18 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation18)),
            19 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation19)),
            20 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation20)),
            54 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation54)),
            _ => string.Empty
        };

        public string GetOffsetRotationVector4Property(int index) => index switch
        {
            0 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation0)),
            1 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation1)),
            2 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation2)),
            3 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation3)),
            4 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation4)),
            5 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation5)),
            6 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation6)),
            7 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation7)),
            8 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation8)),
            9 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation9)),
            10 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation10)),
            11 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation11)),
            12 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation12)),
            13 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation13)),
            14 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation14)),
            15 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation15)),
            16 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation16)),
            17 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation17)),
            18 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation18)),
            19 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation19)),
            20 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation20)),
            54 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation54)),
            _ => string.Empty
        };

        public string GetWeightFloatProperty(int index) => index switch
        {
            0 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight0)),
            1 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight1)),
            2 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight2)),
            3 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight3)),
            4 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight4)),
            5 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight5)),
            6 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight6)),
            7 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight7)),
            8 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight8)),
            9 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight9)),
            10 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight10)),
            11 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight11)),
            12 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight12)),
            13 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight13)),
            14 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight14)),
            15 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight15)),
            16 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight16)),
            17 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight17)),
            18 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight18)),
            19 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight19)),
            20 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight20)),
            54 => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(Weight54)),
            _ => string.Empty
        };
        [SerializeField] Transform m_Hips;
        [SyncSceneToStream, SerializeField] Transform m_chest;
        [SyncSceneToStream, SerializeField] Transform m_neck;
        [SerializeField] Transform m_head;

        [SerializeField] Transform m_LeftUpperLeg;
        [SerializeField] Transform m_LeftLowerLeg;
        [SerializeField] Transform m_leftFoot;
        [SerializeField] Transform m_RightUpperLeg;
        [SerializeField] Transform m_RightLowerLeg;
        [SerializeField] Transform m_RightFoot;

        [SerializeField] Transform m_LeftToe;
        [SerializeField] Transform m_RightToe;

        [SerializeField] Transform m_leftUpperArm;
        [SerializeField] Transform m_leftLowerArm;
        [SerializeField] Transform m_leftHand;

        [SerializeField] Transform m_RightUpperArm;
        [SerializeField] Transform m_RightLowerArm;
        [SerializeField] Transform m_rightHand;

        [SerializeField] Transform m_Spine;
        [SerializeField] Transform m_UpperChest;
        [SerializeField] Transform m_LeftShoulder;
        [SerializeField] Transform m_RightShoulder;

        // Twist bones — derived bones that absorb a fraction of wrist/elbow roll for natural
        // forearm/upper-arm deformation. Optional per rig; when null, the side is skipped.
        [SerializeField] Transform m_LeftUpperArmTwist;
        [SerializeField] Transform m_LeftLowerArmTwist;
        [SerializeField] Transform m_RightUpperArmTwist;
        [SerializeField] Transform m_RightLowerArmTwist;

        // Head
        [SyncSceneToStream, SerializeField] public Vector3 PositionHead;
        [SyncSceneToStream, SerializeField] public Quaternion RotationHead;
        [SyncSceneToStream, SerializeField] public Vector3 ChestPosition;
        [SyncSceneToStream, SerializeField] public Quaternion ChestRotation;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationHead;

        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationRightToe;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationLeftToe;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationChest;

        [SyncSceneToStream, SerializeField] public Quaternion LeftShoulderRotation;
        [SyncSceneToStream, SerializeField] public Quaternion RightShoulderRotation;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationNeck;

        // Hips
        [SyncSceneToStream, SerializeField] public Vector3 PositionHips;
        [SyncSceneToStream, SerializeField] public Quaternion RotationHips;
        [SyncSceneToStream, SerializeField] public Quaternion OffsetRotationHips;

        // Left Leg
        [SyncSceneToStream, SerializeField] public Vector3 LeftFootPosition;
        [SyncSceneToStream, SerializeField] public Quaternion LeftFootRotation;
        [SyncSceneToStream, SerializeField] public Vector3 PositionLeftLowerLeg;
        [SyncSceneToStream, SerializeField] public Quaternion RotationLeftLowerLeg;
        [SyncSceneToStream, SerializeField] public Quaternion M_CalibrationLeftFootRotation;

        // Right Leg
        [SyncSceneToStream, SerializeField] public Vector3 RightFootPosition;
        [SyncSceneToStream, SerializeField] public Quaternion RightFootRotation;
        [SyncSceneToStream, SerializeField] public Vector3 PositionRightLowerLeg;
        [SyncSceneToStream, SerializeField] public Quaternion RotationRightLowerLeg;
        [SyncSceneToStream, SerializeField] public Quaternion M_CalibrationRightFootRotation;

        // Toes
        [SyncSceneToStream, SerializeField] public Vector3 OutGoingLeftToePosition;
        [SyncSceneToStream, SerializeField] public Quaternion OutGoingLeftToeRotation;
        [SyncSceneToStream, SerializeField] public Vector3 OutGoingRightToePosition;
        [SyncSceneToStream, SerializeField] public Quaternion OutGoingRightToeRotation;

        // Left Hand
        [SyncSceneToStream, SerializeField] public Vector3 PositionLeftHand;
        [SyncSceneToStream, SerializeField] public Quaternion RotationLeftHand;
        [SyncSceneToStream, SerializeField] public Vector3 LeftLowerArmPosition;
        [SyncSceneToStream, SerializeField] public Quaternion LeftLowerArmRotation;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationLeftHand;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationLeftHandHint;

        // Right Hand
        [SyncSceneToStream, SerializeField] public Vector3 PositionRightHand;
        [SyncSceneToStream, SerializeField] public Quaternion RotationRightHand;
        [SyncSceneToStream, SerializeField] public Vector3 RightLowerArmPosition;
        [SyncSceneToStream, SerializeField] public Quaternion RightLowerArmRotation;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationRightHand;

        // Misc
        [SyncSceneToStream, SerializeField] public Vector3 SpineBendNormal;
        [SyncSceneToStream, SerializeField] public Vector3 PlayerUp;

        [SyncSceneToStream, SerializeField] public Vector3 ElbowBendPrefLeft;
        [SyncSceneToStream, SerializeField] public Vector3 ElbowBendPrefRight;

        [SyncSceneToStream, SerializeField] public Vector3 KneeBendPrefLeft;
        [SyncSceneToStream, SerializeField] public Vector3 KneeBendPrefRight;

        [SyncSceneToStream, SerializeField] public float m_HandSkin;
        [SyncSceneToStream, SerializeField] public bool m_UseHandCapsule;
        [SyncSceneToStream, SerializeField, Min(0f)] public float m_HandRadius;
        [SyncSceneToStream, SerializeField, Min(0f)] public float m_ChestRadius;
        [SyncSceneToStream, SerializeField, Min(0f)] public float m_CollisionSkin;
        [SyncSceneToStream, SerializeField] bool m_CollisionsEnabled;
        [SyncSceneToStream, SerializeField] bool m_ProtectElbow;

        [SyncSceneToStream, SerializeField] bool m_HintHeadEnabled;
        [SyncSceneToStream, SerializeField] bool m_SpineIKEnabled;

        // IK Lock Mode: 0 = LockHips, 1 = LockHead, 2 = LockBoth (see BasisIKLockMode enum)
        [SyncSceneToStream, SerializeField] float m_IKLockMode;

        [SyncSceneToStream, SerializeField] public bool m_LeftToeEnabled;
        [SyncSceneToStream, SerializeField] public bool m_RightToeEnabled;

        [SyncSceneToStream, SerializeField] float m_LeftLowerLegEnabled;
        [SyncSceneToStream, SerializeField] float m_RightLowerLegEnabled;

        [SyncSceneToStream, SerializeField] float m_HintLeftLowerLegEnabled;
        [SyncSceneToStream, SerializeField] float m_HintRightLowerLegEnabled;

        [SyncSceneToStream, SerializeField] bool m_EnabledLeftHand;
        [SyncSceneToStream, SerializeField] bool m_EnabledRightHand;

        [SyncSceneToStream, SerializeField] bool m_HintRightHandEnabled;
        [SyncSceneToStream, SerializeField] bool m_HintLeftHandEnabled;

        [SyncSceneToStream, SerializeField] float m_MinHeadSpineHeight;
        [SyncSceneToStream, SerializeField] public bool m_enabledLeftShoulder;
        [SyncSceneToStream, SerializeField] public bool m_enabledRightShoulder;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationRightShoulder;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationLeftShoulder;

        [SyncSceneToStream, SerializeField] public float m_MaxBendDeg;
        [SyncSceneToStream, SerializeField] public float m_MinFactor;
        [SyncSceneToStream, SerializeField] public float m_MaxFactor;
        [SyncSceneToStream, SerializeField] public float m_StruggleStart;
        [SyncSceneToStream, SerializeField] public float m_StruggleEnd;
        [SyncSceneToStream, SerializeField] public float m_MaxChestDeltaDeg;
        [SyncSceneToStream, SerializeField] public float m_MaxHipDeltaDeg;

        // Shoulder pre-solve: raises/protracts shoulders based on hand target
        [SyncSceneToStream, SerializeField] bool m_ShoulderSolveEnabled;
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_ShoulderElevationFactor;
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_ShoulderProtractionFactor;

        // Spine bend distribution: per-axis fractions of the hips→head bend pre-applied to lumbar
        // and thoracic joints before the chest→neck→head two-bone solve. Splitting by axis lets
        // forward bend, side bend, and twist be tuned independently — humans are very anisotropic.
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_SpineBendPitch;
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_SpineBendYaw;
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_SpineBendRoll;
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_UpperChestBendPitch;
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_UpperChestBendYaw;
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_UpperChestBendRoll;
        // Hip hinge: when forward lean exceeds the start angle, the pelvis pitches forward by a
        // capped fraction of the excess so the spine doesn't have to swallow the whole reach.
        [SyncSceneToStream, SerializeField, Min(0f)] float m_HipHingeStartDeg;
        [SyncSceneToStream, SerializeField, Min(0f)] float m_HipHingeMaxAddDeg;
        // Chest follow spring: critically-damped second-order spring on the head target before it
        // is consumed by DistributeSpineBend, so quick head turns leave the body momentarily behind.
        [SyncSceneToStream, SerializeField, Min(0f)] float m_ChestSpringHz;
        [SyncSceneToStream, SerializeField, Min(0f)] float m_ChestSpringDamping;
        // Asymmetric flexion clamps: humans flex forward much further than they extend backward.
        // Applied to the per-axis spine + upperChest contributions after distribution.
        [SyncSceneToStream, SerializeField, Min(0f)] float m_SpineMaxForwardDeg;
        [SyncSceneToStream, SerializeField, Min(0f)] float m_SpineMaxBackwardDeg;
        [SyncSceneToStream, SerializeField, Min(0f)] float m_SpineMaxLateralDeg;
        // Squish coupling: scales per-axis bend weights by the head-to-hips compression ratio so
        // the spine folds more when crouched and straightens when reaching up. 0 disables.
        [SyncSceneToStream, SerializeField, Range(0f, 2f)] float m_SpineSquishBoost;
        // Arm-swing chest follow: when hand targets shift laterally, the chest yaws to follow so
        // gestures and walking arm-swing don't read as a stiff torso. Only used without a chest
        // tracker — when one is present, it owns chest rotation directly.
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_ChestArmSwingFactor;
        [SyncSceneToStream, SerializeField, Min(0f)] float m_ChestArmSwingMaxDeg;
        // Arm twist distribution: fractions of the wrist/elbow roll absorbed by the optional
        // forearm/upper-arm twist bones. Without these, the wrist eats 100% of the roll and the
        // mesh pinches around the elbow ("candy-wrap" deformation).
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_LowerArmTwistFraction;
        [SyncSceneToStream, SerializeField, Range(0f, 1f)] float m_UpperArmTwistFraction;

        // Anatomy (Experimental): opt-in IK refinements modeled on real biomechanics. Off by
        // default — enable via the settings panel. Each toggle gates its own solver pass.
        [SyncSceneToStream, SerializeField] bool m_AnatDifferentialStiffness;
        [SyncSceneToStream, SerializeField] bool m_AnatShoulderSlide;
        [SyncSceneToStream, SerializeField] bool m_AnatCervicalLordosis;
        [SyncSceneToStream, SerializeField] bool m_AnatPelvicTwistRouting;
        // Cervical lordosis pitch coupling: extra forward bend per unit of head pitch-down (0..1
        // where 1 = looking straight down). Multiplied by the gain in degrees. Only used when
        // AnatCervicalLordosis is on.
        [SyncSceneToStream, SerializeField, Min(0f)] float m_LordosisPitchGainDeg;
        // Chest spring head-velocity feed-forward: scales upstream head linear velocity into the
        // spring's velocity term so the chest leads quick head moves (anticipates rather than just
        // pulling toward the new position). 0 disables.
        [SyncSceneToStream, SerializeField, Min(0f)] float m_ChestSpringHeadVelGain;
        // Upstream head linear velocity (world m/s), supplied per-frame by the rig driver from the
        // filtered head pose. Only consumed when m_ChestSpringHeadVelGain > 0.
        [SyncSceneToStream, SerializeField] public Vector3 HeadLinearVelocity;

        public float minHeadSpineHeight
        {
            get => m_MinHeadSpineHeight;
            set => m_MinHeadSpineHeight = value;
        }

        public Transform chest { get => m_chest; set => m_chest = value; }
        public Transform neck { get => m_neck; set => m_neck = value; }
        public Transform head { get => m_head; set => m_head = value; }
        public Transform LeftUpperLeg { get => m_LeftUpperLeg; set => m_LeftUpperLeg = value; }
        public Transform LeftLowerLeg { get => m_LeftLowerLeg; set => m_LeftLowerLeg = value; }
        public Transform leftFoot { get => m_leftFoot; set => m_leftFoot = value; }
        public Transform RightUpperLeg { get => m_RightUpperLeg; set => m_RightUpperLeg = value; }
        public Transform RightLowerLeg { get => m_RightLowerLeg; set => m_RightLowerLeg = value; }
        public Transform RightFoot { get => m_RightFoot; set => m_RightFoot = value; }
        public Transform hips { get => m_Hips; set => m_Hips = value; }
        public Transform LeftToe { get => m_LeftToe; set => m_LeftToe = value; }
        public Transform RightToe { get => m_RightToe; set => m_RightToe = value; }
        public Transform leftUpperArm { get => m_leftUpperArm; set => m_leftUpperArm = value; }
        public Transform leftLowerArm { get => m_leftLowerArm; set => m_leftLowerArm = value; }
        public Transform LeftHand { get => m_leftHand; set => m_leftHand = value; }
        public Transform RightUpperArm { get => m_RightUpperArm; set => m_RightUpperArm = value; }
        public Transform RightLowerArm { get => m_RightLowerArm; set => m_RightLowerArm = value; }
        public Transform RightHand { get => m_rightHand; set => m_rightHand = value; }

        public Transform spine { get => m_Spine; set => m_Spine = value; }
        public Transform upperChest { get => m_UpperChest; set => m_UpperChest = value; }
        public Transform LeftShoulder { get => m_LeftShoulder; set => m_LeftShoulder = value; }
        public Transform RightShoulder { get => m_RightShoulder; set => m_RightShoulder = value; }
        public Transform LeftUpperArmTwist { get => m_LeftUpperArmTwist; set => m_LeftUpperArmTwist = value; }
        public Transform LeftLowerArmTwist { get => m_LeftLowerArmTwist; set => m_LeftLowerArmTwist = value; }
        public Transform RightUpperArmTwist { get => m_RightUpperArmTwist; set => m_RightUpperArmTwist = value; }
        public Transform RightLowerArmTwist { get => m_RightLowerArmTwist; set => m_RightLowerArmTwist = value; }
        public string EnabledPropertySpineIK => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_SpineIKEnabled));
        public string HintWeightBoolPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintHeadEnabled));
        public string TargetPositionPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PositionHead));
        public string TargetRotationPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RotationHead));
        public string PropertyChestPosition => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(ChestPosition));
        public string PropertyChestRotation => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(ChestRotation));
        public string BendNormalHeadProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(SpineBendNormal));
        public string PlayerUpProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PlayerUp));
        public string KneeBendPrefLeftProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(KneeBendPrefLeft));
        public string KneeBendPrefRightProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(KneeBendPrefRight));
        public string ElbowBendPrefLeftProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(ElbowBendPrefLeft));
        public string ElbowBendPrefRightProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(ElbowBendPrefRight));

        public string EnabledPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_LeftLowerLegEnabled));
        public string HintWeightBoolPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintLeftLowerLegEnabled));
        public string TargetPositionPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(LeftFootPosition));
        public string TargetRotationPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(LeftFootRotation));
        public string HintPositionPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PositionLeftLowerLeg));
        public string HintRotationPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RotationLeftLowerLeg));
        public string EnabledPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_RightLowerLegEnabled));
        public string HintWeightBoolPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintRightLowerLegEnabled));
        public string TargetPositionPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RightFootPosition));
        public string TargetRotationPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RightFootRotation));
        public string HintPositionPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PositionRightLowerLeg));
        public string HintRotationPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RotationRightLowerLeg));
        public string TargetPositionPropertyHips => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PositionHips));
        public string TargetRotationPropertyHips => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RotationHips));
        public string OffsetRotationPropertyHips => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotationHips));
        public string LeftToeEnabledProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_LeftToeEnabled));
        public string RightToeEnabledProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_RightToeEnabled));
        public string LeftDrivenTargetPosProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OutGoingLeftToePosition));
        public string LeftDrivenTargetRotProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OutGoingLeftToeRotation));
        public string RightDrivenTargetPosProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OutGoingRightToePosition));
        public string RightDrivenTargetRotProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OutGoingRightToeRotation));
        public string HintWeightBoolPropertyLeftHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintLeftHandEnabled));
        public string TargetPositionPropertyLeftHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PositionLeftHand));
        public string TargetRotationPropertyLeftHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RotationLeftHand));
        public string HintPositionPropertyLeftHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(LeftLowerArmPosition));
        public string HintRotationPropertyLeftHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(LeftLowerArmRotation));
        public string EnabledPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_EnabledRightHand));
        public string EnabledPropertyLeftHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_EnabledLeftHand));
        public string HintWeightBoolPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintRightHandEnabled));
        public string TargetPositionPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PositionRightHand));
        public string TargetRotationPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RotationRightHand));
        public string HintPositionPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RightLowerArmPosition));
        public string HintRotationPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RightLowerArmRotation));
        public string ChestRadiusFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ChestRadius));
        public string CollisionSkinFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_CollisionSkin));
        public string CollisionsEnabledBoolProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_CollisionsEnabled));
        public string HandRadiusFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HandRadius));
        public string HandSkinFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HandSkin));
        public string UseHandCapsuleBoolProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_UseHandCapsule));
        public string ProtectElbowBoolProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ProtectElbow));

        public string EnabledLeftShoulderProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_enabledLeftShoulder));
        public string EnabledRightShoulderProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_enabledRightShoulder));
        public string MinHeadSpineHeightFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_MinHeadSpineHeight));

        public string TargetRotationLeftShoulderProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(LeftShoulderRotation));
        public string TargetRotationRightShoulderProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RightShoulderRotation));

        public string MaxBendDegFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_MaxBendDeg));
        public string MinFactorFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_MinFactor));
        public string MaxFactorFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_MaxFactor));
        public string StruggleStartFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_StruggleStart));
        public string StruggleEndFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_StruggleEnd));
        public string MaxHipDeltaPropertyDegFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_MaxHipDeltaDeg));
        public string MaxChestDeltaPropertyDegFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_MaxChestDeltaDeg));
        public bool WeightChest { get => m_HintHeadEnabled; set => m_HintHeadEnabled = value; }
        public bool EnabledSpineIK { get => m_SpineIKEnabled; set => m_SpineIKEnabled = value; }
        public float IKLockMode { get => m_IKLockMode; set => m_IKLockMode = value; }
        public string IKLockModeFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_IKLockMode));
        public float EnableLeftLowerLeg { get => m_HintLeftLowerLegEnabled; set => m_HintLeftLowerLegEnabled = value; }
        public float EnableLeftLeg { get => m_LeftLowerLegEnabled; set => m_LeftLowerLegEnabled = value; }
        public float EnableRightLowerLeg { get => m_HintRightLowerLegEnabled; set => m_HintRightLowerLegEnabled = value; }
        public float EnableRightLeg { get => m_RightLowerLegEnabled; set => m_RightLowerLegEnabled = value; }
        public bool LeftToeEnabled { get => m_LeftToeEnabled; set => m_LeftToeEnabled = value; }
        public bool RightToeEnabled { get => m_RightToeEnabled; set => m_RightToeEnabled = value; }
        public bool HintWeightLeftHand { get => m_HintLeftHandEnabled; set => m_HintLeftHandEnabled = value; }
        public bool EnabledLeftHand { get => m_EnabledLeftHand; set => m_EnabledLeftHand = value; }

        public bool EnabledRightHand { get => m_EnabledRightHand; set => m_EnabledRightHand = value; }
        public bool ProtectElbow { get => m_ProtectElbow; set => m_ProtectElbow = value; }
        public bool HintWeightRightHand { get => m_HintRightHandEnabled; set => m_HintRightHandEnabled = value; }
        public float HandRadius { get => m_HandRadius; set => m_HandRadius = value; }
        public float HandSkin { get => m_HandSkin; set => m_HandSkin = value; }
        public bool UseHandCapsule { get => m_UseHandCapsule; set => m_UseHandCapsule = value; }
        public float ChestRadius { get => m_ChestRadius; set => m_ChestRadius = value; }
        public float CollisionSkin { get => m_CollisionSkin; set => m_CollisionSkin = value; }
        public bool CollisionsEnabled { get => m_CollisionsEnabled; set => m_CollisionsEnabled = value; }
        public bool EnabledRightShoulder { get => m_enabledRightShoulder; set => m_enabledRightShoulder = value; }
        public bool EnabledLeftShoulder { get => m_enabledLeftShoulder; set => m_enabledLeftShoulder = value; }

        public float MaxBendDeg { get => m_MaxBendDeg; set => m_MaxBendDeg = value; }
        public float MinFactor { get => m_MinFactor; set => m_MinFactor = value; }
        public float MaxFactor { get => m_MaxFactor; set => m_MaxFactor = value; }
        public float StruggleStart { get => m_StruggleStart; set => m_StruggleStart = value; }
        public float StruggleEnd { get => m_StruggleEnd; set => m_StruggleEnd = value; }
        public float MaxChestDelta { get => m_MaxChestDeltaDeg; set => m_MaxChestDeltaDeg = value; }
        public float MaxHipDelta { get => m_MaxHipDeltaDeg; set => m_MaxHipDeltaDeg = value; }
        public bool ShoulderSolveEnabled { get => m_ShoulderSolveEnabled; set => m_ShoulderSolveEnabled = value; }
        public float ShoulderElevationFactor { get => m_ShoulderElevationFactor; set => m_ShoulderElevationFactor = value; }
        public float ShoulderProtractionFactor { get => m_ShoulderProtractionFactor; set => m_ShoulderProtractionFactor = value; }
        public string ShoulderSolveEnabledProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ShoulderSolveEnabled));
        public string ShoulderElevationFactorProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ShoulderElevationFactor));
        public string ShoulderProtractionFactorProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ShoulderProtractionFactor));
        public float SpineBendPitch { get => m_SpineBendPitch; set => m_SpineBendPitch = value; }
        public float SpineBendYaw { get => m_SpineBendYaw; set => m_SpineBendYaw = value; }
        public float SpineBendRoll { get => m_SpineBendRoll; set => m_SpineBendRoll = value; }
        public float UpperChestBendPitch { get => m_UpperChestBendPitch; set => m_UpperChestBendPitch = value; }
        public float UpperChestBendYaw { get => m_UpperChestBendYaw; set => m_UpperChestBendYaw = value; }
        public float UpperChestBendRoll { get => m_UpperChestBendRoll; set => m_UpperChestBendRoll = value; }
        public float HipHingeStartDeg { get => m_HipHingeStartDeg; set => m_HipHingeStartDeg = value; }
        public float HipHingeMaxAddDeg { get => m_HipHingeMaxAddDeg; set => m_HipHingeMaxAddDeg = value; }
        public float ChestSpringHz { get => m_ChestSpringHz; set => m_ChestSpringHz = value; }
        public float ChestSpringDamping { get => m_ChestSpringDamping; set => m_ChestSpringDamping = value; }
        public float SpineMaxForwardDeg { get => m_SpineMaxForwardDeg; set => m_SpineMaxForwardDeg = value; }
        public float SpineMaxBackwardDeg { get => m_SpineMaxBackwardDeg; set => m_SpineMaxBackwardDeg = value; }
        public float SpineMaxLateralDeg { get => m_SpineMaxLateralDeg; set => m_SpineMaxLateralDeg = value; }
        public float SpineSquishBoost { get => m_SpineSquishBoost; set => m_SpineSquishBoost = value; }
        public float ChestArmSwingFactor { get => m_ChestArmSwingFactor; set => m_ChestArmSwingFactor = value; }
        public float ChestArmSwingMaxDeg { get => m_ChestArmSwingMaxDeg; set => m_ChestArmSwingMaxDeg = value; }
        public float LowerArmTwistFraction { get => m_LowerArmTwistFraction; set => m_LowerArmTwistFraction = value; }
        public float UpperArmTwistFraction { get => m_UpperArmTwistFraction; set => m_UpperArmTwistFraction = value; }
        public bool AnatDifferentialStiffness { get => m_AnatDifferentialStiffness; set => m_AnatDifferentialStiffness = value; }
        public bool AnatShoulderSlide { get => m_AnatShoulderSlide; set => m_AnatShoulderSlide = value; }
        public bool AnatCervicalLordosis { get => m_AnatCervicalLordosis; set => m_AnatCervicalLordosis = value; }
        public bool AnatPelvicTwistRouting { get => m_AnatPelvicTwistRouting; set => m_AnatPelvicTwistRouting = value; }
        public string SpineBendPitchFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_SpineBendPitch));
        public string SpineBendYawFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_SpineBendYaw));
        public string SpineBendRollFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_SpineBendRoll));
        public string UpperChestBendPitchFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_UpperChestBendPitch));
        public string UpperChestBendYawFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_UpperChestBendYaw));
        public string UpperChestBendRollFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_UpperChestBendRoll));
        public string HipHingeStartDegFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HipHingeStartDeg));
        public string HipHingeMaxAddDegFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HipHingeMaxAddDeg));
        public string ChestSpringHzFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ChestSpringHz));
        public string ChestSpringDampingFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ChestSpringDamping));
        public string SpineMaxForwardDegFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_SpineMaxForwardDeg));
        public string SpineMaxBackwardDegFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_SpineMaxBackwardDeg));
        public string SpineMaxLateralDegFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_SpineMaxLateralDeg));
        public string SpineSquishBoostFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_SpineSquishBoost));
        public string ChestArmSwingFactorFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ChestArmSwingFactor));
        public string ChestArmSwingMaxDegFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ChestArmSwingMaxDeg));
        public string LowerArmTwistFractionFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_LowerArmTwistFraction));
        public string UpperArmTwistFractionFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_UpperArmTwistFraction));
        public string AnatDifferentialStiffnessProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_AnatDifferentialStiffness));
        public string AnatShoulderSlideProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_AnatShoulderSlide));
        public string AnatCervicalLordosisProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_AnatCervicalLordosis));
        public string AnatPelvicTwistRoutingProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_AnatPelvicTwistRouting));
        public float LordosisPitchGainDeg { get => m_LordosisPitchGainDeg; set => m_LordosisPitchGainDeg = value; }
        public float ChestSpringHeadVelGain { get => m_ChestSpringHeadVelGain; set => m_ChestSpringHeadVelGain = value; }
        public string LordosisPitchGainDegFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_LordosisPitchGainDeg));
        public string ChestSpringHeadVelGainFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ChestSpringHeadVelGain));
        public string HeadLinearVelocityProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HeadLinearVelocity));
        // ---------- Validation ----------
        bool IAnimationJobData.IsValid()
        {
            bool hipsValid = m_Hips != null;

            bool head = (m_head && m_neck && m_chest &&
                         m_head.IsChildOf(m_neck) && m_neck.IsChildOf(m_chest));

            bool lLeg = (m_leftFoot && m_LeftLowerLeg && m_LeftUpperLeg &&
                         m_leftFoot.IsChildOf(m_LeftLowerLeg) && m_LeftLowerLeg.IsChildOf(m_LeftUpperLeg));

            bool rLeg = (m_RightFoot && m_RightLowerLeg && m_RightUpperLeg &&
                         m_RightFoot.IsChildOf(m_RightLowerLeg) && m_RightLowerLeg.IsChildOf(m_RightUpperLeg));

            bool lHand = (m_leftHand && m_leftLowerArm && m_leftUpperArm &&
                          m_leftHand.IsChildOf(m_leftLowerArm) && m_leftLowerArm.IsChildOf(m_leftUpperArm));

            bool rHand = (m_rightHand && m_RightLowerArm && m_RightUpperArm &&
                          m_rightHand.IsChildOf(m_RightLowerArm) && m_RightLowerArm.IsChildOf(m_RightUpperArm));

            // Any of these being valid is enough to run.
            return head || lLeg || rLeg || lHand || rHand || hipsValid || (m_LeftToe != null) || (m_RightToe != null);
        }

        void IAnimationJobData.SetDefaultValues()
        {
            m_chest = m_neck = m_head = null;
            m_LeftUpperLeg = m_LeftLowerLeg = m_leftFoot = null;
            m_RightUpperLeg = m_RightLowerLeg = m_RightFoot = null;

            m_leftUpperArm = m_leftLowerArm = m_leftHand = null;
            m_RightUpperArm = m_RightLowerArm = m_rightHand = null;

            m_Hips = null;

            m_HintHeadEnabled = true;
            m_HintLeftLowerLegEnabled = m_HintRightLowerLegEnabled = 1f;
            m_SpineIKEnabled = true;
            m_LeftLowerLegEnabled = m_RightLowerLegEnabled = 1f;
            m_IKLockMode = (float)BasisIKLockMode.LockHips;

            m_HintLeftHandEnabled = m_HintRightHandEnabled = true;
            m_EnabledLeftHand = m_EnabledRightHand = true;
            m_CalibratedRotationHead = M_CalibrationLeftFootRotation = M_CalibrationRightFootRotation = Quaternion.identity;
            m_CalibratedRotationLeftHand = m_CalibratedRotationRightHand = Quaternion.identity;

            SpineBendNormal = Vector3.up;
            PlayerUp = Vector3.up;

            PositionHips = Vector3.zero;
            RotationHips = Quaternion.identity;
            OffsetRotationHips = Quaternion.identity;

            // Integrated driven TR defaults
            m_LeftToe = null;
            m_RightToe = null;

            OutGoingLeftToePosition = OutGoingRightToePosition = Vector3.zero;
            OutGoingLeftToeRotation = OutGoingRightToeRotation = Quaternion.identity;
            m_LeftToeEnabled = false;
            m_RightToeEnabled = false;

            // Chest/hand capsule defaults — read from persisted settings
            m_chest = m_neck = null;
            m_ChestRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKChestRadius.RawValue;
            m_CollisionSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionSkin.RawValue;
            m_CollisionsEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKCollisionsEnabled.RawValue;
            m_HandRadius = Basis.BasisUI.BasisSettingsDefaults.FBIKHandRadius.RawValue;
            m_HandSkin = Basis.BasisUI.BasisSettingsDefaults.FBIKHandSkin.RawValue;
            m_UseHandCapsule = Basis.BasisUI.BasisSettingsDefaults.FBIKUseHandCapsule.RawValue;
            m_ProtectElbow = Basis.BasisUI.BasisSettingsDefaults.FBIKProtectElbow.RawValue;

            m_ShoulderSolveEnabled = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderSolveEnabled.RawValue;
            m_ShoulderElevationFactor = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderElevation.RawValue;
            m_ShoulderProtractionFactor = Basis.BasisUI.BasisSettingsDefaults.FBIKShoulderProtraction.RawValue;

            m_SpineBendPitch = 0.45f;
            m_SpineBendYaw = 0.10f;
            m_SpineBendRoll = 0.35f;
            m_UpperChestBendPitch = 0.25f;
            m_UpperChestBendYaw = 0.30f;
            m_UpperChestBendRoll = 0.20f;
            m_HipHingeStartDeg = 30f;
            m_HipHingeMaxAddDeg = 15f;
            m_ChestSpringHz = 12f;
            m_ChestSpringDamping = 1f;
            m_SpineMaxForwardDeg = 60f;
            m_SpineMaxBackwardDeg = 25f;
            m_SpineMaxLateralDeg = 25f;
            m_SpineSquishBoost = 0.5f;
            m_ChestArmSwingFactor = 0.3f;
            m_ChestArmSwingMaxDeg = 15f;
            m_LowerArmTwistFraction = 0.5f;
            m_UpperArmTwistFraction = 0.3f;

            m_AnatDifferentialStiffness = false;
            m_AnatShoulderSlide = false;
            m_AnatCervicalLordosis = false;
            m_AnatPelvicTwistRouting = false;
            m_LordosisPitchGainDeg = 8f;
            m_ChestSpringHeadVelGain = 0.7f;
            HeadLinearVelocity = Vector3.zero;

            // Positions
            TargetPosition0 = TargetPosition1 = TargetPosition2 = TargetPosition3 = TargetPosition4 =
            TargetPosition5 = TargetPosition6 = TargetPosition7 = TargetPosition8 = TargetPosition9 =
            TargetPosition10 = TargetPosition11 = TargetPosition12 = TargetPosition13 = TargetPosition14 =
            TargetPosition15 = TargetPosition16 = TargetPosition17 = TargetPosition18 = TargetPosition19 =
            TargetPosition20 = TargetPosition54 = Vector3.zero;

            // Rotations
            TargetRotation0 = TargetRotation1 = TargetRotation2 = TargetRotation3 = TargetRotation4 =
            TargetRotation5 = TargetRotation6 = TargetRotation7 = TargetRotation8 = TargetRotation9 =
            TargetRotation10 = TargetRotation11 = TargetRotation12 = TargetRotation13 = TargetRotation14 =
            TargetRotation15 = TargetRotation16 = TargetRotation17 = TargetRotation18 = TargetRotation19 =
            TargetRotation20 = TargetRotation54 = Quaternion.identity;

            // Offsets
            OffsetRotation0 = OffsetRotation1 = OffsetRotation2 = OffsetRotation3 = OffsetRotation4 =
            OffsetRotation5 = OffsetRotation6 = OffsetRotation7 = OffsetRotation8 = OffsetRotation9 =
            OffsetRotation10 = OffsetRotation11 = OffsetRotation12 = OffsetRotation13 = OffsetRotation14 =
            OffsetRotation15 = OffsetRotation16 = OffsetRotation17 = OffsetRotation18 = OffsetRotation19 =
            OffsetRotation20 = OffsetRotation54 = Quaternion.identity;

            // Weights default to disabled
            Weight0 = Weight1 = Weight2 = Weight3 = Weight4 =
            Weight5 = Weight6 = Weight7 = Weight8 = Weight9 =
            Weight10 = Weight11 = Weight12 = Weight13 = Weight14 =
            Weight15 = Weight16 = Weight17 = Weight18 = Weight19 =
            Weight20 = Weight54 = false;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTargetPosition(int idx, in Vector3 v)
        {
            switch (idx)
            {
                case 0: TargetPosition0 = v; break;
                case 1: TargetPosition1 = v; break;
                case 2: TargetPosition2 = v; break;
                case 3: TargetPosition3 = v; break;
                case 4: TargetPosition4 = v; break;
                case 5: TargetPosition5 = v; break;
                case 6: TargetPosition6 = v; break;
                case 7: TargetPosition7 = v; break;
                case 8: TargetPosition8 = v; break;
                case 9: TargetPosition9 = v; break;
                case 10: TargetPosition10 = v; break;
                case 11: TargetPosition11 = v; break;
                case 12: TargetPosition12 = v; break;
                case 13: TargetPosition13 = v; break;
                case 14: TargetPosition14 = v; break;
                case 15: TargetPosition15 = v; break;
                case 16: TargetPosition16 = v; break;
                case 17: TargetPosition17 = v; break;
                case 18: TargetPosition18 = v; break;
                case 19: TargetPosition19 = v; break;
                case 20: TargetPosition20 = v; break;
                case 54: TargetPosition54 = v; break;
                default:
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTargetRotation(int idx, in Quaternion q)
        {
            switch (idx)
            {
                case 0: TargetRotation0 = q; break;
                case 1: TargetRotation1 = q; break;
                case 2: TargetRotation2 = q; break;
                case 3: TargetRotation3 = q; break;
                case 4: TargetRotation4 = q; break;
                case 5: TargetRotation5 = q; break;
                case 6: TargetRotation6 = q; break;
                case 7: TargetRotation7 = q; break;
                case 8: TargetRotation8 = q; break;
                case 9: TargetRotation9 = q; break;
                case 10: TargetRotation10 = q; break;
                case 11: TargetRotation11 = q; break;
                case 12: TargetRotation12 = q; break;
                case 13: TargetRotation13 = q; break;
                case 14: TargetRotation14 = q; break;
                case 15: TargetRotation15 = q; break;
                case 16: TargetRotation16 = q; break;
                case 17: TargetRotation17 = q; break;
                case 18: TargetRotation18 = q; break;
                case 19: TargetRotation19 = q; break;
                case 20: TargetRotation20 = q; break;
                case 54: TargetRotation54 = q; break;
                default:
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOffsetRotation(int idx, in Quaternion q)
        {
            switch (idx)
            {
                case 0: OffsetRotation0 = q; break;
                case 1: OffsetRotation1 = q; break;
                case 2: OffsetRotation2 = q; break;
                case 3: OffsetRotation3 = q; break;
                case 4: OffsetRotation4 = q; break;
                case 5: OffsetRotation5 = q; break;
                case 6: OffsetRotation6 = q; break;
                case 7: OffsetRotation7 = q; break;
                case 8: OffsetRotation8 = q; break;
                case 9: OffsetRotation9 = q; break;
                case 10: OffsetRotation10 = q; break;
                case 11: OffsetRotation11 = q; break;
                case 12: OffsetRotation12 = q; break;
                case 13: OffsetRotation13 = q; break;
                case 14: OffsetRotation14 = q; break;
                case 15: OffsetRotation15 = q; break;
                case 16: OffsetRotation16 = q; break;
                case 17: OffsetRotation17 = q; break;
                case 18: OffsetRotation18 = q; break;
                case 19: OffsetRotation19 = q; break;
                case 20: OffsetRotation20 = q; break;
                case 54: OffsetRotation54 = q; break;
                default:
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetWeight(int idx, bool State)
        {
            switch (idx)
            {
                case 0: Weight0 = State; break;
                case 1: Weight1 = State; break;
                case 2: Weight2 = State; break;
                case 3: Weight3 = State; break;
                case 4: Weight4 = State; break;
                case 5: Weight5 = State; break;
                case 6: Weight6 = State; break;
                case 7: Weight7 = State; break;
                case 8: Weight8 = State; break;
                case 9: Weight9 = State; break;
                case 10: Weight10 = State; break;
                case 11: Weight11 = State; break;
                case 12: Weight12 = State; break;
                case 13: Weight13 = State; break;
                case 14: Weight14 = State; break;
                case 15: Weight15 = State; break;
                case 16: Weight16 = State; break;
                case 17: Weight17 = State; break;
                case 18: Weight18 = State; break;
                case 19: Weight19 = State; break;
                case 20: Weight20 = State; break;
                case 54: Weight54 = State; break;
                default:
                    break;
            }
        }
    }
    public interface IBasisFullBodyData
    {
        string GetTargetPositionVector3Property(int index);
        string GetTargetRotationVector4Property(int index);
        string GetOffsetRotationVector4Property(int index);
        string GetWeightFloatProperty(int index);
    }
    [DisallowMultipleComponent]
    [AddComponentMenu("Animation Rigging/Basis FullBody IK")]
    [HelpURL("https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/manual/index.html")]
    public class BasisFullBodyIK : RigConstraint<BasisFullIKConstraintJob, BasisFullBodyData, BasisFullBodyJobBinder>
    {

        protected override void OnValidate()
        {
            base.OnValidate();
            // force serialize dirty for animated bools
            m_Data.WeightChest = m_Data.WeightChest;
            m_Data.EnableLeftLowerLeg = m_Data.EnableLeftLowerLeg;
            m_Data.EnableRightLowerLeg = m_Data.EnableRightLowerLeg;
            m_Data.EnabledSpineIK = m_Data.EnabledSpineIK;

            // new toggles
            m_Data.LeftToeEnabled = m_Data.LeftToeEnabled;
            m_Data.RightToeEnabled = m_Data.RightToeEnabled;

            // hands toggles
            m_Data.HintWeightLeftHand = m_Data.HintWeightLeftHand;
            m_Data.HintWeightRightHand = m_Data.HintWeightRightHand;
            m_Data.EnabledLeftHand = m_Data.EnabledLeftHand;
            m_Data.EnabledRightHand = m_Data.EnabledRightHand;
            m_Data.ProtectElbow = m_Data.ProtectElbow;
            m_Data.ShoulderSolveEnabled = m_Data.ShoulderSolveEnabled;
            m_Data.IKLockMode = m_Data.IKLockMode;
        }
    }

    [Unity.Burst.BurstCompile]
    public struct BasisFullIKConstraintJob : IWeightedAnimationJob
    {
        const float k_Epsilon = 1e-5f; // or 0.00001f
        const float k_MinMag = 1e-6f;// or 0.000001f
        const float k_SqrEpsilon = 1e-8f;// or 0.00000001f

        public ReadWriteTransformHandle HandleChest, HandleNeck, HandleHead,
  HandleLeftUpperLeg, HandleLeftLowerLeg, HandleLeftFoot,
  HandleRightUpperLeg, HandleRightLowerLeg, HandleRightFoot,
  HandleHips, HandleSpine, HandleUpperChest,
            HandleLeftShoulder, HandleRightShoulder,

  HandleLeftToe, HandleRightToe,
  HandleLeftUpperArm, HandleLeftLowerArm, HandleLeftHand,
  HandleRightUpperArm, HandleRightLowerArm, HandleRightHand,
  HandleLeftUpperArmTwist, HandleLeftLowerArmTwist,
  HandleRightUpperArmTwist, HandleRightLowerArmTwist;

        public Vector3Property targetPositionHead, TargetChestPosition, bendNormalHead, playerUp, KneeBendPrefLeft, KneeBendPrefRight, ElbowBendPrefLeft, ElbowBendPrefRight,
targetPositionLeftLowerLeg, hintPositionLeftLowerLeg,
targetPositionRightLowerLeg, hintPositionRightLowerLeg,
targetPositionHips,
leftDrivenTargetPos, rightDrivenTargetPos,
targetPositionLeftHand, hintPositionLeftHand,
targetPositionRightHand, hintPositionRightHand,
p0, p1, p2, p3, p4, p5, p6, p7, p8, p9,
p10, p11, p12, p13, p14, p15, p16, p17, p18, p19,
p20, p54;

        public Vector4Property targetRotationHead, targetChestRotation,
targetRotationLeftLowerLeg, hintRotationLeftLowerLeg,
targetRotationRightLowerLeg, hintRotationRightLowerLeg,
targetRotationHips, offsetRotationHips,
leftDrivenTargetRot, rightDrivenTargetRot,
targetRotationLeftHand, hintRotationLeftHand,
targetRotationRightHand, hintRotationRightHand,
TargetRotationLeftShoulder, TargetRotationRightShoulder,
r0, r1, r2, r3, r4, r5, r6, r7, r8, r9,
r10, r11, r12, r13, r14, r15, r16, r17, r18, r19,
r20, r54,
o0, o1, o2, o3, o4, o5, o6, o7, o8, o9,
o10, o11, o12, o13, o14, o15, o16, o17, o18, o19,
o20, o54;

        // Arm bend lookup tables (HVR-IK inspired)
        public NativeArray<Vector3> ArmBendLookupLeft;
        public NativeArray<Vector3> ArmBendLookupRight;
        public bool HasArmBendLookup;

        public Quaternion targetOffsetNeck, targetOffsetHead, targetOffsetChest, targetOffsetLeftToe,
            targetOffsetRightToe, targetOffsetLeftShoulder, targetOffsetRightShoulder, targetOffsetLeftFoot,
            targetOffsetRightFoot, targetOffsetLeftHand, targetOffsetRightHand;

        public FloatProperty
enabledLeftLowerLeg, enabledRightLowerLeg,
hintWeightLeftLowerLeg, hintWeightRightLowerLeg;

        public BoolProperty
HasChestTracker, enabledSpineIK,
            enabledLeftShoulder, enabledRightShoulder,

leftToeEnabled, RightToeEnabled,
hintWeightLeftHand, enabledLeftHand,
hintWeightRightHand, enabledRightHand,
useHandCapsule, protectElbow,
collisionsEnabled,
w0, w1, w2, w3, w4, w5, w6, w7, w8, w9,
w10, w11, w12, w13, w14, w15, w16, w17, w18, w19,
w20, w54;
        public NativeArray<ReadWriteTransformHandle> ChainChestToHead;
        public NativeArray<ReadWriteTransformHandle> ChainHeadToSpine;
        public NativeArray<float> ChainChestToHeadLengths;
        public NativeArray<float> ChainHeadToSpineLengths;
        public NativeArray<Vector3> ChainChestToHeadLinkPositions;
        public NativeArray<Vector3> ChainHeadToSpineLinkPositions;
        public float MaxReachSpineTohead;
        public float MaxReachHeadToChest;
        // optional tuning (can be constants or properties)
        public CacheIndex spineToleranceIdx;
        public CacheIndex spineMaxIterationsIdx;
        public AnimationJobCache spineCache;
        public Vector3 TposeLengthHeadToChest;
        public Vector3 TposeLengthHeadToHips;
        public FloatProperty handRadius, handSkin, chestRadius, collisionSkin, MinHeadSpineHeight, maxBendDeg, minFactor, maxFactor, struggleStart, struggleEnd, MaxHipDeltaProperty, MaxChestDeltaProperty;
        public FloatProperty shoulderElevationFactor, shoulderProtractionFactor;
        public FloatProperty spineBendPitch, spineBendYaw, spineBendRoll;
        public FloatProperty upperChestBendPitch, upperChestBendYaw, upperChestBendRoll;
        public FloatProperty hipHingeStartDeg, hipHingeMaxAddDeg;
        public FloatProperty chestSpringHz, chestSpringDamping;
        public FloatProperty spineMaxForwardDeg, spineMaxBackwardDeg, spineMaxLateralDeg;
        public FloatProperty spineSquishBoost;
        public FloatProperty chestArmSwingFactor, chestArmSwingMaxDeg;
        public FloatProperty lowerArmTwistFraction, upperArmTwistFraction;
        public BoolProperty anatDifferentialStiffness, anatShoulderSlide, anatCervicalLordosis, anatPelvicTwistRouting;
        public FloatProperty lordosisPitchGainDeg, chestSpringHeadVelGain;
        public Vector3Property headLinearVelocity;
        // Persistent state for the chest follow spring. [0]=smoothed pos, [1]=velocity. Allocated
        // in CreateJob, disposed in Destroy. Initialised lazily on first frame to avoid spring kick.
        public NativeArray<Vector3> chestSpringState;
        public NativeArray<int> chestSpringInit;
        public FloatProperty ikLockMode;
        public BoolProperty shoulderSolveEnabled;
        // T-pose baked reference data for shoulder solve
        public Vector3 TposeLeftShoulderLocalDir, TposeRightShoulderLocalDir;
        public Quaternion TposeLeftShoulderRot, TposeRightShoulderRot;
        public Quaternion TposeChestRot;
        public float TposeShoulderToHandLeft, TposeShoulderToHandRight;
        public FloatProperty jobWeight { get; set; }
        const float maxHorizontalFactor = 0.35f;
        public void ProcessRootMotion(AnimationStream stream) { }
        public void ProcessAnimation(AnimationStream stream)
        {
            float w = jobWeight.Get(stream);
            if (w <= 0f)
            {
                return;
            }

            // 1) Spine: hips + chest/neck/head chain
            SolveSpine(stream);

            // 1b) Anatomy modifiers that act on the spine after the main solve.
            if (anatCervicalLordosis.Get(stream)) ApplyCervicalLordosis(stream);

            // 2) Shoulder pre-solve: elevate/protract based on hand targets before arm IK
            if (shoulderSolveEnabled.Get(stream))
            {
                SolveShoulder(stream, HandleLeftShoulder, enabledLeftShoulder, targetPositionLeftHand,TposeLeftShoulderLocalDir, TposeLeftShoulderRot, TposeChestRot, TposeShoulderToHandLeft, true);
                SolveShoulder(stream, HandleRightShoulder, enabledRightShoulder, targetPositionRightHand,TposeRightShoulderLocalDir, TposeRightShoulderRot, TposeChestRot, TposeShoulderToHandRight, false);
            }
            else
            {
                ApplyRotation(stream, enabledLeftShoulder, HandleLeftShoulder, TargetRotationLeftShoulder, targetOffsetLeftShoulder);
                ApplyRotation(stream, enabledRightShoulder, HandleRightShoulder, TargetRotationRightShoulder, targetOffsetRightShoulder);
            }
            if (anatShoulderSlide.Get(stream)) ApplyShoulderSlide(stream);

            // 3) Legs: two-bone IK with bend normal preference
            SolveLegs(stream, enabledLeftLowerLeg, HandleLeftUpperLeg, HandleLeftLowerLeg, HandleLeftFoot, targetPositionLeftLowerLeg, targetRotationLeftLowerLeg, hintPositionLeftLowerLeg, hintRotationLeftLowerLeg, hintWeightLeftLowerLeg, targetOffsetLeftFoot, KneeBendPrefLeft);
            SolveLegs(stream, enabledRightLowerLeg, HandleRightUpperLeg, HandleRightLowerLeg, HandleRightFoot, targetPositionRightLowerLeg, targetRotationRightLowerLeg, hintPositionRightLowerLeg, hintRotationRightLowerLeg, hintWeightRightLowerLeg, targetOffsetRightFoot, KneeBendPrefRight);

            // 4) Hands: two-bone IK with collision + elbow protection
            SolveHand(stream, enabledLeftHand, HandleLeftUpperArm, HandleLeftLowerArm, HandleLeftHand, targetPositionLeftHand, targetRotationLeftHand, hintPositionLeftHand, hintRotationLeftHand, hintWeightLeftHand, targetOffsetLeftHand, HandleChest, HandleNeck, chestRadius, collisionSkin, collisionsEnabled, handRadius, handSkin, useHandCapsule, protectElbow);
            SolveHand(stream, enabledRightHand, HandleRightUpperArm, HandleRightLowerArm, HandleRightHand, targetPositionRightHand, targetRotationRightHand, hintPositionRightHand, hintRotationRightHand, hintWeightRightHand, targetOffsetRightHand, HandleChest, HandleNeck, chestRadius, collisionSkin, collisionsEnabled, handRadius, handSkin, useHandCapsule, protectElbow);

            // 4b) Arm twist distribution: spread wrist/elbow roll along the optional twist bones
            // so the mesh doesn't pinch at the wrist when the hand rotates.
            float lowerTwist = lowerArmTwistFraction.Get(stream);
            float upperTwist = upperArmTwistFraction.Get(stream);
            SolveArmTwist(stream, HandleLeftLowerArm, HandleLeftHand, HandleLeftLowerArmTwist, lowerTwist);
            SolveArmTwist(stream, HandleRightLowerArm, HandleRightHand, HandleRightLowerArmTwist, lowerTwist);
            SolveArmTwist(stream, HandleLeftUpperArm, HandleLeftLowerArm, HandleLeftUpperArmTwist, upperTwist);
            SolveArmTwist(stream, HandleRightUpperArm, HandleRightLowerArm, HandleRightUpperArmTwist, upperTwist);

            // 5) Toes
            ApplyRotation(stream, leftToeEnabled, HandleLeftToe, leftDrivenTargetRot, targetOffsetLeftToe);
            ApplyRotation(stream, RightToeEnabled, HandleRightToe, rightDrivenTargetRot, targetOffsetRightToe);

            // 6) Generic per-bone overrides (direct tracker control)
            Apply(stream, HandleHips, p0, r0, o0, w0);
            Apply(stream, HandleLeftUpperLeg, p1, r1, o1, w1);
            Apply(stream, HandleRightUpperLeg, p2, r2, o2, w2);
            Apply(stream, HandleLeftLowerLeg, p3, r3, o3, w3);
            Apply(stream, HandleRightLowerLeg, p4, r4, o4, w4);
            Apply(stream, HandleLeftFoot, p5, r5, o5, w5);
            Apply(stream, HandleRightFoot, p6, r6, o6, w6);
            Apply(stream, HandleSpine, p7, r7, o7, w7);
            Apply(stream, HandleChest, p8, r8, o8, w8);
            Apply(stream, HandleNeck, p9, r9, o9, w9);
            Apply(stream, HandleHead, p10, r10, o10, w10);
            Apply(stream, HandleLeftShoulder, p11, r11, o11, w11);
            Apply(stream, HandleRightShoulder, p12, r12, o12, w12);
            Apply(stream, HandleLeftUpperArm, p13, r13, o13, w13);
            Apply(stream, HandleRightUpperArm, p14, r14, o14, w14);
            Apply(stream, HandleLeftLowerArm, p15, r15, o15, w15);
            Apply(stream, HandleRightLowerArm, p16, r16, o16, w16);
            Apply(stream, HandleLeftHand, p17, r17, o17, w17);
            Apply(stream, HandleRightHand, p18, r18, o18, w18);
            Apply(stream, HandleLeftToe, p19, r19, o19, w19);
            Apply(stream, HandleRightToe, p20, r20, o20, w20);
            Apply(stream, HandleUpperChest, p54, r54, o54, w54);
        }
        public void SolveSpine(AnimationStream stream)
        {
            if (!enabledSpineIK.Get(stream))
            {
                return;
            }
            // ---- Read targets ----
            Vector3 headTargetPos = targetPositionHead.Get(stream);
            Vector3 hipsTargetPos = targetPositionHips.Get(stream);

            Quaternion headTargetRot = V4ToQuat(targetRotationHead.Get(stream));
            Quaternion hipsTargetRot = V4ToQuat(targetRotationHips.Get(stream));
            Quaternion offsetHips = V4ToQuat(offsetRotationHips.Get(stream));
            Quaternion chestTargetRot = V4ToQuat(targetChestRotation.Get(stream));

            Quaternion hipDesired = hipsTargetRot * offsetHips;
            Quaternion chestDesired = chestTargetRot * targetOffsetChest;

            float restDist = MinHeadSpineHeight.Get(stream);
            int lockMode = (int)ikLockMode.Get(stream);
            Vector3 up = playerUp.Get(stream);

            // Lock mode determines how hips position relates to head position:
            // 0 = LockHips:  Hips are the anchor; apply hips directly, no head-relative clamping.
            // 1 = LockHead:  Head is the anchor; derive hips position below head.
            // 2 = LockBoth:  Both independently positioned; spine must accommodate (original behavior).
            switch (lockMode)
            {
                case 0: // LockHips - hips are authoritative, skip head-relative clamping
                    break;

                case 1: // LockHead - head is the anchor; push hips down only if within restDist, allow sinking further
                    {
                        float gap = Vector3.Dot(headTargetPos - hipsTargetPos, up);
                        if (gap < restDist)
                        {
                            hipsTargetPos -= up * (restDist - gap);
                        }
                    }
                    break;

                default: // LockBoth (2) - original behavior: clamp hips relative to head
                    hipsTargetPos = AntiContortionist(headTargetPos, headTargetRot, hipsTargetPos, hipDesired, restDist);
                    hipsTargetPos = MitigateSpineBuckling(headTargetPos, hipDesired, hipsTargetPos, restDist, up);
                    float MaxBendDeg = maxBendDeg.Get(stream);
                    hipsTargetPos = EnforceSpineBendLimit(headTargetPos, hipsTargetPos, MaxBendDeg, up);
                    hipsTargetPos = ClampHipsAroundHead(headTargetPos, hipsTargetPos, restDist, minFactor.Get(stream), maxFactor.Get(stream), up);
                    break;
            }

            targetPositionHips.Set(stream, hipsTargetPos);

            hipDesired = ApplyHipHinge(stream, headTargetPos, hipsTargetPos, hipDesired, up);

            // Apply hips driver if valid
            if (HandleHips.IsValid(stream))
            {
                HandleHips.SetPosition(stream, hipsTargetPos);
                HandleHips.SetRotation(stream, hipDesired);
            }
            if (HandleChest.IsValid(stream) & HandleNeck.IsValid(stream) & HandleHead.IsValid(stream))
            {
                // Build target + hint transforms
                var tRot = V4ToQuat(targetRotationHead.Get(stream));
                var target = new AffineTransform(targetPositionHead.Get(stream), tRot);
                var bendNormal = bendNormalHead.Get(stream);

                DistributeSpineBend(stream, target.translation);
                if (!HasChestTracker.Get(stream))
                    ApplyArmSwingChestFollow(stream);
                SolveTwoBoneSpine(stream, HandleChest, HandleNeck, HandleHead, target, targetOffsetHead, bendNormal);
            }
            if (HasChestTracker.Get(stream) && HandleChest.IsValid(stream))
            {
                // Neck rotation produced by your spine IK pass – we keep this
                Quaternion neckRot = HandleNeck.IsValid(stream) ? HandleNeck.GetRotation(stream) : Quaternion.identity;

                // Spine as an extra reference if available (nice stabiliser)
                Quaternion spineRot = HandleSpine.IsValid(stream) ? HandleSpine.GetRotation(stream) : neckRot;

                float Value = MaxChestDeltaProperty.Get(stream);
                // Clamp relative to neck and spine
                Quaternion clampedChestRot = ClampRotation(chestDesired, neckRot, Value);
                clampedChestRot = ClampRotation(clampedChestRot, spineRot, Value);

                HandleChest.SetRotation(stream, clampedChestRot);

                // Build target + hint transforms
                var tRot = V4ToQuat(targetRotationHead.Get(stream));
                var target = new AffineTransform(targetPositionHead.Get(stream), tRot);
                var bendNormal = bendNormalHead.Get(stream);

                DistributeSpineBend(stream, target.translation);
                SolveTwoBoneSpine(stream, HandleChest, HandleNeck, HandleHead, target, targetOffsetHead, bendNormal);
            }
        }
        // Pre-distributes the hips→head bend onto spine and upperChest in hips-local space, split
        // into independent pitch / yaw / roll contributions so anisotropic human ranges of motion
        // can be respected (lumbar twists very little, cervical twists a lot, forward bend ≫ back).
        // Pipeline: (chest spring smooths target) → (decompose bend into pitch/roll, twist into yaw)
        //   → (per-axis weight) → (asymmetric clamp) → (apply as hips-local delta).
        // The chest→neck→head two-bone solve afterwards handles whatever residual reach remains.
        public void DistributeSpineBend(AnimationStream stream, Vector3 headTargetPos)
        {
            if (!HandleHips.IsValid(stream) || !HandleChest.IsValid(stream))
                return;

            bool hasSpine = HandleSpine.IsValid(stream);
            bool hasUpper = HandleUpperChest.IsValid(stream);
            if (!hasSpine && !hasUpper)
                return;

            Vector3 smoothedHead = ApplyChestSpring(stream, headTargetPos);

            Vector3 hipsPos = HandleHips.GetPosition(stream);
            Quaternion hipsRot = HandleHips.GetRotation(stream);
            Quaternion invHips = Quaternion.Inverse(hipsRot);

            Vector3 chestPos = HandleChest.GetPosition(stream);
            Vector3 localChestDir = invHips * (chestPos - hipsPos);
            Vector3 localTargetDir = invHips * (smoothedHead - hipsPos);

            if (localChestDir.sqrMagnitude < k_SqrEpsilon || localTargetDir.sqrMagnitude < k_SqrEpsilon)
                return;

            // Bend produces only swing (pitch + roll) — FromToRotation has no twist component.
            Quaternion bendLocal = Quaternion.FromToRotation(localChestDir.normalized, localTargetDir.normalized);
            Vector3 bendEuler = SignedEuler(bendLocal.eulerAngles);

            // Twist comes from head facing yaw in hips-local frame. Extracted from the head's
            // forward vector projected onto the hips-local horizontal plane — robust at look-up/down
            // where Quaternion.eulerAngles gimbal-locks and emits a phantom ±180° roll/yaw split
            // that, even after the ±maxLat clamp, twists the spine sideways for no real reason.
            Quaternion headRotLocal = invHips * V4ToQuat(targetRotationHead.Get(stream));
            Vector3 headFwdLocal = headRotLocal * Vector3.forward;
            float horizMagSq = headFwdLocal.x * headFwdLocal.x + headFwdLocal.z * headFwdLocal.z;
            float twistY = (horizMagSq < k_SqrEpsilon)
                ? 0f
                : Mathf.Atan2(headFwdLocal.x, headFwdLocal.z) * Mathf.Rad2Deg;

            float maxFwd = Mathf.Max(0f, spineMaxForwardDeg.Get(stream));
            float maxBack = Mathf.Max(0f, spineMaxBackwardDeg.Get(stream));
            float maxLat = Mathf.Max(0f, spineMaxLateralDeg.Get(stream));

            // Squish coupling: compress → fold more, stretch → straighten.
            float squishMult = ComputeSquishMultiplier(stream, smoothedHead - hipsPos);

            // Deadband on the bend (pitch+roll) so tracker / rest-pose micro-misalignments don't
            // get amplified into a visible chest tilt at rest. Twist is unaffected because it's
            // driven by head facing — not by the small chest-vs-head positional offset.
            float bendMag = Mathf.Sqrt(bendEuler.x * bendEuler.x + bendEuler.z * bendEuler.z);
            float bendT = Mathf.Clamp01((bendMag - k_BendDeadbandDeg) / k_BendDeadbandWidthDeg);
            float bendGate = Mathf.SmoothStep(0f, 1f, bendT);

            // Effective per-axis fractions. Anatomy toggles re-route the user weights in two ways:
            //   - DifferentialStiffness: lumbar (spine) is twist-resistant; route most twist to
            //     upperChest by halving spine yaw and boosting upperChest yaw.
            //   - PelvicTwistRouting: when hip vs chest twist is the dominant source, distribute
            //     it ~75% upperChest / 25% spine, mimicking real thoracic-dominant axial rotation.
            float spinePitchEff = Mathf.Clamp01(spineBendPitch.Get(stream));
            float spineYawEff = Mathf.Clamp01(spineBendYaw.Get(stream));
            float spineRollEff = Mathf.Clamp01(spineBendRoll.Get(stream));
            float upperPitchEff = Mathf.Clamp01(upperChestBendPitch.Get(stream));
            float upperYawEff = Mathf.Clamp01(upperChestBendYaw.Get(stream));
            float upperRollEff = Mathf.Clamp01(upperChestBendRoll.Get(stream));
            if (anatDifferentialStiffness.Get(stream))
            {
                spineYawEff *= 0.4f;
                upperYawEff = Mathf.Clamp01(upperYawEff * 1.5f);
            }
            if (anatPelvicTwistRouting.Get(stream))
            {
                float total = spineYawEff + upperYawEff;
                spineYawEff = total * 0.25f;
                upperYawEff = total * 0.75f;
            }

            if (hasSpine)
            {
                Vector3 e = new Vector3(
                    bendEuler.x * spinePitchEff * squishMult * bendGate,
                    twistY * spineYawEff * squishMult,
                    bendEuler.z * spineRollEff * squishMult * bendGate
                );
                e = ClampAsymmetric(e, maxFwd, maxBack, maxLat);
                Quaternion deltaWorld = hipsRot * Quaternion.Euler(e) * invHips;
                HandleSpine.SetRotation(stream, deltaWorld * HandleSpine.GetRotation(stream));
            }
            if (hasUpper)
            {
                Vector3 e = new Vector3(
                    bendEuler.x * upperPitchEff * squishMult * bendGate,
                    twistY * upperYawEff * squishMult,
                    bendEuler.z * upperRollEff * squishMult * bendGate
                );
                e = ClampAsymmetric(e, maxFwd, maxBack, maxLat);
                Quaternion deltaWorld = hipsRot * Quaternion.Euler(e) * invHips;
                HandleUpperChest.SetRotation(stream, deltaWorld * HandleUpperChest.GetRotation(stream));
            }
        }
        const float k_BendDeadbandDeg = 3f;
        const float k_BendDeadbandWidthDeg = 7f;
        // Maps the head-to-hips compression ratio to a per-axis weight multiplier. At rest the
        // multiplier is 1; compressed → up to (1+boost), stretched → down to (1-boost). The 0.7→1.3
        // window covers the range users actually hit (deep crouch to overhead reach).
        float ComputeSquishMultiplier(AnimationStream stream, Vector3 hipsToHead)
        {
            float boost = Mathf.Clamp(spineSquishBoost.Get(stream), 0f, 2f);
            if (boost <= 0f)
                return 1f;

            float restMag = TposeLengthHeadToHips.magnitude;
            if (restMag < k_Epsilon)
                return 1f;

            float currentMag = hipsToHead.magnitude;
            float squish = currentMag / restMag;

            float t = Mathf.Clamp01((squish - 0.7f) / 0.6f);
            return Mathf.Lerp(1f + boost, Mathf.Max(0f, 1f - boost), t);
        }
        // Critically-damped spring on the head target consumed by DistributeSpineBend. Lets the
        // body lag slightly behind quick head moves without affecting the head bone itself.
        // Uses implicit Euler so it stays stable at high Hz / low fps where explicit Euler blows
        // up (omega * dt > 1 → divergent oscillation → NaN → corrupted quaternions downstream).
        Vector3 ApplyChestSpring(AnimationStream stream, Vector3 headTargetPos)
        {
            if (!chestSpringState.IsCreated || !chestSpringInit.IsCreated)
                return headTargetPos;

            float hz = chestSpringHz.Get(stream);
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

            float damping = Mathf.Max(0f, chestSpringDamping.Get(stream));
            float omega = 2f * Mathf.PI * hz;
            float omegaSq = omega * omega;
            float twoOmegaDamping = 2f * omega * damping;

            Vector3 pos = chestSpringState[0];
            Vector3 vel = chestSpringState[1];

            // Head velocity feed-forward: blend a fraction of the upstream head velocity into the
            // spring's velocity term before integrating. Without it the spring is purely reactive
            // (always lags the head); with it the chest leads quick moves the way a real torso
            // anticipates a head turn.
            float vfGain = Mathf.Max(0f, chestSpringHeadVelGain.Get(stream));
            if (vfGain > 0f)
            {
                Vector3 headVel = headLinearVelocity.Get(stream);
                if (IsFinite(headVel))
                    vel = Vector3.Lerp(vel, headVel, Mathf.Clamp01(vfGain));
            }

            // Implicit Euler: solve (vel_new, pos_new = pos + dt*vel_new) such that
            //   vel_new = vel + dt * (omega² * (target - pos_new) - 2*omega*damping * vel_new)
            // Substituting pos_new gives the closed-form denom below. Always stable.
            float denom = 1f + dt * twoOmegaDamping + dt * dt * omegaSq;
            Vector3 newVel = (vel + dt * omegaSq * (headTargetPos - pos)) / denom;
            Vector3 newPos = pos + dt * newVel;

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
        static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
            !float.IsNaN(v.y) && !float.IsInfinity(v.y) &&
            !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        // Pelvis tilts forward to share the lean past the threshold. Without this, a deep forward
        // reach makes the spine swallow the entire bend and everything above the hips folds.
        Quaternion ApplyHipHinge(AnimationStream stream, Vector3 headPos, Vector3 hipsPos, Quaternion hipsRot, Vector3 playerUp)
        {
            float startDeg = hipHingeStartDeg.Get(stream);
            float maxAddDeg = hipHingeMaxAddDeg.Get(stream);
            if (maxAddDeg <= 0f)
                return hipsRot;

            Vector3 hipsToHead = headPos - hipsPos;
            float upDot = Vector3.Dot(hipsToHead, playerUp);
            Vector3 horizontal = hipsToHead - playerUp * upDot;
            float horizMag = horizontal.magnitude;
            if (horizMag < k_Epsilon || upDot <= 0f)
                return hipsRot;

            float leanDeg = Mathf.Atan2(horizMag, upDot) * Mathf.Rad2Deg;
            if (leanDeg <= startDeg)
                return hipsRot;

            float excess = leanDeg - startDeg;
            float addDeg = Mathf.Min(excess * 0.5f, maxAddDeg);

            Vector3 hingeAxis = Vector3.Cross(playerUp, horizontal / horizMag);
            if (hingeAxis.sqrMagnitude < k_SqrEpsilon)
                return hipsRot;
            hingeAxis.Normalize();
            return Quaternion.AngleAxis(addDeg, hingeAxis) * hipsRot;
        }
        // Anatomy: cervical lordosis. Real heads sit a few centimeters forward of the neck axis;
        // adds a small forward pitch to the neck so the head reads more naturally during look-down
        // poses where the rig's stiff vertical neck would otherwise feel stilted.
        // Pitch-aware in both directions:
        //   - at look-down, the base 5° gets a configurable extra-bend (gain) added on top.
        //   - at look-up, the base 5° fades to 0 — without this, the constant forward bend
        //     applied after the IK solve compresses the head down into the upper chest, since
        //     lordosis rotates the neck after the chest→neck→head chain has already reached the
        //     head target and no re-solve happens.
        // Pitch fraction is read from head-target forward vs playerUp, so it stays well-defined at
        // the look-up/down poles (the vector-based dot doesn't gimbal like Euler decomposition).
        void ApplyCervicalLordosis(AnimationStream stream)
        {
            if (!HandleNeck.IsValid(stream))
                return;

            const float baseDeg = 5f;
            float gain = Mathf.Max(0f, lordosisPitchGainDeg.Get(stream));

            Quaternion headRot = V4ToQuat(targetRotationHead.Get(stream));
            Vector3 headForward = headRot * Vector3.forward;
            Vector3 up = playerUp.Get(stream);
            if (up.sqrMagnitude < k_SqrEpsilon) up = Vector3.up;
            else up.Normalize();
            float pitchSigned = Vector3.Dot(headForward, up); // +1 = looking straight up, -1 = down
            float lookUpFrac = Mathf.Clamp01(pitchSigned);
            float lookDownFrac = Mathf.Clamp01(-pitchSigned);

            float totalDeg = baseDeg * (1f - lookUpFrac) + gain * lookDownFrac;
            if (totalDeg <= 0f)
                return;

            Quaternion neckRot = HandleNeck.GetRotation(stream);
            Quaternion delta = Quaternion.AngleAxis(totalDeg, neckRot * Vector3.right);
            HandleNeck.SetRotation(stream, delta * neckRot);
        }
        // Anatomy: shoulder slide. Shoulders don't fully follow chest twist past ~30° because the
        // scapula slides on the rib cage. Counter-yaw both shoulders by a fraction of the chest's
        // twist relative to hips, capped at 15°.
        void ApplyShoulderSlide(AnimationStream stream)
        {
            if (!HandleHips.IsValid(stream) || !HandleChest.IsValid(stream))
                return;

            Quaternion hipsRot = HandleHips.GetRotation(stream);
            Quaternion chestRot = HandleChest.GetRotation(stream);
            Quaternion chestLocal = Quaternion.Inverse(hipsRot) * chestRot;
            float chestYaw = SignedEuler(chestLocal.eulerAngles).y;

            const float threshold = 30f;
            const float maxCounter = 15f;
            const float fraction = 0.4f;
            float excess = Mathf.Abs(chestYaw) - threshold;
            if (excess <= 0f)
                return;

            float counterYaw = -Mathf.Sign(chestYaw) * Mathf.Min(excess * fraction, maxCounter);
            ApplyShoulderYaw(stream, HandleLeftShoulder, hipsRot, counterYaw);
            ApplyShoulderYaw(stream, HandleRightShoulder, hipsRot, counterYaw);
        }
        void ApplyShoulderYaw(AnimationStream stream, ReadWriteTransformHandle shoulder, Quaternion hipsRot, float yawDeg)
        {
            if (!shoulder.IsValid(stream))
                return;
            Quaternion delta = hipsRot * Quaternion.AngleAxis(yawDeg, Vector3.up) * Quaternion.Inverse(hipsRot);
            shoulder.SetRotation(stream, delta * shoulder.GetRotation(stream));
        }
        // Yaw the chest toward the hand-target midpoint relative to hips. Applied around the
        // hips-local Y axis, which is approximately the spine "twist" axis in normal stances —
        // close to orthogonal to the head-reach direction, so SolveTwoBoneSpine doesn't undo it.
        // Skipped when a chest tracker is active; that case owns chest rotation directly.
        void ApplyArmSwingChestFollow(AnimationStream stream)
        {
            float factor = chestArmSwingFactor.Get(stream);
            if (factor <= 0f)
                return;
            if (!HandleHips.IsValid(stream) || !HandleChest.IsValid(stream))
                return;

            bool leftEnabled = enabledLeftHand.Get(stream);
            bool rightEnabled = enabledRightHand.Get(stream);
            if (!leftEnabled && !rightEnabled)
                return;

            Vector3 leftPos = leftEnabled ? targetPositionLeftHand.Get(stream) : Vector3.zero;
            Vector3 rightPos = rightEnabled ? targetPositionRightHand.Get(stream) : Vector3.zero;
            Vector3 handMid;
            if (leftEnabled && rightEnabled)
                handMid = (leftPos + rightPos) * 0.5f;
            else if (leftEnabled)
                handMid = leftPos;
            else
                handMid = rightPos;

            Vector3 hipsPos = HandleHips.GetPosition(stream);
            Quaternion hipsRot = HandleHips.GetRotation(stream);
            Quaternion invHips = Quaternion.Inverse(hipsRot);
            Vector3 localMid = invHips * (handMid - hipsPos);

            float forwardDist = Mathf.Max(0.1f, Mathf.Abs(localMid.z));
            float yawDeg = Mathf.Atan2(localMid.x, forwardDist) * Mathf.Rad2Deg * factor;

            float maxDeg = chestArmSwingMaxDeg.Get(stream);
            if (maxDeg > 0f)
                yawDeg = Mathf.Clamp(yawDeg, -maxDeg, maxDeg);

            Quaternion deltaWorld = hipsRot * Quaternion.AngleAxis(yawDeg, Vector3.up) * invHips;
            HandleChest.SetRotation(stream, deltaWorld * HandleChest.GetRotation(stream));
        }
        // Distributes a fraction of the child bone's roll (around the parent bone's longitudinal
        // axis) onto a twist bone that sits as a child of the parent. Uses swing-twist quaternion
        // decomposition: the child's local rotation is split into a "swing" (axis perpendicular to
        // the bone) and a "twist" (axis along the bone). We apply only the twist component, scaled
        // by `fraction`, to the twist bone — the original child bone's rotation is not changed.
        // No-op when the twist handle isn't bound (rig has no twist bone) or fraction is zero.
        void SolveArmTwist(AnimationStream stream, ReadWriteTransformHandle parent, ReadWriteTransformHandle child, ReadWriteTransformHandle twist, float fraction)
        {
            if (!twist.IsValid(stream) || fraction <= 0f)
                return;
            if (!parent.IsValid(stream) || !child.IsValid(stream))
                return;

            Quaternion parentRot = parent.GetRotation(stream);
            Quaternion childRot = child.GetRotation(stream);

            // Bone-local longitudinal axis: direction from parent origin to child origin in
            // parent's local frame. This adapts to whatever axis the rig uses (X, Y, or Z).
            Vector3 worldDir = child.GetPosition(stream) - parent.GetPosition(stream);
            if (worldDir.sqrMagnitude < k_SqrEpsilon)
                return;
            Vector3 axis = (Quaternion.Inverse(parentRot) * worldDir).normalized;
            if (axis.sqrMagnitude < k_SqrEpsilon)
                return;

            // Child's rotation in parent-local space, then twist component around `axis`.
            Quaternion childLocal = Quaternion.Inverse(parentRot) * childRot;
            Quaternion twistOnly = ExtractTwist(childLocal, axis);
            Quaternion partialTwist = Quaternion.Slerp(Quaternion.identity, twistOnly, Mathf.Clamp01(fraction));

            // Twist bone is a child of `parent`, so its world rotation is parent * partial.
            twist.SetRotation(stream, parentRot * partialTwist);
        }
        // Swing-twist decomposition: extracts the rotation of `q` around `axis` (unit vector).
        // q = swing * twist, where twist's axis is parallel to `axis`.
        static Quaternion ExtractTwist(Quaternion q, Vector3 axis)
        {
            Vector3 ra = new Vector3(q.x, q.y, q.z);
            Vector3 p = Vector3.Project(ra, axis);
            Quaternion twist = new Quaternion(p.x, p.y, p.z, q.w);
            float magSq = twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w;
            if (magSq < k_SqrEpsilon)
                return Quaternion.identity;
            float invMag = 1f / Mathf.Sqrt(magSq);
            return new Quaternion(twist.x * invMag, twist.y * invMag, twist.z * invMag, twist.w * invMag);
        }
        static Vector3 SignedEuler(Vector3 e)
        {
            return new Vector3(
                e.x > 180f ? e.x - 360f : e.x,
                e.y > 180f ? e.y - 360f : e.y,
                e.z > 180f ? e.z - 360f : e.z
            );
        }
        static Vector3 ClampAsymmetric(Vector3 e, float maxFwd, float maxBack, float maxLat)
        {
            if (e.x > 0f) e.x = Mathf.Min(e.x, maxFwd);
            else e.x = Mathf.Max(e.x, -maxBack);
            e.y = Mathf.Clamp(e.y, -maxLat, maxLat);
            e.z = Mathf.Clamp(e.z, -maxLat, maxLat);
            return e;
        }
        public void SolveShoulder(AnimationStream stream, ReadWriteTransformHandle shoulderHandle, BoolProperty enabledProp, Vector3Property handTargetPosProp,  Vector3 tposeLocalDir, Quaternion tposeShoulderRot, Quaternion tposeChestRot, float tposeArmLength, bool isLeft)
        {
            if (!shoulderHandle.IsValid(stream) || !enabledProp.Get(stream))
                return;

            Vector3 handTargetPos = handTargetPosProp.Get(stream);
            Vector3 shoulderPos = shoulderHandle.GetPosition(stream);

            // Get chest orientation for reference frame
            Quaternion chestRot = HandleChest.IsValid(stream) ? HandleChest.GetRotation(stream) : Quaternion.identity;

            // Compute hand direction relative to shoulder in chest space
            Vector3 shoulderToHand = handTargetPos - shoulderPos;
            float reachLen = shoulderToHand.magnitude;
            if (reachLen < k_Epsilon || tposeArmLength < k_Epsilon)
                return;

            // Normalize reach ratio (0 = at rest, 1 = fully extended)
            float rawReachRatio = Mathf.Clamp01(reachLen / (tposeArmLength * 1.1f));

            // HVR-IK inspired: don't engage shoulder rotation until hand is at 70% of arm length
            // This prevents premature shoulder movement for close-to-body motions
            const float shoulderEngageThreshold = 0.7f;
            float reachRatio;
            if (rawReachRatio < shoulderEngageThreshold)
            {
                reachRatio = 0f;
            }
            else
            {
                // Remap 0.7-1.0 range to 0.0-1.0 with smooth start
                float t = (rawReachRatio - shoulderEngageThreshold) / (1f - shoulderEngageThreshold);
                reachRatio = t * t; // quadratic ease-in for natural engagement
            }

            // Transform hand direction into chest-local space
            Quaternion invChest = Quaternion.Inverse(chestRot);
            Vector3 localHandDir = invChest * shoulderToHand.normalized;

            // Elevation: how much the hand is above shoulder level (local Y)
            float elevationFactor = shoulderElevationFactor.Get(stream);
            float elevation = Mathf.Clamp01(localHandDir.y) * reachRatio * elevationFactor;

            // Protraction: how much the hand is in front (local Z)
            float protractionFactor = shoulderProtractionFactor.Get(stream);
            float protraction = Mathf.Clamp01(localHandDir.z) * reachRatio * protractionFactor;

            // Cross-body: hand reaching to opposite side (local X)
            float crossBody = isLeft ? Mathf.Clamp01(-localHandDir.x) : Mathf.Clamp01(localHandDir.x);
            float crossBodyContrib = crossBody * reachRatio * protractionFactor * 0.5f;

            // Build shoulder rotation adjustment
            // Elevation rotates around chest-local Z (raises shoulder)
            // Protraction rotates around chest-local Y (pulls shoulder forward)
            float elevAngle = elevation * 30f; // max 30 degrees elevation
            float protAngle = (protraction + crossBodyContrib) * 20f; // max 20 degrees protraction

            Quaternion elevRot = Quaternion.AngleAxis(isLeft ? elevAngle : -elevAngle, Vector3.forward);
            Quaternion protRot = Quaternion.AngleAxis(isLeft ? -protAngle : protAngle, Vector3.up);

            // Apply in chest space
            Quaternion baseRot = tposeShoulderRot;
            Quaternion adjustedRot = chestRot * (invChest * baseRot) * elevRot * protRot;

            // Blend between tracker rotation and computed rotation
            Quaternion trackerRot = V4ToQuat(isLeft ? TargetRotationLeftShoulder.Get(stream) : TargetRotationRightShoulder.Get(stream));
            Quaternion trackerFinal = trackerRot * (isLeft ? targetOffsetLeftShoulder : targetOffsetRightShoulder);

            // Blend: at low reach, trust tracker more; at high reach, trust computed more
            float computedWeight = Mathf.Clamp01(reachRatio * 1.5f);
            shoulderHandle.SetRotation(stream, Quaternion.Slerp(trackerFinal, adjustedRot, computedWeight * (elevation + protraction + crossBodyContrib)));
        }
        public void SolveTwoBoneSpine(AnimationStream stream, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip, AffineTransform target, Quaternion targetOffset, Vector3 bendNormal)
        {
            // Read current joint positions
            Vector3 aPos = root.GetPosition(stream);
            Vector3 bPos = mid.GetPosition(stream);
            Vector3 cPos = tip.GetPosition(stream);

            // Target with offset applied in target space
            Vector3 tPos = target.translation;
            Quaternion tRot = target.rotation * targetOffset;

            // Current bone vectors
            Vector3 ab = bPos - aPos;
            Vector3 bc = cPos - bPos;
            Vector3 ac = cPos - aPos;
            Vector3 at = tPos - aPos;

            float abLen = ab.magnitude;
            float bcLen = bc.magnitude;
            float acLen = ac.magnitude;
            float atLen = at.magnitude;
            float oldAbcAngle = TriangleAngle(acLen, abLen, bcLen);
            float newAbcAngle = TriangleAngle(atLen, abLen, bcLen);

            // Compute rotation axis for mid joint bend
            Vector3 axis = ComputeIkAxis(bendNormal);

            // Rotate mid joint by half the angle delta (distributes motion)
            float halfAngle = 0.5f * (oldAbcAngle - newAbcAngle);
            float s = Mathf.Sin(halfAngle);
            float c = Mathf.Cos(halfAngle);
            Quaternion deltaMid = new Quaternion(axis.x * s, axis.y * s, axis.z * s, c);
            mid.SetRotation(stream, deltaMid * mid.GetRotation(stream));

            // Re-evaluate and swing root so AC aligns with AT
            cPos = tip.GetPosition(stream);
            ac = cPos - aPos;
            root.SetRotation(stream, QuaternionExt.FromToRotation(ac, at) * root.GetRotation(stream));

            // Set tip rotation to match target orientation (+offset)
            tip.SetRotation(stream, tRot);
        }
        private Vector3 ComputeIkAxis(Vector3 bendNormal)
        {
            Vector3 axis;
            axis = bendNormal;
            float mag2 = axis.sqrMagnitude;
            if (mag2 < k_SqrEpsilon)
            {
                // Deterministic fallback to avoid NaNs/garbage under Burst
                return Vector3.forward;
            }

            return axis / Mathf.Sqrt(mag2);
        }
        static Vector3 ClampHipsAroundHead(Vector3 headPos, Vector3 hipsPos, float restDistance, float minFactor, float maxFactor, Vector3 playerUp)
        {
            Vector3 headToHips = hipsPos - headPos;
            float sqrMag = headToHips.sqrMagnitude;
            if (sqrMag < k_SqrEpsilon)
            {
                return headPos - restDistance * minFactor * playerUp;
            }

            // Use the head→hips direction as the "up" axis for the clamp
            Vector3 up = headToHips / Mathf.Sqrt(sqrMag);

            float verticalDot = Vector3.Dot(headToHips, up);
            Vector3 vertical = up * verticalDot;
            Vector3 lateral = headToHips - vertical;

            float absY = Mathf.Abs(verticalDot);
            float minY = restDistance * minFactor;
            float maxY = restDistance * maxFactor;
            float clampedY = Mathf.Clamp(absY, minY, maxY) * Mathf.Sign(verticalDot);
            vertical = up * clampedY;

            float lateralLen = lateral.magnitude;
            float maxLateral = restDistance * maxHorizontalFactor;

            if (lateralLen > maxLateral && lateralLen > k_Epsilon)
            {
                lateral *= maxLateral / lateralLen;
            }

            return headPos + vertical + lateral;
        }
        static Vector3 EnforceSpineBendLimit(Vector3 headPos, Vector3 hipsPos, float maxBendDeg, Vector3 playerUp)
        {
            if (maxBendDeg <= 0f)
            {
                return hipsPos;
            }

            Vector3 diff = hipsPos - headPos;
            float sqrMag = diff.sqrMagnitude;
            if (sqrMag < k_MinMag)
            {
                return hipsPos;
            }

            Vector3 up = playerUp;

            // Decompose into vertical (along -up, hips below head) and lateral
            float verticalDot = Vector3.Dot(diff, -up); // positive if hips are "below" head
            Vector3 vertical = -up * verticalDot;
            Vector3 lateral = diff - vertical;

            float lateralLen = lateral.magnitude;
            float absVertical = Mathf.Abs(verticalDot);

            if (lateralLen < k_MinMag || absVertical < k_MinMag)
            {
                return hipsPos;
            }

            // Current bend angle from head to hips
            float currentAngle = Mathf.Atan2(lateralLen, absVertical) * Mathf.Rad2Deg;
            if (currentAngle <= maxBendDeg)
            {
                return hipsPos;
            }

            // We want lateral / newVertical = tan(maxBend)
            float maxRatio = Mathf.Tan(maxBendDeg * Mathf.Deg2Rad);
            float newVertical = lateralLen / Mathf.Max(maxRatio, k_MinMag);

            // Push hips further down in the same direction along -up
            float finalVertical = Mathf.Sign(verticalDot) * Mathf.Max(newVertical, absVertical);
            Vector3 newVerticalVec = -up * finalVertical;

            Vector3 newDiff = newVerticalVec + (lateralLen > k_MinMag ? lateral.normalized * lateralLen : Vector3.zero);
            return headPos + newDiff;
        }
        /// <summary>
        /// Anti-contortionist: enforces minimum hip-to-head distance based on angular similarity
        /// between head and hip facing directions. When facing same direction, min distance is near
        /// full rest length; facing opposite, it can compress more. From HVR-IK's HIKSpineSolver.
        /// </summary>
        static Vector3 AntiContortionist(Vector3 headPos, Quaternion headRot, Vector3 hipsPos, Quaternion hipsRot, float restDistance)
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
        /// <summary>
        /// Spine buckling fix: when the body is upright but the hip-to-head distance is shorter
        /// than rest pose, the FABRIK chain can buckle into unnatural S-curves. This pushes the
        /// hips downward to prevent oscillation. From HVR-IK's HIKSpineSolver.
        /// </summary>
        static Vector3 MitigateSpineBuckling(Vector3 headPos, Quaternion hipsRot, Vector3 hipsPos, float restDistance, Vector3 playerUp)
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
        static Quaternion ClampRotation(Quaternion current, Quaternion reference, float maxAngleDeg)
        {
            // Angle between the two orientations
            float angle = Quaternion.Angle(reference, current);
            if (angle <= maxAngleDeg)
            {
                return current;
            }

            // Scale back toward the reference so the final difference is exactly maxAngleDeg
            float t = maxAngleDeg / Mathf.Max(angle, k_Epsilon);
            return Quaternion.Slerp(reference, current, t);
        }
        public void ApplyRotation(AnimationStream stream, BoolProperty enabledProp, ReadWriteTransformHandle handle, Vector4Property targetRotProp, Quaternion RotationOffset)
        {
            if (!handle.IsValid(stream))
            {
                return;
            }

            if (enabledProp.Get(stream))
            {
                handle.SetRotation(stream, V4ToQuat(targetRotProp.Get(stream)) * RotationOffset);
            }
        }
        public void SolveTwoBoneIKArms(AnimationStream stream, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip, AffineTransform target, AffineTransform hint, bool hintWeight, Quaternion targetOffset)
        {
            Vector3 aPosition = root.GetPosition(stream);
            Vector3 bPosition = mid.GetPosition(stream);
            Vector3 cPosition = tip.GetPosition(stream);

            Vector3 targetPos = target.translation;
            Quaternion targetRot = target.rotation;

            Vector3 tPosition = targetPos;
            Quaternion tRotation = targetRot * targetOffset;

            // Segment vectors
            Vector3 ab = bPosition - aPosition;
            Vector3 bc = cPosition - bPosition;
            Vector3 ac = cPosition - aPosition;

            float abLen = ab.magnitude;
            float bcLen = bc.magnitude;
            float totalLen = abLen + bcLen;

            // Original target vector
            Vector3 atCorrected = tPosition - aPosition;
            float acLen = ac.magnitude;

            float oldAbcAngle = TriangleAngle(acLen, abLen, bcLen);
            //Vector3 atCorrected = correctedTargetPos - aPosition;
            float atCorrectedLen = atCorrected.magnitude;

            float newAbcAngle = TriangleAngle(atCorrectedLen, abLen, bcLen);
            // -------------------------------------------------------------

            // Prefer current bend plane; fallbacks to hint / at if collinear.
            Vector3 axis = Vector3.Cross(ab, bc);
            if (axis.sqrMagnitude < k_SqrEpsilon)
            {
                axis = hintWeight ? Vector3.Cross(hint.translation - aPosition, bc) : Vector3.zero;
                if (axis.sqrMagnitude < k_SqrEpsilon)
                {
                    axis = Vector3.Cross(atCorrected, bc); // use corrected
                }

                if (axis.sqrMagnitude < k_SqrEpsilon)
                {
                    axis = playerUp.Get(stream);
                }
            }
            axis = axis.normalized;

            float a = 0.5f * (oldAbcAngle - newAbcAngle);
            float sin = Mathf.Sin(a);
            float cos = Mathf.Cos(a);
            Quaternion deltaR = new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, cos);
            mid.SetRotation(stream, deltaR * mid.GetRotation(stream));

            // Re-evaluate after rotating mid
            cPosition = tip.GetPosition(stream);
            ac = cPosition - aPosition;

            // --- IMPORTANT: rotate root towards *corrected* direction, not raw tPosition ---
            if (atCorrectedLen > k_Epsilon)
            {
                Quaternion rootDelta = QuaternionExt.FromToRotation(ac, atCorrected);
                root.SetRotation(stream, rootDelta * root.GetRotation(stream));
            }
            if (hintWeight)
            {
                float acSqrMag = ac.sqrMagnitude;
                if (acSqrMag > 0f)
                {
                    bPosition = mid.GetPosition(stream);
                    cPosition = tip.GetPosition(stream);
                    ab = bPosition - aPosition;
                    ac = cPosition - aPosition;

                    Vector3 acNorm = ac / Mathf.Sqrt(acSqrMag);
                    Vector3 ah = hint.translation - aPosition;
                    Vector3 abProj = ab - acNorm * Vector3.Dot(ab, acNorm);
                    Vector3 ahProj = ah - acNorm * Vector3.Dot(ah, acNorm);

                    // you can also soften this threshold if hinting fights with max reach
                    if (abProj.sqrMagnitude > (totalLen * totalLen * 0.001f) && ahProj.sqrMagnitude > 0f)
                    {
                        Quaternion hintR = QuaternionExt.FromToRotation(abProj, ahProj);
                        hintR = QuaternionExt.NormalizeSafe(hintR);
                        root.SetRotation(stream, hintR * root.GetRotation(stream));
                    }
                }
            }

            tip.SetRotation(stream, tRotation);
        }
        /// <summary>
        /// Computes arm bend direction using the 3D lookup table.
        /// Converts hand position to chest-relative normalized space, then samples the table.
        /// </summary>
        Vector3 ComputeArmBendFromLookup(AnimationStream stream, Vector3 shoulderPos, Vector3 handTargetPos, float armLength, bool isLeft)
        {
            if (!HandleChest.IsValid(stream) || armLength < k_Epsilon)
                return isLeft ? Vector3.left : Vector3.right;

            Quaternion chestRot = HandleChest.GetRotation(stream);
            Quaternion invChest = Quaternion.Inverse(chestRot);

            // Transform hand position to chest-local, shoulder-centered, arm-length-normalized space
            Vector3 shoulderToHand = handTargetPos - shoulderPos;
            Vector3 localPos = invChest * shoulderToHand / armLength;

            // Mirror X for left arm (lookup table is generated for right arm perspective)
            if (isLeft)
                localPos.x = -localPos.x;

            // Sample the lookup table
            NativeArray<Vector3> table = isLeft ? ArmBendLookupLeft : ArmBendLookupRight;
            Vector3 localBend = BasisArmBendLookup.SampleTrilinear(table, localPos);

            // Mirror result back for left arm
            if (isLeft)
                localBend.x = -localBend.x;

            // Transform bend direction back to world space
            return (chestRot * localBend).normalized;
        }
        public static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abSqr = Vector3.Dot(ab, ab);
            if (abSqr <= k_SqrEpsilon) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / abSqr);
            return a + ab * t;
        }
        public static void SegmentSegmentClosestPoints(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s, out float t, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);

            if (a <= k_SqrEpsilon && e <= k_SqrEpsilon)
            {
                s = t = 0.0f; c1 = p1; c2 = p2; return;
            }
            if (a <= k_SqrEpsilon)
            {
                s = 0.0f; t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= k_SqrEpsilon)
                {
                    t = 0.0f; s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;

                    if (denom != 0.0f) s = Mathf.Clamp01((b * f - c * e) / denom);
                    else s = 0.0f;

                    t = (b * s + f) / e;
                    if (t < 0.0f) { t = 0.0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1.0f) { t = 1.0f; s = Mathf.Clamp01((b - c) / a); }
                }
            }

            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }
        public static Vector3 CapsuleCapsuleResolve(Vector3 p1, Vector3 q1, float r1, Vector3 p2, Vector3 q2, float r2, Vector3 playerUp)
        {
            SegmentSegmentClosestPoints(p1, q1, p2, q2, out _, out _, out var c1, out var c2);
            Vector3 n = c1 - c2;
            float dSqr = Vector3.Dot(n, n);
            float rSum = r1 + r2;

            if (dSqr >= rSum * rSum) return Vector3.zero;

            Vector3 normal;
            if (dSqr > k_SqrEpsilon) normal = n / Mathf.Sqrt(dSqr);
            else
            {
                Vector3 axis = (q2 - p2);
                normal = Vector3.Normalize(Vector3.Cross(axis, playerUp));
                if (normal.sqrMagnitude < k_MinMag) normal = Vector3.Normalize(Vector3.Cross(axis, Vector3.right));
                if (normal.sqrMagnitude < k_MinMag) normal = playerUp;
            }

            float d = Mathf.Sqrt(Mathf.Max(dSqr, 0f));
            float penetration = (rSum - d);
            return normal * penetration;
        }
        public static void SwingElbowAroundAC(AnimationStream stream, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip, Vector3 desiredB)
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
        public static Vector3 PushOutFromCapsule(Vector3 p, Vector3 a, Vector3 b, float radiusWithSkin, Vector3 playerUp)
        {
            Vector3 q = ClosestPointOnSegment(p, a, b);
            Vector3 qp = p - q;
            float dSqr = Vector3.Dot(qp, qp);
            if (dSqr >= radiusWithSkin * radiusWithSkin) return p;
            float d = Mathf.Sqrt(Mathf.Max(dSqr, k_SqrEpsilon));
            Vector3 n = (d > 0f) ? (qp / d) : playerUp;
            return q + n * radiusWithSkin;
        }
        /// <summary>
        /// Evaluates the Two-Bone IK algorithm.
        /// </summary>
        /// <param name="stream">The animation stream to work on.</param>
        /// <param name="root">The transform handle for the root transform.</param>
        /// <param name="mid">The transform handle for the mid transform.</param>
        /// <param name="tip">The transform handle for the tip transform.</param>
        /// <param name="target">The transform handle for the target transform.</param>
        /// <param name="hint">The transform handle for the hint transform.</param>
        /// <param name="HasHint">The weight for which hint transform has an effect on IK calculations. This is a value in between 0 and 1.</param>
        /// <param name="targetOffset">The offset applied to the target transform.</param>
        public void SolveTwoBone(AnimationStream stream, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip, AffineTransform target, AffineTransform hint, float hintWeight, Quaternion targetOffset, Vector3 BendNormal)
        {
            Vector3 aPosition = root.GetPosition(stream);
            Vector3 bPosition = mid.GetPosition(stream);
            Vector3 cPosition = tip.GetPosition(stream);

            Vector3 targetPos = target.translation;
            Quaternion targetRot = target.rotation;

            Vector3 tPosition = targetPos;
            Quaternion tRotation = targetRot * targetOffset;

            bool hasHint = hintWeight > 0f;

            // Segment vectors
            Vector3 ab = bPosition - aPosition;
            Vector3 bc = cPosition - bPosition;
            Vector3 ac = cPosition - aPosition;

            float abLen = ab.magnitude;
            float bcLen = bc.magnitude;
            float acLen = ac.magnitude;

            float maxReach = abLen + bcLen;
            float oldAbcAngle = TriangleAngle(acLen, abLen, bcLen);
            Vector3 atCorrected = tPosition - aPosition;
            float atCorrectedLen = atCorrected.magnitude;

            float newAbcAngle = TriangleAngle(atCorrectedLen, abLen, bcLen);

            Vector3 axis;
            if (hasHint)
            {
                axis = Vector3.Cross(hint.translation - aPosition, bc);

                if (axis.sqrMagnitude < k_SqrEpsilon)
                    axis = Vector3.Cross(atCorrected, bc);

                if (axis.sqrMagnitude < k_SqrEpsilon)
                    axis = BendNormal;
            }
            else
            {
                axis = BendNormal;
            }

            // Near full extension the cross products above become unreliable.
            // Blend toward BendNormal which is always stable (derived from hips rotation).
            float extensionRatio = (maxReach > k_Epsilon) ? (atCorrectedLen / maxReach) : 0f;
            if (extensionRatio > 0.9f)
            {
                float blend = Mathf.Clamp01((extensionRatio - 0.9f) / 0.1f);
                axis = Vector3.Slerp(axis.normalized, BendNormal.normalized, blend);
            }

            axis = Vector3.Normalize(axis);

            float a = 0.5f * (oldAbcAngle - newAbcAngle);
            float sin = Mathf.Sin(a);
            float cos = Mathf.Cos(a);
            Quaternion deltaR = new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, cos);
            mid.SetRotation(stream, deltaR * mid.GetRotation(stream));

            // Re-evaluate after rotating mid
            cPosition = tip.GetPosition(stream);
            ac = cPosition - aPosition;

            if (atCorrectedLen > k_Epsilon)
            {
                root.SetRotation(stream, QuaternionExt.FromToRotation(ac, atCorrected) * root.GetRotation(stream));
            }

            if (hasHint)
            {
                float acSqrMag = ac.sqrMagnitude;
                if (acSqrMag > 0f)
                {
                    bPosition = mid.GetPosition(stream);
                    cPosition = tip.GetPosition(stream);
                    ab = bPosition - aPosition;
                    ac = cPosition - aPosition;

                    Vector3 acNorm = ac / Mathf.Sqrt(acSqrMag);
                    Vector3 ah = hint.translation - aPosition;
                    Vector3 abProj = ab - acNorm * Vector3.Dot(ab, acNorm);
                    Vector3 ahProj = ah - acNorm * Vector3.Dot(ah, acNorm);

                    if (abProj.sqrMagnitude > (maxReach * maxReach * 0.001f) && ahProj.sqrMagnitude > 0f)
                    {
                        // Scale hint rotation by weight — matches Unity's TwoBoneIK approach.
                        // At weight 1: full hint influence. At partial: proportionally less.
                        Quaternion hintR = QuaternionExt.FromToRotation(abProj, ahProj);
                        hintR.x *= hintWeight;
                        hintR.y *= hintWeight;
                        hintR.z *= hintWeight;
                        hintR = QuaternionExt.NormalizeSafe(hintR);
                        root.SetRotation(stream, hintR * root.GetRotation(stream));
                    }
                }
            }

            tip.SetRotation(stream, tRotation);
        }
        public Quaternion V4ToQuat(Vector4 v) => new Quaternion(v.x, v.y, v.z, v.w);
        public void SolveLegs(AnimationStream stream, FloatProperty enabledProp, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip, Vector3Property targetPosProp, Vector4Property targetRotProp, Vector3Property hintPosProp, Vector4Property hintRotProp, FloatProperty hintWeightProp, Quaternion targetOffset, Vector3Property bendNormalProp)
        {
            float posWeight = enabledProp.Get(stream);
            if (posWeight <= 0f)
            {
                return;
            }

            if (!(root.IsValid(stream) && mid.IsValid(stream) && tip.IsValid(stream)))
            {
                return;
            }

            // Save the pre-solve (animation) transforms so we can blend back toward them.
            // Reading tip/mid/root positions BEFORE the solve gives us the true animation pose,
            // not a stale IK-modified pose from a previous frame.
            Vector3 origRootPos = root.GetPosition(stream);
            Quaternion origRootRot = root.GetRotation(stream);
            Vector3 origMidPos = mid.GetPosition(stream);
            Quaternion origMidRot = mid.GetRotation(stream);
            Vector3 origTipPos = tip.GetPosition(stream);
            Quaternion origTipRot = tip.GetRotation(stream);

            // Solve at full strength toward the IK target
            Quaternion tRot = V4ToQuat(targetRotProp.Get(stream));
            Quaternion hRot = V4ToQuat(hintRotProp.Get(stream));
            float hintW = hintWeightProp.Get(stream);

            AffineTransform target = new AffineTransform(targetPosProp.Get(stream), tRot);
            AffineTransform hint = new AffineTransform(hintPosProp.Get(stream), hRot);
            Vector3 bendNormal = bendNormalProp.Get(stream);

            SolveTwoBone(stream, root, mid, tip, target, hint, hintW, targetOffset, bendNormal);

            // Blend the solved result back toward the original animation pose using the weight.
            // At weight 1: fully IK. At weight 0.5: halfway. Near 0: almost pure animation.
            if (posWeight < 1f)
            {
                root.SetPosition(stream, Vector3.Lerp(origRootPos, root.GetPosition(stream), posWeight));
                root.SetRotation(stream, Quaternion.Slerp(origRootRot, root.GetRotation(stream), posWeight));
                mid.SetRotation(stream, Quaternion.Slerp(origMidRot, mid.GetRotation(stream), posWeight));
                tip.SetPosition(stream, Vector3.Lerp(origTipPos, tip.GetPosition(stream), posWeight));
                tip.SetRotation(stream, Quaternion.Slerp(origTipRot, tip.GetRotation(stream), posWeight));
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Apply(AnimationStream stream, ReadWriteTransformHandle h, Vector3Property p, Vector4Property r, Vector4Property o, BoolProperty sw)
        {
            if (h.IsValid(stream))
            {
                if (sw.Get(stream))
                {

                    Vector3 targetPos = p.Get(stream);
                    Quaternion targetRot = V4ToQuat(r.Get(stream));
                    Quaternion offsetRot = V4ToQuat(o.Get(stream));
                    Quaternion finalRot = targetRot * offsetRot;

                    h.SetPosition(stream, targetPos);
                    h.SetRotation(stream, finalRot);
                }
            }
        }
        public void SolveHand(AnimationStream stream, BoolProperty enabledProp, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip, Vector3Property targetPosProp, Vector4Property targetRotProp, Vector3Property hintPosProp, Vector4Property hintRotProp, BoolProperty hintWeightProp, Quaternion targetOffset, ReadWriteTransformHandle chestStart, ReadWriteTransformHandle chestEnd, FloatProperty chestRadius, FloatProperty collisionSkin, BoolProperty collisionsEnabled, FloatProperty handRadius, FloatProperty handSkin, BoolProperty useHandCapsule, BoolProperty protectElbow)
        {
            if (!enabledProp.Get(stream))
            {
                return;
            }
            if (!(root.IsValid(stream) && mid.IsValid(stream) && tip.IsValid(stream)))
            {
                return;
            }

            // Read inputs
            Vector3 tgtPos = targetPosProp.Get(stream);
            Quaternion tgtRot = V4ToQuat(targetRotProp.Get(stream));
            Vector3 hintPos = hintPosProp.Get(stream);
            Quaternion hintRot = V4ToQuat(hintRotProp.Get(stream));

            var target = new AffineTransform(tgtPos, tgtRot);
            var hint = new AffineTransform(hintPos, hintRot);
            bool hasHint = hintWeightProp.Get(stream);

            // Use lookup table for arm bend direction when available
            if (HasArmBendLookup)
            {
                Vector3 shoulderPos = root.GetPosition(stream);
                float upperLen = (mid.GetPosition(stream) - shoulderPos).magnitude;
                float lowerLen = (tip.GetPosition(stream) - mid.GetPosition(stream)).magnitude;
                float armLen = upperLen + lowerLen;
                bool isLeft = Vector3.Dot(shoulderPos - HandleChest.GetPosition(stream), HandleChest.GetRotation(stream) * Vector3.right) < 0f;

                Vector3 lookupBend = ComputeArmBendFromLookup(stream, shoulderPos, tgtPos, armLen, isLeft);
                // Blend lookup direction with existing hint (70% lookup, 30% original hint)
                Vector3 blendedHint = Vector3.Lerp(hintPos, shoulderPos + lookupBend * armLen * 0.5f, 0.7f);
                hint = new AffineTransform(blendedHint, hintRot);
            }

            // Solve arm — hand lands exactly at controller position.
            // Collision NEVER influences the IK solve. The hand must match the controller 1:1.
            SolveTwoBoneIKArms(stream, root, mid, tip, target, hint, hasHint, targetOffset);

            // Post-solve cosmetic push: gently nudge the elbow outward if it's inside the chest.
            // SwingElbowAroundAC rotates the shoulder around the shoulder→hand axis,
            // which moves the elbow without moving the hand (hand is on the rotation axis).
            // No re-solve needed — this is a push, not a wall. The arm CAN enter the chest.
            bool doCollisions = collisionsEnabled.Get(stream) && chestStart.IsValid(stream) && chestEnd.IsValid(stream);
            if (doCollisions && protectElbow.Get(stream))
            {
                Vector3 chestA = chestStart.GetPosition(stream);
                Vector3 chestB = chestEnd.GetPosition(stream);
                float chestR = Mathf.Max(0f, chestRadius.Get(stream) + collisionSkin.Get(stream));

                Vector3 elbowPos = mid.GetPosition(stream);
                Vector3 closest = ClosestPointOnSegment(elbowPos, chestA, chestB);
                Vector3 toElbow = elbowPos - closest;
                float dist = toElbow.magnitude;

                // Only push if elbow is inside the capsule radius
                if (dist < chestR && dist > k_Epsilon)
                {
                    // Push strength: gentle and proportional to penetration depth
                    // At the surface (dist == chestR): no push
                    // At the center (dist == 0): max push
                    float penetration = chestR - dist;
                    float pushStrength = penetration / chestR; // 0 at surface, 1 at center
                    pushStrength *= 0.35f; // cap — never fully resolve in one frame

                    Vector3 pushDir = toElbow / dist; // outward from capsule axis
                    Vector3 desiredElbow = elbowPos + pushDir * (penetration * pushStrength);
                    SwingElbowAroundAC(stream, root, mid, tip, desiredElbow);
                }
            }
        }
        public float TriangleAngle(float aLen, float aLen1, float aLen2)
        {
            if (aLen1 <= k_Epsilon || aLen2 <= k_Epsilon)
            {
                return 0f;
            }

            float c = Mathf.Clamp((aLen1 * aLen1 + aLen2 * aLen2 - aLen * aLen) / (2.0f * aLen1 * aLen2), -1.0f, 1.0f);
            return Mathf.Acos(c);
        }
    }
    public class BasisFullBodyJobBinder : AnimationJobBinder<BasisFullIKConstraintJob, BasisFullBodyData>
    {
        public override BasisFullIKConstraintJob Create(Animator animator, ref BasisFullBodyData data, Component component)
        {
            var job = new BasisFullIKConstraintJob
            {
                HandleHips = BindHandle(animator, data.hips),
                HandleChest = BindHandle(animator, data.chest),
                HandleNeck = BindHandle(animator, data.neck),
                HandleHead = BindHandle(animator, data.head),
                HandleLeftUpperLeg = BindHandle(animator, data.LeftUpperLeg),
                HandleLeftLowerLeg = BindHandle(animator, data.LeftLowerLeg),
                HandleLeftFoot = BindHandle(animator, data.leftFoot),
                HandleRightUpperLeg = BindHandle(animator, data.RightUpperLeg),
                HandleRightLowerLeg = BindHandle(animator, data.RightLowerLeg),
                HandleRightFoot = BindHandle(animator, data.RightFoot),
                HandleLeftToe = BindHandle(animator, data.LeftToe),
                HandleRightToe = BindHandle(animator, data.RightToe),
                HandleLeftUpperArm = BindHandle(animator, data.leftUpperArm),
                HandleLeftLowerArm = BindHandle(animator, data.leftLowerArm),
                HandleLeftHand = BindHandle(animator, data.LeftHand),
                HandleRightUpperArm = BindHandle(animator, data.RightUpperArm),
                HandleRightLowerArm = BindHandle(animator, data.RightLowerArm),
                HandleRightHand = BindHandle(animator, data.RightHand),
                HandleLeftUpperArmTwist = BindHandle(animator, data.LeftUpperArmTwist),
                HandleLeftLowerArmTwist = BindHandle(animator, data.LeftLowerArmTwist),
                HandleRightUpperArmTwist = BindHandle(animator, data.RightUpperArmTwist),
                HandleRightLowerArmTwist = BindHandle(animator, data.RightLowerArmTwist),
                HandleSpine = BindHandle(animator, data.spine),
                HandleUpperChest = BindHandle(animator, data.upperChest),
                HandleLeftShoulder = BindHandle(animator, data.LeftShoulder),
                HandleRightShoulder = BindHandle(animator, data.RightShoulder),
                targetPositionHips = Vector3Property.Bind(animator, component, data.TargetPositionPropertyHips),
                targetPositionHead = Vector3Property.Bind(animator, component, data.TargetPositionPropertyHead),
                TargetChestPosition = Vector3Property.Bind(animator, component, data.PropertyChestPosition),
                bendNormalHead = Vector3Property.Bind(animator, component, data.BendNormalHeadProperty),
                playerUp = Vector3Property.Bind(animator, component, data.PlayerUpProperty),

                KneeBendPrefLeft = Vector3Property.Bind(animator, component, data.KneeBendPrefLeftProperty),
                KneeBendPrefRight = Vector3Property.Bind(animator, component, data.KneeBendPrefRightProperty),

                ElbowBendPrefLeft = Vector3Property.Bind(animator, component, data.ElbowBendPrefLeftProperty),
                ElbowBendPrefRight = Vector3Property.Bind(animator, component, data.ElbowBendPrefRightProperty),

                targetPositionLeftLowerLeg = Vector3Property.Bind(animator, component, data.TargetPositionPropertyLeftLowerLeg),
                hintPositionLeftLowerLeg = Vector3Property.Bind(animator, component, data.HintPositionPropertyLeftLowerLeg),
                targetPositionRightLowerLeg = Vector3Property.Bind(animator, component, data.TargetPositionPropertyRightLowerLeg),
                hintPositionRightLowerLeg = Vector3Property.Bind(animator, component, data.HintPositionPropertyRightLowerLeg),
                leftDrivenTargetPos = Vector3Property.Bind(animator, component, data.LeftDrivenTargetPosProperty),
                rightDrivenTargetPos = Vector3Property.Bind(animator, component, data.RightDrivenTargetPosProperty),
                targetPositionLeftHand = Vector3Property.Bind(animator, component, data.TargetPositionPropertyLeftHand),
                hintPositionLeftHand = Vector3Property.Bind(animator, component, data.HintPositionPropertyLeftHand),
                targetPositionRightHand = Vector3Property.Bind(animator, component, data.TargetPositionPropertyRightHand),
                hintPositionRightHand = Vector3Property.Bind(animator, component, data.HintPositionPropertyRightHand),
                targetRotationHips = Vector4Property.Bind(animator, component, data.TargetRotationPropertyHips),
                offsetRotationHips = Vector4Property.Bind(animator, component, data.OffsetRotationPropertyHips),
                targetRotationHead = Vector4Property.Bind(animator, component, data.TargetRotationPropertyHead),
                targetChestRotation = Vector4Property.Bind(animator, component, data.PropertyChestRotation),
                TargetRotationLeftShoulder = Vector4Property.Bind(animator, component, data.TargetRotationLeftShoulderProperty),
                TargetRotationRightShoulder = Vector4Property.Bind(animator, component, data.TargetRotationRightShoulderProperty),
                targetRotationLeftLowerLeg = Vector4Property.Bind(animator, component, data.TargetRotationPropertyLeftLowerLeg),
                hintRotationLeftLowerLeg = Vector4Property.Bind(animator, component, data.HintRotationPropertyLeftLowerLeg),
                targetRotationRightLowerLeg = Vector4Property.Bind(animator, component, data.TargetRotationPropertyRightLowerLeg),
                hintRotationRightLowerLeg = Vector4Property.Bind(animator, component, data.HintRotationPropertyRightLowerLeg),
                leftDrivenTargetRot = Vector4Property.Bind(animator, component, data.LeftDrivenTargetRotProperty),
                rightDrivenTargetRot = Vector4Property.Bind(animator, component, data.RightDrivenTargetRotProperty),
                targetRotationLeftHand = Vector4Property.Bind(animator, component, data.TargetRotationPropertyLeftHand),
                hintRotationLeftHand = Vector4Property.Bind(animator, component, data.HintRotationPropertyLeftHand),
                targetRotationRightHand = Vector4Property.Bind(animator, component, data.TargetRotationPropertyRightHand),
                hintRotationRightHand = Vector4Property.Bind(animator, component, data.HintRotationPropertyRightHand),
                enabledSpineIK = BoolProperty.Bind(animator, component, data.EnabledPropertySpineIK),
                HasChestTracker = BoolProperty.Bind(animator, component, data.HintWeightBoolPropertyHead),
                enabledLeftLowerLeg = FloatProperty.Bind(animator, component, data.EnabledPropertyLeftLowerLeg),
                hintWeightLeftLowerLeg = FloatProperty.Bind(animator, component, data.HintWeightBoolPropertyLeftLowerLeg),
                enabledRightLowerLeg = FloatProperty.Bind(animator, component, data.EnabledPropertyRightLowerLeg),
                hintWeightRightLowerLeg = FloatProperty.Bind(animator, component, data.HintWeightBoolPropertyRightLowerLeg),
                leftToeEnabled = BoolProperty.Bind(animator, component, data.LeftToeEnabledProperty),
                RightToeEnabled = BoolProperty.Bind(animator, component, data.RightToeEnabledProperty),
                enabledLeftHand = BoolProperty.Bind(animator, component, data.EnabledPropertyLeftHand),
                hintWeightLeftHand = BoolProperty.Bind(animator, component, data.HintWeightBoolPropertyLeftHand),
                enabledRightHand = BoolProperty.Bind(animator, component, data.EnabledPropertyRightHand),
                hintWeightRightHand = BoolProperty.Bind(animator, component, data.HintWeightBoolPropertyRightHand),
                protectElbow = BoolProperty.Bind(animator, component, data.ProtectElbowBoolProperty),
                collisionsEnabled = BoolProperty.Bind(animator, component, data.CollisionsEnabledBoolProperty),
                useHandCapsule = BoolProperty.Bind(animator, component, data.UseHandCapsuleBoolProperty),
                chestRadius = FloatProperty.Bind(animator, component, data.ChestRadiusFloatProperty),
                collisionSkin = FloatProperty.Bind(animator, component, data.CollisionSkinFloatProperty),
                handRadius = FloatProperty.Bind(animator, component, data.HandRadiusFloatProperty),
                handSkin = FloatProperty.Bind(animator, component, data.HandSkinFloatProperty),
                maxBendDeg = FloatProperty.Bind(animator, component, data.MaxBendDegFloatProperty),
                minFactor = FloatProperty.Bind(animator, component, data.MinFactorFloatProperty),
                maxFactor = FloatProperty.Bind(animator, component, data.MaxFactorFloatProperty),
                struggleStart = FloatProperty.Bind(animator, component, data.StruggleStartFloatProperty),
                struggleEnd = FloatProperty.Bind(animator, component, data.StruggleEndFloatProperty),
                MaxHipDeltaProperty = FloatProperty.Bind(animator, component, data.MaxHipDeltaPropertyDegFloatProperty),
                MaxChestDeltaProperty = FloatProperty.Bind(animator, component, data.MaxChestDeltaPropertyDegFloatProperty),
                enabledLeftShoulder = BoolProperty.Bind(animator, component, data.EnabledLeftShoulderProperty),
                enabledRightShoulder = BoolProperty.Bind(animator, component, data.EnabledRightShoulderProperty),
                targetOffsetLeftShoulder = data.m_CalibratedRotationLeftShoulder,
                targetOffsetRightShoulder = data.m_CalibratedRotationRightShoulder,
                targetOffsetNeck = data.m_CalibratedRotationNeck,
                targetOffsetHead = data.m_CalibratedRotationHead,
                targetOffsetChest = data.m_CalibratedRotationChest,
                targetOffsetLeftToe = data.m_CalibratedRotationLeftToe,
                targetOffsetRightToe = data.m_CalibratedRotationRightToe,
                targetOffsetLeftFoot = data.M_CalibrationLeftFootRotation,
                targetOffsetRightFoot = data.M_CalibrationRightFootRotation,
                targetOffsetLeftHand = data.m_CalibratedRotationLeftHand,
                targetOffsetRightHand = data.m_CalibratedRotationRightHand,
                MinHeadSpineHeight = FloatProperty.Bind(animator, component, data.MinHeadSpineHeightFloatProperty),

                // Shoulder solve bindings
                shoulderSolveEnabled = BoolProperty.Bind(animator, component, data.ShoulderSolveEnabledProperty),
                shoulderElevationFactor = FloatProperty.Bind(animator, component, data.ShoulderElevationFactorProperty),
                shoulderProtractionFactor = FloatProperty.Bind(animator, component, data.ShoulderProtractionFactorProperty),

                // Spine bend distribution bindings (per-axis pitch/yaw/roll)
                spineBendPitch = FloatProperty.Bind(animator, component, data.SpineBendPitchFloatProperty),
                spineBendYaw = FloatProperty.Bind(animator, component, data.SpineBendYawFloatProperty),
                spineBendRoll = FloatProperty.Bind(animator, component, data.SpineBendRollFloatProperty),
                upperChestBendPitch = FloatProperty.Bind(animator, component, data.UpperChestBendPitchFloatProperty),
                upperChestBendYaw = FloatProperty.Bind(animator, component, data.UpperChestBendYawFloatProperty),
                upperChestBendRoll = FloatProperty.Bind(animator, component, data.UpperChestBendRollFloatProperty),
                hipHingeStartDeg = FloatProperty.Bind(animator, component, data.HipHingeStartDegFloatProperty),
                hipHingeMaxAddDeg = FloatProperty.Bind(animator, component, data.HipHingeMaxAddDegFloatProperty),
                chestSpringHz = FloatProperty.Bind(animator, component, data.ChestSpringHzFloatProperty),
                chestSpringDamping = FloatProperty.Bind(animator, component, data.ChestSpringDampingFloatProperty),
                spineMaxForwardDeg = FloatProperty.Bind(animator, component, data.SpineMaxForwardDegFloatProperty),
                spineMaxBackwardDeg = FloatProperty.Bind(animator, component, data.SpineMaxBackwardDegFloatProperty),
                spineMaxLateralDeg = FloatProperty.Bind(animator, component, data.SpineMaxLateralDegFloatProperty),
                spineSquishBoost = FloatProperty.Bind(animator, component, data.SpineSquishBoostFloatProperty),
                chestArmSwingFactor = FloatProperty.Bind(animator, component, data.ChestArmSwingFactorFloatProperty),
                chestArmSwingMaxDeg = FloatProperty.Bind(animator, component, data.ChestArmSwingMaxDegFloatProperty),
                lowerArmTwistFraction = FloatProperty.Bind(animator, component, data.LowerArmTwistFractionFloatProperty),
                upperArmTwistFraction = FloatProperty.Bind(animator, component, data.UpperArmTwistFractionFloatProperty),

                anatDifferentialStiffness = BoolProperty.Bind(animator, component, data.AnatDifferentialStiffnessProperty),
                anatShoulderSlide = BoolProperty.Bind(animator, component, data.AnatShoulderSlideProperty),
                anatCervicalLordosis = BoolProperty.Bind(animator, component, data.AnatCervicalLordosisProperty),
                anatPelvicTwistRouting = BoolProperty.Bind(animator, component, data.AnatPelvicTwistRoutingProperty),
                lordosisPitchGainDeg = FloatProperty.Bind(animator, component, data.LordosisPitchGainDegFloatProperty),
                chestSpringHeadVelGain = FloatProperty.Bind(animator, component, data.ChestSpringHeadVelGainFloatProperty),
                headLinearVelocity = Vector3Property.Bind(animator, component, data.HeadLinearVelocityProperty),

                // IK Lock Mode binding
                ikLockMode = FloatProperty.Bind(animator, component, data.IKLockModeFloatProperty),

                // Baked T-pose data for shoulder solve
                TposeLeftShoulderRot = data.LeftShoulder != null ? data.LeftShoulder.rotation : Quaternion.identity,
                TposeRightShoulderRot = data.RightShoulder != null ? data.RightShoulder.rotation : Quaternion.identity,
                TposeChestRot = data.chest != null ? data.chest.rotation : Quaternion.identity,
                TposeLeftShoulderLocalDir = (data.LeftShoulder != null && data.leftUpperArm != null)
                    ? (data.leftUpperArm.position - data.LeftShoulder.position).normalized : Vector3.left,
                TposeRightShoulderLocalDir = (data.RightShoulder != null && data.RightUpperArm != null)
                    ? (data.RightUpperArm.position - data.RightShoulder.position).normalized : Vector3.right,
                TposeShoulderToHandLeft = (data.LeftShoulder != null && data.LeftHand != null)
                    ? Vector3.Distance(data.LeftShoulder.position, data.LeftHand.position) : 0.6f,
                TposeShoulderToHandRight = (data.RightShoulder != null && data.RightHand != null)
                    ? Vector3.Distance(data.RightShoulder.position, data.RightHand.position) : 0.6f,
            };
            // Bind positions
            job.p0 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(0));
            job.p1 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(1));
            job.p2 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(2));
            job.p3 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(3));
            job.p4 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(4));
            job.p5 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(5));
            job.p6 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(6));
            job.p7 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(7));
            job.p8 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(8));
            job.p9 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(9));
            job.p10 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(10));
            job.p11 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(11));
            job.p12 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(12));
            job.p13 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(13));
            job.p14 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(14));
            job.p15 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(15));
            job.p16 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(16));
            job.p17 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(17));
            job.p18 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(18));
            job.p19 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(19));
            job.p20 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(20));
            job.p54 = Vector3Property.Bind(animator, component, data.GetTargetPositionVector3Property(54));
            // Bind rotations (as Vector4)
            job.r0 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(0));
            job.r1 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(1));
            job.r2 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(2));
            job.r3 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(3));
            job.r4 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(4));
            job.r5 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(5));
            job.r6 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(6));
            job.r7 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(7));
            job.r8 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(8));
            job.r9 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(9));
            job.r10 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(10));
            job.r11 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(11));
            job.r12 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(12));
            job.r13 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(13));
            job.r14 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(14));
            job.r15 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(15));
            job.r16 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(16));
            job.r17 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(17));
            job.r18 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(18));
            job.r19 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(19));
            job.r20 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(20));
            job.r54 = Vector4Property.Bind(animator, component, data.GetTargetRotationVector4Property(54));
            // Bind offsets
            job.o0 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(0));
            job.o1 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(1));
            job.o2 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(2));
            job.o3 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(3));
            job.o4 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(4));
            job.o5 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(5));
            job.o6 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(6));
            job.o7 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(7));
            job.o8 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(8));
            job.o9 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(9));
            job.o10 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(10));
            job.o11 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(11));
            job.o12 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(12));
            job.o13 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(13));
            job.o14 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(14));
            job.o15 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(15));
            job.o16 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(16));
            job.o17 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(17));
            job.o18 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(18));
            job.o19 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(19));
            job.o20 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(20));
            job.o54 = Vector4Property.Bind(animator, component, data.GetOffsetRotationVector4Property(54));
            // Bind per-slot weights
            job.w0 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(0));
            job.w1 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(1));
            job.w2 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(2));
            job.w3 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(3));
            job.w4 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(4));
            job.w5 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(5));
            job.w6 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(6));
            job.w7 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(7));
            job.w8 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(8));
            job.w9 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(9));
            job.w10 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(10));
            job.w11 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(11));
            job.w12 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(12));
            job.w13 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(13));
            job.w14 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(14));
            job.w15 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(15));
            job.w16 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(16));
            job.w17 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(17));
            job.w18 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(18));
            job.w19 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(19));
            job.w20 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(20));
            job.w54 = BoolProperty.Bind(animator, component, data.GetWeightFloatProperty(54));


            GenerateHeadToSpine(animator, ref job, ref data);
            GenerateChestToHead(animator, ref job, ref data);

            // Generate arm bend lookup tables
            var leftTable = BasisArmBendLookup.GenerateDefaultTable(true);
            var rightTable = BasisArmBendLookup.GenerateDefaultTable(false);
            job.ArmBendLookupLeft = new NativeArray<Vector3>(leftTable, Allocator.Persistent);
            job.ArmBendLookupRight = new NativeArray<Vector3>(rightTable, Allocator.Persistent);
            job.HasArmBendLookup = true;

            var cacheBuilder = new AnimationJobCacheBuilder();

            job.spineMaxIterationsIdx = cacheBuilder.Add(20);
            job.spineToleranceIdx = cacheBuilder.Add(0.001f);
            job.spineCache = cacheBuilder.Build();

            job.chestSpringState = new NativeArray<Vector3>(2, Allocator.Persistent);
            job.chestSpringInit = new NativeArray<int>(1, Allocator.Persistent);



            return job;
        }
        public void GenerateHeadToSpine(Animator animator, ref BasisFullIKConstraintJob job, ref BasisFullBodyData data)
        {
            var HeadToSpine = new Transform[] { data.head, data.neck, data.chest, data.spine, data.hips };
            int SpineToHeadLength = HeadToSpine.Length;
            job.ChainHeadToSpine = new NativeArray<ReadWriteTransformHandle>(SpineToHeadLength, Allocator.Persistent);
            job.ChainHeadToSpineLengths = new NativeArray<float>(SpineToHeadLength, Allocator.Persistent);
            job.ChainHeadToSpineLinkPositions = new NativeArray<Vector3>(SpineToHeadLength, Allocator.Persistent);

            job.MaxReachSpineTohead = 0f;

            int tip = SpineToHeadLength - 1;
            for (int i = 0; i < SpineToHeadLength; i++)
            {
                job.ChainHeadToSpine[i] = ReadWriteTransformHandle.Bind(animator, HeadToSpine[i]);
                job.ChainHeadToSpineLengths[i] = (i != tip) ? Vector3.Distance(HeadToSpine[i].position, HeadToSpine[i + 1].position) : 0f;

                job.MaxReachSpineTohead += job.ChainHeadToSpineLengths[i];
            }
            if (data.hips != null && data.head != null)
            {
                job.TposeLengthHeadToHips = (data.head.position - data.hips.position);
            }
            else
            {
                job.TposeLengthHeadToHips = Vector3.zero;
            }
        }
        public void GenerateChestToHead(Animator animator, ref BasisFullIKConstraintJob job, ref BasisFullBodyData data)
        {

            var ChestToHead = new Transform[] { data.chest, data.neck, data.head };
            int ChestToHeadLength = ChestToHead.Length;
            job.ChainChestToHead = new NativeArray<ReadWriteTransformHandle>(ChestToHeadLength, Allocator.Persistent);
            job.ChainChestToHeadLengths = new NativeArray<float>(ChestToHeadLength, Allocator.Persistent);
            job.ChainChestToHeadLinkPositions = new NativeArray<Vector3>(ChestToHeadLength, Allocator.Persistent);
            job.MaxReachHeadToChest = 0f;

            int tip = ChestToHeadLength - 1;
            for (int i = 0; i < ChestToHeadLength; i++)
            {
                job.ChainChestToHead[i] = ReadWriteTransformHandle.Bind(animator, ChestToHead[i]);

                job.ChainChestToHeadLengths[i] = (i != tip) ? Vector3.Distance(ChestToHead[i].position, ChestToHead[i + 1].position) : 0f;

                job.MaxReachHeadToChest += job.ChainChestToHeadLengths[i];
            }
            if (data.head != null && data.chest != null)
            {
                job.TposeLengthHeadToChest = (data.head.position - data.chest.position);
            }
            else
            {
                job.TposeLengthHeadToChest = Vector3.zero;
            }
        }
        static ReadWriteTransformHandle BindHandle(Animator animator, Transform t) => (t != null) ? ReadWriteTransformHandle.Bind(animator, t) : default;
        public override void Destroy(BasisFullIKConstraintJob job)
        {
            if (job.ChainHeadToSpine.IsCreated) job.ChainHeadToSpine.Dispose();
            if (job.ChainHeadToSpineLengths.IsCreated) job.ChainHeadToSpineLengths.Dispose();
            if (job.ChainHeadToSpineLinkPositions.IsCreated) job.ChainHeadToSpineLinkPositions.Dispose();

            if (job.ChainChestToHead.IsCreated) job.ChainChestToHead.Dispose();
            if (job.ChainChestToHeadLengths.IsCreated) job.ChainChestToHeadLengths.Dispose();
            if (job.ChainChestToHeadLinkPositions.IsCreated) job.ChainChestToHeadLinkPositions.Dispose();

            if (job.ArmBendLookupLeft.IsCreated) job.ArmBendLookupLeft.Dispose();
            if (job.ArmBendLookupRight.IsCreated) job.ArmBendLookupRight.Dispose();

            if (job.chestSpringState.IsCreated) job.chestSpringState.Dispose();
            if (job.chestSpringInit.IsCreated) job.chestSpringInit.Dispose();

            job.spineCache.Dispose();
        }
    }
}
