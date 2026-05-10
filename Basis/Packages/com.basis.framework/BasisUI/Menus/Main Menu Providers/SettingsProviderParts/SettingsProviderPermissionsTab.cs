using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using BasisPermissions;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Settings "Permissions" tab. Any user can view the permission store (groups, users, nodes).
    /// Only admins can modify permissions (add/remove groups, nodes, parents, user assignments).
    /// </summary>
    public static class SettingsProviderPermissionsTab
    {
        public static void BuildPermissionsUI(RectTransform container, GameObject controllerHost)
        {
            PermissionsTabController controller = controllerHost.AddComponent<PermissionsTabController>();
            controller.Container = container;

            // Status / info group
            PanelElementDescriptor statusGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            statusGroup.SetTitle(BasisLocalization.Get("settings.permissions.status"));
            statusGroup.SetDescription(BasisLocalization.Get("settings.permissions.status.description"));

            PanelButton refreshBtn = PanelButton.CreateNew(statusGroup.ContentParent);
            refreshBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.refresh"));
            refreshBtn.Descriptor.SetDescription(BasisLocalization.Get("settings.permissions.refresh.description"));
            refreshBtn.OnClicked += () => BasisNetworkModeration.RequestPermissions();

            controller.StatusGroup = statusGroup;

            // Groups section (populated after data arrives)
            PanelElementDescriptor groupsGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            groupsGroup.SetTitle(BasisLocalization.Get("menu.individualPlayer.groups"));
            groupsGroup.SetDescription(BasisLocalization.Get("settings.permissions.groups.description"));
            controller.GroupsParent = groupsGroup.ContentParent;
            controller.GroupsGroup = groupsGroup;

            // Users section (populated after data arrives)
            PanelElementDescriptor usersGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            usersGroup.SetTitle(BasisLocalization.Get("settings.permissions.users"));
            usersGroup.SetDescription(BasisLocalization.Get("settings.permissions.users.description"));
            controller.UsersParent = usersGroup.ContentParent;
            controller.UsersGroup = usersGroup;

            // Admin-only: create/delete group controls
            PanelElementDescriptor adminGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            adminGroup.SetTitle(BasisLocalization.Get("settings.permissions.adminActions"));
            adminGroup.SetDescription(BasisLocalization.Get("settings.permissions.adminActions.description"));

            PanelTextField newGroupField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            newGroupField.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.groupName"));
            newGroupField.Descriptor.SetDescription(BasisLocalization.Get("settings.permissions.groupName.description"));
            controller.GroupNameField = newGroupField;

            PanelButton createGroupBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            createGroupBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.createGroup"));
            createGroupBtn.OnClicked += () =>
            {
                string name = controller.GetGroupNameText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    BasisNetworkModeration.CreateGroup(name);
                    BasisNetworkModeration.RequestPermissions();
                }
            };

            PanelButton deleteGroupBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            deleteGroupBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.deleteGroup"));
            deleteGroupBtn.OnClicked += () =>
            {
                string name = controller.GetGroupNameText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (BasisMainMenu.Instance != null)
                    {
                        BasisMainMenu.Instance.OpenDialogue(
                            BasisLocalization.Get("settings.permissions.deleteGroup.title"),
                            BasisLocalization.Get("settings.permissions.deleteGroup.confirm", name),
                            BasisLocalization.Get("library.delete"),
                            BasisLocalization.Get("ui.cancel"),
                            confirmed =>
                            {
                                if (confirmed)
                                {
                                    BasisNetworkModeration.DeleteGroup(name);
                                    BasisNetworkModeration.RequestPermissions();
                                }
                            });
                    }
                }
            };

            // Assign user to group
            PanelTextField assignUuidField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            assignUuidField.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.userUuid"));
            assignUuidField.Descriptor.SetDescription(BasisLocalization.Get("settings.permissions.userUuid.description"));
            controller.AssignUuidField = assignUuidField;

            PanelTextField assignGroupField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            assignGroupField.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.targetGroup"));
            assignGroupField.Descriptor.SetDescription(BasisLocalization.Get("settings.permissions.targetGroup.description"));
            controller.AssignGroupField = assignGroupField;

            PanelButton addToGroupBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            addToGroupBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.addUserToGroup"));
            addToGroupBtn.OnClicked += () =>
            {
                string uuid = controller.GetAssignUuidText();
                string group = controller.GetAssignGroupText();
                if (!string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(group))
                {
                    BasisNetworkModeration.SetUserGroup(uuid, group, true);
                    BasisNetworkModeration.RequestPermissions();
                }
            };

            PanelButton removeFromGroupBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            removeFromGroupBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.removeUserFromGroup"));
            removeFromGroupBtn.OnClicked += () =>
            {
                string uuid = controller.GetAssignUuidText();
                string group = controller.GetAssignGroupText();
                if (!string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(group))
                {
                    BasisNetworkModeration.SetUserGroup(uuid, group, false);
                    BasisNetworkModeration.RequestPermissions();
                }
            };

            // Add/remove node from group
            PanelTextField nodeGroupField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            nodeGroupField.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.nodeGroup"));
            nodeGroupField.Descriptor.SetDescription(BasisLocalization.Get("settings.permissions.nodeGroup.description"));
            controller.NodeGroupField = nodeGroupField;

            PanelTextField nodeValueField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            nodeValueField.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.permissionNode"));
            nodeValueField.Descriptor.SetDescription(BasisLocalization.Get("settings.permissions.permissionNode.description"));
            controller.NodeValueField = nodeValueField;

            PanelButton addNodeBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            addNodeBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.addNodeToGroup"));
            addNodeBtn.OnClicked += () =>
            {
                string group = controller.GetNodeGroupText();
                string node = controller.GetNodeValueText();
                if (!string.IsNullOrWhiteSpace(group) && !string.IsNullOrWhiteSpace(node))
                {
                    BasisNetworkModeration.SetGroupNode(group, node, true);
                    BasisNetworkModeration.RequestPermissions();
                }
            };

            PanelButton removeNodeBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            removeNodeBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.removeNodeFromGroup"));
            removeNodeBtn.OnClicked += () =>
            {
                string group = controller.GetNodeGroupText();
                string node = controller.GetNodeValueText();
                if (!string.IsNullOrWhiteSpace(group) && !string.IsNullOrWhiteSpace(node))
                {
                    BasisNetworkModeration.SetGroupNode(group, node, false);
                    BasisNetworkModeration.RequestPermissions();
                }
            };

            // Add/remove parent from group
            PanelTextField parentGroupField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            parentGroupField.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.childGroup"));
            parentGroupField.Descriptor.SetDescription(BasisLocalization.Get("settings.permissions.childGroup.description"));
            controller.ParentGroupField = parentGroupField;

            PanelTextField parentNameField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            parentNameField.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.parentGroup"));
            parentNameField.Descriptor.SetDescription(BasisLocalization.Get("settings.permissions.parentGroup.description"));
            controller.ParentNameField = parentNameField;

            PanelButton addParentBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            addParentBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.addParentToGroup"));
            addParentBtn.OnClicked += () =>
            {
                string child = controller.GetParentGroupText();
                string parent = controller.GetParentNameText();
                if (!string.IsNullOrWhiteSpace(child) && !string.IsNullOrWhiteSpace(parent))
                {
                    BasisNetworkModeration.SetGroupParent(child, parent, true);
                    BasisNetworkModeration.RequestPermissions();
                }
            };

            PanelButton removeParentBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            removeParentBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.removeParentFromGroup"));
            removeParentBtn.OnClicked += () =>
            {
                string child = controller.GetParentGroupText();
                string parent = controller.GetParentNameText();
                if (!string.IsNullOrWhiteSpace(child) && !string.IsNullOrWhiteSpace(parent))
                {
                    BasisNetworkModeration.SetGroupParent(child, parent, false);
                    BasisNetworkModeration.RequestPermissions();
                }
            };

            controller.AdminGroup = adminGroup;
            controller.AdminGroupRoot = adminGroup.gameObject;

            // Initially hide admin controls until we know if user is admin
            adminGroup.gameObject.SetActive(false);

            // Auto-fetch on open
            BasisNetworkModeration.RequestPermissions();
        }

        /// <summary>
        /// Controller that handles dynamic permission data display and lifecycle.
        /// </summary>
        private sealed class PermissionsTabController : MonoBehaviour
        {
            public RectTransform Container;
            public PanelElementDescriptor StatusGroup;
            public RectTransform GroupsParent;
            public PanelElementDescriptor GroupsGroup;
            public RectTransform UsersParent;
            public PanelElementDescriptor UsersGroup;
            public PanelElementDescriptor AdminGroup;
            public GameObject AdminGroupRoot;

            // Admin input fields
            public PanelTextField GroupNameField;
            public PanelTextField AssignUuidField;
            public PanelTextField AssignGroupField;
            public PanelTextField NodeGroupField;
            public PanelTextField NodeValueField;
            public PanelTextField ParentGroupField;
            public PanelTextField ParentNameField;

            private readonly List<GameObject> _groupEntries = new();
            private readonly List<GameObject> _userEntries = new();

            private void OnEnable()
            {
                BasisNetworkModeration.OnPermissionsReceived += OnPermissionsReceived;
                SettingsProviderAdminTab.OnPlayerUuidSelected += OnPlayerUuidSelected;
            }

            private void OnDestroy()
            {
                BasisNetworkModeration.OnPermissionsReceived -= OnPermissionsReceived;
                SettingsProviderAdminTab.OnPlayerUuidSelected -= OnPlayerUuidSelected;
            }

            private void OnPlayerUuidSelected(string uuid)
            {
                SetFieldText(AssignUuidField, uuid);
            }

            private void OnPermissionsReceived(BasisNetworkModeration.PermissionSnapshot snapshot)
            {
                RebuildDisplay(snapshot);
            }

            private void ClearEntries(List<GameObject> entries)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i] != null)
                        Destroy(entries[i]);
                }
                entries.Clear();
            }

            private void RebuildDisplay(BasisNetworkModeration.PermissionSnapshot snapshot)
            {
                if (this == null || GroupsGroup == null || UsersGroup == null)
                    return;

                ClearEntries(_groupEntries);
                ClearEntries(_userEntries);

                // Show/hide admin controls
                if (AdminGroupRoot != null)
                {
                    AdminGroupRoot.SetActive(BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsEdit));
                }

                // Build group display
                GroupsGroup.SetDescription($"{snapshot.Groups.Count} group(s) on server.");
                foreach (var group in snapshot.Groups)
                {
                    PanelElementDescriptor entry =
                        PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, GroupsParent);
                    entry.SetTitle(group.Name);

                    string nodesStr = group.Nodes.Count > 0 ? string.Join(", ", group.Nodes) : "(none)";
                    string parentsStr = group.Parents.Count > 0 ? string.Join(", ", group.Parents) : "(none)";
                    entry.SetDescription($"Nodes: {nodesStr}\nInherits: {parentsStr}");

                    _groupEntries.Add(entry.gameObject);

                    // Admin: quick actions per group
                    if (BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsEdit))
                    {
                        PanelButton fillBtn = PanelButton.CreateNew(entry.ContentParent);
                        fillBtn.Descriptor.SetTitle("Select");
                        fillBtn.Descriptor.SetDescription("Fill this group name into the admin fields below.");
                        string groupName = group.Name;
                        fillBtn.OnClicked += () =>
                        {
                            SetFieldText(GroupNameField, groupName);
                            SetFieldText(AssignGroupField, groupName);
                            SetFieldText(NodeGroupField, groupName);
                            SetFieldText(ParentGroupField, groupName);
                        };
                        _groupEntries.Add(fillBtn.gameObject);
                    }
                }

                // Build user display
                UsersGroup.SetDescription($"{snapshot.Users.Count} user(s) with explicit entries.");
                foreach (var user in snapshot.Users)
                {
                    PanelElementDescriptor entry =
                        PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, UsersParent);

                    // Try to resolve display name from connected players
                    string displayName = ResolveDisplayName(user.Uuid);
                    entry.SetTitle(displayName != null ? $"{displayName} ({ShortenUuid(user.Uuid)})" : user.Uuid);

                    string groupsStr = user.Groups.Count > 0 ? string.Join(", ", user.Groups) : "(default)";
                    string nodesStr = user.Nodes.Count > 0 ? string.Join(", ", user.Nodes) : "(none)";
                    entry.SetDescription($"Groups: {groupsStr}\nNodes: {nodesStr}");

                    _userEntries.Add(entry.gameObject);

                    // Admin: fill UUID into assign field
                    if (BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsEdit))
                    {
                        PanelButton fillBtn = PanelButton.CreateNew(entry.ContentParent);
                        fillBtn.Descriptor.SetTitle("Select");
                        fillBtn.Descriptor.SetDescription("Fill this user's UUID into the admin fields below.");
                        string uuid = user.Uuid;
                        fillBtn.OnClicked += () =>
                        {
                            SetFieldText(AssignUuidField, uuid);
                        };
                        _userEntries.Add(fillBtn.gameObject);
                    }
                }

                // Force layout rebuild
                if (Container != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(Container);
            }

            private static string ResolveDisplayName(string uuid)
            {
                if (BasisNetworkPlayers.Players == null) return null;
                foreach (var player in BasisNetworkPlayers.Players.Values)
                {
                    if (player.Player != null && player.Player.UUID == uuid)
                        return player.Player.SafeDisplayName;
                }
                return null;
            }

            private static string ShortenUuid(string uuid)
            {
                if (string.IsNullOrEmpty(uuid) || uuid.Length <= 12) return uuid;
                return uuid.Substring(0, 6) + "..." + uuid.Substring(uuid.Length - 4);
            }

            private static void SetFieldText(PanelTextField field, string text)
            {
                if (field == null) return;
                TMP_InputField input = field.GetComponentInChildren<TMP_InputField>(true);
                if (input != null) input.SetTextWithoutNotify(text);
            }

            public string GetGroupNameText() => GetFieldText(GroupNameField);
            public string GetAssignUuidText() => GetFieldText(AssignUuidField);
            public string GetAssignGroupText() => GetFieldText(AssignGroupField);
            public string GetNodeGroupText() => GetFieldText(NodeGroupField);
            public string GetNodeValueText() => GetFieldText(NodeValueField);
            public string GetParentGroupText() => GetFieldText(ParentGroupField);
            public string GetParentNameText() => GetFieldText(ParentNameField);

            private static string GetFieldText(PanelTextField field)
            {
                if (field == null) return string.Empty;
                TMP_InputField input = field.GetComponentInChildren<TMP_InputField>(true);
                return input != null ? input.text : string.Empty;
            }
        }
    }
}
