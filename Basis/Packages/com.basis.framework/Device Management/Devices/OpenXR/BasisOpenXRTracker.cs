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
        if (Position.action != null) RawFinal.position = Position.action.ReadValue<Vector3>();
        if (Rotation.action != null) RawFinal.rotation = Rotation.action.ReadValue<Quaternion>();

        DeviceFinal.position = RawFinal.position * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;
        DeviceFinal.rotation = RawFinal.rotation;

        ControlOnlyAsDevice();
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
