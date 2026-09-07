using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
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
    public InputActionProperty Primary2DAxisClick;
    public InputActionProperty Secondary2DAxisClick;
    public InputActionProperty PrimaryTouch;
    public InputActionProperty SecondaryTouch;
    public InputActionProperty Primary2DAxisTouch;
    public InputActionProperty Secondary2DAxisTouch;
    public InputActionProperty TriggerTouch;
    public InputActionProperty ThumbrestTouch;
    public InputActionProperty PalmPoseActionPosition;
    public InputActionProperty PalmPoseActionRotation;
    public InputActionProperty pointerPosition;
    public InputActionProperty pointerRotation;

    [System.NonSerialized]
    public UnityEngine.XR.InputDevice Device;
    public const float TriggerDownAmount = 0.5f;

    // Cached underlying InputAction refs — avoids InputActionProperty.action property-getter cost per read.
    private InputAction _triggerAction;
    private InputAction _gripAction;
    private InputAction _primaryButtonAction;
    private InputAction _secondaryButtonAction;
    private InputAction _menuButtonAction;
    private InputAction _primary2DAxisAction;
    private InputAction _secondary2DAxisAction;
    private InputAction _primary2DAxisClickAction;
    private InputAction _secondary2DAxisClickAction;
    private InputAction _primaryTouchAction;
    private InputAction _secondaryTouchAction;
    private InputAction _primary2DAxisTouchAction;
    private InputAction _secondary2DAxisTouchAction;
    private InputAction _triggerTouchAction;
    private InputAction _thumbrestTouchAction;
    private InputAction _devicePositionAction;
    private InputAction _deviceRotationAction;
    private InputAction _palmPoseActionPosition;
    private InputAction _palmPoseActionRotation;
    private InputAction _pointerPositionAction;
    private InputAction _pointerRotationAction;

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

        leftHandToIKPositionOffset = new Vector3(0, 0, -0.05f);
        rightHandToIKPositionOffset = new Vector3(0, 0, -0.05f);

        // Quest swaps this device between a held controller and articulated hand tracking at runtime, so
        // the honest answer changes frame to frame; OnHandUpdate re-stamps it from the subsystem.
        TrackingHardware = BasisTrackingHardware.InsideOut;
        InitializeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole, true);
        string devicePath = basisBoneTrackedRole == BasisBoneTrackedRole.LeftHand ? "<XRController>{LeftHand}" : "<XRController>{RightHand}";
        string devicePosePath = basisBoneTrackedRole == BasisBoneTrackedRole.LeftHand ? "<PalmPose>{LeftHand}" : "<PalmPose>{RightHand}";

        if (string.IsNullOrEmpty(devicePath))
        {
            BasisDebug.LogError("Device path is null or empty.", BasisDebug.LogTag.Device);
            return;
        }
        Trigger = new InputActionProperty(new InputAction(devicePath + "/trigger", InputActionType.Value, devicePath + "/trigger", expectedControlType: "Float"));
        Grip = new InputActionProperty(new InputAction(devicePath + "/grip", InputActionType.Value, devicePath + "/grip", expectedControlType: "Float"));
        PrimaryButton = new InputActionProperty(new InputAction(devicePath + "/primaryButton", InputActionType.Button, devicePath + "/primaryButton", expectedControlType: "Button"));
        SecondaryButton = new InputActionProperty(new InputAction(devicePath + "/secondaryButton", InputActionType.Button, devicePath + "/secondaryButton", expectedControlType: "Button"));
        MenuButton = new InputActionProperty(new InputAction(devicePath + "/menuButton", InputActionType.Button, devicePath + "/menuButton", expectedControlType: "Button"));
        Primary2DAxis = new InputActionProperty(new InputAction(devicePath + "/primary2DAxis", InputActionType.Value, devicePath + "/primary2DAxis", expectedControlType: "Vector2"));
        Secondary2DAxis = new InputActionProperty(new InputAction(devicePath + "/secondary2DAxis", InputActionType.Value, devicePath + "/secondary2DAxis", expectedControlType: "Vector2"));
        Primary2DAxisClick = new InputActionProperty(new InputAction(devicePath + "/primary2DAxisClick", InputActionType.Button, devicePath + "/primary2DAxisClick", expectedControlType: "Button"));
        Secondary2DAxisClick = new InputActionProperty(new InputAction(devicePath + "/secondary2DAxisClick", InputActionType.Button, devicePath + "/secondary2DAxisClick", expectedControlType: "Button"));
        PrimaryTouch = new InputActionProperty(new InputAction(devicePath + "/primaryTouched", InputActionType.Button, devicePath + "/primaryTouched", expectedControlType: "Button"));
        SecondaryTouch = new InputActionProperty(new InputAction(devicePath + "/secondaryTouched", InputActionType.Button, devicePath + "/secondaryTouched", expectedControlType: "Button"));
        Primary2DAxisTouch = new InputActionProperty(new InputAction(devicePath + "/thumbstickTouched", InputActionType.Button, devicePath + "/thumbstickTouched", expectedControlType: "Button"));
        Secondary2DAxisTouch = new InputActionProperty(new InputAction(devicePath + "/{Secondary2DAxisTouch}", InputActionType.Button, devicePath + "/{Secondary2DAxisTouch}", expectedControlType: "Button"));
        TriggerTouch = new InputActionProperty(new InputAction(devicePath + "/triggerTouched", InputActionType.Button, devicePath + "/triggerTouched", expectedControlType: "Button"));
        ThumbrestTouch = new InputActionProperty(new InputAction(devicePath + "/thumbrestTouched", InputActionType.Button, devicePath + "/thumbrestTouched", expectedControlType: "Button"));

        // Interaction-profile layouts don't all alias these generic control names — the stick/pad
        // click controls carry no matching name or alias on the WMR, Index, and Touch layouts, and
        // WMR names its buttons differently across the board — but every layout stamps the standard
        // usages, so each action also binds the usage as a fallback.
        Trigger.action.AddBinding(devicePath + "/{Trigger}");
        Grip.action.AddBinding(devicePath + "/{Grip}");
        PrimaryButton.action.AddBinding(devicePath + "/{PrimaryButton}");
        SecondaryButton.action.AddBinding(devicePath + "/{SecondaryButton}");
        MenuButton.action.AddBinding(devicePath + "/{MenuButton}");
        Primary2DAxis.action.AddBinding(devicePath + "/{Primary2DAxis}");
        Secondary2DAxis.action.AddBinding(devicePath + "/{Secondary2DAxis}");
        Primary2DAxisClick.action.AddBinding(devicePath + "/{Primary2DAxisClick}");
        Secondary2DAxisClick.action.AddBinding(devicePath + "/{Secondary2DAxisClick}");
        PrimaryTouch.action.AddBinding(devicePath + "/{PrimaryTouch}");
        PrimaryTouch.action.AddBinding(devicePath + "/{PrimaryButtonTouch}");
        SecondaryTouch.action.AddBinding(devicePath + "/{SecondaryTouch}");
        SecondaryTouch.action.AddBinding(devicePath + "/{SecondaryButtonTouch}");
        Primary2DAxisTouch.action.AddBinding(devicePath + "/{Primary2DAxisTouch}");
        TriggerTouch.action.AddBinding(devicePath + "/{TriggerTouch}");
        ThumbrestTouch.action.AddBinding(devicePath + "/{ThumbrestTouch}");

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
        CacheActionReferences();
    }
    private void CacheActionReferences()
    {
        _triggerAction = Trigger.action;
        _gripAction = Grip.action;
        _primaryButtonAction = PrimaryButton.action;
        _secondaryButtonAction = SecondaryButton.action;
        _menuButtonAction = MenuButton.action;
        _primary2DAxisAction = Primary2DAxis.action;
        _secondary2DAxisAction = Secondary2DAxis.action;
        _primary2DAxisClickAction = Primary2DAxisClick.action;
        _secondary2DAxisClickAction = Secondary2DAxisClick.action;
        _primaryTouchAction = PrimaryTouch.action;
        _secondaryTouchAction = SecondaryTouch.action;
        _primary2DAxisTouchAction = Primary2DAxisTouch.action;
        _secondary2DAxisTouchAction = Secondary2DAxisTouch.action;
        _triggerTouchAction = TriggerTouch.action;
        _thumbrestTouchAction = ThumbrestTouch.action;
        _devicePositionAction = DeviceActionPosition.action;
        _deviceRotationAction = DeviceActionRotation.action;
        _palmPoseActionPosition = PalmPoseActionPosition.action;
        _palmPoseActionRotation = PalmPoseActionRotation.action;
        _pointerPositionAction = pointerPosition.action;
        _pointerRotationAction = pointerRotation.action;
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
        yield return Primary2DAxisClick;
        yield return Secondary2DAxisClick;
        yield return PrimaryTouch;
        yield return SecondaryTouch;
        yield return Primary2DAxisTouch;
        yield return Secondary2DAxisTouch;
        yield return TriggerTouch;
        yield return ThumbrestTouch;
    }
    public new void OnDestroy()
    {
        DisableInputActions();
        base.OnDestroy();
    }
    /// <summary>
    /// Matches the 50% dpad deadzone in bindings_holographic_controller.json so the pad-click
    /// substitution feels the same in OpenXR mode as it does under SteamVR.
    /// </summary>
    public const float PadSubstituteDeadzone = 0.5f;
    private void PollButtonsAndAxes()
    {
        Vector2 secondaryAxis = _secondary2DAxisAction?.ReadValue<Vector2>() ?? Vector2.zero;
        bool secondaryAxisClick = _secondary2DAxisClickAction?.ReadValue<float>() > TriggerDownAmount;
        bool primaryButton = _primaryButtonAction?.ReadValue<float>() > TriggerDownAmount;
        bool secondaryButton = _secondaryButtonAction?.ReadValue<float>() > TriggerDownAmount;

        // WMR-era wands have no A/B buttons, which would leave jump/mute and the main menu
        // unreachable. Mirror the SteamVR holographic binding (pad-click east = primary,
        // west = secondary) whenever the layout resolves a clickable pad but no primary button.
        if (secondaryAxisClick && _primaryButtonAction != null && _primaryButtonAction.controls.Count == 0)
        {
            if (secondaryAxis.x > PadSubstituteDeadzone)
            {
                primaryButton = true;
            }
            else if (secondaryAxis.x < -PadSubstituteDeadzone)
            {
                secondaryButton = true;
            }
        }

        CurrentInputState.Primary2DAxisRaw = _primary2DAxisAction?.ReadValue<Vector2>() ?? Vector2.zero;
        CurrentInputState.Secondary2DAxisRaw = secondaryAxis;
        CurrentInputState.Primary2DAxisClick = _primary2DAxisClickAction?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.Secondary2DAxisClick = secondaryAxisClick;
        float grip = _gripAction?.ReadValue<float>() ?? 0f;
        CurrentInputState.SecondaryTrigger = grip;
        CurrentInputState.GripButton = grip > TriggerDownAmount;
        CurrentInputState.SystemOrMenuButton = _menuButtonAction?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.PrimaryButtonGetState = primaryButton;
        CurrentInputState.SecondaryButtonGetState = secondaryButton;
        CurrentInputState.Trigger = _triggerAction?.ReadValue<float>() ?? 0f;
        CurrentInputState.PrimaryButtonTouch = _primaryTouchAction?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.SecondaryButtonTouch = _secondaryTouchAction?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.Primary2DAxisTouch = _primary2DAxisTouchAction?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.Secondary2DAxisTouch = _secondary2DAxisTouchAction?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.TriggerTouch = _triggerTouchAction?.ReadValue<float>() > TriggerDownAmount;
        CurrentInputState.ThumbrestTouch = _thumbrestTouchAction?.ReadValue<float>() > TriggerDownAmount;
    }
    public override void LateDoPollData()
    {
        // Poll input state here so InputUpdate() sees fresh data after LastUpdatePlayerControl()
        PollButtonsAndAxes();
        PollPose();
    }
    public override void RenderPollData()
    {
        PollButtonsAndAxes();

        PollPose();
        if (_pointerPositionAction != null)
        {
            ComputeUnscaledDeviceCoord(ref PointerPositionYScaled, _pointerPositionAction.ReadValue<Vector3>());
        }

        UpdateRaycastOffset();
        float playerToAvatar = BasisHeightDriver.DeviceScale;

        var originLocal = PointerPositionYScaled.position * playerToAvatar;
        var originWorld = OffsetCoords.position + (OffsetCoords.rotation * originLocal);

        Quaternion aimWorldRotation = _pointerRotationAction != null
            ? OffsetCoords.rotation * _pointerRotationAction.ReadValue<Quaternion>()
            : HandFinal.rotation;

        ComputeRaycastDirection(
            originWorld,
            aimWorldRotation,
            ActiveRaycastOffset
        );
        UpdateInputEvents();
    }
    private void PollPose()
    {
        if (_devicePositionAction != null)
        {
            ComputeUnscaledDeviceCoord(ref UnscaledDeviceCoord, _devicePositionAction.ReadValue<Vector3>());
        }
        if (_deviceRotationAction != null)
        {
            UnscaledDeviceCoord.rotation = _deviceRotationAction.ReadValue<Quaternion>();
        }
        ConvertToScaledDeviceCoord();
        ControlOnlyAsHand(HandFinal.position, HandFinal.rotation);
    }
    public BasisCalibratedCoords PointerPositionYScaled;
    /// <summary>
    /// meta/ unity need to pull something out of there ass here,
    /// currently on quest the below system swaps between controllers and hand tracking but you can't have controller & hand.
    /// steamvr did this correctly.
    /// </summary>
    /// <param name="subsystem"></param>
    /// <param name="flags"></param>
    /// <param name="updateType"></param>
    public void OnHandUpdate(XRHandSubsystem subsystem, XRHandSubsystem.UpdateSuccessFlags flags, XRHandSubsystem.UpdateType updateType)
    {
        if (!TryGetRole(out BasisBoneTrackedRole assignedRole)) return;

        // Articulated hands are camera-tracked and want far heavier filtering than the controller this
        // same device reports as when one is picked back up.
        if (assignedRole == BasisBoneTrackedRole.LeftHand || assignedRole == BasisBoneTrackedRole.RightHand)
        {
            bool opticallyTracked = assignedRole == BasisBoneTrackedRole.LeftHand ? subsystem.leftHand.isTracked : subsystem.rightHand.isTracked;
            TrackingHardware = opticallyTracked ? BasisTrackingHardware.Optical : BasisTrackingHardware.InsideOut;
        }

        float playerToAvatar = BasisHeightDriver.DeviceScale;

        // helper local function (keeps the diff small)
        Vector3 ApplyOffsetToPos(Vector3 rawLocalPos)
        {
            // rawLocalPos is in your "device/player" space; we scale first, then rotate+translate by offset
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

                    // keep your existing "final rotation" logic, but (optionally) parent it to OffsetCoords.rotation
                    HandFinal.rotation = HandleHandFinalRotation(ApplyOffsetToRot(HandRaw.rotation));
                    HandFinal.position = ApplyOffsetToPos(HandRaw.position);
                }
                else
                {
                    HandRaw.position = _palmPoseActionPosition.ReadValue<Vector3>();
                    HandRaw.rotation = _palmPoseActionRotation.ReadValue<Quaternion>();

                    var corrected = math.mul(HandRaw.rotation, Quaternion.Euler(LeftHandPalmCorrection));
                    HandFinal.rotation = ApplyOffsetToRot(corrected);
                    HandFinal.position = ApplyOffsetToPos(HandRaw.position);

                    FallbackHand(BasisLocalPlayer.Instance.LocalHandDriver.LeftHand);

                    if (UseIKPositionOffset)
                    {
                        // IK offset should be rotated by the *same* frame you're using (use HandFinal.rotation usually)
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
                    HandRaw.position = _palmPoseActionPosition.ReadValue<Vector3>();
                    HandRaw.rotation = _palmPoseActionRotation.ReadValue<Quaternion>();

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
    private BasisOpenXRRenderModel _runtimeModel;
    public override void ShowTrackedVisual()
    {
        ShowTrackedVisualDefaultImplementation();
    }
    public override bool TryShowRuntimeDeviceModel()
    {
        bool isLeftHand = TryGetRole(out BasisBoneTrackedRole role) && role == BasisBoneTrackedRole.LeftHand;
        return BasisOpenXRRenderModel.TryLoad(this, isLeftHand, ref _runtimeModel);
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
