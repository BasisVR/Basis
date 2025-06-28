using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Mathematics;
using UnityEngine;
[System.Serializable]
public class BasisLocalVirtualSpineDriver
{
    [SerializeField] public BasisLocalBoneControl CenterEye;
    [SerializeField] public BasisLocalBoneControl Head;
    [SerializeField] public BasisLocalBoneControl Neck;
    [SerializeField] public BasisLocalBoneControl Chest;
    [SerializeField] public BasisLocalBoneControl Spine;
    [SerializeField] public BasisLocalBoneControl Hips;
    [SerializeField] public BasisLocalBoneControl RightShoulder;
    [SerializeField] public BasisLocalBoneControl LeftShoulder;
    [SerializeField] public BasisLocalBoneControl LeftLowerArm;
    [SerializeField] public BasisLocalBoneControl RightLowerArm;
    [SerializeField] public BasisLocalBoneControl LeftLowerLeg;
    [SerializeField] public BasisLocalBoneControl RightLowerLeg;
    [SerializeField] public BasisLocalBoneControl LeftHand;
    [SerializeField] public BasisLocalBoneControl RightHand;
    [SerializeField] public BasisLocalBoneControl LeftFoot;
    [SerializeField] public BasisLocalBoneControl RightFoot;
    public float NeckRotationSpeed = 40;
    public float ChestRotationSpeed = 25;
    public float SpineRotationSpeed = 30;
    public float HipsRotationSpeed = 40;

    public Vector3 headOffset;          // Eye-to-head in eye local space
    public Vector3 neckOffset;          // Head-to-neck in head local space
    public Vector3 neckEyeOffset;
    public void Initialize()
    {
        var boneDriver = BasisLocalPlayer.Instance.LocalBoneDriver;

        TryAssignBone(BasisBoneTrackedRole.CenterEye, out CenterEye, hasVirtualOverride: false);
        TryAssignBone(BasisBoneTrackedRole.Head, out Head, hasVirtualOverride: true);
        TryAssignBone(BasisBoneTrackedRole.Neck, out Neck, hasVirtualOverride: true);
        TryAssignBone(BasisBoneTrackedRole.Chest, out Chest, hasVirtualOverride: true);
        TryAssignBone(BasisBoneTrackedRole.Spine, out Spine, hasVirtualOverride: true);
        TryAssignBone(BasisBoneTrackedRole.Hips, out Hips, hasVirtualOverride: true);

        TryAssignBone(BasisBoneTrackedRole.LeftLowerArm, out LeftLowerArm, hasVirtualOverride: false);
        TryAssignBone(BasisBoneTrackedRole.RightLowerArm, out RightLowerArm, hasVirtualOverride: false);
        TryAssignBone(BasisBoneTrackedRole.LeftLowerLeg, out LeftLowerLeg, hasVirtualOverride: false);
        TryAssignBone(BasisBoneTrackedRole.RightLowerLeg, out RightLowerLeg, hasVirtualOverride: false);
        TryAssignBone(BasisBoneTrackedRole.LeftHand, out LeftHand, hasVirtualOverride: false);
        TryAssignBone(BasisBoneTrackedRole.RightHand, out RightHand, hasVirtualOverride: false);
        TryAssignBone(BasisBoneTrackedRole.LeftFoot, out LeftFoot, hasVirtualOverride: false);
        TryAssignBone(BasisBoneTrackedRole.RightFoot, out RightFoot, hasVirtualOverride: false);

        BasisLocalPlayer.Instance.OnPreSimulateBones += OnSimulateHead;
    }

    private void TryAssignBone(BasisBoneTrackedRole role, out BasisLocalBoneControl bone, bool hasVirtualOverride)
    {
        var boneDriver = BasisLocalPlayer.Instance.LocalBoneDriver;
        if (boneDriver.FindBone(out bone, role) && hasVirtualOverride)
        {
            bone.HasVirtualOverride = true;
        }
    }
    public void DeInitialize()
    {
        if (Neck != null)
        {
            Neck.HasVirtualOverride = false;
        }
        if (Chest != null)
        {
            Chest.HasVirtualOverride = false;
        }
        if (Hips != null)
        {
            Hips.HasVirtualOverride = false;
        }
        if (Spine != null)
        {
            Spine.HasVirtualOverride = false;
        }
        BasisLocalPlayer.Instance.OnPreSimulateBones -= OnSimulateHead;
    }
    public void OnSimulateHead()
    {
        float deltaTime = Time.deltaTime;

        Head.OutGoingData.rotation = CenterEye.OutGoingData.rotation;
        Neck.OutGoingData.rotation = Head.OutGoingData.rotation;

        // Now, apply the spine curve progressively:
        // The chest should not follow the head directly, it should follow the neck but with reduced influence.
        Quaternion targetChestRotation = Quaternion.Slerp(Chest.OutGoingData.rotation, Neck.OutGoingData.rotation, deltaTime * ChestRotationSpeed);
        Vector3 EulerChestRotation = targetChestRotation.eulerAngles;
        Chest.OutGoingData.rotation = Quaternion.Euler(0, EulerChestRotation.y, 0);

        // The hips should stay upright, using chest rotation as a reference
        Quaternion targetSpineRotation = Quaternion.Slerp(Spine.OutGoingData.rotation, Chest.OutGoingData.rotation, deltaTime * SpineRotationSpeed);// Lesser influence for hips to remain more upright
        Vector3 targetSpineRotationEuler = targetSpineRotation.eulerAngles;
        Spine.OutGoingData.rotation = Quaternion.Euler(0, targetSpineRotationEuler.y, 0);

        // The hips should stay upright, using chest rotation as a reference
        Quaternion targetHipsRotation = Quaternion.Slerp(Hips.OutGoingData.rotation, Spine.OutGoingData.rotation, deltaTime * HipsRotationSpeed);// Lesser influence for hips to remain more upright
        Vector3 targetHipsRotationEuler = targetHipsRotation.eulerAngles;
        Hips.OutGoingData.rotation = Quaternion.Euler(0, targetHipsRotationEuler.y, 0);

        // Handle position control for each segment if targets are set (as before)
        ApplyPositionControl(Head);
        ApplyPositionControl(Neck);
        ApplyPositionControl(Chest);
        ApplyPositionControl(Spine);
        ApplyPositionControl(Hips);
    }
    private void ApplyPositionControl(BasisLocalBoneControl boneControl)
    {

        Quaternion targetRotation = boneControl.Target.OutGoingData.rotation;

        // Extract yaw-only forward vector
        float3 forward = math.mul(targetRotation, new float3(0, 0, 1));
        forward.y = 0;
        forward = math.normalize(forward);

        Quaternion yawRotation = quaternion.LookRotationSafe(forward, new float3(0, 1, 0));
        Vector3 offset = math.mul(yawRotation, boneControl.ScaledOffset);

        boneControl.OutGoingData.position = boneControl.Target.OutGoingData.position + offset;
    }
}
