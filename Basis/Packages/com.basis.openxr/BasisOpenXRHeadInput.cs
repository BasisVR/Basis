using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.OpenXR;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.InputSystem;
public class BasisOpenXRHeadInput : BasisInput
{
    public BasisOpenXRInputEye BasisOpenXRInputEye;
    public InputActionProperty Position;
    public InputActionProperty Rotation;

    private InputAction _positionAction;
    private InputAction _rotationAction;

    public void Initialize(string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole)
    {
        TrackingHardware = BasisTrackingHardware.InsideOut;
        InitializeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, BasisBoneTrackedRole.CenterEye);

        Position = new InputActionProperty(new InputAction("<XRHMD>/centerEyePosition", InputActionType.Value, "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
        Rotation = new InputActionProperty(new InputAction("<XRHMD>/centerEyeRotation", InputActionType.Value, "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));

        Position.action.Enable();
        Rotation.action.Enable();

        _positionAction = Position.action;
        _rotationAction = Rotation.action;

        BasisOpenXRInputEye = gameObject.AddComponent<BasisOpenXRInputEye>();
        BasisOpenXRInputEye.Initialize();
    }

    private void DisableInputActions()
    {
        Position.action?.Disable();
        Rotation.action?.Disable();
    }

    public new void OnDestroy()
    {
        DisableInputActions();
        BasisOpenXRInputEye?.Shutdown();
        base.OnDestroy();
    }

    public override void LateDoPollData()
    {
        PollPose();
    }
    public override void RenderPollData()
    {
        PollPose();
        ComputeRaycastDirection(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation, Quaternion.identity);
        UpdateInputEvents();

        if (BasisOpenXRInputEye != null)
        {
            BasisOpenXRInputEye.Simulate();
        }
    }
    private void PollPose()
    {
        ComputeUnscaledDeviceCoord(ref UnscaledDeviceCoord, _positionAction.ReadValue<Vector3>());
        UnscaledDeviceCoord.rotation = _rotationAction.ReadValue<Quaternion>();

        ConvertToScaledDeviceCoord();
        ControlOnlyAsDevice();
    }
    public override void ShowTrackedVisual()
    {
        if (BasisVisualTracker == null)
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
    public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
    {
       // BasisDebug.LogError("XRHead does not support Haptics Playback");
    }
    public override void PlaySoundEffect(string SoundEffectName, float Volume)
    {
        PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
    }
}
