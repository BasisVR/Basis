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

    [Header("Controls")]
    public Slider UserVolumeOverride;
    public Button ToggleAvatar;
    public Button ToggleAvatarInteraction;
    public Button RequestAvatarClone;

    [Header("Texts")]
    public TextMeshProUGUI AvatarVisibleText;
    public TextMeshProUGUI AvatarInteractionsText;
    public TextMeshProUGUI SliderVolumePercentage;
    public TextMeshProUGUI PlayerName;
    public TextMeshProUGUI PlayerUUID;

    [Header("Context")]
    public BasisRemotePlayer RemotePlayer;
    public BasisUIVolumeSampler BasisUIVolumeSampler;

    [Header("Config")]
    public float step = 0.05f; // The interval between values
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
        BasisUIBase Base = OpenMenuNow(Path);
        var PlayerSettings = (BasisIndividualPlayerSettings)Base;
        await PlayerSettings.Initalize(RemotePlayer);
    }

    public async Task Initalize(BasisRemotePlayer remotePlayer)
    {
        RemotePlayer = remotePlayer;
        BasisUIVolumeSampler.Initalize(remotePlayer);

        PlayerName.text = RemotePlayer.DisplayName;
        PlayerUUID.text = RemotePlayer.UUID;

        // Slider setup
        UserVolumeOverride.wholeNumbers = false;
        UserVolumeOverride.maxValue = 1.5f;
        UserVolumeOverride.minValue = 0f;

        // Load settings
        var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(RemotePlayer.UUID);

        // Apply to UI (set values BEFORE wiring listeners so we don't trigger saves immediately)
        UserVolumeOverride.SetValueWithoutNotify(settings.VolumeLevel);
        SliderVolumePercentage.text = Mathf.RoundToInt(settings.VolumeLevel * 100) + "%";

        AvatarVisibleText.text = settings.AvatarVisible ? "Hide Avatar" : "Show Avatar";
        AvatarInteractionsText.text = settings.AvatarInteraction ? "Disable Interactions" : "Enable Interactions";

        // Wire listeners
        ToggleAvatar.onClick.RemoveAllListeners();
        ToggleAvatar.onClick.AddListener(() => ToggleAvatarPressed(RemotePlayer.UUID));

        ToggleAvatarInteraction.onClick.RemoveAllListeners();
        ToggleAvatarInteraction.onClick.AddListener(() => ToggleAvatarInteractions(RemotePlayer.UUID));

        // If this button should *clone* the avatar, point it to the correct action.
        // Kept as-is in case your original intent was to reuse the visibility toggle.
        RequestAvatarClone.onClick.RemoveAllListeners();
        RequestAvatarClone.onClick.AddListener(() => ToggleAvatarPressed(RemotePlayer.UUID));

        UserVolumeOverride.onValueChanged.RemoveAllListeners();
        UserVolumeOverride.onValueChanged.AddListener(value => ChangePlayersVolume(RemotePlayer.UUID, value));
    }

    float SnapValue(float value)
    {
        return Mathf.Round(value / step) * step;
    }

    public async void ToggleAvatarInteractions(string playerUUID)
    {
        var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(playerUUID);
        settings.AvatarInteraction = !settings.AvatarInteraction;
        await BasisPlayerSettingsManager.SetPlayerSettings(settings);

        AvatarInteractionsText.text = settings.AvatarInteraction ? "Disable Interactions" : "Enable Interactions";

        if (RemotePlayer != null)
        {
            RemotePlayer.ReloadAvatar();
        }
    }

    public async void ToggleAvatarPressed(string playerUUID)
    {
        var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(playerUUID);
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

        var settings = await BasisPlayerSettingsManager.RequestPlayerSettings(playerUUID);
        settings.VolumeLevel = volume;

        SliderVolumePercentage.text = Mathf.RoundToInt(volume * 100) + "%";
        await BasisPlayerSettingsManager.SetPlayerSettings(settings);

        if (RemotePlayer != null)
        {
            RemotePlayer.NetworkReceiver.AudioReceiverModule.ChangeRemotePlayersVolumeSettings(volume);
        }
        bool over = volume > 1.0f;
        SliderVolumePercentage.color = over ? Color.red : Color.white;
    }
}
