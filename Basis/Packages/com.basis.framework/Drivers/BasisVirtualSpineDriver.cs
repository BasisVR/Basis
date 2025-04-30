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

    // Weights should add up to 1.
    [Header("Bone Weights")]
    [SerializeField] private float neckWeight = 0.1f;    // How much the neck stretches
    [SerializeField] private float chestWeight = 0.3f;    // How much the chest stretches
    [SerializeField] private float spineWeight = 0.6f;   // How much the spine stretches
    [SerializeField] private float hipsWeight = 0f;       // Hips don't stretch

    private float yHeadDiff = 0f;

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

		head.OutGoingData.rotation = centerEye.OutGoingData.rotation;
		neck.OutGoingData.rotation = head.OutGoingData.rotation;

        // Now, apply the spine curve progressively:
        // The chest should not follow the head directly, it should follow the neck but with reduced influence.
        Quaternion targetChestRotation = Quaternion.Slerp(chest.OutGoingData.rotation, neck.OutGoingData.rotation, deltaTime * chestRotationSpeed);
        Vector3 EulerChestRotation = targetChestRotation.eulerAngles;
        chest.OutGoingData.rotation = Quaternion.Euler(0, EulerChestRotation.y, 0);

        // The hips should stay upright, using chest rotation as a reference
        Quaternion targetSpineRotation = Quaternion.Slerp(spine.OutGoingData.rotation, chest.OutGoingData.rotation, deltaTime * spineRotationSpeed);// Lesser influence for hips to remain more upright
		Vector3 targetSpineRotationEuler = targetSpineRotation.eulerAngles;
		spine.OutGoingData.rotation = Quaternion.Euler(0, targetSpineRotationEuler.y, 0);

		// The hips should stay upright, using chest rotation as a reference
		Quaternion targetHipsRotation = Quaternion.Slerp(hips.OutGoingData.rotation, spine.OutGoingData.rotation, deltaTime * hipsRotationSpeed);// Lesser influence for hips to remain more upright
		Vector3 targetHipsRotationEuler = targetHipsRotation.eulerAngles;
		hips.OutGoingData.rotation = Quaternion.Euler(0, targetHipsRotationEuler.y, 0);

		// Apply position control with weighted offsets
		ApplyBasicPositionControl(head);

        ApplyPositionControlWithWeight(neck, neckWeight);
        ApplyPositionControlWithWeight(chest, chestWeight);
        ApplyPositionControlWithWeight(spine, spineWeight);
        ApplyPositionControlWithWeight(hips, hipsWeight);
    }

    /// <summary>
    /// Calculates the angle between the head's forward direction and the hip-head vector.
    /// </summary>
	private float CalculateStretchAmount()
	{
        var stretchAmount = Mathf.Clamp(yHeadDiff, -maximumCompressionAmount, maximumStretchAmount);
        //Debug.Log("yHeadDiff: " + yHeadDiff + " MaximumCompression: " + -maximumCompressionAmount + " MaximumStretch: " + maximumStretchAmount);
        return stretchAmount;
	}

    private float maximumCompressionAmount => head.Offset.y - -Vector3.Magnitude(head.Offset);
    private float maximumStretchAmount => Vector3.Magnitude(head.Offset) - head.Offset.y;

	private float3 RelativeOffset(BasisBoneControl bone)
    {
        return bone.Target.OutGoingData.position + math.mul(bone.Target.OutGoingData.rotation, bone.Offset);
	}
    private float3 TPoseOffset(BasisBoneControl bone)
    {
        return bone.Target.TposeLocal.position + math.mul(bone.Target.TposeLocal.rotation, bone.Offset);
    }

	/// <summary>
	/// Applies position control for the head bone.
	/// </summary>
	private void ApplyBasicPositionControl(BasisBoneControl boneControl)
    {
        yHeadDiff = RelativeOffset(boneControl).y - TPoseOffset(boneControl).y;
		boneControl.OutGoingData.position = RelativeOffset(boneControl);
	}

	/// <summary>
	/// Applies position control for a bone based on its target's rotation, with weighted offset.
	/// </summary>
	private void ApplyPositionControlWithWeight(BasisBoneControl boneControl, float weight)
    {
        quaternion targetRotation = boneControl.Target.OutGoingData.rotation;

        // Extract yaw-only forward vector
        float3 forward = math.mul(targetRotation, new float3(0, 0, 1));
        forward.y = 0;
        forward = math.normalize(forward);

        quaternion yawRotation = quaternion.LookRotationSafe(forward, new float3(0, 1, 0));
        
        // Calculate base offset
        float3 baseOffset = math.mul(yawRotation, boneControl.Offset);
        
        // Apply weighted stretch to the offset
        float3 stretchedOffset = baseOffset;

        stretchedOffset.y -= CalculateStretchAmount() * weight;

        boneControl.OutGoingData.position = boneControl.Target.OutGoingData.position + stretchedOffset;
    }
}
