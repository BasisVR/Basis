namespace UnityEngine.Animations.Rigging
{
    /// <summary>
    /// Minimal constraint that ONLY drives the hips transform (position + rotation).
    /// Added: calibration offset (position + quaternion) to bake T-pose offsets.
    /// </summary>
    [System.Serializable]
    public struct BasisIKConstraintData : IAnimationJobData, BasisIHipsHeadIKConstraintData
    {
        [SerializeField] Transform m_target;

        // Live targets
        [SyncSceneToStream, SerializeField] public Vector3 TargetPosition;
        // NOTE: kept your original naming/comment, but this is a Quaternion (x,y,z,w), not Euler angles.
        [SyncSceneToStream, SerializeField] public Quaternion TargetRotationEuler; // degrees

        // Calibration offsets (applied on top of the target each frame)
        [SyncSceneToStream, SerializeField] public Quaternion OffsetRotation;

        public Transform target { get => m_target; set => m_target = value; }

        // Property name bindings so the binder can hook Vector/Quat Properties.
        string BasisIHipsHeadIKConstraintData.TargetPositionVector3Property
            => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetPosition));
        // Kept the original name suffix ("Vector3Property") to avoid breaking changes,
        // even though it's bound as a Vector4Property.
        string BasisIHipsHeadIKConstraintData.TargetRotationVector3Property
            => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(TargetRotationEuler));
        string BasisIHipsHeadIKConstraintData.OffsetRotationVector4Property
            => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(OffsetRotation));

        // IAnimationJobData
        bool IAnimationJobData.IsValid() => m_target != null;

        void IAnimationJobData.SetDefaultValues()
        {
            m_target = null;

            TargetPosition = Vector3.zero;
            TargetRotationEuler = Quaternion.identity;
            OffsetRotation = Quaternion.identity;
        }
    }

    public interface BasisIHipsHeadIKConstraintData
    {
        Transform target { get; }

        // Existing targets
        string TargetPositionVector3Property { get; }
        string TargetRotationVector3Property { get; }
        string OffsetRotationVector4Property { get; }
    }

    [Unity.Burst.BurstCompile]
    public struct BasisIKConstraintJob : IWeightedAnimationJob
    {
        public ReadWriteTransformHandle handletarget;

        // Targets
        public Vector3Property TargetPosition;
        public Vector4Property TargetRotation;

        public Vector4Property OffsetRotation;

        public FloatProperty jobWeight { get; set; }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float w = jobWeight.Get(stream);
            if (w <= 0f)
            {
                BasisAnimationRuntimeUtils.PassThrough(stream, handletarget);
                return;
            }

            // Read targets
            Vector3 targetPos = TargetPosition.Get(stream);
            Vector4 targetRotV4 = TargetRotation.Get(stream);
            Quaternion targetRot = Vector4ToRotation(targetRotV4);

            Vector4 offsetRotV4 = OffsetRotation.Get(stream);
            Quaternion offsetRot = Vector4ToRotation(offsetRotV4);

            // Rotation: target followed by offset (apply offset in target's space).
            // If you require the opposite order for your rig, swap to: finalRot = offsetRot * targetRot;
            Quaternion finalRot = targetRot * offsetRot;

            // Apply directly
            handletarget.SetPosition(stream, targetPos);
            handletarget.SetRotation(stream, finalRot);
        }

        public static Quaternion Vector4ToRotation(Vector4 r)
        {
            return new Quaternion(r.x, r.y, r.z, r.w);
        }
    }

    public class BasisIKConstraintJobBinder<T>: AnimationJobBinder<BasisIKConstraintJob, T>where T : struct, IAnimationJobData, BasisIHipsHeadIKConstraintData
    {
        public override BasisIKConstraintJob Create(Animator animator, ref T data, Component component)
        {
            var job = new BasisIKConstraintJob
            {
                handletarget = ReadWriteTransformHandle.Bind(animator, data.target),

                // Targets
                TargetPosition = Vector3Property.Bind(
                    animator, component, data.TargetPositionVector3Property),
                TargetRotation = Vector4Property.Bind(
                    animator, component, data.TargetRotationVector3Property),
                OffsetRotation = Vector4Property.Bind(
                    animator, component, data.OffsetRotationVector4Property),
            };

            return job;
        }

        public override void Destroy(BasisIKConstraintJob job)
        {
            // Nothing allocated with Allocator here.
        }
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Animation Rigging Basis/IKConstraint")]
    [HelpURL("https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/manual/index.html")]
    public class BasisIKConstraint
        : RigConstraint<BasisIKConstraintJob,
                        BasisIKConstraintData,
                        BasisIKConstraintJobBinder<BasisIKConstraintData>>
    {
        // No extra editor/debug state – this constraint only drives hips, with T-pose calibration offsets.
    }
}
