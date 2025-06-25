using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;
public class BasisOpenXRHandInput : BasisInputController
{
    public InputActionProperty DeviceActionPosition;
    public InputActionProperty DeviceActionRotation;
    public InputActionProperty Trigger;
    public InputActionProperty Grip;
    public InputActionProperty PrimaryButton;
    public InputActionProperty SecondaryButton;
    public InputActionProperty MenuButton;
    public InputActionProperty Primary2DAxis;
    public InputActionProperty Secondary2DAxis;
    public UnityEngine.XR.InputDevice Device;
    public const float TriggerDownAmount = 0.5f;
    public InputActionProperty PalmPoseActionPosition;
    public InputActionProperty PalmPoseActionRotation;

    public Vector3 LeftHandPalmCorrection;
    public Vector3 RightHandPalmCorrection;
    public void Initialize(string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole)
    {
        leftHandToIKRotationOffset = new float3(-90, -90, -90);
        rightHandToIKRotationOffset = new float3(-90, 90, 90);
        RaycastRotationOffset = new float3(0, 0, 0);
        LeftHandPalmCorrection = new Vector3(0, 0, 90);
        RightHandPalmCorrection = new Vector3(0,0,-90);
        InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole);
        string devicePath = basisBoneTrackedRole == BasisBoneTrackedRole.LeftHand ? "<XRController>{LeftHand}" : "<XRController>{RightHand}";
        SetupInputActions(devicePath);

        DeviceActionPosition = new InputActionProperty(new InputAction($"{devicePath}/devicePosition", InputActionType.Value, $"{devicePath}/devicePosition", expectedControlType: "Vector3"));
        DeviceActionRotation = new InputActionProperty(new InputAction($"{devicePath}/deviceRotation", InputActionType.Value, $"{devicePath}/deviceRotation", expectedControlType: "Quaternion"));


        //<PalmPose>{LeftHand}/palmPosition
        string devicePosePath = basisBoneTrackedRole == BasisBoneTrackedRole.LeftHand ? "<PalmPose>{LeftHand}" : "<PalmPose>{RightHand}";
        PalmPoseActionPosition = new InputActionProperty(new InputAction($"{devicePosePath}/PosePosition", InputActionType.Value, $"{devicePosePath}/palmPosition", expectedControlType: "Vector3"));
        PalmPoseActionRotation = new InputActionProperty(new InputAction($"{devicePosePath}/PoseRotation", InputActionType.Value, $"{devicePosePath}/palmRotation", expectedControlType: "Quaternion"));
        ///input/palm_ext/pose

        PalmPoseActionPosition.action.Enable();
        PalmPoseActionRotation.action.Enable();
        DeviceActionPosition.action.Enable();
        DeviceActionRotation.action.Enable();
    }
    private void SetupInputActions(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
        {
            Debug.LogError("Device path is null or empty.");
            return;
        }
        Trigger = new InputActionProperty(new InputAction(devicePath + "/trigger", InputActionType.Value, devicePath + "/trigger", expectedControlType: "Float"));
        Grip = new InputActionProperty(new InputAction(devicePath + "/grip", InputActionType.Value, devicePath + "/grip", expectedControlType: "Float"));
        PrimaryButton = new InputActionProperty(new InputAction(devicePath + "/primaryButton", InputActionType.Button, devicePath + "/primaryButton", expectedControlType: "Button"));
        SecondaryButton = new InputActionProperty(new InputAction(devicePath + "/secondaryButton", InputActionType.Button, devicePath + "/secondaryButton", expectedControlType: "Button"));
        MenuButton = new InputActionProperty(new InputAction(devicePath + "/menuButton", InputActionType.Button, devicePath + "/menuButton", expectedControlType: "Button"));
        Primary2DAxis = new InputActionProperty(new InputAction(devicePath + "/primary2DAxis", InputActionType.Value, devicePath + "/primary2DAxis", expectedControlType: "Vector2"));
        Secondary2DAxis = new InputActionProperty(new InputAction(devicePath + "/secondary2DAxis", InputActionType.Value, devicePath + "/secondary2DAxis", expectedControlType: "Vector2"));
        EnableInputActions();
    }
    private void EnableInputActions()
    {
        foreach (var action in GetAllActions())
        {
            action.action?.Enable();
        }
    }
    private void DisableInputActions()
    {
        foreach (var action in GetAllActions())
        {
            action.action?.Disable();
        }
    }
    private IEnumerable<InputActionProperty> GetAllActions()
    {
        yield return Trigger;
        yield return Grip;
        yield return PrimaryButton;
        yield return SecondaryButton;
        yield return MenuButton;
        yield return Primary2DAxis;
        yield return Secondary2DAxis;
    }
    public new void OnDestroy()
    {
        DisableInputActions();
        base.OnDestroy();
    }
    public override void DoPollData()
    {
        CurrentInputState.Primary2DAxis = Primary2DAxis.action?.ReadValue<Vector2>() ?? Vector2.zero;
        CurrentInputState.Secondary2DAxis = Secondary2DAxis.action?.ReadValue<Vector2>() ?? Vector2.zero;
        CurrentInputState.GripButton = Grip.action?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.SecondaryTrigger = Grip.action?.ReadValue<float>() ?? 0f;
        CurrentInputState.SystemOrMenuButton = MenuButton.action?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.PrimaryButtonGetState = PrimaryButton.action?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.SecondaryButtonGetState = SecondaryButton.action?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.Trigger = Trigger.action?.ReadValue<float>() ?? 0f;

        RawFinal.position = DeviceActionPosition.action.ReadValue<Vector3>();
        RawFinal.rotation = DeviceActionRotation.action.ReadValue<Quaternion>();

        float scale = BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;

        DeviceFinal.position = RawFinal.position * scale;
        DeviceFinal.rotation = RawFinal.rotation;

        HandFinal.position = HandRaw.position * scale;

        ControlOnlyAsHand();
        UpdatePlayerControl();
        ComputeRaycastDirection();
    }
    /// <summary>
    /// meta/ unity need to pull something out of there ass here,
    /// currently on quest the below system swaps between controllers and hand tracking but you cant have controller & hand.
    /// steamvr did this correctly.
    /// </summary>
    /// <param name="subsystem"></param>
    /// <param name="flags"></param>
    /// <param name="updateType"></param>
    public void OnHandUpdate(XRHandSubsystem subsystem, XRHandSubsystem.UpdateSuccessFlags flags, XRHandSubsystem.UpdateType updateType)
    {
        if (TryGetRole(out BasisBoneTrackedRole assignedRole))
        {
            switch (assignedRole)
            {
                case BasisBoneTrackedRole.LeftHand:
                    if (subsystem.leftHand.isTracked)
                    {
                        UpdateHandPose(subsystem.leftHand, BasisLocalPlayer.Instance.LocalHandDriver.LeftHand, out HandRaw.position, out HandRaw.rotation);
                        HandFinal.rotation = HandRaw.rotation;
                    }
                    else
                    {
                        HandRaw.position = PalmPoseActionPosition.action.ReadValue<Vector3>();
                        HandRaw.rotation = PalmPoseActionRotation.action.ReadValue<Quaternion>();
                        HandFinal.rotation = math.mul(HandRaw.rotation, Quaternion.Euler(LeftHandPalmCorrection));

                        var LeftHand = BasisLocalPlayer.Instance.LocalHandDriver.LeftHand;
                        LeftHand.IndexPercentage[0] = Remap01ToMinus1To1(CurrentInputState.Trigger);
                        LeftHand.MiddlePercentage[0] = Remap01ToMinus1To1(CurrentInputState.SecondaryTrigger);
                        LeftHand.RingPercentage[0] = Remap01ToMinus1To1(CurrentInputState.SecondaryTrigger);
                        LeftHand.LittlePercentage[0] = Remap01ToMinus1To1(CurrentInputState.SecondaryTrigger);
                    }
                    break;

                case BasisBoneTrackedRole.RightHand:
                    if (subsystem.rightHand.isTracked)
                    {
                        UpdateHandPose(subsystem.rightHand, BasisLocalPlayer.Instance.LocalHandDriver.RightHand, out HandRaw.position, out HandRaw.rotation);
                        HandFinal.rotation = HandRaw.rotation;
                    }
                    else
                    {
                        HandRaw.position = PalmPoseActionPosition.action.ReadValue<Vector3>();
                        HandRaw.rotation = PalmPoseActionRotation.action.ReadValue<Quaternion>();
                        HandFinal.rotation = math.mul(HandRaw.rotation, Quaternion.Euler(RightHandPalmCorrection));

                        var RightHand = BasisLocalPlayer.Instance.LocalHandDriver.RightHand;

                      //  RightHand.ThumbPercentage[0] = Remap01ToMinus1To1(CurrentInputState.PrimaryButtonGetState);
                        RightHand.IndexPercentage[0] = Remap01ToMinus1To1(CurrentInputState.Trigger);
                        RightHand.MiddlePercentage[0] = Remap01ToMinus1To1(CurrentInputState.SecondaryTrigger);
                        RightHand.RingPercentage[0] = Remap01ToMinus1To1(CurrentInputState.SecondaryTrigger);
                        RightHand.LittlePercentage[0] = Remap01ToMinus1To1(CurrentInputState.SecondaryTrigger);

                    }
                    break;
            }
        }
    }

    private void UpdateHandPose(XRHand hand, BasisFingerPose fingerPose, out float3 position, out quaternion rotation)
    {
        XRHandJoint joint = hand.GetJoint(XRHandJointID.Wrist);
        if (joint.TryGetPose(out Pose pose))
        {
            position = pose.position;
            rotation = pose.rotation;
        }
        else
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
        }

        fingerPose.ThumbPercentage[0] = RemapFingerValue(hand, XRHandFingerID.Thumb);
        fingerPose.IndexPercentage[0] = RemapFingerValue(hand, XRHandFingerID.Index);
        fingerPose.MiddlePercentage[0] = RemapFingerValue(hand, XRHandFingerID.Middle);
        fingerPose.RingPercentage[0] = RemapFingerValue(hand, XRHandFingerID.Ring);
        fingerPose.LittlePercentage[0] = RemapFingerValue(hand, XRHandFingerID.Little);
    }

    private float RemapFingerValue(XRHand hand, XRHandFingerID fingerID)
    {
        if (TryGetShapePercentage(hand, fingerID, XRFingerShapeTypes.FullCurl, XRFingerShapeType.FullCurl, out float value))
        {
            return Remap01ToMinus1To1(value);
        }
        return 0f;
    }
    public bool TryGetShapePercentage(XRHand hand, XRHandFingerID fingerID, XRFingerShapeTypes typesNeeded, XRFingerShapeType shapeType, out float value)
    {
        XRFingerShape fingerShape = hand.CalculateFingerShape(fingerID, typesNeeded);

        switch (shapeType)
        {
            case XRFingerShapeType.FullCurl: return fingerShape.TryGetFullCurl(out value);
            case XRFingerShapeType.BaseCurl: return fingerShape.TryGetBaseCurl(out value);
            case XRFingerShapeType.TipCurl: return fingerShape.TryGetTipCurl(out value);
            case XRFingerShapeType.Pinch: return fingerShape.TryGetPinch(out value);
            case XRFingerShapeType.Spread: return fingerShape.TryGetSpread(out value);
            default:
                value = 0f;
                return false;
        }
    }
    public override void ShowTrackedVisual()
    {
        if (BasisVisualTracker == null && LoadedDeviceRequest == null)
        {
            DeviceSupportInformation Match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
            if (Match.CanDisplayPhysicalTracker)
            {
                LoadModelWithKey(Match.DeviceID);
            }
            else
            {
                if (UseFallbackModel())
                {
                    LoadModelWithKey(FallbackDeviceID);
                }
            }
        }
    }
    /// <summary>
    /// Duration does not work on OpenXRHands, in the future we should handle it for the user.
    /// </summary>
    /// <param name="duration"></param>
    /// <param name="amplitude"></param>
    /// <param name="frequency"></param>
    public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
    {
        Device.SendHapticImpulse(0, amplitude, duration);
    }
    public override void PlaySoundEffect(string SoundEffectName, float Volume)
    {
        switch (SoundEffectName)
        {
            case "hover":
                AudioSource.PlayClipAtPoint(BasisDeviceManagement.Instance.HoverUI, transform.position, Volume);
                break;
            case "press":
                AudioSource.PlayClipAtPoint(BasisDeviceManagement.Instance.pressUI, transform.position, Volume);
                break;
        }
    }
}
