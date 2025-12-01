using System.Runtime.CompilerServices;

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

        // Head
        [SyncSceneToStream, SerializeField] public Vector3 PositionHead;
        [SyncSceneToStream, SerializeField] public Quaternion RotationHead;
        [SyncSceneToStream, SerializeField] public Vector3 HintPositionHead;
        [SyncSceneToStream, SerializeField] public Quaternion HintRotationHead;
        [SyncSceneToStream, SerializeField] public Vector3 m_CalibratedOffsetHead;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationHead;

        // Hips
        [SyncSceneToStream, SerializeField] public Vector3 PositionHips;
        [SyncSceneToStream, SerializeField] public Quaternion RotationEulerHips;
        [SyncSceneToStream, SerializeField] public Quaternion OffsetRotationHips;

        // Left Leg
        [SyncSceneToStream, SerializeField] public Vector3 LeftFootPosition;
        [SyncSceneToStream, SerializeField] public Quaternion LeftFootRotation;
        [SyncSceneToStream, SerializeField] public Vector3 HintPositionLeftLowerLeg;
        [SyncSceneToStream, SerializeField] public Quaternion HintRotationLeftLowerLeg;
        [SyncSceneToStream, SerializeField] public Vector3 m_CalibratedOffsetLeftFoot;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationLeftFoot;

        // Right Leg
        [SyncSceneToStream, SerializeField] public Vector3 RightFootPosition;
        [SyncSceneToStream, SerializeField] public Quaternion RightFootRotation;
        [SyncSceneToStream, SerializeField] public Vector3 HintPositionRightFoot;
        [SyncSceneToStream, SerializeField] public Quaternion HintRotationRightFoot;
        [SyncSceneToStream, SerializeField] public Vector3 m_CalibratedOffsetRightFoot;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationRightFoot;

        // Toes
        [SyncSceneToStream, SerializeField] public Vector3 OutGoingLeftToePosition;
        [SyncSceneToStream, SerializeField] public Quaternion OutGoingLeftToeRotation;
        [SyncSceneToStream, SerializeField] public Vector3 OutGoingRightToePosition;
        [SyncSceneToStream, SerializeField] public Quaternion OutGoingRightToeRotation;

        // Left Hand
        [SyncSceneToStream, SerializeField] public Vector3 PositionLeftHand;
        [SyncSceneToStream, SerializeField] public Quaternion RotationLeftHand;
        [SyncSceneToStream, SerializeField] public Vector3 HintPositionLeftHand;
        [SyncSceneToStream, SerializeField] public Quaternion HintRotationLeftHand;
        [SyncSceneToStream, SerializeField] public Vector3 m_CalibratedOffsetLeftHand;
        [SyncSceneToStream, SerializeField] public Vector3 m_CalibratedOffsetLeftHandHint;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationLeftHand;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationLeftHandHint;

        // Right Hand
        [SyncSceneToStream, SerializeField] public Vector3 PositionRightHand;
        [SyncSceneToStream, SerializeField] public Quaternion RotationRightHand;
        [SyncSceneToStream, SerializeField] public Vector3 HintPositionRightHand;
        [SyncSceneToStream, SerializeField] public Quaternion HintRotationRightHand;
        [SyncSceneToStream, SerializeField] public Vector3 m_CalibratedOffsetRightHand;
        [SyncSceneToStream, SerializeField] public Quaternion m_CalibratedRotationRightHand;

        // Misc
        [SyncSceneToStream, SerializeField] public Vector3 m_HintDirection;
        [SyncSceneToStream, SerializeField] public Vector3 m_HandLocalStart;
        [SyncSceneToStream, SerializeField] public Vector3 m_HandLocalEnd;

        [SyncSceneToStream, SerializeField] public float m_HandSkin;
        [SyncSceneToStream, SerializeField] public bool m_UseHandCapsule;
        [SyncSceneToStream, SerializeField, Min(0f)] public float m_HandRadius;
        [SyncSceneToStream, SerializeField, Min(0f)] public float m_ChestRadius;
        [SyncSceneToStream, SerializeField, Min(0f)] public float m_CollisionSkin;
        [SyncSceneToStream, SerializeField] bool m_CollisionsEnabled;
        [SyncSceneToStream, SerializeField] bool m_ProtectElbow;

        [SyncSceneToStream, SerializeField] bool m_HintHeadEnabled;
        [SyncSceneToStream, SerializeField] bool m_SpineIKEnabled;

        [SyncSceneToStream, SerializeField] public bool m_LeftToeEnabled;
        [SyncSceneToStream, SerializeField] public bool m_RightToeEnabled;

        [SyncSceneToStream, SerializeField] bool m_LeftLowerLegEnabled;
        [SyncSceneToStream, SerializeField] bool m_RightLowerLegEnabled;

        [SyncSceneToStream, SerializeField] bool m_HintLeftLowerLegEnabled;
        [SyncSceneToStream, SerializeField] bool m_HintRightLowerLegEnabled;

        [SyncSceneToStream, SerializeField] bool m_EnabledLeftHand;
        [SyncSceneToStream, SerializeField] bool m_EnabledRightHand;

        [SyncSceneToStream, SerializeField] bool m_HintRightHandEnabled;
        [SyncSceneToStream, SerializeField] bool m_HintLeftHandEnabled;

        [SyncSceneToStream, SerializeField] bool m_HaisIKEnabled;

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
        public string EnabledPropertySpineIK => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_SpineIKEnabled));
        public string HintWeightBoolPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintHeadEnabled));
        public string TargetPositionPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PositionHead));
        public string TargetRotationPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RotationHead));
        public string HintPositionPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintPositionHead));
        public string HintRotationPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintRotationHead));
        public string bendNormalHeadProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintDirection));
        public string EnabledPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_LeftLowerLegEnabled));
        public string HintWeightBoolPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintLeftLowerLegEnabled));
        public string TargetPositionPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(LeftFootPosition));
        public string TargetRotationPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(LeftFootRotation));
        public string HintPositionPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintPositionLeftLowerLeg));
        public string HintRotationPropertyLeftLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintRotationLeftLowerLeg));
        public string EnabledPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_RightLowerLegEnabled));
        public string HintWeightBoolPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintRightLowerLegEnabled));
        public string TargetPositionPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RightFootPosition));
        public string TargetRotationPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RightFootRotation));
        public string HintPositionPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintPositionRightFoot));
        public string HintRotationPropertyRightLowerLeg => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintRotationRightFoot));
        public string TargetPositionPropertyHips => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PositionHips));
        public string TargetRotationPropertyHips => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RotationEulerHips));
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
        public string HintPositionPropertyLeftHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintPositionLeftHand));
        public string HintRotationPropertyLeftHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintRotationLeftHand));
        public string EnabledPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_EnabledRightHand));
        public string EnabledPropertyLeftHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_EnabledLeftHand));
        public string HintWeightBoolPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintRightHandEnabled));
        public string TargetPositionPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PositionRightHand));
        public string TargetRotationPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RotationRightHand));
        public string HintPositionPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintPositionRightHand));
        public string HintRotationPropertyRightHand => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintRotationRightHand));
        public string ChestRadiusFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ChestRadius));
        public string CollisionSkinFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_CollisionSkin));
        public string CollisionsEnabledBoolProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_CollisionsEnabled));
        public string HandLocalStartVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HandLocalStart));
        public string HandLocalEndVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HandLocalEnd));
        public string HandRadiusFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HandRadius));
        public string HandSkinFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HandSkin));
        public string UseHandCapsuleBoolProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_UseHandCapsule));
        public string ProtectElbowBoolProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_ProtectElbow));

        public string HaisIKboolProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HaisIKEnabled));
        public bool hintWeightHead { get => m_HintHeadEnabled; set => m_HintHeadEnabled = value; }
        public bool EnabledSpineIK { get => m_SpineIKEnabled; set => m_SpineIKEnabled = value; }
        public bool HintWeightLeftLowerLeg { get => m_HintLeftLowerLegEnabled; set => m_HintLeftLowerLegEnabled = value; }
        public bool EnableLeftLeg { get => m_LeftLowerLegEnabled; set => m_LeftLowerLegEnabled = value; }
        public bool HintWeightRightLowerLeg { get => m_HintRightLowerLegEnabled; set => m_HintRightLowerLegEnabled = value; }
        public bool EnableRightLeg { get => m_RightLowerLegEnabled; set => m_RightLowerLegEnabled = value; }
        public bool LeftToeEnabled { get => m_LeftToeEnabled; set => m_LeftToeEnabled = value; }
        public bool RightToeEnabled { get => m_RightToeEnabled; set => m_RightToeEnabled = value; }
        public bool hintWeightLeftHand { get => m_HintLeftHandEnabled; set => m_HintLeftHandEnabled = value; }
        public bool enabledLeftHand { get => m_EnabledLeftHand; set => m_EnabledLeftHand = value; }

        public bool enabledRightHand { get => m_EnabledRightHand; set => m_EnabledRightHand = value; }
        public bool protectElbow { get => m_ProtectElbow; set => m_ProtectElbow = value; }
        public bool hintWeightRightHand { get => m_HintRightHandEnabled; set => m_HintRightHandEnabled = value; }
        public float handRadius { get => m_HandRadius; set => m_HandRadius = value; }
        public float handSkin { get => m_HandSkin; set => m_HandSkin = value; }
        public bool useHandCapsule { get => m_UseHandCapsule; set => m_UseHandCapsule = value; }
        public float chestRadius { get => m_ChestRadius; set => m_ChestRadius = value; }
        public float collisionSkin { get => m_CollisionSkin; set => m_CollisionSkin = value; }
        public bool collisionsEnabled { get => m_CollisionsEnabled; set => m_CollisionsEnabled = value; }

        public bool HaisIKEnabled { get => m_HaisIKEnabled; set => m_HaisIKEnabled = value; }

        public Vector3 handLocalStartLeft { get => m_HandLocalStart; set => m_HandLocalStart = value; }
        public Vector3 handLocalEndLeft { get => m_HandLocalEnd; set => m_HandLocalEnd = value; }
        public Vector3 handLocalStartRight { get => m_HandLocalStart; set => m_HandLocalStart = value; }
        public Vector3 handLocalEndRight { get => m_HandLocalEnd; set => m_HandLocalEnd = value; }

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

            m_HintHeadEnabled = m_HintLeftLowerLegEnabled = m_HintRightLowerLegEnabled = true;
            m_SpineIKEnabled = m_LeftLowerLegEnabled = m_RightLowerLegEnabled = true;

            m_HintLeftHandEnabled = m_HintRightHandEnabled = true;
            m_EnabledLeftHand = m_EnabledRightHand = true;

            m_CalibratedOffsetHead = m_CalibratedOffsetLeftFoot = m_CalibratedOffsetRightFoot = Vector3.zero;
            m_CalibratedRotationHead = m_CalibratedRotationLeftFoot = m_CalibratedRotationRightFoot = Quaternion.identity;

            m_CalibratedOffsetLeftHand = m_CalibratedOffsetRightHand = Vector3.zero;
            m_CalibratedRotationLeftHand = m_CalibratedRotationRightHand = Quaternion.identity;

            m_HintDirection = Vector3.up;

            PositionHips = Vector3.zero;
            RotationEulerHips = Quaternion.identity;
            OffsetRotationHips = Quaternion.identity;

            // Integrated driven TR defaults
            m_LeftToe = null;
            m_RightToe = null;

            OutGoingLeftToePosition = OutGoingRightToePosition = Vector3.zero;
            OutGoingLeftToeRotation = OutGoingRightToeRotation = Quaternion.identity;
            m_LeftToeEnabled = false;
            m_RightToeEnabled = false;

            // Chest/hand capsule defaults (left)
            m_chest = m_neck = null;
            m_ChestRadius = 0.18f; m_CollisionSkin = 0.02f; m_CollisionsEnabled = true;
            m_HandLocalStart = new Vector3(0f, 0f, -0.05f);
            m_HandLocalEnd = new Vector3(0f, 0f, 0.08f);
            m_HandRadius = 0.05f; m_HandSkin = 0.01f; m_UseHandCapsule = true;
            m_ProtectElbow = true;

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
            m_Data.hintWeightHead = m_Data.hintWeightHead;
            m_Data.HintWeightLeftLowerLeg = m_Data.HintWeightLeftLowerLeg;
            m_Data.HintWeightRightLowerLeg = m_Data.HintWeightRightLowerLeg;
            m_Data.EnabledSpineIK = m_Data.EnabledSpineIK;

            // new toggles
            m_Data.LeftToeEnabled = m_Data.LeftToeEnabled;
            m_Data.RightToeEnabled = m_Data.RightToeEnabled;

            // hands toggles
            m_Data.hintWeightLeftHand = m_Data.hintWeightLeftHand;
            m_Data.hintWeightRightHand = m_Data.hintWeightRightHand;
            m_Data.enabledLeftHand = m_Data.enabledLeftHand;
            m_Data.enabledRightHand = m_Data.enabledRightHand;
            m_Data.protectElbow = m_Data.protectElbow;
        }
    }

    [Unity.Burst.BurstCompile]
    public struct BasisFullIKConstraintJob : IWeightedAnimationJob
    {
        public ReadWriteTransformHandle HandleChest, HandleNeck, HandleHead,
  HandleLeftUpperLeg, HandleLeftLowerLeg, HandleLeftFoot,
  HandleRightUpperLeg, HandleRightLowerLeg, HandleRightFoot,
  HandleHips, HandleSpine, HandleUpperChest,
            HandleLeftShoulder, HandleRightShoulder,

  HandleLeftToe, HandleRightToe,
  HandleLeftUpperArm, HandleLeftLowerArm, HandleLeftHand,
  HandleRightUpperArm, HandleRightLowerArm, HandleRightHand;

        public Vector3Property targetPositionHead, hintPositionHead, bendNormalHead,
targetPositionLeftLowerLeg, hintPositionLeftLowerLeg,
targetPositionRightLowerLeg, hintPositionRightLowerLeg,
targetPositionHips,
leftDrivenTargetPos, rightDrivenTargetPos,
targetPositionLeftHand, hintPositionLeftHand,
targetPositionRightHand, hintPositionRightHand,
handLocalStart, handLocalEnd,
p0, p1, p2, p3, p4, p5, p6, p7, p8, p9,
p10, p11, p12, p13, p14, p15, p16, p17, p18, p19,
p20, p54;

        public Vector4Property targetRotationHead, hintRotationHead,
targetRotationLeftLowerLeg, hintRotationLeftLowerLeg,
targetRotationRightLowerLeg, hintRotationRightLowerLeg,
targetRotationHips, offsetRotationHips,
leftDrivenTargetRot, rightDrivenTargetRot,
targetRotationLeftHand, hintRotationLeftHand,
targetRotationRightHand, hintRotationRightHand,
r0, r1, r2, r3, r4, r5, r6, r7, r8, r9,
r10, r11, r12, r13, r14, r15, r16, r17, r18, r19,
r20, r54,
o0, o1, o2, o3, o4, o5, o6, o7, o8, o9,
o10, o11, o12, o13, o14, o15, o16, o17, o18, o19,
o20, o54;

        public AffineTransform targetOffsetHead,
targetOffsetLeftFoot,
targetOffsetRightFoot,
targetOffsetLeftHand,
targetOffsetRightHand;

        public BoolProperty
hintWeightHead, enabledSpineIK,
hintWeightLeftLowerLeg, enabledLeftLowerLeg,
hintWeightRightLowerLeg, enabledRightLowerLeg,

leftToeEnabled, RightToeEnabled,
hintWeightLeftHand, enabledLeftHand,
hintWeightRightHand, enabledRightHand,
useHandCapsule, protectElbow,
collisionsEnabled, HaisIKEnabled,
w0, w1, w2, w3, w4, w5, w6, w7, w8, w9,
w10, w11, w12, w13, w14, w15, w16, w17, w18, w19,
w20, w54;

        public FloatProperty
handRadius, handSkin,
chestRadius, collisionSkin;
        public FloatProperty jobWeight { get; set; }
        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float w = jobWeight.Get(stream);
            if (w <= 0f)
            {

                BasisAnimationRuntimeUtils.Pass(stream, HandleHips, HandleLeftToe, HandleRightToe);

                BasisAnimationRuntimeUtils.Pass(stream, HandleChest, HandleNeck, HandleHead);

                BasisAnimationRuntimeUtils.Pass(stream, HandleLeftUpperLeg, HandleLeftLowerLeg, HandleLeftFoot);
                BasisAnimationRuntimeUtils.Pass(stream, HandleRightUpperLeg, HandleRightLowerLeg, HandleRightFoot);

                BasisAnimationRuntimeUtils.Pass(stream, HandleLeftUpperArm, HandleLeftLowerArm, HandleLeftHand);
                BasisAnimationRuntimeUtils.Pass(stream, HandleRightUpperArm, HandleRightLowerArm, HandleRightHand);

                return;
            }
            if (HaisIKEnabled.Get(stream))
            {
                BasisAnimationFullBodyIK.SolveHipsAndSpine(stream,

    // --- Hips ---
    targetPositionHips,
    targetRotationHips,
    offsetRotationHips,

    // --- Head + Legs ---
    enabledSpineIK,
    HandleHips,
    //  HandleSpine,
    HandleChest,
    // HandleUpperChest,
    HandleNeck,
    HandleHead,
    targetPositionHead,
    targetRotationHead,
    hintPositionHead,
    hintRotationHead,
    hintWeightHead,
    targetOffsetHead,
    bendNormalHead
);

                BasisAnimationRuntimeUtils.SolveLegs(stream, enabledLeftLowerLeg, HandleLeftUpperLeg, HandleLeftLowerLeg, HandleLeftFoot,
    targetPositionLeftLowerLeg, targetRotationLeftLowerLeg, hintPositionLeftLowerLeg, hintRotationLeftLowerLeg,
    hintWeightLeftLowerLeg, targetOffsetLeftFoot, bendNormalHead);

                BasisAnimationRuntimeUtils.SolveLegs(stream, enabledRightLowerLeg, HandleRightUpperLeg, HandleRightLowerLeg, HandleRightFoot,
                    targetPositionRightLowerLeg, targetRotationRightLowerLeg, hintPositionRightLowerLeg, hintRotationRightLowerLeg,
                    hintWeightRightLowerLeg, targetOffsetRightFoot, bendNormalHead);

                // --- Hands (TwoBone with capsules + elbow protection) ---
                BasisAnimationRuntimeUtils.SolveHand(stream,
                    enabledLeftHand, HandleLeftUpperArm, HandleLeftLowerArm, HandleLeftHand,
                    targetPositionLeftHand, targetRotationLeftHand, hintPositionLeftHand, hintRotationLeftHand,
                    hintWeightLeftHand, targetOffsetLeftHand,
                    HandleChest, HandleNeck, chestRadius, collisionSkin, collisionsEnabled,
                    handLocalStart, handLocalEnd, handRadius, handSkin, useHandCapsule,
                    protectElbow);

                BasisAnimationRuntimeUtils.SolveHand(stream,
                    enabledRightHand, HandleRightUpperArm, HandleRightLowerArm, HandleRightHand,
                    targetPositionRightHand, targetRotationRightHand, hintPositionRightHand, hintRotationRightHand,
                    hintWeightRightHand, targetOffsetRightHand,
                    HandleChest, HandleNeck, chestRadius, collisionSkin, collisionsEnabled,
                    handLocalStart, handLocalEnd, handRadius, handSkin, useHandCapsule,
                    protectElbow);
            }
            else
            {
                BasisAnimationFullBodyIK.SolveHipsAndSpine(stream,

                    // --- Hips ---
                    targetPositionHips,
                    targetRotationHips,
                    offsetRotationHips,

                    // --- Head + Legs ---
                    enabledSpineIK,
                    HandleHips,
                    //  HandleSpine,
                    HandleChest,
                    // HandleUpperChest,
                    HandleNeck,
                    HandleHead,
                    targetPositionHead,
                    targetRotationHead,
                    hintPositionHead,
                    hintRotationHead,
                    hintWeightHead,
                    targetOffsetHead,
                    bendNormalHead
                );

                BasisAnimationRuntimeUtils.SolveLegs(stream, enabledLeftLowerLeg, HandleLeftUpperLeg, HandleLeftLowerLeg, HandleLeftFoot,
                    targetPositionLeftLowerLeg, targetRotationLeftLowerLeg, hintPositionLeftLowerLeg, hintRotationLeftLowerLeg,
                    hintWeightLeftLowerLeg, targetOffsetLeftFoot, bendNormalHead);

                BasisAnimationRuntimeUtils.SolveLegs(stream, enabledRightLowerLeg, HandleRightUpperLeg, HandleRightLowerLeg, HandleRightFoot,
                    targetPositionRightLowerLeg, targetRotationRightLowerLeg, hintPositionRightLowerLeg, hintRotationRightLowerLeg,
                    hintWeightRightLowerLeg, targetOffsetRightFoot, bendNormalHead);

                // --- Hands (TwoBone with capsules + elbow protection) ---
                BasisAnimationRuntimeUtils.SolveHand(stream,
                    enabledLeftHand, HandleLeftUpperArm, HandleLeftLowerArm, HandleLeftHand,
                    targetPositionLeftHand, targetRotationLeftHand, hintPositionLeftHand, hintRotationLeftHand,
                    hintWeightLeftHand, targetOffsetLeftHand,
                    HandleChest, HandleNeck, chestRadius, collisionSkin, collisionsEnabled,
                    handLocalStart, handLocalEnd, handRadius, handSkin, useHandCapsule,
                    protectElbow);

                BasisAnimationRuntimeUtils.SolveHand(stream,
                    enabledRightHand, HandleRightUpperArm, HandleRightLowerArm, HandleRightHand,
                    targetPositionRightHand, targetRotationRightHand, hintPositionRightHand, hintRotationRightHand,
                    hintWeightRightHand, targetOffsetRightHand,
                    HandleChest, HandleNeck, chestRadius, collisionSkin, collisionsEnabled,
                    handLocalStart, handLocalEnd, handRadius, handSkin, useHandCapsule,
                    protectElbow);
            }
            // --- Integrated "damped TR" application (world-space) ---
            BasisAnimationRuntimeUtils.ApplyToeRotation(stream, leftToeEnabled, HandleLeftToe, leftDrivenTargetPos, leftDrivenTargetRot);
            BasisAnimationRuntimeUtils.ApplyToeRotation(stream, RightToeEnabled, HandleRightToe, rightDrivenTargetPos, rightDrivenTargetRot);

            BasisAnimationRuntimeUtils.Apply(stream, HandleHips, p0, r0, o0, w0);
            BasisAnimationRuntimeUtils.Apply(stream, HandleLeftUpperLeg, p1, r1, o1, w1);
            BasisAnimationRuntimeUtils.Apply(stream, HandleRightUpperLeg, p2, r2, o2, w2);

            BasisAnimationRuntimeUtils.Apply(stream, HandleLeftLowerLeg, p3, r3, o3, w3);
            BasisAnimationRuntimeUtils.Apply(stream, HandleRightLowerLeg, p4, r4, o4, w4);

            BasisAnimationRuntimeUtils.Apply(stream, HandleLeftFoot, p5, r5, o5, w5);
            BasisAnimationRuntimeUtils.Apply(stream, HandleRightFoot, p6, r6, o6, w6);

            BasisAnimationRuntimeUtils.Apply(stream, HandleSpine, p7, r7, o7, w7);

            BasisAnimationRuntimeUtils.Apply(stream, HandleChest, p8, r8, o8, w8);
            BasisAnimationRuntimeUtils.Apply(stream, HandleNeck, p9, r9, o9, w9);

            BasisAnimationRuntimeUtils.Apply(stream, HandleHead, p10, r10, o10, w10);

            BasisAnimationRuntimeUtils.Apply(stream, HandleLeftShoulder, p11, r11, o11, w11);
            BasisAnimationRuntimeUtils.Apply(stream, HandleRightShoulder, p12, r12, o12, w12);

            BasisAnimationRuntimeUtils.Apply(stream, HandleLeftUpperArm, p13, r13, o13, w13);
            BasisAnimationRuntimeUtils.Apply(stream, HandleRightUpperArm, p14, r14, o14, w14);

            BasisAnimationRuntimeUtils.Apply(stream, HandleRightUpperArm, p15, r15, o15, w15);
            BasisAnimationRuntimeUtils.Apply(stream, HandleRightLowerArm, p16, r16, o16, w16);

            BasisAnimationRuntimeUtils.Apply(stream, HandleLeftHand, p17, r17, o17, w17);
            BasisAnimationRuntimeUtils.Apply(stream, HandleRightHand, p18, r18, o18, w18);

            BasisAnimationRuntimeUtils.Apply(stream, HandleLeftToe, p19, r19, o19, w19);
            BasisAnimationRuntimeUtils.Apply(stream, HandleRightToe, p20, r20, o20, w20);

            BasisAnimationRuntimeUtils.Apply(stream, HandleUpperChest, p54, r54, o54, w54);
        }
    }

    public class BasisFullBodyJobBinder : AnimationJobBinder<BasisFullIKConstraintJob, BasisFullBodyData>
    {
        public override BasisFullIKConstraintJob Create(Animator animator, ref BasisFullBodyData data, Component component)
        {
            var job = new BasisFullIKConstraintJob
            {
                // Transforms
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

                HandleSpine = BindHandle(animator, data.spine),
                HandleUpperChest = BindHandle(animator, data.upperChest),
                HandleLeftShoulder = BindHandle(animator, data.LeftShoulder),
                HandleRightShoulder = BindHandle(animator, data.RightShoulder),

                targetPositionHips = Vector3Property.Bind(animator, component, data.TargetPositionPropertyHips),
                targetPositionHead = Vector3Property.Bind(animator, component, data.TargetPositionPropertyHead),
                hintPositionHead = Vector3Property.Bind(animator, component, data.HintPositionPropertyHead),
                bendNormalHead = Vector3Property.Bind(animator, component, data.bendNormalHeadProperty),

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

                handLocalStart = Vector3Property.Bind(animator, component, data.HandLocalStartVector3Property),
                handLocalEnd = Vector3Property.Bind(animator, component, data.HandLocalEndVector3Property),

                targetRotationHips = Vector4Property.Bind(animator, component, data.TargetRotationPropertyHips),
                offsetRotationHips = Vector4Property.Bind(animator, component, data.OffsetRotationPropertyHips),

                targetRotationHead = Vector4Property.Bind(animator, component, data.TargetRotationPropertyHead),
                hintRotationHead = Vector4Property.Bind(animator, component, data.HintRotationPropertyHead),

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
                hintWeightHead = BoolProperty.Bind(animator, component, data.HintWeightBoolPropertyHead),

                enabledLeftLowerLeg = BoolProperty.Bind(animator, component, data.EnabledPropertyLeftLowerLeg),
                hintWeightLeftLowerLeg = BoolProperty.Bind(animator, component, data.HintWeightBoolPropertyLeftLowerLeg),

                enabledRightLowerLeg = BoolProperty.Bind(animator, component, data.EnabledPropertyRightLowerLeg),
                hintWeightRightLowerLeg = BoolProperty.Bind(animator, component, data.HintWeightBoolPropertyRightLowerLeg),

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

                targetOffsetHead = new AffineTransform(data.m_CalibratedOffsetHead, data.m_CalibratedRotationHead),

                targetOffsetLeftFoot = new AffineTransform(data.m_CalibratedOffsetLeftFoot, data.m_CalibratedRotationLeftFoot),
                targetOffsetRightFoot = new AffineTransform(data.m_CalibratedOffsetRightFoot, data.m_CalibratedRotationRightFoot),

                targetOffsetLeftHand = new AffineTransform(data.m_CalibratedOffsetLeftHand, data.m_CalibratedRotationLeftHand),
                targetOffsetRightHand = new AffineTransform(data.m_CalibratedOffsetRightHand, data.m_CalibratedRotationRightHand),

                HaisIKEnabled = BoolProperty.Bind(animator, component, data.HaisIKboolProperty)

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
            return job;
        }
        static ReadWriteTransformHandle BindHandle(Animator animator, Transform t)
    => (t != null) ? ReadWriteTransformHandle.Bind(animator, t) : default;
        public override void Destroy(BasisFullIKConstraintJob job) { }
    }
}
