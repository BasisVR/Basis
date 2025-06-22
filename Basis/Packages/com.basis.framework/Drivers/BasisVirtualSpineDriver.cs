using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Mathematics;
using UnityEngine;
[System.Serializable]
public class BasisVirtualSpineDriver
{
    [SerializeField] public BasisBoneControl CenterEye;
    [SerializeField] public BasisBoneControl Head;
    [SerializeField] public BasisBoneControl Neck;
    [SerializeField] public BasisBoneControl Chest;
    [SerializeField] public BasisBoneControl Spine;
    [SerializeField] public BasisBoneControl Hips;
    [SerializeField] public BasisBoneControl RightShoulder;
    [SerializeField] public BasisBoneControl LeftShoulder;
    [SerializeField] public BasisBoneControl LeftLowerArm;
    [SerializeField] public BasisBoneControl RightLowerArm;
    [SerializeField] public BasisBoneControl LeftLowerLeg;
    [SerializeField] public BasisBoneControl RightLowerLeg;
    [SerializeField] public BasisBoneControl LeftHand;
    [SerializeField] public BasisBoneControl RightHand;
    [SerializeField] public BasisBoneControl LeftFoot;
    [SerializeField] public BasisBoneControl RightFoot;
    public float NeckRotationSpeed = 40;
    public float ChestRotationSpeed = 25;
    public float SpineRotationSpeed = 30;
    public float HipsRotationSpeed = 40;

    public Vector3 headOffset;          // Eye-to-head in eye local space
    public Vector3 neckOffset;          // Head-to-neck in head local space
    public Vector3 neckEyeOffset;
    public void Initialize()
    {
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out CenterEye, BasisBoneTrackedRole.CenterEye))
        {
        }
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out Head, BasisBoneTrackedRole.Head))
        {
            Head.HasVirtualOverride = true;
        }

        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out Neck, BasisBoneTrackedRole.Neck))
        {
            Neck.HasVirtualOverride = true;
        }
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out Chest, BasisBoneTrackedRole.Chest))
        {
            Chest.HasVirtualOverride = true;
        }
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out Spine, BasisBoneTrackedRole.Spine))
        {
            Spine.HasVirtualOverride = true;
        }
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out Hips, BasisBoneTrackedRole.Hips))
        {
            Hips.HasVirtualOverride = true;
        }
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out LeftLowerArm, BasisBoneTrackedRole.LeftLowerArm))
        {
         //  LeftLowerArm.HasVirtualOverride = true;
        }

        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out RightLowerArm, BasisBoneTrackedRole.RightLowerArm))
        {
         //  RightLowerArm.HasVirtualOverride = true;
        }

        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out LeftLowerLeg, BasisBoneTrackedRole.LeftLowerLeg))
        {
         //  LeftLowerLeg.HasVirtualOverride = true;
        }
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out RightLowerLeg, BasisBoneTrackedRole.RightLowerLeg))
        {
            //RightLowerLeg.HasVirtualOverride = true;
        }
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out LeftHand, BasisBoneTrackedRole.LeftHand))
        {
            // LeftHand.HasVirtualOverride = true;
        }
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out RightHand, BasisBoneTrackedRole.RightHand))
        {
            //   RightHand.HasVirtualOverride = true;
        }
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out LeftFoot, BasisBoneTrackedRole.LeftFoot))
        {
            // LeftHand.HasVirtualOverride = true;
        }
        if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out RightFoot, BasisBoneTrackedRole.RightFoot))
        {
            //   RightHand.HasVirtualOverride = true;
        }
        BasisLocalPlayer.Instance.OnPreSimulateBones += OnSimulateHead;//instead of virtual run just run at the start
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
    /*
    public void OnSimulateHead()
    {
        // Calculate head offset from eyes in eyes' local space
        headOffset = Quaternion.Inverse(CenterEye.TposeLocal.rotation) * (Head.TposeLocal.position - CenterEye.TposeLocal.position);

        // Calculate neck offset from head in head's local space
        neckOffset = Quaternion.Inverse(Head.TposeLocal.rotation) * (Neck.TposeLocal.position - Head.TposeLocal.position);

        neckEyeOffset = Quaternion.Inverse(CenterEye.TposeLocal.rotation) * (Neck.TposeLocal.position - CenterEye.TposeLocal.position);

        float deltaTime = Time.deltaTime;
        Quaternion Rotation = Neck.OutGoingData.rotation;
        Vector3 Forward = Rotation * Vector3.forward;

        CalculateBones(Forward, CenterEye.OutGoingData.position, CenterEye.OutGoingData.rotation,
        out Vector3 headWorldPos);

        Head.OutGoingData.position = headWorldPos;
        Head.OutGoingData.rotation = CenterEye.OutGoingData.rotation; //Quaternion.Slerp(Head.OutGoingData.rotation, CenterEye.OutGoingData.rotation,deltaTime * NeckRotationSpeed);

       // Neck.OutGoingData.position = finalNeckWorldPos;
       // Neck.OutGoingData.rotation = Quaternion.Slerp(Neck.OutGoingData.rotation, neckYawRot, deltaTime * NeckRotationSpeed);

        // Process rotations with reduced influence (progressive damping)
        ApplyClampedRotation(Neck, Head, deltaTime * NeckRotationSpeed);
        ApplyClampedRotation(Chest, Neck, deltaTime * ChestRotationSpeed);
        ApplyClampedRotation(Spine, Chest, deltaTime * SpineRotationSpeed);
        ApplyClampedRotation(Hips, Spine, deltaTime * HipsRotationSpeed);
        // Apply positional constraints (if any)
        ApplyPositionControl(Neck);
        ApplyPositionControl(Chest);
        ApplyPositionControl(Spine);
        ApplyPositionControl(Hips);
    }
    */

    private void ApplyClampedRotation(BasisBoneControl targetBone, BasisBoneControl sourceBone, float lerpFactor)
    {
        Vector3 euler = Quaternion.Slerp(targetBone.OutGoingData.rotation, sourceBone.OutGoingData.rotation, lerpFactor).eulerAngles;
        targetBone.OutGoingData.rotation = Quaternion.Euler(0, euler.y, 0);
    }

    private void ApplyPositionControl(BasisBoneControl boneControl)
    {

        quaternion targetRotation = boneControl.Target.OutGoingData.rotation;

        // Extract yaw-only forward vector
        float3 forward = math.mul(targetRotation, new float3(0, 0, 1));
        forward.y = 0;
        forward = math.normalize(forward);

        quaternion yawRotation = quaternion.LookRotationSafe(forward, new float3(0, 1, 0));
        float3 offset = math.mul(yawRotation, boneControl.ScaledOffset);

        boneControl.OutGoingData.position = boneControl.Target.OutGoingData.position + offset;
    }
    public void CalculateBones(Vector3 Forward, Vector3 eyeWorldPos, Quaternion eyeWorldRot, out Vector3 HeadPosition)
    {
        HeadPosition = eyeWorldPos + eyeWorldRot * headOffset;
        // === NECK FOLLOW (world space, with locked Y) ===
      //  Vector3 rawNeckWorldPos = HeadPosition + eyeWorldRot * neckOffset;

        //this code is fault in vr
       // Vector3 rawNeckLocalToRoot = rawNeckWorldPos;
       // rawNeckLocalToRoot.y = Neck.TposeLocal.position.y;
       // NeckPosition = rawNeckLocalToRoot;
        //NeckPosition.y = eyeWorldPos.y + neckEyeOffset.y;
        //

        // Calculate yaw-only rotation from eye forward
       // Vector3 flatForward = eyeWorldRot * Vector3.forward;
        //flatForward.y = 0f;
       // if (flatForward.sqrMagnitude < 0.001f)
       // {
       //     flatForward = Forward;//neckTransform.forward;
       // }
       // neckYawRot = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
    }
}
