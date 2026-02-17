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
        public string EnabledPropertySpineIK => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_SpineIKEnabled));
        public string HintWeightBoolPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintHeadEnabled));
        public string TargetPositionPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(PositionHead));
        public string TargetRotationPropertyHead => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(RotationHead));
        public string PropertyChestPosition => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(ChestPosition));
        public string PropertyChestRotation => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(ChestRotation));
        public string BendNormalHeadProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(SpineBendNormal));
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
        public bool HintWeightHead { get => m_HintHeadEnabled; set => m_HintHeadEnabled = value; }
        public bool EnabledSpineIK { get => m_SpineIKEnabled; set => m_SpineIKEnabled = value; }
        public bool EnableLeftLowerLeg { get => m_HintLeftLowerLegEnabled; set => m_HintLeftLowerLegEnabled = value; }
        public bool EnableLeftLeg { get => m_LeftLowerLegEnabled; set => m_LeftLowerLegEnabled = value; }
        public bool EnableRightLowerLeg { get => m_HintRightLowerLegEnabled; set => m_HintRightLowerLegEnabled = value; }
        public bool EnableRightLeg { get => m_RightLowerLegEnabled; set => m_RightLowerLegEnabled = value; }
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
            m_CalibratedRotationHead = M_CalibrationLeftFootRotation = M_CalibrationRightFootRotation = Quaternion.identity;
            m_CalibratedRotationLeftHand = m_CalibratedRotationRightHand = Quaternion.identity;

            SpineBendNormal = Vector3.up;

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

            // Chest/hand capsule defaults (left)
            m_chest = m_neck = null;
            m_ChestRadius = 0.18f; m_CollisionSkin = 0.02f; m_CollisionsEnabled = true;
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
            m_Data.HintWeightHead = m_Data.HintWeightHead;
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
  HandleRightUpperArm, HandleRightLowerArm, HandleRightHand;

        public Vector3Property targetPositionHead, TargetChestPosition, bendNormalHead, KneeBendPrefLeft, KneeBendPrefRight, ElbowBendPrefLeft, ElbowBendPrefRight,
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

        public Quaternion targetOffsetNeck, targetOffsetHead, targetOffsetChest, targetOffsetLeftToe,
            targetOffsetRightToe, targetOffsetLeftShoulder, targetOffsetRightShoulder, targetOffsetLeftFoot,
            targetOffsetRightFoot, targetOffsetLeftHand, targetOffsetRightHand;

        public BoolProperty
HasChestTracker, enabledSpineIK,
hintWeightLeftLowerLeg, enabledLeftLowerLeg,
hintWeightRightLowerLeg, enabledRightLowerLeg,
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
            SolveSpine(stream);

            ApplyRotation(stream, enabledLeftShoulder, HandleLeftShoulder, TargetRotationLeftShoulder, targetOffsetLeftShoulder);
            ApplyRotation(stream, enabledRightShoulder, HandleRightShoulder, TargetRotationRightShoulder, targetOffsetRightShoulder);

            SolveLegs(stream, enabledLeftLowerLeg, HandleLeftUpperLeg, HandleLeftLowerLeg, HandleLeftFoot, targetPositionLeftLowerLeg, targetRotationLeftLowerLeg, hintPositionLeftLowerLeg, hintRotationLeftLowerLeg, hintWeightLeftLowerLeg, targetOffsetLeftFoot, KneeBendPrefLeft);
            SolveLegs(stream, enabledRightLowerLeg, HandleRightUpperLeg, HandleRightLowerLeg, HandleRightFoot, targetPositionRightLowerLeg, targetRotationRightLowerLeg, hintPositionRightLowerLeg, hintRotationRightLowerLeg, hintWeightRightLowerLeg, targetOffsetRightFoot, KneeBendPrefRight);

            SolveHand(stream, enabledLeftHand, HandleLeftUpperArm, HandleLeftLowerArm, HandleLeftHand, targetPositionLeftHand, targetRotationLeftHand, hintPositionLeftHand, hintRotationLeftHand, hintWeightLeftHand, targetOffsetLeftHand, HandleChest, HandleNeck, chestRadius, collisionSkin, collisionsEnabled, handRadius, handSkin, useHandCapsule, protectElbow);
            SolveHand(stream, enabledRightHand, HandleRightUpperArm, HandleRightLowerArm, HandleRightHand, targetPositionRightHand, targetRotationRightHand, hintPositionRightHand, hintRotationRightHand, hintWeightRightHand, targetOffsetRightHand, HandleChest, HandleNeck, chestRadius, collisionSkin, collisionsEnabled, handRadius, handSkin, useHandCapsule, protectElbow);

            ApplyRotation(stream, leftToeEnabled, HandleLeftToe, leftDrivenTargetRot, targetOffsetLeftToe);
            ApplyRotation(stream, RightToeEnabled, HandleRightToe, rightDrivenTargetRot, targetOffsetRightToe);


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
            Vector3 chestTargetPos = TargetChestPosition.Get(stream);

            Quaternion headTargetRot = V4ToQuat(targetRotationHead.Get(stream));
            Quaternion hipsTargetRot = V4ToQuat(targetRotationHips.Get(stream));
            Quaternion offsetHips = V4ToQuat(offsetRotationHips.Get(stream));
            Quaternion chestTargetRot = V4ToQuat(targetChestRotation.Get(stream));

            Quaternion hipDesired = hipsTargetRot * offsetHips;
            Quaternion headDesired = headTargetRot * targetOffsetHead;
            Quaternion chestDesired = chestTargetRot * targetOffsetChest;

            // 1) HIPS: clamp pos + limit rot
            float restDist = MinHeadSpineHeight.Get(stream);

            float MaxBendDeg = maxBendDeg.Get(stream);
            // 1) Limit spine bend by pushing hips down if needed
            hipsTargetPos = EnforceSpineBendLimit(headTargetPos, hipsTargetPos, MaxBendDeg);

            hipsTargetPos = ClampHipsAroundHead(headTargetPos, hipsTargetPos, restDist, minFactor.Get(stream), maxFactor.Get(stream));

            targetPositionHips.Set(stream, hipsTargetPos);

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

                SolveTwoBoneSpine(stream, HandleChest, HandleNeck, HandleHead, target, targetOffsetHead, bendNormal);
            }
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
        static Vector3 ClampHipsAroundHead(Vector3 headPos, Vector3 hipsPos, float restDistance, float minFactor, float maxFactor)
        {
            Vector3 headToHips = hipsPos - headPos;
            float sqrMag = headToHips.sqrMagnitude;
            if (sqrMag < k_SqrEpsilon)
            {
                return headPos + restDistance * minFactor * Vector3.down; // could also use previous frame’s axis
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
        static Vector3 EnforceSpineBendLimit(Vector3 headPos, Vector3 hipsPos, float maxBendDeg)
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

            Vector3 up = Vector3.up;

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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector3 ClampToReach(Vector3 root, Vector3 target, float maxReach)
        {
            Vector3 d = target - root;
            float dist = d.magnitude;
            if (dist <= maxReach) return target;
            if (dist < 1e-6f) return root;
            return root + d * (maxReach / dist);
        }
        // Twist is a rotation *around axis*. We clamp its signed angle around that axis.
        static Quaternion ClampTwistAroundAxis(Quaternion twist, Vector3 axis, float maxDegrees)
        {
            axis = axis.normalized;
            twist = NormalizeSafe(twist);

            twist.ToAngleAxis(out float ang, out Vector3 ax);
            if (ang > 180f) ang -= 360f;

            if (Vector3.Dot(ax, axis) < 0f)
                ang = -ang;

            float clamped = Mathf.Clamp(ang, -maxDegrees, maxDegrees);
            return Quaternion.AngleAxis(clamped, axis);
        }
        // Decompose q into swing (no twist around axis) and twist (pure rotation around axis).
        static void SwingTwistDecompose(Quaternion q, Vector3 axis, out Quaternion swing, out Quaternion twist)
        {
            axis = axis.normalized;
            Vector3 r = new Vector3(q.x, q.y, q.z);

            // Project rotation vector part onto axis => twist component
            Vector3 proj = axis * Vector3.Dot(r, axis);
            twist = NormalizeSafe(new Quaternion(proj.x, proj.y, proj.z, q.w));
            swing = NormalizeSafe(q * Quaternion.Inverse(twist));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Quaternion NormalizeSafe(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 1e-8f) return Quaternion.identity;
            float inv = 1f / m;
            return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Saturate(float x) => Mathf.Clamp01(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float Remap01(float x, float a, float b)
        {
            if (Mathf.Abs(b - a) < 1e-6f) return 1f;
            return Saturate((x - a) / (b - a));
        }
        public bool SolveChestToHeadFABRIK(AnimationStream stream, Vector3 headTargetPos)
        {
            if (!ChainChestToHead.IsCreated || ChainChestToHead.Length < 2)
            {
                if (HandleHead.IsValid(stream))
                    HandleHead.SetPosition(stream, headTargetPos);
                return false;
            }

            int n = ChainChestToHead.Length;
            int tipIndex = n - 1;

            // Cache current positions
            for (int i = 0; i < n; i++)
                ChainChestToHeadLinkPositions[i] = ChainChestToHead[i].GetPosition(stream);

            Vector3 b0 = ChainChestToHeadLinkPositions[0];
            Vector3 b1 = ChainChestToHeadLinkPositions[1];
            Vector3 b2 = (n > 2) ? ChainChestToHeadLinkPositions[2] : Vector3.zero;

            float tol = spineCache.GetRaw(spineToleranceIdx);
            int iters = (int)spineCache.GetRaw(spineMaxIterationsIdx);

            // --- Continuous reach handling (prevents solved/unsolved toggling) ---
            Vector3 chestPos = ChainChestToHeadLinkPositions[0];
            float maxReach = MaxReachHeadToChest;

            // tiny slack so we don’t sit exactly on the boundary (reduces numeric flapping)
            const float reachSlack = 1e-4f;
            Vector3 reachableTarget = ClampToReach(chestPos, headTargetPos, Mathf.Max(0f, maxReach - reachSlack));

            bool solved = AnimationRuntimeUtils.SolveFABRIK(
                ref ChainChestToHeadLinkPositions,
                ref ChainChestToHeadLengths,
                reachableTarget,
                tol,
                MaxReachHeadToChest,
                iters
            );

            // If FABRIK still fails, do a deterministic “stretch line” fallback
            if (!solved)
            {
                Vector3 dir = reachableTarget - chestPos;
                float d = dir.magnitude;
                dir = (d > 1e-6f) ? (dir / d) : Vector3.forward;

                ChainChestToHeadLinkPositions[0] = chestPos;
                for (int i = 1; i < n; i++)
                    ChainChestToHeadLinkPositions[i] = ChainChestToHeadLinkPositions[i - 1] + dir * ChainChestToHeadLengths[i - 1];
            }

            // Write positions back (neck/head)
            for (int i = 1; i < n; i++)
                ChainChestToHead[i].SetPosition(stream, ChainChestToHeadLinkPositions[i]);

            // Rotate links to match new segment directions
            for (int i = 0; i < tipIndex; i++)
            {
                Vector3 beforeA = (i == 0) ? b0 : b1;
                Vector3 beforeB = (i == 0) ? b1 : b2;

                Vector3 afterA = ChainChestToHeadLinkPositions[i];
                Vector3 afterB = ChainChestToHeadLinkPositions[i + 1];

                Vector3 prevDir = beforeB - beforeA;
                Vector3 newDir = afterB - afterA;

                if (prevDir.sqrMagnitude < k_SqrEpsilon || newDir.sqrMagnitude < k_SqrEpsilon)
                    continue;

                Quaternion rot = ChainChestToHead[i].GetRotation(stream);
                Quaternion delta = QuaternionExt.FromToRotation(prevDir, newDir);
                ChainChestToHead[i].SetRotation(stream, delta * rot);
            }

            // Final: you may pin the head to the *true* target for tracker truth,
            // but NOW the chain has a coherent pose (no snapping between two modes).
            if (ChainChestToHead[tipIndex].IsValid(stream))
                ChainChestToHead[tipIndex].SetPosition(stream, reachableTarget);

            return true; // returning “true” avoids external code branching too
        }
        public void SolveSpineFABRIK(AnimationStream stream, Vector3 Target)
        {
            if (!ChainHeadToSpine.IsCreated || ChainHeadToSpine.Length < 2)
            {
                return;
            }

            // read current positions
            for (int i = 0; i < ChainHeadToSpine.Length; i++)
            {
                ChainHeadToSpineLinkPositions[i] = ChainHeadToSpine[i].GetPosition(stream);
            }

            int tipIndex = ChainHeadToSpine.Length - 1;

            float tol = spineCache.GetRaw(spineToleranceIdx);
            int iters = (int)spineCache.GetRaw(spineMaxIterationsIdx);

            if (AnimationRuntimeUtils.SolveFABRIK(ref ChainHeadToSpineLinkPositions, ref ChainHeadToSpineLengths, Target, tol, MaxReachSpineTohead, iters))
            {
                for (int i = 0; i < tipIndex; ++i)
                {
                    var prevDir = ChainHeadToSpine[i + 1].GetPosition(stream) - ChainHeadToSpine[i].GetPosition(stream);
                    var newDir = ChainHeadToSpineLinkPositions[i + 1] - ChainHeadToSpineLinkPositions[i];
                    var rot = ChainHeadToSpine[i].GetRotation(stream);
                    ChainHeadToSpine[i].SetRotation(stream, Quaternion.Lerp(rot, QuaternionExt.FromToRotation(prevDir, newDir) * rot, 1));
                }
            }
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
                    axis = Vector3.up;
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
        public static Vector3 CapsuleCapsuleResolve(Vector3 p1, Vector3 q1, float r1, Vector3 p2, Vector3 q2, float r2)
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
                normal = Vector3.Normalize(Vector3.Cross(axis, Vector3.up));
                if (normal.sqrMagnitude < k_MinMag) normal = Vector3.Normalize(Vector3.Cross(axis, Vector3.right));
                if (normal.sqrMagnitude < k_MinMag) normal = Vector3.up;
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
        public static Vector3 PushOutFromCapsule(Vector3 p, Vector3 a, Vector3 b, float radiusWithSkin)
        {
            Vector3 q = ClosestPointOnSegment(p, a, b);
            Vector3 qp = p - q;
            float dSqr = Vector3.Dot(qp, qp);
            if (dSqr >= radiusWithSkin * radiusWithSkin) return p;
            float d = Mathf.Sqrt(Mathf.Max(dSqr, k_SqrEpsilon));
            Vector3 n = (d > 0f) ? (qp / d) : Vector3.up;
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
        public void SolveTwoBone(AnimationStream stream, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip, AffineTransform target, AffineTransform hint, bool HasHint, Quaternion targetOffset, Vector3 BendNormal)
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
            float acLen = ac.magnitude;

            float maxReach = abLen + bcLen;
            float oldAbcAngle = TriangleAngle(acLen, abLen, bcLen);
            Vector3 atCorrected = tPosition - aPosition;
            // Vector3 atCorrected = correctedTargetPos - aPosition;
            float atCorrectedLen = atCorrected.magnitude;

            float newAbcAngle = TriangleAngle(atCorrectedLen, abLen, bcLen);

            Vector3 axis;
            if (HasHint)
            {
                axis = Vector3.Cross(hint.translation - aPosition, bc);

                if (axis.sqrMagnitude < k_SqrEpsilon)
                {
                    // use corrected vector, not raw tPosition
                    axis = Vector3.Cross(atCorrected, bc);
                }

                if (axis.sqrMagnitude < k_SqrEpsilon)
                {
                    axis = BendNormal;
                }
            }
            else
            {
                axis = BendNormal;
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
                // Swing root toward corrected target
                root.SetRotation(stream, QuaternionExt.FromToRotation(ac, atCorrected) * root.GetRotation(stream));
            }

            if (HasHint)
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
                        Quaternion hintR = QuaternionExt.FromToRotation(abProj, ahProj);
                        hintR = QuaternionExt.NormalizeSafe(hintR);
                        root.SetRotation(stream, hintR * root.GetRotation(stream));
                    }
                }
            }

            tip.SetRotation(stream, tRotation);
        }
        public Quaternion V4ToQuat(Vector4 v) => new Quaternion(v.x, v.y, v.z, v.w);
        public void SolveLegs(AnimationStream stream, BoolProperty enabledProp, ReadWriteTransformHandle root, ReadWriteTransformHandle mid, ReadWriteTransformHandle tip, Vector3Property targetPosProp, Vector4Property targetRotProp, Vector3Property hintPosProp, Vector4Property hintRotProp, BoolProperty hintWeightProp, Quaternion targetOffset, Vector3Property bendNormalProp)
        {
            if (!enabledProp.Get(stream))
            {
                return;
            }

            if (!(root.IsValid(stream) && mid.IsValid(stream) && tip.IsValid(stream)))
            {
                return;
            }

            Quaternion tRot = V4ToQuat(targetRotProp.Get(stream));
            Quaternion hRot = V4ToQuat(hintRotProp.Get(stream));

            AffineTransform target = new AffineTransform(targetPosProp.Get(stream), tRot);
            AffineTransform hint = new AffineTransform(hintPosProp.Get(stream), hRot);
            Vector3 bendNormal = bendNormalProp.Get(stream);

            SolveTwoBone(stream, root, mid, tip, target, hint, hintWeightProp.Get(stream), targetOffset, bendNormal);
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
            bool doCollisions = collisionsEnabled.Get(stream) && chestStart.IsValid(stream) && chestEnd.IsValid(stream);
            if (doCollisions)
            {
                Vector3 a = chestStart.GetPosition(stream);
                Vector3 b = chestEnd.GetPosition(stream);
                float chestR = Mathf.Max(0f, chestRadius.Get(stream) + collisionSkin.Get(stream));

                if (useHandCapsule.Get(stream))
                {
                    float hRad = Mathf.Max(0f, handRadius.Get(stream) + handSkin.Get(stream));

                    // Use the actual current mid & tip positions as the hand capsule ends
                    Vector3 handA = mid.GetPosition(stream);
                    Vector3 handB = tip.GetPosition(stream);

                    Vector3 correction = CapsuleCapsuleResolve(handA, handB, hRad, a, b, chestR);
                    if (correction.sqrMagnitude > 0f)
                    {
                        // Move the IK target & hint by the same correction
                        tgtPos += correction;
                        hintPos += correction * 0.25f; // steer elbow slightly
                    }
                }
                else
                {
                    tgtPos = PushOutFromCapsule(tgtPos, a, b, chestR);
                    Vector3 nudgedHint = PushOutFromCapsule(hintPos, a, b, chestR * 0.9f);
                    hintPos = Vector3.Lerp(hintPos, nudgedHint, 0.6f);
                }
            }
            var target = new AffineTransform(tgtPos, tgtRot);
            var hint = new AffineTransform(hintPos, hintRot);
            // First solve (arms variant to preserve wrist)
            SolveTwoBoneIKArms(stream, root, mid, tip, target, hint, hintWeightProp.Get(stream), targetOffset);
            // Optional elbow protection pass
            if (protectElbow.Get(stream) && doCollisions)
            {
                Vector3 a = chestStart.GetPosition(stream);
                Vector3 b = chestEnd.GetPosition(stream);
                float chestR = Mathf.Max(0f, chestRadius.Get(stream) + collisionSkin.Get(stream));

                Vector3 B = mid.GetPosition(stream);
                Vector3 pushedB = PushOutFromCapsule(B, a, b, chestR);
                if ((pushedB - B).sqrMagnitude > 1e-10f)
                {
                    SwingElbowAroundAC(stream, root, mid, tip, pushedB);
                    // Re-lock wrist to target after elbow swing
                    SolveTwoBoneIKArms(stream, root, mid, tip, target, hint, hintWeightProp.Get(stream), targetOffset);
                }
            }
        }
        public float TriangleAngle(float aLen, float aLen1, float aLen2)
        {
            if (aLen1 <= k_SqrEpsilon || aLen2 <= k_SqrEpsilon)
            {
                return 0f;
            }

            float c = Mathf.Clamp((aLen1 * aLen1 + aLen2 * aLen2 - aLen * aLen) / (aLen1 * aLen2) / 2.0f, -1.0f, 1.0f);
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
                HandleSpine = BindHandle(animator, data.spine),
                HandleUpperChest = BindHandle(animator, data.upperChest),
                HandleLeftShoulder = BindHandle(animator, data.LeftShoulder),
                HandleRightShoulder = BindHandle(animator, data.RightShoulder),
                targetPositionHips = Vector3Property.Bind(animator, component, data.TargetPositionPropertyHips),
                targetPositionHead = Vector3Property.Bind(animator, component, data.TargetPositionPropertyHead),
                TargetChestPosition = Vector3Property.Bind(animator, component, data.PropertyChestPosition),
                bendNormalHead = Vector3Property.Bind(animator, component, data.BendNormalHeadProperty),

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
                MinHeadSpineHeight = FloatProperty.Bind(animator, component, data.MinHeadSpineHeightFloatProperty)


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

            var cacheBuilder = new AnimationJobCacheBuilder();

            job.spineMaxIterationsIdx = cacheBuilder.Add(10);
            job.spineToleranceIdx = cacheBuilder.Add(0.001f);
            job.spineCache = cacheBuilder.Build();



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

            job.spineCache.Dispose();
        }
    }
}
