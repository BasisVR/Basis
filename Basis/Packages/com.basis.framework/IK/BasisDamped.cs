using UnityEngine;
using UnityEngine.Animations.Rigging;
/// <summary>
/// The DampedTransform constraint data.
/// </summary>
[System.Serializable]
public struct BasisDampedTransformData : IAnimationJobData, BasisIDampedTransformData
{
    [SerializeField] Transform m_ConstrainedObject;
    /// <inheritdoc />
    public Transform constrainedObject { get => m_ConstrainedObject; set => m_ConstrainedObject = value; }

    [SyncSceneToStream, SerializeField]
    public Vector3 TargetPosition;
    [SyncSceneToStream, SerializeField]
    public Vector3 TargetRotation;
    Vector3 BasisIDampedTransformData.TargetPosition { get => TargetPosition; }

    Vector3 BasisIDampedTransformData.TargetRotation { get => TargetRotation; }

    public string TargetpositionVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition));

    public string TargetrotationVector3Property => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotation));

    /// <inheritdoc />
    bool IAnimationJobData.IsValid() => !(m_ConstrainedObject == null);

    /// <inheritdoc />
    void IAnimationJobData.SetDefaultValues()
    {
        m_ConstrainedObject = null;
    }
}

/// <summary>
/// DampedTransform constraint.
/// </summary>
[DisallowMultipleComponent, AddComponentMenu("Animation Rigging/Damped Transform")]
[HelpURL("https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/manual/constraints/DampedTransform.html")]
public class BasisDamped : RigConstraint< BasisDampedTransformJob, BasisDampedTransformData, BasisJobBinder<BasisDampedTransformData>>
{
    /// <inheritdoc />
    protected override void OnValidate()
    {
        base.OnValidate();
    }
}

namespace UnityEngine.Animations.Rigging
{
    /// <summary>
    /// The DampedTransform job.
    /// </summary>
    [Unity.Burst.BurstCompile]
    public struct BasisDampedTransformJob : IWeightedAnimationJob
    {
        /// <summary>The Transform handle for the constrained object Transform.</summary>
        public ReadWriteTransformHandle driven;
        /// <summary>The transform handle for the target transform.</summary>
        public Vector3Property targetPosition;
        /// <summary>The transform handle for the target transform.</summary>
        public Vector3Property targetRotation;

        /// <summary>Initial TR offset from source to constrained object.</summary>
        public AffineTransform localBindTx;

        /// <inheritdoc />
        public FloatProperty jobWeight { get; set; }

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
                AffineTransform sourceTx = new AffineTransform(targetPosition.Get(stream), Quaternion.Euler(targetRotation.Get(stream)));
                BasisDebug.Log("Value is " + sourceTx.translation);

                AffineTransform targetTx = sourceTx * localBindTx;
                driven.SetGlobalTR(stream, targetTx.translation, targetTx.rotation);
            }
            else
            {
                AnimationRuntimeUtils.PassThrough(stream, driven);
            }
        }
    }

    /// <summary>
    /// This interface defines the data mapping for DampedTransform.
    /// </summary>
    public interface BasisIDampedTransformData
    {
        /// <summary>The Transform affected by the constraint Source Transform.</summary>
        Transform constrainedObject { get; }
        public Vector3 TargetPosition { get; }
        public Vector3 TargetRotation { get; }

        /// <summary>The path to the override position property in the constraint component.</summary>
        string TargetpositionVector3Property { get; }
        /// <summary>The path to the override rotation property in the constraint component.</summary>
        string TargetrotationVector3Property { get; }
    }

    /// <summary>
    /// The DampedTransform job binder.
    /// </summary>
    /// <typeparam name="T">The constraint data type</typeparam>
    public class BasisJobBinder<T> : AnimationJobBinder<BasisDampedTransformJob, T> where T : struct, IAnimationJobData, BasisIDampedTransformData
    {
        /// <inheritdoc />
        public override BasisDampedTransformJob Create(Animator animator, ref T data, Component component)
        {
            var job = new BasisDampedTransformJob
            {
                driven = ReadWriteTransformHandle.Bind(animator, data.constrainedObject)
            };


            var drivenTx = new AffineTransform(data.constrainedObject.position, data.constrainedObject.rotation);
            var sourceTx = new AffineTransform(data.TargetPosition, Quaternion.Euler(data.TargetRotation));

            job.targetPosition = Vector3Property.Bind(animator, component, data.TargetpositionVector3Property);
            job.targetRotation = Vector3Property.Bind(animator, component, data.TargetrotationVector3Property);

            job.localBindTx = sourceTx.InverseMul(drivenTx);

            return job;
        }
        /// <inheritdoc />
        public override void Destroy(BasisDampedTransformJob job)
        {
        }
    }
}
