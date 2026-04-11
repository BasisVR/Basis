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
    /// New Settings "Admin" tab built using PanelTabPage + Panel* elements (no prefab UI).
    /// </summary>
    public static class SettingsProviderAdminTab
    {
        /// <summary>Fired when a player is selected in the admin player list. Carries the UUID.</summary>
        public static event Action<string> OnPlayerUuidSelected;
        public static PanelTabPage AdminTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;

            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle("Admin & Moderation");
            descriptor.SetDescription("Moderation tools and quick utilities.");

            RectTransform container = descriptor.ContentParent;

            // --- Player list group ---
            PanelElementDescriptor playersGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            playersGroup.SetTitle("Players");
            playersGroup.SetDescription("Click a player to target them (fills UUID).");

            // A controller MonoBehaviour to manage lifetime + rebuild list on joins/leaves.
            AdminTabController controller = tab.gameObject.AddComponent<AdminTabController>();
            controller.PlayerListParent = playersGroup.ContentParent;

            PanelTextField playerSearch = PanelTextField.CreateNewEntry(playersGroup.ContentParent);
            playerSearch.Descriptor.SetTitle("Search");
            playerSearch.Descriptor.SetDescription("Filter players by name.");
            playerSearch.OnValueChanged += controller.OnSearchChanged;
            controller.SearchField = playerSearch;

            PanelButton refreshPlayers = PanelButton.CreateNew(playersGroup.ContentParent);
            refreshPlayers.Descriptor.SetTitle("Refresh Player List");
            refreshPlayers.Descriptor.SetDescription("Rebuilds the list from current network state.");
            refreshPlayers.OnClicked += controller.RebuildPlayerList;

            // --- Target group ---
            PanelElementDescriptor targetGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            targetGroup.SetTitle("Target");
            targetGroup.SetDescription("UUID can be filled by selecting a player above.");

            PanelTextField uuidField = PanelTextField.CreateNewEntry(targetGroup.ContentParent);
            uuidField.Descriptor.SetTitle("UUID / Target");
            uuidField.Descriptor.SetDescription("Player UUID (or paste a UUID).");

            PanelTextField reasonField = PanelTextField.CreateNewEntry(targetGroup.ContentParent);
            reasonField.Descriptor.SetTitle("Reason / Message");
            reasonField.Descriptor.SetDescription("Reason for moderation actions, or message contents.");

            // Make the reason field nicer for longer text (optional).
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
            actionsGroup.SetTitle("Actions");
            actionsGroup.SetDescription("Moderation + utility actions.");

            // ------------------
            // Teleport actions
            // ------------------
            PanelButton teleportToSelected = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportToSelected.Descriptor.SetTitle("Teleport To Player");
            teleportToSelected.Descriptor.SetDescription("Teleports you to the selected player's location.");
            GuardedClick(
                teleportToSelected,
                "Teleport to player?",
                "Teleport you to the selected player's location?",
                "Teleport",
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null)
                    {
                        BasisDebug.LogError("No player available.");
                        return;
                    }
                    BasisNetworkModeration.TryTeleportToPlayer(target.playerId);
                });

            PanelButton teleportAll = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportAll.Descriptor.SetTitle("Teleport All To Target");
            teleportAll.Descriptor.SetDescription("Teleports everyone to the selected player's location.");
            GuardedClick(
                teleportAll,
                "Teleport everyone?",
                "This will teleport ALL players to the selected target's location. Continue?",
                "Teleport",
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null)
                    {
                        BasisDebug.LogError("No player available.");
                        return;
                    }
                    BasisNetworkModeration.TeleportAll(target.playerId);
                });

            PanelButton teleportHere = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportHere.Descriptor.SetTitle("Teleport Player Here");
            teleportHere.Descriptor.SetDescription("Teleports the selected player to your location.");
            GuardedClick(
                teleportHere,
                "Teleport player to you?",
                "Teleport the selected player to your location?",
                "Teleport",
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null)
                    {
                        BasisDebug.LogError("No player available.");
                        return;
                    }
                    BasisNetworkModeration.TeleportHere(target.playerId);
                });

            // ------------------
            // Moderation actions
            // ------------------
            PanelButton ban = PanelButton.CreateNew(actionsGroup.ContentParent);
            ban.Descriptor.SetTitle("Ban (UUID)");
            ban.Descriptor.SetDescription("Bans by UUID using the reason/message field.");
            GuardedClick(
                ban,
                "Ban player?",
                "Ban the player with this UUID? This may be irreversible depending on server policy.",
                "Ban",
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid))
                    {
                        BasisDebug.LogError("UUID is empty.");
                        return;
                    }
                    BasisNetworkModeration.SendBan(uuid, controller.GetReasonText());
                });

            PanelButton kick = PanelButton.CreateNew(actionsGroup.ContentParent);
            kick.Descriptor.SetTitle("Kick (UUID)");
            kick.Descriptor.SetDescription("Kicks by UUID using the reason/message field.");
            GuardedClick(
                kick,
                "Kick player?",
                "Kick the player with this UUID?",
                "Kick",
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid))
                    {
                        BasisDebug.LogError("UUID is empty.");
                        return;
                    }
                    BasisNetworkModeration.SendKick(uuid, controller.GetReasonText());
                });

            PanelButton ipBan = PanelButton.CreateNew(actionsGroup.ContentParent);
            ipBan.Descriptor.SetTitle("IP Ban (UUID)");
            ipBan.Descriptor.SetDescription("IP bans by UUID using the reason/message field.");
            GuardedClick(
                ipBan,
                "IP ban player?",
                "IP-ban the player with this UUID? This can affect multiple accounts on the same connection.",
                "IP Ban",
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid))
                    {
                        BasisDebug.LogError("UUID is empty.");
                        return;
                    }
                    BasisNetworkModeration.SendIPBan(uuid, controller.GetReasonText());
                });

            PanelButton unban = PanelButton.CreateNew(actionsGroup.ContentParent);
            unban.Descriptor.SetTitle("Unban (UUID)");
            unban.Descriptor.SetDescription("Removes ban by UUID.");
            GuardedClick(
                unban,
                "Unban player?",
                "Remove the ban for this UUID?",
                "Unban",
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid))
                    {
                        BasisDebug.LogError("UUID is empty.");
                        return;
                    }
                    BasisNetworkModeration.UnBan(uuid);
                });

            // ------------------
            // Messaging actions
            // ------------------
            PanelButton sendMessage = PanelButton.CreateNew(actionsGroup.ContentParent);
            sendMessage.Descriptor.SetTitle("Send Message (UUID)");
            sendMessage.Descriptor.SetDescription("Sends the message to the target player (requires UUID -> network id lookup).");
            GuardedClick(
                sendMessage,
                "Send message?",
                "Send this message to the target player?",
                "Send",
                () =>
                {
                    string uuid = controller.GetUUIDText();
                    if (string.IsNullOrWhiteSpace(uuid))
                    {
                        BasisDebug.LogError("UUID is empty.");
                        return;
                    }

                    if (controller.TryFindId(uuid, out ushort id))
                        BasisNetworkModeration.SendMessage(id, controller.GetReasonText());
                    else
                        BasisDebug.LogError("Can't find ID for UUID: " + uuid);
                });

            PanelButton sendAll = PanelButton.CreateNew(actionsGroup.ContentParent);
            sendAll.Descriptor.SetTitle("Send Message To All");
            sendAll.Descriptor.SetDescription("Broadcasts the message to all players.");
            GuardedClick(
                sendAll,
                "Broadcast message?",
                "Send this message to ALL players?",
                "Broadcast",
                () =>
                {
                    string msg = controller.GetReasonText();
                    if (string.IsNullOrWhiteSpace(msg))
                    {
                        BasisDebug.LogError("Message/Reason is empty.");
                        return;
                    }
                    BasisNetworkModeration.SendMessageAll(msg);
                });

            // ------------------
            // Shout mode actions
            // ------------------
            PanelButton enableShout = PanelButton.CreateNew(actionsGroup.ContentParent);
            enableShout.Descriptor.SetTitle("Enable Shout Mode");
            enableShout.Descriptor.SetDescription("Grants non-spatialized broadcast voice to the selected player.");
            GuardedClick(
                enableShout,
                "Enable shout mode?",
                "Enable non-spatialized broadcast voice for this player?",
                "Enable",
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null)
                    {
                        BasisDebug.LogError("No player available.");
                        return;
                    }
                    BasisNetworkModeration.EnableShoutMode(target.playerId);
                });

            PanelButton disableShout = PanelButton.CreateNew(actionsGroup.ContentParent);
            disableShout.Descriptor.SetTitle("Disable Shout Mode");
            disableShout.Descriptor.SetDescription("Removes non-spatialized broadcast voice from the selected player.");
            GuardedClick(
                disableShout,
                "Disable shout mode?",
                "Disable non-spatialized broadcast voice for this player?",
                "Disable",
                () =>
                {
                    BasisNetworkPlayer target = controller.GetEffectivePlayer();
                    if (target == null)
                    {
                        BasisDebug.LogError("No player available.");
                        return;
                    }
                    BasisNetworkModeration.DisableShoutMode(target.playerId);
                });

            // --- Global lock group ---
            PanelElementDescriptor lockGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            lockGroup.SetTitle("Global Content Locks");
            lockGroup.SetDescription("Globally disable loading for all non-admin players. Everyone is notified.");

            PanelToggle avatarLock = PanelToggle.CreateNewEntry(lockGroup.ContentParent);
            avatarLock.Descriptor.SetTitle("Lock Avatars");
            avatarLock.Descriptor.SetDescription("Prevents all non-admin avatar loading over the network.");
            avatarLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalAvatarsLocked);
            avatarLock.OnValueChanged += _ => BasisNetworkModeration.GlobalToggleAvatars();

            PanelToggle propLock = PanelToggle.CreateNewEntry(lockGroup.ContentParent);
            propLock.Descriptor.SetTitle("Lock Props");
            propLock.Descriptor.SetDescription("Prevents all non-admin prop loading over the network.");
            propLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalPropsLocked);
            propLock.OnValueChanged += _ => BasisNetworkModeration.GlobalToggleProps();

            PanelToggle worldLock = PanelToggle.CreateNewEntry(lockGroup.ContentParent);
            worldLock.Descriptor.SetTitle("Lock Worlds");
            worldLock.Descriptor.SetDescription("Prevents all non-admin world loading over the network.");
            worldLock.SetValueWithoutNotify(BasisNetworkModeration.GlobalWorldsLocked);
            worldLock.OnValueChanged += _ => BasisNetworkModeration.GlobalToggleWorlds();

            PanelToggle headlessAudioToggle = PanelToggle.CreateNewEntry(lockGroup.ContentParent);
            headlessAudioToggle.Descriptor.SetTitle("Headless audio off");
            headlessAudioToggle.Descriptor.SetDescription("Silences headless BasisAudioClipPlayer clients over the network.");
            headlessAudioToggle.SetValueWithoutNotify(BasisNetworkModeration.GlobalHeadlessAudioOff);
            headlessAudioToggle.OnValueChanged += value => BasisNetworkModeration.SetGlobalHeadlessAudio(value);

            PanelToggle disallowHeadlessToggle = PanelToggle.CreateNewEntry(lockGroup.ContentParent);
            disallowHeadlessToggle.Descriptor.SetTitle("Disallow headless");
            disallowHeadlessToggle.Descriptor.SetDescription("Disconnects connected headless clients and blocks new headless clients while enabled.");
            disallowHeadlessToggle.SetValueWithoutNotify(BasisNetworkModeration.GlobalHeadlessDisallowed);
            disallowHeadlessToggle.OnValueChanged += value => BasisNetworkModeration.SetGlobalHeadlessDisallow(value);

            controller.AvatarLockToggle = avatarLock;
            controller.PropLockToggle = propLock;
            controller.WorldLockToggle = worldLock;
            controller.HeadlessAudioToggle = headlessAudioToggle;
            controller.HeadlessDisallowToggle = disallowHeadlessToggle;

            // Permissions section
            SettingsProviderPermissionsTab.BuildPermissionsUI(container, tab.gameObject);

            descriptor.ForceRebuild();
            return tab;
        }

        // ------------------
        // CONFIRMATION HELPERS
        // ------------------

        private static void WithConfirm(
            string title,
            string body,
            string confirmText,
            string cancelText,
            Action onConfirm)
        {
            if (BasisMainMenu.Instance == null)
            {
                BasisDebug.LogError("BasisMainMenu.Instance was null; cannot show confirmation dialog.");
                return;
            }

            BasisMainMenu.Instance.OpenDialogue(
                title,
                body,
                confirmText,
                cancelText,
                value =>
                {
                    if (!value) return;
                    onConfirm?.Invoke();
                });
        }

        private static void GuardedClick(
            PanelButton button,
            string title,
            string body,
            string confirmText,
            Action actionOnConfirm,
            string cancelText = "Cancel")
        {
            button.OnClicked += () =>
                WithConfirm(title, body, confirmText, cancelText, actionOnConfirm);
        }

        /// <summary>
        /// Handles player list lifetime + selection + network graph helpers.
        /// </summary>
        private sealed class AdminTabController : MonoBehaviour
        {
            public RectTransform PlayerListParent;

            public PanelTextField UUIDField;
            public PanelTextField ReasonField;
            public PanelTextField SearchField;

            public PanelToggle AvatarLockToggle;
            public PanelToggle PropLockToggle;
            public PanelToggle WorldLockToggle;
            public PanelToggle HeadlessAudioToggle;
            public PanelToggle HeadlessDisallowToggle;

            public BasisNetworkPlayer SelectedPlayer;
            private string _searchQuery = string.Empty;

            /// <summary>Returns the selected player, or falls back to the local player.</summary>
            public BasisNetworkPlayer GetEffectivePlayer()
            {
                return SelectedPlayer ?? BasisNetworkPlayer.LocalPlayer;
            }

            private readonly List<PanelButton> _playerButtons = new();
            private readonly List<BasisNetworkPlayer> _playerRefs = new();

            private void OnEnable()
            {
                BasisNetworkPlayer.OnRemotePlayerJoined += OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft += OnRemotePlayersChanged;
                BasisNetworkModeration.OnGlobalLockStateChanged += OnGlobalLockStateChanged;
                BasisNetworkModeration.OnGlobalHeadlessAudioStateChanged += OnGlobalHeadlessAudioStateChanged;
                BasisNetworkModeration.OnGlobalHeadlessDisallowStateChanged += OnGlobalHeadlessDisallowStateChanged;
                RebuildPlayerList();
            }

            private void OnDestroy()
            {
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayersChanged;
                BasisNetworkModeration.OnGlobalLockStateChanged -= OnGlobalLockStateChanged;
                BasisNetworkModeration.OnGlobalHeadlessAudioStateChanged -= OnGlobalHeadlessAudioStateChanged;
                BasisNetworkModeration.OnGlobalHeadlessDisallowStateChanged -= OnGlobalHeadlessDisallowStateChanged;

                ClearPlayerButtons();
            }

            private void OnGlobalLockStateChanged(bool avatars, bool props, bool worlds)
            {
                if (AvatarLockToggle != null) AvatarLockToggle.SetValueWithoutNotify(avatars);
                if (PropLockToggle != null) PropLockToggle.SetValueWithoutNotify(props);
                if (WorldLockToggle != null) WorldLockToggle.SetValueWithoutNotify(worlds);
            }

            private void OnGlobalHeadlessAudioStateChanged(bool headlessAudioOff)
            {
                if (HeadlessAudioToggle != null) HeadlessAudioToggle.SetValueWithoutNotify(headlessAudioOff);
            }

            private void OnGlobalHeadlessDisallowStateChanged(bool headlessDisallowed)
            {
                if (HeadlessDisallowToggle != null) HeadlessDisallowToggle.SetValueWithoutNotify(headlessDisallowed);
            }

            private void OnRemotePlayersChanged(BasisNetworkPlayer _p1, BasisRemotePlayer _p2)
            {
                RebuildPlayerList();
            }

            public string GetUUIDText()
            {
                return UUIDField != null ? UUIDField.Value ?? string.Empty : string.Empty;
            }

            public string GetReasonText()
            {
                return ReasonField != null ? ReasonField.Value ?? string.Empty : string.Empty;
            }

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

                OnPlayerUuidSelected?.Invoke(SelectedPlayer.Player.UUID);
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
