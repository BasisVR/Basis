using System.IO;

namespace UnityEngine.Animations.Rigging
{
    /// <summary>
    /// The TwoBoneIK constraint data.
    /// </summary>
    [System.Serializable]
    public struct BasisTwoBoneIKConstraintData : IAnimationJobData, BasisITwoBoneIKConstraintData
    {
        [SerializeField] Transform m_Root;
        [SerializeField] Transform m_Mid;
        [SerializeField] Transform m_Tip;

        [SyncSceneToStream, SerializeField]
        public Vector3 TargetPosition;
        [SyncSceneToStream, SerializeField]
        public Quaternion TargetRotation;
        [SyncSceneToStream, SerializeField]
        public Vector3 HintPosition;
        [SyncSceneToStream, SerializeField]
        public Quaternion HintRotation;

        Vector3 BasisITwoBoneIKConstraintData.targetPosition { get => TargetPosition; }
        Quaternion BasisITwoBoneIKConstraintData.targetRotation { get => TargetRotation; }

        Vector3 BasisITwoBoneIKConstraintData.hintPosition { get => HintPosition; }
        Quaternion BasisITwoBoneIKConstraintData.HintRotation { get => HintRotation; }

        [SyncSceneToStream, SerializeField]
        bool m_HintWeight;

        [SyncSceneToStream, SerializeField]
        bool m_Enabled;

        public Transform root { get => m_Root; set => m_Root = value; }
        public Transform mid { get => m_Mid; set => m_Mid = value; }
        public Transform tip { get => m_Tip; set => m_Tip = value; }
        public bool hintWeight { get => m_HintWeight; set => m_HintWeight = value; }

        public bool IsEnabled { get => m_Enabled; set => m_Enabled = value; }

        string BasisITwoBoneIKConstraintData.EnabledProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_Enabled));
        string BasisITwoBoneIKConstraintData.hintWeightFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintWeight));
        string BasisITwoBoneIKConstraintData.TargetpositionVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition));
        string BasisITwoBoneIKConstraintData.TargetrotationVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation));
        string BasisITwoBoneIKConstraintData.HintpositionVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintPosition));
        string BasisITwoBoneIKConstraintData.HintrotationVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintRotation));
        string BasisITwoBoneIKConstraintData.HintDirectionProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintDirection));

        [SyncSceneToStream, SerializeField]
        public Vector3 M_CalibratedOffset;
        [SyncSceneToStream, SerializeField]
        public Quaternion M_CalibratedRotation;

        public Vector3 CalibratedOffset
        {
            get
            {
                return M_CalibratedOffset;
            }
        }

        public Quaternion CalibratedRotation
        {
            get
            {
                return M_CalibratedRotation;
            }
        }

        [SyncSceneToStream, SerializeField]
        public Vector3 m_HintDirection;
        Vector3 BasisITwoBoneIKConstraintData.HintDirection
        {
            get
            {
                return m_HintDirection;
            }
        }

        bool IAnimationJobData.IsValid() => (m_Tip != null && m_Mid != null && m_Root != null && m_Tip.IsChildOf(m_Mid) && m_Mid.IsChildOf(m_Root));

        void IAnimationJobData.SetDefaultValues()
        {
            m_Root = null;
            m_Mid = null;
            m_Tip = null;
            m_HintWeight = true;

        }
    }

    [DisallowMultipleComponent, AddComponentMenu("Animation Rigging/Two Bone IK Constraint")]
    [HelpURL("https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/manual/constraints/TwoBoneIKConstraint.html")]
    public class BasisTwoBoneIKConstraint : RigConstraint<BasisTwoBoneIKConstraintJob, BasisTwoBoneIKConstraintData, BasisTwoBoneIKConstraintJobBinder<BasisTwoBoneIKConstraintData>>
    {
        /// <inheritdoc />
        protected override void OnValidate()
        {
            base.OnValidate();
            m_Data.hintWeight = m_Data.hintWeight;
        }
    }

    [Unity.Burst.BurstCompile]
    public struct BasisTwoBoneIKConstraintJob : IWeightedAnimationJob
    {
        public ReadWriteTransformHandle root;
        public ReadWriteTransformHandle mid;
        public ReadWriteTransformHandle tip;

        public Vector3Property hintPosition;
        public Vector3Property targetPosition;

        public Vector4Property hintRotation;
        public Vector4Property targetRotation;

        public AffineTransform targetOffset;
        public BoolProperty hintWeight;
        public FloatProperty jobWeight { get; set; }
        public Vector3Property BendNormal;
        public BoolProperty IsEnabledWeight;
        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float w = jobWeight.Get(stream);
            if(IsEnabledWeight.Get(stream) == false)
            {
                Pass(stream);
                return;
            }
            if (w > 0f)
            {
                Vector4 Rotation = targetRotation.Get(stream);
                Vector4 RotationHint = hintRotation.Get(stream);
                Quaternion Rot = new Quaternion(Rotation.x, Rotation.y, Rotation.z, Rotation.w);
                Quaternion HintRot = new Quaternion(RotationHint.x, RotationHint.y, RotationHint.z, RotationHint.w);

                // BasisDebug.Log("Value is " + targetPosition);
                AffineTransform target = new AffineTransform(targetPosition.Get(stream), Rot);
                AffineTransform hint = new AffineTransform(hintPosition.Get(stream), HintRot);
                Vector3 BendNormalOutput = BendNormal.Get(stream);
                //   BasisDebug.Log("Output Normal is " + BendNormalOutput);
                BasisAnimationRuntimeUtils.SolveTwoBone(stream, root, mid, tip, target, hint, hintWeight.Get(stream), targetOffset, BendNormalOutput);
            }
            else
            {
                Pass(stream);
            }
        }
        public void Pass(AnimationStream stream)
        {
            BasisAnimationRuntimeUtils.PassThrough(stream, root);
            BasisAnimationRuntimeUtils.PassThrough(stream, mid);
            BasisAnimationRuntimeUtils.PassThrough(stream, tip);
        }
    }


    public interface BasisITwoBoneIKConstraintData
    {
        Transform root { get; }
        Transform mid { get; }
        Transform tip { get; }

        public Vector3 targetPosition { get; }
        public Vector3 hintPosition { get; }
        public Vector3 CalibratedOffset { get; }
        public Vector3 HintDirection { get; }

        public Quaternion targetRotation { get; }
        public Quaternion HintRotation { get; }
        public Quaternion CalibratedRotation { get; }

        string hintWeightFloatProperty { get; }

        string TargetpositionVector3Property { get; }
        string TargetrotationVector3Property { get; }

        string HintpositionVector3Property { get; }
        string HintrotationVector3Property { get; }
        string HintDirectionProperty { get; }
        string EnabledProperty { get; }

    }

    public class BasisTwoBoneIKConstraintJobBinder<T> : AnimationJobBinder<BasisTwoBoneIKConstraintJob, T> where T : struct, IAnimationJobData, BasisITwoBoneIKConstraintData
    {
        public override BasisTwoBoneIKConstraintJob Create(Animator animator, ref T data, Component component)
        {
            BasisTwoBoneIKConstraintJob job = new BasisTwoBoneIKConstraintJob
            {
                root = ReadWriteTransformHandle.Bind(animator, data.root),
                mid = ReadWriteTransformHandle.Bind(animator, data.mid),
                tip = ReadWriteTransformHandle.Bind(animator, data.tip),
                targetPosition = Vector3Property.Bind(animator, component, data.TargetpositionVector3Property),
                targetRotation = Vector4Property.Bind(animator, component, data.TargetrotationVector3Property),

                hintPosition = Vector3Property.Bind(animator, component, data.HintpositionVector3Property),
                hintRotation = Vector4Property.Bind(animator, component, data.HintrotationVector3Property),

                targetOffset = new AffineTransform(data.CalibratedOffset, data.CalibratedRotation),

                hintWeight = BoolProperty.Bind(animator, component, data.hintWeightFloatProperty),
                BendNormal = Vector3Property.Bind(animator, component, data.HintDirectionProperty),

                IsEnabledWeight = BoolProperty.Bind(animator, component, data.EnabledProperty),
            };
            return job;
        }

        public override void Destroy(BasisTwoBoneIKConstraintJob job) { }
    }
}
