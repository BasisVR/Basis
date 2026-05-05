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
        public static PanelTabPage ModeratorTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle(BasisLocalization.Get("settings.moderator.title"));
            descriptor.SetDescription(BasisLocalization.Get("settings.moderator.description"));

            RectTransform container = descriptor.ContentParent;

            // --- Player list group ---
            PanelElementDescriptor playersGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            playersGroup.SetTitle(BasisLocalization.Get("menu.provider.players"));
            playersGroup.SetDescription(BasisLocalization.Get("settings.admin.players.description"));

            ModeratorTabController controller = tab.gameObject.AddComponent<ModeratorTabController>();
            controller.PlayerListParent = playersGroup.ContentParent;

            PanelTextField playerSearch = PanelTextField.CreateNewEntry(playersGroup.ContentParent);
            playerSearch.Descriptor.SetTitle(BasisLocalization.Get("ui.search.label"));
            playerSearch.Descriptor.SetDescription(BasisLocalization.Get("menu.players.search.byName"));
            playerSearch.OnValueChanged += controller.OnSearchChanged;
            controller.SearchField = playerSearch;

            PanelButton refreshPlayers = PanelButton.CreateNew(playersGroup.ContentParent);
            refreshPlayers.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.refreshPlayers"));
            refreshPlayers.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.refreshPlayers.description"));
            refreshPlayers.OnClicked += controller.RebuildPlayerList;

            PanelToggle autoRefreshToggle = PanelToggle.CreateNewEntry(playersGroup.ContentParent);
            autoRefreshToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.autoRefresh"));
            autoRefreshToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.autoRefresh.description"));
            autoRefreshToggle.AssignBinding(BasisSettingsDefaults.AdminAutoRefreshPlayerList);

            // --- Target group ---
            PanelElementDescriptor targetGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            targetGroup.SetTitle(BasisLocalization.Get("settings.admin.target"));
            targetGroup.SetDescription(BasisLocalization.Get("settings.admin.target.description"));

            PanelTextField uuidField = PanelTextField.CreateNewEntry(targetGroup.ContentParent);
            uuidField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.uuidTarget"));
            uuidField.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.uuidTarget.description"));

            PanelTextField reasonField = PanelTextField.CreateNewEntry(targetGroup.ContentParent);
            reasonField.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.reason"));
            reasonField.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.reason.description"));

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
            actionsGroup.SetDescription(BasisLocalization.Get("settings.admin.actions.description"));

            // Teleport
            PanelButton teleportToSelected = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportToSelected.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.teleportTo"));
            teleportToSelected.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.teleportTo.description"));
            GuardedClick(teleportToSelected, "Teleport to player?",
                "Teleport you to the selected player's location?", "Teleport",
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.TryTeleportToPlayer(target.playerId);
                });

            PanelButton teleportAll = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportAll.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.teleportAll"));
            teleportAll.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.teleportAll.description"));
            GuardedClick(teleportAll, "Teleport everyone?",
                "This will teleport ALL players to the selected target's location. Continue?", "Teleport",
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.TeleportAll(target.playerId);
                });

            PanelButton teleportHere = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportHere.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.teleportHere"));
            teleportHere.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.teleportHere.description"));
            GuardedClick(teleportHere, "Teleport player to you?",
                "Teleport the selected player to your location?", "Teleport",
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.TeleportHere(target.playerId);
                });

            // Moderation
            PanelButton ban = PanelButton.CreateNew(actionsGroup.ContentParent);
            ban.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.banUuid"));
            ban.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.banUuid.description"));
            GuardedClick(ban, "Ban player?",
                "Ban the player with this UUID? This may be irreversible depending on server policy.", "Ban",
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid)) { BasisDebug.LogError("UUID is empty."); return; }
                    BasisNetworkModeration.SendBan(uuid, controller.GetReasonText());
                });

            PanelButton kick = PanelButton.CreateNew(actionsGroup.ContentParent);
            kick.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.kickUuid"));
            kick.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.kickUuid.description"));
            GuardedClick(kick, "Kick player?",
                "Kick the player with this UUID?", "Kick",
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid)) { BasisDebug.LogError("UUID is empty."); return; }
                    BasisNetworkModeration.SendKick(uuid, controller.GetReasonText());
                });

            PanelButton ipBan = PanelButton.CreateNew(actionsGroup.ContentParent);
            ipBan.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.ipBanUuid"));
            ipBan.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.ipBanUuid.description"));
            GuardedClick(ipBan, "IP ban player?",
                "IP-ban the player with this UUID? This can affect multiple accounts on the same connection.", "IP Ban",
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid)) { BasisDebug.LogError("UUID is empty."); return; }
                    BasisNetworkModeration.SendIPBan(uuid, controller.GetReasonText());
                });

            PanelButton unban = PanelButton.CreateNew(actionsGroup.ContentParent);
            unban.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.unbanUuid"));
            unban.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.unbanUuid.description"));
            GuardedClick(unban, "Unban player?",
                "Remove the ban for this UUID?", "Unban",
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid)) { BasisDebug.LogError("UUID is empty."); return; }
                    BasisNetworkModeration.UnBan(uuid);
                });

            // Messaging
            PanelButton sendMessage = PanelButton.CreateNew(actionsGroup.ContentParent);
            sendMessage.Descriptor.SetTitle(BasisLocalization.Get("settings.admin.sendMessageUuid"));
            sendMessage.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.sendMessageUuid.description"));
            GuardedClick(sendMessage, "Send message?",
                "Send this message to the target player?", "Send",
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
            sendAll.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.sendAll.description"));
            GuardedClick(sendAll, "Broadcast message?",
                "Send this message to ALL players?", "Broadcast",
                () =>
                {
                    string msg = controller.GetReasonText();
                    if (string.IsNullOrWhiteSpace(msg)) { BasisDebug.LogError("Message/Reason is empty."); return; }
                    BasisNetworkModeration.SendMessageAll(msg);
                });

            // Shout
            PanelButton enableShout = PanelButton.CreateNew(actionsGroup.ContentParent);
            enableShout.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.shout.enable"));
            enableShout.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.enableShout.description"));
            GuardedClick(enableShout, "Enable shout mode?",
                "Enable non-spatialized broadcast voice for this player?", "Enable",
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.EnableShoutMode(target.playerId);
                });

            PanelButton disableShout = PanelButton.CreateNew(actionsGroup.ContentParent);
            disableShout.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.shout.disable"));
            disableShout.Descriptor.SetDescription(BasisLocalization.Get("settings.admin.disableShout.description"));
            GuardedClick(disableShout, "Disable shout mode?",
                "Disable non-spatialized broadcast voice for this player?", "Disable",
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null) { BasisDebug.LogError("No player available."); return; }
                    BasisNetworkModeration.DisableShoutMode(target.playerId);
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
            Action actionOnConfirm, string cancelText = "Cancel")
        {
            button.OnClicked += () => WithConfirm(title, body, confirmText, cancelText, actionOnConfirm);
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
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerJoined += OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft += OnRemotePlayersChanged;
                RebuildPlayerList();
            }

            private void OnDisable()
            {
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
