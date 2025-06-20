using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.InputSystem;
public class BasisOpenXRTracker : BasisInput
{
    public InputActionProperty Position;
    public InputActionProperty Rotation;
    public InputDevice InputDevice;
    public void Initialize(InputDevice device, string usage, string UniqueID, string UnUniqueID, string subSystems)
    {
        InputDevice = device;
        InitalizeTracking(UniqueID, UnUniqueID + usage, subSystems, false, BasisBoneTrackedRole.CenterEye);
        var layoutName = device.GetType().Name;
        Position = new InputActionProperty(new InputAction($"Position_{usage}", InputActionType.Value, $"<{layoutName}>{{{usage}}}/devicePosition", expectedControlType: "Vector3"));

        Rotation = new InputActionProperty(new InputAction($"Rotation_{usage}", InputActionType.Value, $"<{layoutName}>{{{usage}}}/deviceRotation", expectedControlType: "Quaternion"));

        Position.action.Enable();
        Rotation.action.Enable();
    }
    private void DisableInputActions()
    {
        Position.action?.Disable();
        Rotation.action?.Disable();
    }
    public new void OnDestroy()
    {
        DisableInputActions();
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
        BasisDebug.LogError("Tracker does not support Haptics Playback");
    }
}
