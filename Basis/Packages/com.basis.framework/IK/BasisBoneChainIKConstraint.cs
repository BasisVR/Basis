using Unity.Collections;
using UnityEngine;
namespace UnityEngine.Animations.Rigging
{
    /// <summary>
    /// The TwoBoneIK constraint data.
    /// </summary>
    [System.Serializable]
    public struct BasisBoneChainIKConstraintData : IAnimationJobData, BasisIBoneChainIKConstraintData
    {

        [SerializeField] Transform m_Mid;
        [SerializeField] Transform m_Tip;
        [SyncSceneToStream, SerializeField]
        public Vector3 HintPosition;
        [SyncSceneToStream, SerializeField]
        public Vector3 HintRotation;

        [SyncSceneToStream, SerializeField]
        public Vector3 TargetPosition;
        [SyncSceneToStream, SerializeField]
        public Vector3 TargetRotation;
        Vector3 BasisIBoneChainIKConstraintData.targetPosition { get => TargetPosition; }

        Vector3 BasisIBoneChainIKConstraintData.targetRotation { get => TargetRotation; }

        Vector3 BasisIBoneChainIKConstraintData.hintPosition { get => HintPosition; }
        Vector3 BasisIBoneChainIKConstraintData.HintRotation { get => HintRotation; }
        [SyncSceneToStream, SerializeField]
        bool m_HintWeight;
        /// <inheritdoc />
        public Transform mid { get => m_Mid; set => m_Mid = value; }
        /// <inheritdoc />
        public Transform tip { get => m_Tip; set => m_Tip = value; }
        /// <inheritdoc />
        /// <summary>The weight for which hint transform has an effect on IK calculations. This is a value in between 0 and 1.</summary>
        public bool hintWeight { get => m_HintWeight; set => m_HintWeight = value; }
        /// <inheritdoc />
        string BasisIBoneChainIKConstraintData.hintWeightFloatProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintWeight));

        string BasisIBoneChainIKConstraintData.TargetpositionVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition));

        string BasisIBoneChainIKConstraintData.TargetrotationVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation));

        string  BasisIBoneChainIKConstraintData.HintpositionVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintPosition));

        string BasisIBoneChainIKConstraintData.HintrotationVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(HintRotation));

        string BasisIBoneChainIKConstraintData.HintDirectionProperty => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(m_HintDirection));

        [SyncSceneToStream, SerializeField]
        public Vector3 M_CalibratedOffset;
        [SyncSceneToStream, SerializeField]
        public Vector3 M_CalibratedRotation;

        public Vector3 CalibratedOffset
        {
            get
            {
                return M_CalibratedOffset;
            }
        }

        public Vector3 CalibratedRotation
        {
            get
            {
                return M_CalibratedRotation;
            }
        }
        [SyncSceneToStream, SerializeField]
        public Vector3 m_HintDirection;
        Vector3 BasisIBoneChainIKConstraintData.HintDirection
        {
            get
            {
                return m_HintDirection;
            }
        }

        Transform[] BasisIBoneChainIKConstraintData.Destinations => throw new System.NotImplementedException();

        Transform BasisIBoneChainIKConstraintData.mid => throw new System.NotImplementedException();

        Transform BasisIBoneChainIKConstraintData.tip => throw new System.NotImplementedException();

        Vector3 BasisIBoneChainIKConstraintData.CalibratedOffset => throw new System.NotImplementedException();

        Vector3 BasisIBoneChainIKConstraintData.CalibratedRotation => throw new System.NotImplementedException();

        /// <inheritdoc />
        bool IAnimationJobData.IsValid() => (m_Tip != null && m_Mid != null && m_Tip.IsChildOf(m_Mid));

        /// <inheritdoc />
        void IAnimationJobData.SetDefaultValues()
        {
            m_Mid = null;
            m_Tip = null;
            m_HintWeight = true;

        }
    }

    /// <summary>
    /// TwoBoneIK constraint
    /// </summary>
    [DisallowMultipleComponent, AddComponentMenu("Animation Rigging/Two Bone IK Constraint")]
    [HelpURL("https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/manual/constraints/TwoBoneIKConstraint.html")]
    public class BasisBoneChainIKConstraint : RigConstraint<BasisBoneChainIKConstraintJob, BasisBoneChainIKConstraintData, BasisBoneChainIKConstraintJobBinder<BasisBoneChainIKConstraintData>>
    {
        /// <inheritdoc />
        protected override void OnValidate()
        {
            base.OnValidate();
            m_Data.hintWeight = m_Data.hintWeight;
        }
    }
    /// <summary>
    /// The TwoBoneIK constraint job.
    /// </summary>
    [Unity.Burst.BurstCompile]
    public struct BasisBoneChainIKConstraintJob : IWeightedAnimationJob
    {
        /// <summary>The transform handle for the root transform.</summary>
        public ReadWriteTransformHandle root;
        /// <summary>The transform handle for the mid transform.</summary>
        public ReadWriteTransformHandle mid;
        /// <summary>The transform handle for the tip transform.</summary>
        public ReadWriteTransformHandle tip;

        /// <summary>The transform handle for the hint transform.</summary>
        public Vector3Property hintPosition;
        /// <summary>The transform handle for the target transform.</summary>
        public Vector3Property targetPosition;

        /// <summary>The transform handle for the hint transform.</summary>
        public Vector3Property hintRotation;
        /// <summary>The transform handle for the target transform.</summary>
        public Vector3Property targetRotation;

        /// <summary>The offset applied to the target transform if maintainTargetPositionOffset or maintainTargetRotationOffset is enabled.</summary>
        public AffineTransform targetOffset;
        /// <summary>The weight for which hint transform has an effect on IK calculations. This is a value in between 0 and 1.</summary>
        public BoolProperty hintWeight;

        /// <summary>The main weight given to the constraint. This is a value in between 0 and 1.</summary>
        public FloatProperty jobWeight { get; set; }

        /// <summary>The transform handle for the hint transform.</summary>
        public Vector3Property BendNormal;
        /// <summary>
        /// Defines what to do when processing the root motion.
        /// </summary>
        /// <param name="stream">The animation stream to work on.</param>
        public void ProcessRootMotion(AnimationStream stream) { }

        /// <summary>
        /// Defines what to do when processing the animation.
        /// </summary>
        /// <param name="stream">The animation stream to work on.</param>
        public void ProcessAnimation(AnimationStream stream)
        {
            float w = jobWeight.Get(stream);
            if (w > 0f)
            {
                // BasisDebug.Log("Value is " + targetPosition);
               Vector3 Position = targetPosition.Get(stream);
                float Tolerence = 1;

                NativeArray<Vector3> linkPositions = new NativeArray<Vector3>(3, Allocator.TempJob);
                NativeArray<float> linkLengths = new NativeArray<float>(3, Allocator.TempJob);
                float MaxReach = 1;
                int MaxSolve = 12;
                BasisAnimationRuntimeUtils.SolveFABRIK(ref linkPositions,ref linkLengths, Position, Tolerence, MaxReach, MaxSolve);
            }
            else
            {
                BasisAnimationRuntimeUtils.PassThrough(stream, root);
                BasisAnimationRuntimeUtils.PassThrough(stream, mid);
                BasisAnimationRuntimeUtils.PassThrough(stream, tip);
            }
        }
    }

    /// <summary>
    /// This interface defines the data mapping for the TwoBoneIK constraint.
    /// </summary>
    public interface BasisIBoneChainIKConstraintData
    {
        /// <summary>The root transform of the two bones hierarchy.</summary>
        Transform[] Destinations { get; }
        /// <summary>The mid transform of the two bones hierarchy.</summary>
        Transform mid { get; }
        /// <summary>The tip transform of the two bones hierarchy.</summary>
        Transform tip { get; }
        public Vector3 targetPosition { get; }
        public Vector3 targetRotation { get; }
        public Vector3 hintPosition { get; }
        public Vector3 HintRotation { get; }

        public Vector3 CalibratedOffset { get; }
        public Vector3 CalibratedRotation { get; }
        /// <summary>The path to the hint weight property in the constraint component.</summary>
        string hintWeightFloatProperty { get; }

        /// <summary>The path to the override position property in the constraint component.</summary>
        string TargetpositionVector3Property { get; }
        /// <summary>The path to the override rotation property in the constraint component.</summary>
        string TargetrotationVector3Property { get; }

        /// <summary>The path to the override position property in the constraint component.</summary>
        string HintpositionVector3Property { get; }
        /// <summary>The path to the override rotation property in the constraint component.</summary>
        string HintrotationVector3Property { get; }
        string HintDirectionProperty { get; }

        public Vector3 HintDirection { get; }

    }

    /// <summary>
    /// The TwoBoneIK constraint job binder.
    /// </summary>
    /// <typeparam name="T">The constraint data type</typeparam>
    public class BasisBoneChainIKConstraintJobBinder<T> : AnimationJobBinder<BasisBoneChainIKConstraintJob, T> where T : struct, IAnimationJobData, BasisIBoneChainIKConstraintData
    {
        /// <summary>
        /// Creates the animation job.
        /// </summary>
        /// <param name="animator">The animated hierarchy Animator component.</param>
        /// <param name="data">The constraint data.</param>
        /// <param name="component">The constraint component.</param>
        /// <returns>Returns a new job interface.</returns>
        public override BasisBoneChainIKConstraintJob Create(Animator animator, ref T data, Component component)
        {
            BasisBoneChainIKConstraintJob job = new BasisBoneChainIKConstraintJob
            {
             //   root = ReadWriteTransformHandle.Bind(animator, data.root),
                mid = ReadWriteTransformHandle.Bind(animator, data.mid),
                tip = ReadWriteTransformHandle.Bind(animator, data.tip),
                targetPosition = Vector3Property.Bind(animator, component, data.TargetpositionVector3Property),
                targetRotation = Vector3Property.Bind(animator, component, data.TargetrotationVector3Property),

                hintPosition = Vector3Property.Bind(animator, component, data.HintpositionVector3Property),
                hintRotation = Vector3Property.Bind(animator, component, data.HintrotationVector3Property),

                targetOffset = AffineTransform.identity,
            };
            job.targetOffset.translation = data.CalibratedOffset;
            job.targetOffset.rotation = Quaternion.Euler(data.CalibratedRotation);
            job.hintWeight = BoolProperty.Bind(animator, component, data.hintWeightFloatProperty);
            job.BendNormal = Vector3Property.Bind(animator, component, data.HintDirectionProperty);

            return job;
        }

        /// <summary>
        /// Destroys the animation job.
        /// </summary>
        /// <param name="job">The animation job to destroy.</param>
        public override void Destroy(BasisBoneChainIKConstraintJob job)
        {
        }
    }
}
