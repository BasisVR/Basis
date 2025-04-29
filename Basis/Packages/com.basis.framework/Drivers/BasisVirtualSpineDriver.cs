using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Controls the virtual spine and bone hierarchy for character animation.
/// </summary>
[System.Serializable]
public class BasisVirtualSpineDriver
{
    #region Bone Controls
    [Header("Core Bones")]
    [SerializeField] private BasisBoneControl centerEye;
    [SerializeField] private BasisBoneControl head;
    [SerializeField] private BasisBoneControl neck;
    [SerializeField] private BasisBoneControl chest;
    [SerializeField] private BasisBoneControl spine;
    [SerializeField] private BasisBoneControl hips;

    [Header("Upper Body")]
    [SerializeField] private BasisBoneControl rightShoulder;
    [SerializeField] private BasisBoneControl leftShoulder;
    [SerializeField] private BasisBoneControl leftLowerArm;
    [SerializeField] private BasisBoneControl rightLowerArm;
    [SerializeField] private BasisBoneControl leftHand;
    [SerializeField] private BasisBoneControl rightHand;

    [Header("Lower Body")]
    [SerializeField] private BasisBoneControl leftLowerLeg;
    [SerializeField] private BasisBoneControl rightLowerLeg;
    [SerializeField] private BasisBoneControl leftFoot;
    [SerializeField] private BasisBoneControl rightFoot;
    #endregion

    #region Configuration
    [Header("Rotation Settings")]
    [SerializeField] private float neckRotationSpeed = 40f;
    [SerializeField] private float chestRotationSpeed = 25f;
    [SerializeField] private float spineRotationSpeed = 30f;
    [SerializeField] private float hipsRotationSpeed = 40f;

    [Header("Offset Settings")]
    [SerializeField] private Vector3 headOffset;    // Eye-to-head in eye local space
    [SerializeField] private Vector3 neckOffset;    // Head-to-neck in head local space
    [SerializeField] private Vector3 neckEyeOffset;
    #endregion

    /// <summary>
    /// Initializes the bone controls and sets up virtual overrides.
    /// </summary>
    public void Initialize()
    {
        var localBoneDriver = BasisLocalPlayer.Instance.LocalBoneDriver;
        
        if (localBoneDriver.FindBone(out centerEye, BasisBoneTrackedRole.CenterEye)) { }
        if (localBoneDriver.FindBone(out head, BasisBoneTrackedRole.Head))
        {
            head.HasVirtualOverride = true;
        }
        if (localBoneDriver.FindBone(out neck, BasisBoneTrackedRole.Neck))
        {
            neck.HasVirtualOverride = true;
        }
        if (localBoneDriver.FindBone(out chest, BasisBoneTrackedRole.Chest))
        {
            chest.HasVirtualOverride = true;
        }
        if (localBoneDriver.FindBone(out spine, BasisBoneTrackedRole.Spine))
        {
            spine.HasVirtualOverride = true;
        }
        if (localBoneDriver.FindBone(out hips, BasisBoneTrackedRole.Hips))
        {
            hips.HasVirtualOverride = true;
        }

        // Initialize remaining bones without virtual override
        localBoneDriver.FindBone(out leftLowerArm, BasisBoneTrackedRole.LeftLowerArm);
        localBoneDriver.FindBone(out rightLowerArm, BasisBoneTrackedRole.RightLowerArm);
        localBoneDriver.FindBone(out leftLowerLeg, BasisBoneTrackedRole.LeftLowerLeg);
        localBoneDriver.FindBone(out rightLowerLeg, BasisBoneTrackedRole.RightLowerLeg);
        localBoneDriver.FindBone(out leftHand, BasisBoneTrackedRole.LeftHand);
        localBoneDriver.FindBone(out rightHand, BasisBoneTrackedRole.RightHand);
        localBoneDriver.FindBone(out leftFoot, BasisBoneTrackedRole.LeftFoot);
        localBoneDriver.FindBone(out rightFoot, BasisBoneTrackedRole.RightFoot);

        BasisLocalPlayer.Instance.OnPreSimulateBones += OnSimulateHead;
    }

    /// <summary>
    /// Cleans up virtual overrides and unsubscribes from events.
    /// </summary>
    public void DeInitialize()
    {
        if (neck != null) neck.HasVirtualOverride = false;
        if (chest != null) chest.HasVirtualOverride = false;
        if (hips != null) hips.HasVirtualOverride = false;
        if (spine != null) spine.HasVirtualOverride = false;

        BasisLocalPlayer.Instance.OnPreSimulateBones -= OnSimulateHead;
    }

    /// <summary>
    /// Simulates head and spine movement based on center eye tracking.
    /// </summary>
    private void OnSimulateHead()
    {
        float deltaTime = Time.deltaTime;

        // Update head and neck rotation
        head.OutGoingData.rotation = centerEye.OutGoingData.rotation;
        neck.OutGoingData.rotation = head.OutGoingData.rotation;

        // Update chest rotation with reduced influence
        Quaternion targetChestRotation = Quaternion.Slerp(chest.OutGoingData.rotation, neck.OutGoingData.rotation, deltaTime * chestRotationSpeed);
        chest.OutGoingData.rotation = Quaternion.Euler(0, targetChestRotation.eulerAngles.y, 0);

        // Update spine rotation
        Quaternion targetSpineRotation = Quaternion.Slerp(spine.OutGoingData.rotation, chest.OutGoingData.rotation, deltaTime * spineRotationSpeed);
        spine.OutGoingData.rotation = Quaternion.Euler(0, targetSpineRotation.eulerAngles.y, 0);

        // Update hips rotation
        Quaternion targetHipsRotation = Quaternion.Slerp(hips.OutGoingData.rotation, spine.OutGoingData.rotation, deltaTime * hipsRotationSpeed);
        hips.OutGoingData.rotation = Quaternion.Euler(0, targetHipsRotation.eulerAngles.y, 0);

        // Apply position control
        ApplyHeadPositionControl(head);
        ApplyPositionControl(neck);
        ApplyPositionControl(chest);
        ApplyPositionControl(spine);
        ApplyPositionControl(hips);
    }

    /// <summary>
    /// Applies position control for the head bone.
    /// </summary>
    private void ApplyHeadPositionControl(BasisBoneControl boneControl)
    {
        boneControl.OutGoingData.position = boneControl.RelativeOffset;
    }

    /// <summary>
    /// Applies position control for a bone based on its target's rotation.
    /// </summary>
    private void ApplyPositionControl(BasisBoneControl boneControl)
    {
        quaternion targetRotation = boneControl.Target.OutGoingData.rotation;

        // Extract yaw-only forward vector
        float3 forward = math.mul(targetRotation, new float3(0, 0, 1));
        forward.y = 0;
        forward = math.normalize(forward);

        quaternion yawRotation = quaternion.LookRotationSafe(forward, new float3(0, 1, 0));
        float3 offset = math.mul(yawRotation, boneControl.Offset);

        boneControl.OutGoingData.position = boneControl.Target.OutGoingData.position + offset;
    }
}
