using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// New Settings "Admin" tab built using PanelTabPage + Panel* elements (no prefab UI).
    /// </summary>
    public static class SettingsProviderAdminTab
    {
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

            // Teleport
            PanelButton teleportAll = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportAll.Descriptor.SetTitle("Teleport All To Target");
            teleportAll.Descriptor.SetDescription("Teleports everyone to the selected player's location.");
            teleportAll.OnClicked += () =>
            {
                BasisNetworkModeration.TeleportAll(controller.SelectedPlayer?.playerId);
            };

            PanelButton teleportTo = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportTo.Descriptor.SetTitle("Teleport To Player (by UUID)");
            teleportTo.Descriptor.SetDescription("Teleports you to the player with the UUID above.");
            teleportTo.OnClicked += () =>
            {
                if (controller.TryFindId(controller.GetUUIDText(), out ushort id))
                    BasisNetworkModeration.TryTeleportToPlayer(id);
                else
                    BasisDebug.LogError("Can't find ID for UUID: " + controller.GetUUIDText());
            };

            PanelButton teleportHere = PanelButton.CreateNew(actionsGroup.ContentParent);
            teleportHere.Descriptor.SetTitle("Teleport Player Here (by UUID)");
            teleportHere.Descriptor.SetDescription("Teleports the player with the UUID above to you.");
            teleportHere.OnClicked += () =>
            {
                if (controller.TryFindId(controller.GetUUIDText(), out ushort id))
                    BasisNetworkModeration.TeleportHere(id);
                else
                    BasisDebug.LogError("Can't find ID for UUID: " + controller.GetUUIDText());
            };

            // Moderation
            PanelButton ban = PanelButton.CreateNew(actionsGroup.ContentParent);
            ban.Descriptor.SetTitle("Ban (UUID)");
            ban.Descriptor.SetDescription("Bans by UUID using the reason/message field.");
            ban.OnClicked += () =>
            {
                BasisNetworkModeration.SendBan(controller.GetUUIDText(), controller.GetReasonText());
            };

            PanelButton kick = PanelButton.CreateNew(actionsGroup.ContentParent);
            kick.Descriptor.SetTitle("Kick (UUID)");
            kick.Descriptor.SetDescription("Kicks by UUID using the reason/message field.");
            kick.OnClicked += () =>
            {
                BasisNetworkModeration.SendKick(controller.GetUUIDText(), controller.GetReasonText());
            };

            PanelButton ipBan = PanelButton.CreateNew(actionsGroup.ContentParent);
            ipBan.Descriptor.SetTitle("IP Ban (UUID)");
            ipBan.Descriptor.SetDescription("IP bans by UUID using the reason/message field.");
            ipBan.OnClicked += () =>
            {
                BasisNetworkModeration.SendIPBan(controller.GetUUIDText(), controller.GetReasonText());
            };

            PanelButton unban = PanelButton.CreateNew(actionsGroup.ContentParent);
            unban.Descriptor.SetTitle("Unban (UUID)");
            unban.Descriptor.SetDescription("Removes ban by UUID.");
            unban.OnClicked += () =>
            {
                BasisNetworkModeration.UnBan(controller.GetUUIDText());
            };

            PanelButton addAdmin = PanelButton.CreateNew(actionsGroup.ContentParent);
            addAdmin.Descriptor.SetTitle("Add Admin (UUID)");
            addAdmin.Descriptor.SetDescription("Grants admin to UUID.");
            addAdmin.OnClicked += () =>
            {
                BasisNetworkModeration.AddAdmin(controller.GetUUIDText());
            };

            PanelButton removeAdmin = PanelButton.CreateNew(actionsGroup.ContentParent);
            removeAdmin.Descriptor.SetTitle("Remove Admin (UUID)");
            removeAdmin.Descriptor.SetDescription("Revokes admin from UUID.");
            removeAdmin.OnClicked += () =>
            {
                BasisNetworkModeration.RemoveAdmin(controller.GetUUIDText());
            };

            // Messaging
            PanelButton sendMessage = PanelButton.CreateNew(actionsGroup.ContentParent);
            sendMessage.Descriptor.SetTitle("Send Message (UUID)");
            sendMessage.Descriptor.SetDescription("Sends the message to the target player (requires UUID -> network id lookup).");
            sendMessage.OnClicked += () =>
            {
                if (controller.TryFindId(controller.GetUUIDText(), out ushort id))
                    BasisNetworkModeration.SendMessage(id, controller.GetReasonText());
                else
                    BasisDebug.LogError("Can't find ID for UUID: " + controller.GetUUIDText());
            };

            PanelButton sendAll = PanelButton.CreateNew(actionsGroup.ContentParent);
            sendAll.Descriptor.SetTitle("Send Message To All");
            sendAll.Descriptor.SetDescription("Broadcasts the message to all players.");
            sendAll.OnClicked += () =>
            {
                BasisNetworkModeration.SendMessageAll(controller.GetReasonText());
            };

            descriptor.ForceRebuild();
            return tab;
        }

        /// <summary>
        /// Handles player list lifetime + selection + network graph helpers.
        /// </summary>
        private sealed class AdminTabController : MonoBehaviour
        {
            public RectTransform PlayerListParent;

            public PanelTextField UUIDField;
            public PanelTextField ReasonField;

            public BasisNetworkPlayer SelectedPlayer;

            private readonly List<PanelButton> _playerButtons = new();

            private void OnEnable()
            {
                BasisNetworkPlayer.OnRemotePlayerJoined += OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft += OnRemotePlayersChanged;
                RebuildPlayerList();
            }

            private void OnDestroy()
            {
                BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemotePlayersChanged;
                BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemotePlayersChanged;

                ClearPlayerButtons();
            }

            private void OnRemotePlayersChanged(BasisNetworkPlayer _p1, BasisRemotePlayer _p2)
            {
                RebuildPlayerList();
            }

            public string GetUUIDText()
            {
                TMP_InputField input = UUIDField ? UUIDField.GetComponentInChildren<TMP_InputField>(true) : null;
                return input ? input.text : string.Empty;
            }

            public string GetReasonText()
            {
                TMP_InputField input = ReasonField ? ReasonField.GetComponentInChildren<TMP_InputField>(true) : null;
                return input ? input.text : string.Empty;
            }

            private void ClearPlayerButtons()
            {
                for (int i = 0; i < _playerButtons.Count; i++)
                {
                    if (_playerButtons[i] != null) _playerButtons[i].ReleaseInstance();
                }
                _playerButtons.Clear();
            }

            public void RebuildPlayerList()
            {
                if (!PlayerListParent) return;

                // Remove old list (keep the "Refresh Player List" button which is created outside this controller)
                // We only track/destroy the buttons we created.
                ClearPlayerButtons();

                foreach (BasisNetworkPlayer player in BasisNetworkPlayers.Players.Values)
                {
                    PanelButton b = PanelButton.CreateNew(PlayerListParent);
                    b.Descriptor.SetTitle($"{player.playerId} > {player.Player.SafeDisplayName}");
                    b.OnClicked += () => SelectPlayer(player);

                    _playerButtons.Add(b);
                }
            }

            private void SelectPlayer(BasisNetworkPlayer player)
            {
                SelectedPlayer = player;

                // Fill UUID field
                TMP_InputField input = UUIDField ? UUIDField.GetComponentInChildren<TMP_InputField>(true) : null;
                if (input) input.SetTextWithoutNotify(SelectedPlayer.Player.UUID);
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
