using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>
    /// Per-user moderation tab — player list, kicks/bans/IP-bans/unbans,
    /// teleports, direct messages, broadcast, and shout-mode toggles.
    /// Server config and other persistent admin tools live on the Admin tab.
    /// </summary>
    public static class SettingsProviderModeratorTab
    {
        /// <summary>Bitrate the per-player override slider starts on before an admin moves it.</summary>
        private const int DefaultPlayerOpusBitrate = 32000;

        public static PanelTabPage ModeratorTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle(BasisLocalization.Get("settings.moderator.title"));

            RectTransform container = descriptor.ContentParent;

            // --- Player list group ---
            PanelElementDescriptor playersGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            playersGroup.SetTitle(BasisLocalization.Get("menu.provider.players"));

            ModeratorTabController controller = tab.gameObject.AddComponent<ModeratorTabController>();
            controller.PlayerListParent = playersGroup.ContentParent;

            PanelTextField playerSearch = PanelTextField.CreateNewEntry(playersGroup.ContentParent);
            playerSearch.Descriptor.SetTitle(BasisLocalization.Get("ui.search.label"));
            playerSearch.Descriptor.SetTooltip(BasisLocalization.Get("ui.search.label.tooltip"));
            playerSearch.OnValueChanged += controller.OnSearchChanged;
            controller.SearchField = playerSearch;

            PanelButton refreshPlayers = PanelButton.CreateNew(playersGroup.ContentParent);
            refreshPlayers.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.refreshPlayers"));
            refreshPlayers.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.refreshPlayers.tooltip"));
            refreshPlayers.OnClicked += controller.RebuildPlayerList;

            PanelToggle autoRefreshToggle = PanelToggle.CreateNewEntry(playersGroup.ContentParent);
            autoRefreshToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.autoRefresh"));
            autoRefreshToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.autoRefresh.tooltip"));
            autoRefreshToggle.AssignBinding(BasisSettingsDefaults.AdminAutoRefreshPlayerList);

            // --- Target group ---
            PanelElementDescriptor targetGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            targetGroup.SetTitle(BasisLocalization.Get("settings.admin.target"));

            PanelTextField uuidField = PanelTextField.CreateNewEntry(targetGroup.ContentParent);
            uuidField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.uuidTarget"));
            uuidField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.uuidTarget.tooltip"));

            PanelTextField reasonField = PanelTextField.CreateNewEntry(targetGroup.ContentParent);
            reasonField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.reason"));
            reasonField.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.reason.tooltip"));

            TMP_InputField reasonInput = reasonField.GetComponentInChildren<TMP_InputField>(true);
            if (reasonInput)
            {
                reasonInput.lineType = TMP_InputField.LineType.MultiLineNewline;
                reasonInput.scrollSensitivity = 2f;
            }

            controller.UUIDField = uuidField;
            controller.ReasonField = reasonField;

            // --- Actions group ---
            PanelElementDescriptor actionsGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            actionsGroup.SetTitle(BasisLocalization.Get("settings.admin.actions"));

            // Teleport
            PanelButton teleportToSelected = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportToSelected.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.teleportTo"));
            teleportToSelected.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.teleportTo.tooltip"));
            GuardedClick(teleportToSelected, BasisLocalization.Get("settings.admin.confirm.teleportTo.title"),
                BasisLocalization.Get("settings.admin.confirm.teleportTo.body"),
                BasisLocalization.Get("settings.admin.confirm.teleportTo.confirm"),
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.TryTeleportToPlayer(target.playerId);
                });

            PanelButton teleportAll = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportAll.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.teleportAll"));
            teleportAll.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.teleportAll.tooltip"));
            GuardedClick(teleportAll, BasisLocalization.Get("settings.admin.confirm.teleportAll.title"),
                BasisLocalization.Get("settings.admin.confirm.teleportAll.body"),
                BasisLocalization.Get("settings.admin.confirm.teleportAll.confirm"),
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.TeleportAll(target.playerId);
                });

            PanelButton teleportHere = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportHere.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.teleportHere"));
            teleportHere.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.teleportHere.tooltip"));
            GuardedClick(teleportHere, BasisLocalization.Get("settings.admin.confirm.teleportHere.title"),
                BasisLocalization.Get("settings.admin.confirm.teleportHere.body"),
                BasisLocalization.Get("settings.admin.confirm.teleportHere.confirm"),
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.TeleportHere(target.playerId);
                });

            // Moderation
            PanelButton ban = PanelButton.CreateNew(actionsGroup.ContentParent);
            ban.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.banUuid"));
            ban.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.banUuid.tooltip"));
            GuardedClick(ban, BasisLocalization.Get("settings.admin.confirm.ban.title"),
                BasisLocalization.Get("settings.admin.confirm.ban.body"),
                BasisLocalization.Get("settings.admin.confirm.ban.confirm"),
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid)) { BasisDebug.LogError("UUID is empty."); return; }
                    BasisNetworkModeration.SendBan(uuid, controller.GetReasonText());
                });

            PanelButton kick = PanelButton.CreateNew(actionsGroup.ContentParent);
            kick.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.kickUuid"));
            kick.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.kickUuid.tooltip"));
            GuardedClick(kick, BasisLocalization.Get("settings.admin.confirm.kick.title"),
                BasisLocalization.Get("settings.admin.confirm.kick.body"),
                BasisLocalization.Get("settings.admin.confirm.kick.confirm"),
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid)) { BasisDebug.LogError("UUID is empty."); return; }
                    BasisNetworkModeration.SendKick(uuid, controller.GetReasonText());
                });

            PanelButton ipBan = PanelButton.CreateNew(actionsGroup.ContentParent);
            ipBan.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.ipBanUuid"));
            ipBan.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.ipBanUuid.tooltip"));
            GuardedClick(ipBan, BasisLocalization.Get("settings.admin.confirm.ipBan.title"),
                BasisLocalization.Get("settings.admin.confirm.ipBan.body"),
                BasisLocalization.Get("settings.admin.confirm.ipBan.confirm"),
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid)) { BasisDebug.LogError("UUID is empty."); return; }
                    BasisNetworkModeration.SendIPBan(uuid, controller.GetReasonText());
                });

            PanelButton unban = PanelButton.CreateNew(actionsGroup.ContentParent);
            unban.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.unbanUuid"));
            unban.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.unbanUuid.tooltip"));
            GuardedClick(unban, BasisLocalization.Get("settings.admin.confirm.unban.title"),
                BasisLocalization.Get("settings.admin.confirm.unban.body"),
                BasisLocalization.Get("settings.admin.confirm.unban.confirm"),
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid)) { BasisDebug.LogError("UUID is empty."); return; }
                    BasisNetworkModeration.UnBan(uuid);
                });

            // An IP ban is stored against the banned UUID's recorded address, so lifting it needs
            // its own command — a plain Unban leaves the address blocked.
            PanelButton unIpBan = PanelButton.CreateNew(actionsGroup.ContentParent);
            unIpBan.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.unIpBanUuid"));
            unIpBan.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.unIpBanUuid.tooltip"));
            GuardedClick(unIpBan, BasisLocalization.Get("settings.admin.confirm.unIpBan.title"),
                BasisLocalization.Get("settings.admin.confirm.unIpBan.body"),
                BasisLocalization.Get("settings.admin.confirm.unIpBan.confirm"),
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid)) { BasisDebug.LogError("UUID is empty."); return; }
                    BasisNetworkModeration.UnIpBan(uuid);
                });

            // Messaging
            PanelButton sendMessage = PanelButton.CreateNew(actionsGroup.ContentParent);
            sendMessage.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.sendMessageUuid"));
            sendMessage.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.sendMessageUuid.tooltip"));
            GuardedClick(sendMessage, BasisLocalization.Get("settings.admin.confirm.sendMessage.title"),
                BasisLocalization.Get("settings.admin.confirm.sendMessage.body"),
                BasisLocalization.Get("settings.admin.confirm.sendMessage.confirm"),
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid)) { BasisDebug.LogError("UUID is empty."); return; }
                    if (controller.TryFindId(uuid, out ushort id))
                        BasisNetworkModeration.SendMessage(id, controller.GetReasonText());
                    else
                        BasisDebug.LogError("Can't find ID for UUID: " + uuid);
                });

            PanelButton sendAll = PanelButton.CreateNew(actionsGroup.ContentParent);
            sendAll.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.sendAll"));
            sendAll.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.sendAll.tooltip"));
            GuardedClick(sendAll, BasisLocalization.Get("settings.admin.confirm.sendAll.title"),
                BasisLocalization.Get("settings.admin.confirm.sendAll.body"),
                BasisLocalization.Get("settings.admin.confirm.sendAll.confirm"),
                () =>
                {
                    string msg = controller.GetReasonText();
                    if (string.IsNullOrWhiteSpace(msg)) { BasisDebug.LogError("Message/Reason is empty."); return; }
                    BasisNetworkModeration.SendMessageAll(msg);
                });

            // Shout
            PanelButton enableShout = PanelButton.CreateNew(actionsGroup.ContentParent);
            enableShout.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.shout.enable"));
            enableShout.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.shout.enable.tooltip"));
            GuardedClick(enableShout, BasisLocalization.Get("settings.admin.confirm.shoutEnable.title"),
                BasisLocalization.Get("settings.admin.confirm.shoutEnable.body"),
                BasisLocalization.Get("settings.admin.confirm.shoutEnable.confirm"),
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.EnableShoutMode(target.playerId);
                });

            PanelButton disableShout = PanelButton.CreateNew(actionsGroup.ContentParent);
            disableShout.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.shout.disable"));
            disableShout.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.shout.disable.tooltip"));
            GuardedClick(disableShout, BasisLocalization.Get("settings.admin.confirm.shoutDisable.title"),
                BasisLocalization.Get("settings.admin.confirm.shoutDisable.body"),
                BasisLocalization.Get("settings.admin.confirm.shoutDisable.confirm"),
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.DisableShoutMode(target.playerId);
                });

            // Full-quality broadcast
            PanelButton enableFullQuality = PanelButton.CreateNew(actionsGroup.ContentParent);
            enableFullQuality.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.fullquality.enable"));
            enableFullQuality.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.fullquality.enable.tooltip"));
            GuardedClick(enableFullQuality, BasisLocalization.Get("settings.admin.confirm.fullQualityEnable.title"),
                BasisLocalization.Get("settings.admin.confirm.fullQualityEnable.body"),
                BasisLocalization.Get("settings.admin.confirm.fullQualityEnable.confirm"),
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.SetFullQualityBroadcast(target.playerId, true);
                });

            PanelButton disableFullQuality = PanelButton.CreateNew(actionsGroup.ContentParent);
            disableFullQuality.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.fullquality.disable"));
            disableFullQuality.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.fullquality.disable.tooltip"));
            GuardedClick(disableFullQuality, BasisLocalization.Get("settings.admin.confirm.fullQualityDisable.title"),
                BasisLocalization.Get("settings.admin.confirm.fullQualityDisable.body"),
                BasisLocalization.Get("settings.admin.confirm.fullQualityDisable.confirm"),
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.SetFullQualityBroadcast(target.playerId, false);
                });

            // --- Per-player voice bitrate ---
            // Targets the runtime player id rather than a UUID, so it only applies to someone
            // currently connected. A per-user override wins over the server-wide bitrate.
            PanelElementDescriptor voiceGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            voiceGroup.SetTitle(BasisLocalization.Get("settings.admin.playerVoice"));

            PanelSlider bitrateSlider = PanelSlider.CreateNew(PanelSlider.SliderStyles.Entry, voiceGroup.ContentParent);
            bitrateSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("settings.admin.playerOpusBitrate"), 6000f, 128000f, true, 0, ValueDisplayMode.Compact));
            bitrateSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.playerOpusBitrate.tooltip"));
            bitrateSlider.SetValueWithoutNotify(DefaultPlayerOpusBitrate);

            PanelButton applyBitrate = PanelButton.CreateNew(voiceGroup.ContentParent);
            applyBitrate.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.playerOpusBitrate.apply"));
            applyBitrate.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.playerOpusBitrate.apply.tooltip"));
            GuardedClick(applyBitrate, BasisLocalization.Get("settings.admin.confirm.bitrateApply.title"),
                BasisLocalization.Get("settings.admin.confirm.bitrateApply.body"),
                BasisLocalization.Get("settings.admin.confirm.bitrateApply.confirm"),
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.SetUserOpusBitrate(target.playerId, Mathf.RoundToInt(bitrateSlider.Value));
                });

            PanelButton clearBitrate = PanelButton.CreateNew(voiceGroup.ContentParent);
            clearBitrate.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.playerOpusBitrate.clear"));
            clearBitrate.Descriptor.SetTooltip(BasisLocalization.Get("settings.admin.playerOpusBitrate.clear.tooltip"));
            GuardedClick(clearBitrate, BasisLocalization.Get("settings.admin.confirm.bitrateClear.title"),
                BasisLocalization.Get("settings.admin.confirm.bitrateClear.body"),
                BasisLocalization.Get("settings.admin.confirm.bitrateClear.confirm"),
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.SetUserOpusBitrate(target.playerId, 0);
                });

            controller.RebuildPlayerList();
            descriptor.ForceRebuild();
            return tab;
        }

        private static void WithConfirm(string title, string body, string confirmText, string cancelText, Action onConfirm)
        {
            if (BasisMainMenu.Instance == null)
            {
                BasisDebug.LogError("BasisMainMenu.Instance was null; cannot show confirmation dialog.");
                return;
            }
            BasisMainMenu.Instance.OpenDialogue(title, body, confirmText, cancelText, value =>
            {
                if (!value) return;
                onConfirm?.Invoke();
            });
        }

        private static void GuardedClick(PanelButton button, string title, string body, string confirmText,
            Action actionOnConfirm, string cancelText = null)
        {
            button.OnClicked += () => WithConfirm(title, body, confirmText,
                cancelText ?? BasisLocalization.Get("ui.cancel"), actionOnConfirm);
        }

        private sealed class ModeratorTabController : MonoBehaviour
        {
            public RectTransform PlayerListParent;
            public PanelTextField UUIDField;
            public PanelTextField ReasonField;
            public PanelTextField SearchField;

            public BasisNetworkPlayer SelectedPlayer;
            private string _searchQuery = string.Empty;

            private readonly List<PanelButton> _playerButtons = new();
            private readonly List<BasisNetworkPlayer> _playerRefs = new();

            public BasisNetworkPlayer GetEffectivePlayer()
            {
                return SelectedPlayer ?? BasisNetworkPlayer.LocalPlayer;
            }

            private void OnEnable()
            {
                // Moderator panel open → route every popup into the notification list.
                BasisNotificationCenter.BeginForcedScope();
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerJoined += OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft += OnRemotePlayersChanged;
                RebuildPlayerList();
            }

            private void OnDisable()
            {
                // Moderator panel closed/hidden → resume normal popup handling.
                BasisNotificationCenter.EndForcedScope();
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayersChanged;
            }

            private void OnDestroy()
            {
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayersChanged;
                ClearPlayerButtons();
            }

            private void OnRemotePlayersChanged(BasisNetworkPlayer _p1, BasisRemotePlayer _p2)
            {
                if (!BasisSettingsDefaults.AdminAutoRefreshPlayerList.RawValue) return;
                RebuildPlayerList();
            }

            public string GetUUIDText() => UUIDField != null ? UUIDField.Value ?? string.Empty : string.Empty;
            public string GetReasonText() => ReasonField != null ? ReasonField.Value ?? string.Empty : string.Empty;

            private void ClearPlayerButtons()
            {
                for (int i = 0; i < _playerButtons.Count; i++)
                {
                    if (_playerButtons[i] != null) _playerButtons[i].ReleaseInstance();
                }
                _playerButtons.Clear();
                _playerRefs.Clear();
            }

            public void OnSearchChanged(string query)
            {
                _searchQuery = query ?? string.Empty;
                ApplyFilter();
            }

            public void RebuildPlayerList()
            {
                if (!PlayerListParent) return;
                ClearPlayerButtons();

                foreach (BasisNetworkPlayer player in BasisNetworkPlayers.Players.Values)
                {
                    PanelButton b = PanelButton.CreateNew(PlayerListParent);
                    bool isLocal = BasisNetworkPlayer.LocalPlayer != null && player.playerId == BasisNetworkPlayer.LocalPlayer.playerId;
                    bool isShouting = isLocal ? BasisNetworkModeration.LocalPlayerInShoutMode : BasisShoutAudioDriver.IsInShoutMode(player.playerId);
                    string shoutTag = isShouting ? " [SHOUT]" : "";
                    b.Descriptor.SetTitle($"{player.playerId} > {player.Player.SafeDisplayName}{shoutTag}");
                    b.OnClicked += () => SelectPlayer(player);

                    _playerButtons.Add(b);
                    _playerRefs.Add(player);
                }

                ApplyFilter();
                LayoutRebuilder.ForceRebuildLayoutImmediate(PlayerListParent);
            }

            private void ApplyFilter()
            {
                string q = _searchQuery.Trim().ToLowerInvariant();
                bool hasQuery = q.Length > 0;

                for (int i = 0; i < _playerButtons.Count; i++)
                {
                    if (_playerButtons[i] == null) continue;
                    bool show = !hasQuery || (_playerRefs[i].Player != null &&
                        (_playerRefs[i].Player.SafeDisplayName ?? "").ToLowerInvariant().Contains(q));
                    _playerButtons[i].gameObject.SetActive(show);
                }
            }

            private void SelectPlayer(BasisNetworkPlayer player)
            {
                SelectedPlayer = player;
                if (UUIDField != null)
                    UUIDField.SetValueWithoutNotify(SelectedPlayer.Player.UUID);

                // Forward selection so the Permissions section on the Admin tab can autofill.
                SettingsProviderAdminTab.RaisePlayerUuidSelected(SelectedPlayer.Player.UUID);
            }

            public bool TryFindId(string uuid, out ushort id)
            {
                foreach (BasisNetworkPlayer player in BasisNetworkPlayers.Players.Values)
                {
                    if (uuid == player.Player.UUID)
                    {
                        id = player.playerId;
                        return true;
                    }
                }
                id = 0;
                return false;
            }
        }
    }
}
