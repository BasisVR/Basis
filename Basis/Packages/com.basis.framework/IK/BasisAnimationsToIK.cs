using Basis.Scripts.Device_Management.Devices.Simulation;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using System.Collections.Generic;
using Basis.Scripts.Device_Management.Devices.Desktop;
public class BasisAnimationsToIK : MonoBehaviour
{
    public Animator Animator;

    // One binding per tracker
    private readonly List<TrackerBinding> _bindings = new List<TrackerBinding>();

    private class TrackerBinding
    {
        public BasisInputXRSimulate Tracker;
        public Transform Bone;

        // Tracker pose relative to the bone at startup
        public Vector3 LocalOffsetPosition;
        public Quaternion LocalOffsetRotation;
    }

    void OnEnable()
    {
        _bindings.Clear();

        BasisSimulateXR simulated = FindAnyObjectByType<BasisSimulateXR>();
        if (simulated == null || Animator == null)
        {
            Debug.LogWarning("BasisAnimationsToIK: Missing SimulateXR or Animator.");
            return;
        }

        foreach (BasisInputXRSimulate tracker in simulated.Inputs)
        {
            if (tracker == null)
            {
                continue;
            }

            if (tracker.TryGetRole(out BasisBoneTrackedRole role) && BasisAvatarDriver.TryConvertToHumanoidRole(role, out HumanBodyBones humanoidBone))
            {
                Transform bone = Animator.GetBoneTransform(humanoidBone);
                if (bone == null)
                {
                    continue;
                }

                Transform followTransform = tracker.FollowMovement;

                bone.GetPositionAndRotation(out var bonePos, out var boneRot);

                followTransform.GetPositionAndRotation(out Vector3 trackerPos, out Quaternion trackerRot);

                // Rotation offset: tracker is boneRot * localOffsetRot
                Quaternion localOffsetRot = Quaternion.Inverse(boneRot) * trackerRot;

                // Position offset in bone space: trackerPos = bonePos + boneRot * localOffsetPos
                Vector3 localOffsetPos = Quaternion.Inverse(boneRot) * (trackerPos - bonePos);

                if (humanoidBone == HumanBodyBones.LeftHand || humanoidBone == HumanBodyBones.RightHand)
                {
                    _bindings.Add(new TrackerBinding
                    {
                        Tracker = tracker,
                        Bone = bone,
                        LocalOffsetPosition = Vector3.zero,
                        LocalOffsetRotation = Quaternion.identity,
                    });
                }
                else
                {
                    _bindings.Add(new TrackerBinding
                    {
                        Tracker = tracker,
                        Bone = bone,
                        LocalOffsetPosition = localOffsetPos,
                        LocalOffsetRotation = localOffsetRot
                    });
                }

                Debug.Log($"Mapped {tracker.name} → {humanoidBone} with offset.");
            }
        }
    }
    void Update()
    {
        for (int Index = 0; Index < _bindings.Count; Index++)
        {
            TrackerBinding b = _bindings[Index];
            if (b.Tracker == null || b.Bone == null)
            {
                continue;
            }

            // Rebuild the tracker’s world pose from bone pose + stored offset
            b.Bone.GetPositionAndRotation(out var bonePos, out var boneRot);

            Quaternion targetRot = boneRot * b.LocalOffsetRotation;
            Vector3 targetPos = bonePos + boneRot * b.LocalOffsetPosition;

            b.Tracker.FollowMovement.SetPositionAndRotation(targetPos, targetRot);
        }
        Transform Trans = Animator.GetBoneTransform(HumanBodyBones.Head);
        BasisDesktopEye.Instance.ScaledDeviceCoord.position = Trans.position;
        BasisDesktopEye.Instance.ScaledDeviceCoord.rotation = Trans.rotation;
    }
}
