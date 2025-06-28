using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Enums;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.UI.UI_Panels;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class BasisIndividualPlayerSettings : BasisUIBase
{
    public static string Path = "Packages/com.basis.sdk/Prefabs/UI/PlayerSelectionPanel.prefab";
    public static string CursorRequest = "PlayerSelectionPanel";
    public Slider UserVolumeOverride;
    public Button ToggleAvatar;

    public TextMeshProUGUI AvatarVisibleText;
    public TextMeshProUGUI SliderVolumePercentage;

    public TextMeshProUGUI PlayerName;
    public TextMeshProUGUI PlayerUUID;
    public BasisRemotePlayer RemotePlayer;
    public BasisUIVolumeSampler BasisUIVolumeSampler;

    public Button RequestAvatarClone;
    public override void DestroyEvent()
    {
        BasisCursorManagement.LockCursor(CursorRequest);
    }
    public override void InitalizeEvent()
    {
        BasisCursorManagement.UnlockCursor(CursorRequest);
    }

    public static async void OpenPlayerSettings(BasisRemotePlayer RemotePlayer)
    {
        BasisUIManagement.CloseAllMenus();
        AddressableGenericResource resource = new AddressableGenericResource(Path, AddressableExpectedResult.SingleItem);
        BasisUIBase Base = OpenMenuNow(resource);
        BasisIndividualPlayerSettings PlayerSettings = (BasisIndividualPlayerSettings)Base;
        await PlayerSettings.Initalize(RemotePlayer);
    }
    public async Task Initalize(BasisRemotePlayer remotePlayer)
    {
        RemotePlayer = remotePlayer;
        BasisUIVolumeSampler.Initalize(remotePlayer);
        PlayerName.text = RemotePlayer.DisplayName;
        PlayerUUID.text = RemotePlayer.UUID;
        string playerUUID = RemotePlayer.UUID;
        // UI Setup
        UserVolumeOverride.wholeNumbers = false;
        UserVolumeOverride.maxValue = 1.5f;
        UserVolumeOverride.minValue = 0f;

        BasisPlayerSettingsData settings = await BasisPlayerSettingsManager.RequestPlayerSettings(playerUUID);

        // Apply settings
        UserVolumeOverride.value = settings.VolumeLevel;
        SliderVolumePercentage.text = Mathf.RoundToInt(settings.VolumeLevel * 100) + "%";
        AvatarVisibleText.text = settings.AvatarVisible ? "Hide Avatar" : "Show Avatar";

        // Event Listeners
        ToggleAvatar.onClick.AddListener(() => ToggleAvatarPressed(playerUUID));

        RequestAvatarClone.onClick.AddListener(() => ToggleAvatarPressed(playerUUID));

        UserVolumeOverride.onValueChanged.AddListener(value => ChangePlayersVolume(playerUUID, value));
    }
    public float step = 0.05f; // The interval between values
    float SnapValue(float value)
    {
        return Mathf.Round(value / step) * step;
    }
    public async void ToggleAvatarPressed(string playerUUID)
    {
        BasisPlayerSettingsData settings = await BasisPlayerSettingsManager.RequestPlayerSettings(playerUUID);
        settings.AvatarVisible = !settings.AvatarVisible;
        await BasisPlayerSettingsManager.SetPlayerSettings(settings);

        AvatarVisibleText.text = settings.AvatarVisible ? "Hide Avatar" : "Show Avatar";
        if (RemotePlayer != null)
        {
            RemotePlayer.ReloadAvatar();
        }
    }
    public async void ChangePlayersVolume(string playerUUID, float volume)
    {
        volume = SnapValue(volume);
        UserVolumeOverride.SetValueWithoutNotify(volume);
        BasisPlayerSettingsData settings = await BasisPlayerSettingsManager.RequestPlayerSettings(playerUUID);
        settings.VolumeLevel = volume;
        SliderVolumePercentage.text = Mathf.RoundToInt(volume * 100) + "%";
        await BasisPlayerSettingsManager.SetPlayerSettings(settings);
        if (RemotePlayer != null)
        {
            RemotePlayer.NetworkReceiver.AudioReceiverModule.ChangeRemotePlayersVolumeSettings(volume);
        }
    }
}
