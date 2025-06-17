using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;
public class BasisOpenXRHandInput : BasisInput
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
    public XRHandSubsystem m_Subsystem;

    public float3 leftHandToIKRotationOffset = new float3(0, 90, -180);
    public float3 rightHandToIKRotationOffset = new float3(0, -90, -180);
    public float3 AddedPosition = new float3(0, -0.05f, 0);
    public float3 WristPos;
    public quaternion HandPalmRotation;
    public void Initialize(string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole)
    {
        InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole);
        string devicePath = basisBoneTrackedRole == BasisBoneTrackedRole.LeftHand ? "<XRController>{LeftHand}" : "<XRController>{RightHand}";
        string DevicePalmPath = basisBoneTrackedRole == BasisBoneTrackedRole.LeftHand ? "<PalmPose>{LeftHand}" : "<PalmPose>{RightHand}";
        SetupInputActions(devicePath);

        DeviceActionPosition = new InputActionProperty(new InputAction($"{devicePath}/devicePosition", InputActionType.Value, $"{devicePath}/devicePosition", expectedControlType: "Vector3"));
        DeviceActionRotation = new InputActionProperty(new InputAction($"{devicePath}/deviceRotation", InputActionType.Value, $"{devicePath}/deviceRotation", expectedControlType: "Quaternion"));

        DeviceActionPosition.action.Enable();
        DeviceActionRotation.action.Enable();
        m_Subsystem = XRGeneralSettings.Instance?.Manager?.activeLoader?.GetLoadedSubsystem<XRHandSubsystem>();

        if (m_Subsystem != null)
        {
            m_Subsystem.updatedHands += OnHandUpdate;
        }
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
        EnableInputAction(Trigger);
        EnableInputAction(Grip);
        EnableInputAction(PrimaryButton);
        EnableInputAction(SecondaryButton);
        EnableInputAction(MenuButton);
        EnableInputAction(Primary2DAxis);
        EnableInputAction(Secondary2DAxis);
    }
    private void DisableInputActions()
    {
        DisableInputAction(Trigger);
        DisableInputAction(Grip);
        DisableInputAction(PrimaryButton);
        DisableInputAction(SecondaryButton);
        DisableInputAction(MenuButton);
        DisableInputAction(Primary2DAxis);
        DisableInputAction(Secondary2DAxis);
    }
    private void EnableInputAction(InputActionProperty actionProperty) => actionProperty.action?.Enable();
    private void DisableInputAction(InputActionProperty actionProperty) => actionProperty.action?.Disable();

    public new void OnDestroy()
    {
        DisableInputActions();
        base.OnDestroy();
        if (m_Subsystem != null)
        {
            m_Subsystem.updatedHands -= OnHandUpdate;
        }
    }
    public override void DoPollData()
    {
        CurrentInputState.Primary2DAxis = Primary2DAxis.action?.ReadValue<Vector2>() ?? Vector2.zero;
        CurrentInputState.Secondary2DAxis = Secondary2DAxis.action?.ReadValue<Vector2>() ?? Vector2.zero;
        CurrentInputState.GripButton = Grip.action?.ReadValue<float>() > 0.5f;
        CurrentInputState.SecondaryTrigger = Grip.action?.ReadValue<float>() ?? 0f;
        CurrentInputState.SystemOrMenuButton = MenuButton.action?.ReadValue<float>() > 0.5f;
        CurrentInputState.PrimaryButtonGetState = PrimaryButton.action?.ReadValue<float>() > 0.5f;
        CurrentInputState.SecondaryButtonGetState = SecondaryButton.action?.ReadValue<float>() > 0.5f;
        CurrentInputState.Trigger = Trigger.action?.ReadValue<float>() ?? 0f;


        LocalRawPosition = DeviceActionPosition.action.ReadValue<Vector3>();
        LocalRawRotation = DeviceActionRotation.action.ReadValue<Quaternion>();
        float scale = BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;

        ControllerFinalPosition = LocalRawPosition * scale;
        ControllerFinalRotation = LocalRawRotation;

        HandFinalPosition = WristPos * scale;
        HandFinalRotation = HandPalmRotation;

        float3 FinalPalmPosition = (AddedPosition + HandFinalPosition) * scale;
        quaternion ConvertedRotation = new quaternion();
        if (TryGetRole(out BasisBoneTrackedRole AssignedRole))
        {
            switch (AssignedRole)
            {
                case  BasisBoneTrackedRole.LeftHand:
                    ConvertedRotation = math.mul(HandPalmRotation, Quaternion.Euler(leftHandToIKRotationOffset));
                    break;
                case BasisBoneTrackedRole.RightHand:
                    ConvertedRotation = math.mul(HandPalmRotation, Quaternion.Euler(rightHandToIKRotationOffset));
                    break;
                default:
                    ConvertedRotation = HandPalmRotation;
                    break;
            }
        }
        HandFinalRotation = math.mul(ControllerFinalRotation, ConvertedRotation);

        HandFinalPosition = FinalPalmPosition;

        if (hasRoleAssigned && Control.HasTracked != BasisHasTracked.HasNoTracker)
        {
            Control.IncomingData.position = HandFinalPosition;
            Control.IncomingData.rotation = HandFinalRotation;
        }
        UpdatePlayerControl();
    }
    private void OnHandUpdate(XRHandSubsystem subsystem, XRHandSubsystem.UpdateSuccessFlags flags, XRHandSubsystem.UpdateType updateType)
    {
        if (updateType != XRHandSubsystem.UpdateType.BeforeRender)
        {
            return;
        }
        if (TryGetRole(out BasisBoneTrackedRole AssignedRole))
        {
            BasisDebug.Log("run1");
            if (AssignedRole == BasisBoneTrackedRole.LeftHand)
            {
                if (subsystem.leftHand.isTracked)
                {
                    BasisDebug.Log("run2");
                    XRHand leftHand = subsystem.leftHand;
                    // Palm and Wrist
                    //UpdateJointPose(leftHand, XRHandJointID.Palm, out PalmPos, out PalmRot);
                    UpdateJointPose(leftHand, XRHandJointID.Wrist, out WristPos, out HandPalmRotation); // Fixed: Use Wrist joint here
                }
            }
            else
            {
                if (AssignedRole == BasisBoneTrackedRole.RightHand)
                {
                    if (subsystem.rightHand.isTracked)
                    {
                        BasisDebug.Log("run3");
                        XRHand rightHand = subsystem.rightHand;
                        // Palm and Wrist
                        //  UpdateJointPose(rightHand, XRHandJointID.Palm, out PalmPos, out PalmRot);
                        UpdateJointPose(rightHand, XRHandJointID.Wrist, out WristPos, out HandPalmRotation); // Fixed: Use Wrist joint here
                    }
                }
            }
        }
    }
    private void UpdateJointPose(XRHand hand, XRHandJointID jointId, out float3 Position, out quaternion Rotation)
    {
        XRHandJoint joint = hand.GetJoint(jointId);
        if (joint.TryGetPose(out Pose pose))
        {

            Position = pose.position;
            Rotation = pose.rotation;
        }
        else
        {
            Position = Vector3.zero;
            Rotation = Quaternion.identity;
        }
    }
    public override void ShowTrackedVisual()
    {
        if (BasisVisualTracker == null && LoadedDeviceRequest == null)
        {
            BasisDeviceMatchSettings Match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
            if (Match.CanDisplayPhysicalTracker)
            {
                InputDeviceCharacteristics Hand = InputDeviceCharacteristics.None;
                if (TryGetRole(out BasisBoneTrackedRole HandRole))
                {
                    switch (HandRole)
                    {
                        case BasisBoneTrackedRole.LeftHand:
                            Hand = InputDeviceCharacteristics.Left;
                            break;
                        case BasisBoneTrackedRole.RightHand:
                            Hand = InputDeviceCharacteristics.Right;
                            break;
                        default:
                            useFallback();
                            return;
                    }
                    InputDeviceCharacteristics input = Hand | InputDeviceCharacteristics.Controller;
                    List<UnityEngine.XR.InputDevice> inputDevices = new List<UnityEngine.XR.InputDevice>();
                    InputDevices.GetDevicesWithCharacteristics(input, inputDevices);
                    if (inputDevices.Count != 0)
                    {
                        Device = inputDevices[0];
                        string LoadRequest;
                        string HandString = Hand.ToString().ToLower();
                        switch (Device.name)
                        {
                            case "Oculus Touch Controller OpenXR":
                                LoadRequest = $"oculus_quest_plus_controller_{HandString}";
                                break;
                            case "Valve Index Controller OpenXR":
                                LoadRequest = $"valve_controller_knu_{HandString}";
                                break;
                            case "Meta Quest Touch Pro Controller OpenXR":
                                LoadRequest = $"oculus_quest_pro_controller_{HandString}";
                                break;
                            case "Meta Quest Touch Plus Controller OpenXR":
                                LoadRequest = $"oculus_quest_plus_controller_{HandString}";
                                break;
                            default:
                                LoadRequest = $"valve_controller_knu_{HandString}";
                                break;
                        }

                        BasisDebug.Log("name was found to be " + LoadRequest + " for device " + Device.name + " picked from " + inputDevices.Count, BasisDebug.LogTag.Device);

                        var op = Addressables.LoadAssetAsync<GameObject>(LoadRequest);
                        GameObject go = op.WaitForCompletion();
                        GameObject gameObject = Object.Instantiate(go, this.transform);
                        gameObject.name = CommonDeviceIdentifier;
                        if (gameObject.TryGetComponent(out BasisVisualTracker))
                        {
                            BasisVisualTracker.Initialization(this);
                        }
                    }
                    else
                    {
                        useFallback();
                    }
                }
                else
                {
                    useFallback();
                }
            }
            else
            {
                if (UseFallbackModel())
                {
                    useFallback();
                }
            }
        }
    }
    public void useFallback()
    {
        UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op = Addressables.LoadAssetAsync<GameObject>(FallbackDeviceID);
        GameObject go = op.WaitForCompletion();
        GameObject gameObject = Object.Instantiate(go);
        gameObject.name = CommonDeviceIdentifier;
        gameObject.transform.parent = this.transform;
        if (gameObject.TryGetComponent(out BasisVisualTracker))
        {
            BasisVisualTracker.Initialization(this);
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
}
