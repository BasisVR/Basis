using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace UnityEngine.Animations.Rigging
{
    /// <summary>
    /// Minimal constraint that ONLY drives the hips transform (position + rotation).
    /// Added: calibration offset (position + quaternion) to bake T-pose offsets.
    /// </summary>
    [System.Serializable]
    public struct BasisHipsHeadIKConstraintData : IAnimationJobData, BasisIHipsHeadIKConstraintData
    {
        [SerializeField] Transform m_Hips;

        // Live targets
        [SyncSceneToStream, SerializeField] public Vector3 hipsTargetPosition;
        // NOTE: kept your original naming/comment, but this is a Quaternion (x,y,z,w), not Euler angles.
        [SyncSceneToStream, SerializeField] public Quaternion hipsTargetRotationEuler; // degrees

        // Calibration offsets (applied on top of the target each frame)
        [SyncSceneToStream, SerializeField] public Quaternion hipsOffsetRotation;

        public Transform hips { get => m_Hips; set => m_Hips = value; }

        // Property name bindings so the binder can hook Vector/Quat Properties.
        string BasisIHipsHeadIKConstraintData.hipsTargetPositionVector3Property
            => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(hipsTargetPosition));
        // Kept the original name suffix ("Vector3Property") to avoid breaking changes,
        // even though it's bound as a Vector4Property.
        string BasisIHipsHeadIKConstraintData.hipsTargetRotationVector3Property
            => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(hipsTargetRotationEuler));
        string BasisIHipsHeadIKConstraintData.hipsOffsetRotationVector4Property
            => ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(hipsOffsetRotation));

        // IAnimationJobData
        bool IAnimationJobData.IsValid() => m_Hips != null;

        void IAnimationJobData.SetDefaultValues()
        {
            m_Hips = null;

            hipsTargetPosition = Vector3.zero;
            hipsTargetRotationEuler = Quaternion.identity;
            hipsOffsetRotation = Quaternion.identity;
        }
    }

    public interface BasisIHipsHeadIKConstraintData
    {
        Transform hips { get; }

        // Existing targets
        string hipsTargetPositionVector3Property { get; }
        string hipsTargetRotationVector3Property { get; }
        string hipsOffsetRotationVector4Property { get; }
    }

    [Unity.Burst.BurstCompile]
    public struct BasisHipsHeadIKConstraintJob : IWeightedAnimationJob
    {
        public ReadWriteTransformHandle hips;

        // Targets
        public Vector3Property hipsTargetPosition;
        public Vector4Property hipsTargetRotation;

        public Vector4Property hipsOffsetRotation;

        public FloatProperty jobWeight { get; set; }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float w = jobWeight.Get(stream);
            if (w <= 0f)
            {
                BasisAnimationRuntimeUtils.PassThrough(stream, hips);
                return;
            }

            // Read targets
            Vector3 targetPos = hipsTargetPosition.Get(stream);
            Vector4 targetRotV4 = hipsTargetRotation.Get(stream);
            Quaternion targetRot = Vector4ToRotation(targetRotV4);

            Vector4 offsetRotV4 = hipsOffsetRotation.Get(stream);
            Quaternion offsetRot = Vector4ToRotation(offsetRotV4);

            // Rotation: target followed by offset (apply offset in target's space).
            // If you require the opposite order for your rig, swap to: finalRot = offsetRot * targetRot;
            Quaternion finalRot = targetRot * offsetRot;

            // Apply directly
            hips.SetPosition(stream, targetPos);
            hips.SetRotation(stream, finalRot);
        }

        public static Quaternion Vector4ToRotation(Vector4 r)
        {
            return new Quaternion(r.x, r.y, r.z, r.w);
        }
    }

    public class BasisHipsHeadIKConstraintJobBinder<T>
        : AnimationJobBinder<BasisHipsHeadIKConstraintJob, T>
        where T : struct, IAnimationJobData, BasisIHipsHeadIKConstraintData
    {
        public override BasisHipsHeadIKConstraintJob Create(Animator animator, ref T data, Component component)
        {
            var job = new BasisHipsHeadIKConstraintJob
            {
                hips = ReadWriteTransformHandle.Bind(animator, data.hips),

                // Targets
                hipsTargetPosition = Vector3Property.Bind(
                    animator, component, data.hipsTargetPositionVector3Property),
                hipsTargetRotation = Vector4Property.Bind(
                    animator, component, data.hipsTargetRotationVector3Property),
                hipsOffsetRotation = Vector4Property.Bind(
                    animator, component, data.hipsOffsetRotationVector4Property),
            };

            return job;
        }

        public override void Destroy(BasisHipsHeadIKConstraintJob job)
        {
            // Nothing allocated with Allocator here.
        }
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Animation Rigging/Hips + Head IK Constraint")]
    [HelpURL("https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.3/manual/index.html")]
    public class BasisHipsHeadIKConstraint
        : RigConstraint<BasisHipsHeadIKConstraintJob,
                        BasisHipsHeadIKConstraintData,
                        BasisHipsHeadIKConstraintJobBinder<BasisHipsHeadIKConstraintData>>
    {
        // No extra editor/debug state – this constraint only drives hips, with T-pose calibration offsets.
    }
}
