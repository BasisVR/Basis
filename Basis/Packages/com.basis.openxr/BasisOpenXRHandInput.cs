using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;
public class BasisOpenXRHandInput : BasisInputController
{
    public Vector3 LeftHandPalmCorrection;
    public Vector3 RightHandPalmCorrection;

    public InputActionProperty DeviceActionPosition;
    public InputActionProperty DeviceActionRotation;
    public InputActionProperty Trigger;
    public InputActionProperty Grip;
    public InputActionProperty PrimaryButton;
    public InputActionProperty SecondaryButton;
    public InputActionProperty MenuButton;
    public InputActionProperty Primary2DAxis;
    public InputActionProperty Secondary2DAxis;
    public InputActionProperty PalmPoseActionPosition;
    public InputActionProperty PalmPoseActionRotation;
    public InputActionProperty pointerPosition;
    public InputActionProperty pointerRotation;

    public UnityEngine.XR.InputDevice Device;
    public const float TriggerDownAmount = 0.5f;

    /// <summary>
    /// Raw unmodified hand coordinates before final calibration.
    /// </summary>
    public BasisCalibratedCoords HandRaw = new BasisCalibratedCoords();
    public void Initialize(string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole)
    {
        HandBiasSplay = 0;
        leftHandToIKRotationOffset = new Vector3(0, 0, -180);
        rightHandToIKRotationOffset = new Vector3(0, 0, 0);//mistake

        LeftHandPalmCorrection = new Vector3(0, 0, -90);

        RightHandPalmCorrection = new Vector3(0, 0, -90);

        leftHandToIKPositionOffset = new Vector3(0,0, -0.05f);
        rightHandToIKPositionOffset = new Vector3(0,0, -0.05f);

        InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole,true);
        string devicePath = basisBoneTrackedRole == BasisBoneTrackedRole.LeftHand ? "<XRController>{LeftHand}" : "<XRController>{RightHand}";
        string devicePosePath = basisBoneTrackedRole == BasisBoneTrackedRole.LeftHand ? "<PalmPose>{LeftHand}" : "<PalmPose>{RightHand}";

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

        DeviceActionPosition = new InputActionProperty(new InputAction($"{devicePath}/devicePosition", InputActionType.Value, $"{devicePath}/devicePosition", expectedControlType: "Vector3"));
        DeviceActionRotation = new InputActionProperty(new InputAction($"{devicePath}/deviceRotation", InputActionType.Value, $"{devicePath}/deviceRotation", expectedControlType: "Quaternion"));

        PalmPoseActionPosition = new InputActionProperty(new InputAction($"{devicePosePath}/PosePosition", InputActionType.Value, $"{devicePosePath}/palmPosition", expectedControlType: "Vector3"));
        PalmPoseActionRotation = new InputActionProperty(new InputAction($"{devicePosePath}/PoseRotation", InputActionType.Value, $"{devicePosePath}/palmRotation", expectedControlType: "Quaternion"));

        pointerPosition = new InputActionProperty(new InputAction($"{devicePath}/pointerPosition", InputActionType.Value, $"{devicePath}/pointerPosition", expectedControlType: "Vector3"));
        pointerRotation = new InputActionProperty(new InputAction($"{devicePath}/pointerRotation", InputActionType.Value, $"{devicePath}/pointerRotation", expectedControlType: "Quaternion"));

        PalmPoseActionPosition.action.Enable();
        PalmPoseActionRotation.action.Enable();

        DeviceActionPosition.action.Enable();
        DeviceActionRotation.action.Enable();

        pointerPosition.action.Enable();
        pointerRotation.action.Enable();

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
    public override void LateDoPollData()
    {
    }
    public override void RenderPollData()
    {
        CurrentInputState.Primary2DAxisRaw = Primary2DAxis.action?.ReadValue<Vector2>() ?? Vector2.zero;
        CurrentInputState.Secondary2DAxisRaw = Secondary2DAxis.action?.ReadValue<Vector2>() ?? Vector2.zero;
        CurrentInputState.GripButton = Grip.action?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.SecondaryTrigger = Grip.action?.ReadValue<float>() ?? 0f;
        CurrentInputState.SystemOrMenuButton = MenuButton.action?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.PrimaryButtonGetState = PrimaryButton.action?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.SecondaryButtonGetState = SecondaryButton.action?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.Trigger = Trigger.action?.ReadValue<float>() ?? 0f;

        if (DeviceActionPosition != null)
        {
            ComputeUnscaledDeviceCoord(ref UnscaledDeviceCoord, DeviceActionPosition.action.ReadValue<Vector3>());
        }
        if (DeviceActionRotation != null)
        {
            UnscaledDeviceCoord.rotation = DeviceActionRotation.action.ReadValue<Quaternion>();
        }
        if (pointerPosition != null)
        {
            ComputeUnscaledDeviceCoord(ref PointerPositionYScaled, pointerPosition.action.ReadValue<Vector3>());
        }

        ConvertToScaledDeviceCoord();
        ControlOnlyAsHand(HandFinal.position, HandFinal.rotation);
        UpdateRaycastOffset();
        float playerToAvatar = BasisHeightDriver.DeviceScale;

        var originLocal = PointerPositionYScaled.position * playerToAvatar;
        var originWorld = OffsetCoords.position + (OffsetCoords.rotation * originLocal);

        ComputeRaycastDirection(
            originWorld,
            HandFinal.rotation,
            ActiveRaycastOffset
        );
        UpdateInputEvents();
    }
    public BasisCalibratedCoords PointerPositionYScaled;
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
        if (!TryGetRole(out BasisBoneTrackedRole assignedRole)) return;

        float playerToAvatar = BasisHeightDriver.DeviceScale;

        // helper local function (keeps the diff small)
        Vector3 ApplyOffsetToPos(Vector3 rawLocalPos)
        {
            // rawLocalPos is in your “device/player” space; we scale first, then rotate+translate by offset
            Vector3 scaled = ChangeHandYHeight(rawLocalPos) * playerToAvatar;
            return OffsetCoords.position + (OffsetCoords.rotation * scaled);
        }

        Quaternion ApplyOffsetToRot(Quaternion rawRot)
        {
            return OffsetCoords.rotation * rawRot;
        }

        switch (assignedRole)
        {
            case BasisBoneTrackedRole.LeftHand:
                if (subsystem.leftHand.isTracked)
                {
                    UpdateHandPose(subsystem.leftHand, BasisLocalPlayer.Instance.LocalHandDriver.LeftHand, out HandRaw.position, out HandRaw.rotation);

                    // keep your existing “final rotation” logic, but (optionally) parent it to OffsetCoords.rotation
                    HandFinal.rotation = HandleHandFinalRotation(ApplyOffsetToRot(HandRaw.rotation));
                    HandFinal.position = ApplyOffsetToPos(HandRaw.position);
                }
                else
                {
                    HandRaw.position = PalmPoseActionPosition.action.ReadValue<Vector3>();
                    HandRaw.rotation = PalmPoseActionRotation.action.ReadValue<Quaternion>();

                    var corrected = math.mul(HandRaw.rotation, Quaternion.Euler(LeftHandPalmCorrection));
                    HandFinal.rotation = ApplyOffsetToRot(corrected);
                    HandFinal.position = ApplyOffsetToPos(HandRaw.position);

                    FallbackHand(BasisLocalPlayer.Instance.LocalHandDriver.LeftHand);

                    if (UseIKPositionOffset)
                    {
                        // IK offset should be rotated by the *same* frame you’re using (use HandFinal.rotation usually)
                        HandFinal.position += (HandFinal.rotation * (leftHandToIKPositionOffset * playerToAvatar));
                    }
                }
                break;

            case BasisBoneTrackedRole.RightHand:
                if (subsystem.rightHand.isTracked)
                {
                    UpdateHandPose(subsystem.rightHand, BasisLocalPlayer.Instance.LocalHandDriver.RightHand, out HandRaw.position, out HandRaw.rotation);

                    HandFinal.rotation = HandleHandFinalRotation(ApplyOffsetToRot(HandRaw.rotation));
                    HandFinal.position = ApplyOffsetToPos(HandRaw.position);
                }
                else
                {
                    HandRaw.position = PalmPoseActionPosition.action.ReadValue<Vector3>();
                    HandRaw.rotation = PalmPoseActionRotation.action.ReadValue<Quaternion>();

                    var corrected = math.mul(HandRaw.rotation, Quaternion.Euler(RightHandPalmCorrection));
                    HandFinal.rotation = ApplyOffsetToRot(corrected);
                    HandFinal.position = ApplyOffsetToPos(HandRaw.position);

                    FallbackHand(BasisLocalPlayer.Instance.LocalHandDriver.RightHand);

                    if (UseIKPositionOffset)
                    {
                        HandFinal.position += (HandFinal.rotation * (rightHandToIKPositionOffset * playerToAvatar));
                    }
                }
                break;
        }
    }
    public void FallbackHand(BasisFingerPose Hand)
    {
        Hand.IndexPercentage[0] = Remap01ToMinus1To1(CurrentInputState.Trigger);
        Hand.MiddlePercentage[0] = Remap01ToMinus1To1(CurrentInputState.SecondaryTrigger);
        Hand.RingPercentage[0] = Remap01ToMinus1To1(CurrentInputState.SecondaryTrigger);
        Hand.LittlePercentage[0] = Remap01ToMinus1To1(CurrentInputState.SecondaryTrigger);
    }
    private void UpdateHandPose(XRHand hand, BasisFingerPose fingerPose, out Vector3 position, out Quaternion rotation)
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

        float ThumbPercentage = RemapSplayFingerValue(hand, XRHandFingerID.Thumb);
        float IndexPercentage = RemapSplayFingerValue(hand, XRHandFingerID.Index);
        float MiddlePercentage = RemapSplayFingerValue(hand, XRHandFingerID.Middle);
        float RingPercentage = RemapSplayFingerValue(hand, XRHandFingerID.Ring);
        float LittlePercentage = RemapSplayFingerValue(hand, XRHandFingerID.Little);


        // Map to your rig space [-1..1] and assign to the splay channel [1]
        fingerPose.ThumbPercentage[1] = ThumbPercentage;
        fingerPose.IndexPercentage[1] = IndexPercentage;
        fingerPose.MiddlePercentage[1] = MiddlePercentage;
        fingerPose.RingPercentage[1] = RingPercentage;
        fingerPose.LittlePercentage[1] = LittlePercentage;
    }
    private float RemapFingerValue(XRHand hand, XRHandFingerID fingerID)
    {
        if (TryGetShapePercentage(hand, fingerID, XRFingerShapeTypes.FullCurl, XRFingerShapeType.FullCurl, out float value))
        {
            return Remap01ToMinus1To1(value);
        }
        return 0f;
    }
    private float RemapSplayFingerValue(XRHand hand, XRHandFingerID fingerID)
    {
        if (TryGetShapePercentage(hand, fingerID, XRFingerShapeTypes.Spread, XRFingerShapeType.Spread, out float value))
        {
            return SplayConversion(value);
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
        ShowTrackedVisualDefaultImplementation();
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
        PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
    }
}
