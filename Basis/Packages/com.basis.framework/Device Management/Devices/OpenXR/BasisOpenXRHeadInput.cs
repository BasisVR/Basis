using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.OpenXR;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.InputSystem;
public class BasisOpenXRHeadInput : BasisInput
{
    public BasisOpenXRInputEye BasisOpenXRInputEye;
    public BasisVirtualSpineDriver BasisVirtualSpine = new BasisVirtualSpineDriver();
    public InputActionProperty Position;
    public InputActionProperty Rotation;

    public void Initialize(string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole)
    {
        InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, BasisBoneTrackedRole.CenterEye);

        Position = new InputActionProperty(new InputAction("<XRHMD>/centerEyePosition", InputActionType.Value, "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
        Rotation = new InputActionProperty(new InputAction("<XRHMD>/centerEyeRotation", InputActionType.Value, "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));

        Position.action.Enable();
        Rotation.action.Enable();

        BasisOpenXRInputEye = gameObject.AddComponent<BasisOpenXRInputEye>();
        BasisOpenXRInputEye.Initalize();
        BasisVirtualSpine.Initialize();
    }

    private void DisableInputActions()
    {
        Position.action?.Disable();
        Rotation.action?.Disable();
    }

    public new void OnDestroy()
    {
        DisableInputActions();
        BasisVirtualSpine.DeInitialize();
        BasisOpenXRInputEye?.Shutdown();
        base.OnDestroy();
    }

    public override void DoPollData()
    {
        if (Position.action != null) LocalRawPosition = Position.action.ReadValue<Vector3>();
        if (Rotation.action != null) DeviceFinalRotation = Rotation.action.ReadValue<Quaternion>();

        DeviceFinalPosition = BasisLocalPlayer.Instance?.CurrentHeight != null
            ? LocalRawPosition * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale
            : LocalRawPosition;

        if (hasRoleAssigned && Control.HasTracked != BasisHasTracked.HasNoTracker)
        {
            // Apply position offset using math.mul for quaternion-vector multiplication
            Control.IncomingData.position = DeviceFinalPosition;

            // Apply rotation offset using math.mul for quaternion multiplication
            Control.IncomingData.rotation = DeviceFinalRotation;
        }

        UpdatePlayerControl();
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
    public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
    {
        BasisDebug.LogError("XRHead does not support Haptics Playback");
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
