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

            // Status / info section (collapsible)
            PanelSectionToggle statusToggle = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor statusGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                statusToggle, container, BasisLocalization.Get("settings.permissions.status"), false);

            PanelButton refreshBtn = PanelButton.CreateNew(statusGroup.ContentParent);
            refreshBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.refresh"));
            refreshBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.refresh.tooltip"));
            refreshBtn.OnClicked += () => BasisNetworkModeration.RequestPermissions();

            controller.StatusGroup = statusGroup;
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(statusToggle, statusGroup, false, null);

            // Groups section (collapsible; populated after data arrives)
            PanelSectionToggle groupsToggle = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor groupsGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                groupsToggle, container, BasisLocalization.Get("menu.individualPlayer.groups"), false);
            controller.GroupsParent = groupsGroup.ContentParent;
            controller.GroupsGroup = groupsGroup;
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(groupsToggle, groupsGroup, false, null);

            // Users section (collapsible; populated after data arrives)
            PanelSectionToggle usersToggle = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor usersGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                usersToggle, container, BasisLocalization.Get("settings.permissions.users"), false);
            controller.UsersParent = usersGroup.ContentParent;
            controller.UsersGroup = usersGroup;
            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(usersToggle, usersGroup, false, null);

            // Admin-only: create/delete group controls (collapsible). The whole bar is
            // hidden for non-admins via AdminGroupRoot below.
            PanelSectionToggle adminToggle = PanelSectionToggle.CreateNewEntry(container);
            PanelElementDescriptor adminGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                adminToggle, container, BasisLocalization.Get("settings.permissions.adminActions"), false);

            PanelTextField newGroupField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            newGroupField.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.groupName"));
            newGroupField.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.groupName.tooltip"));
            controller.GroupNameField = newGroupField;

            PanelButton createGroupBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            createGroupBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.createGroup"));
            createGroupBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.createGroup.tooltip"));
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
            deleteGroupBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.deleteGroup.tooltip"));
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
            assignUuidField.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.userUuid.tooltip"));
            controller.AssignUuidField = assignUuidField;

            PanelTextField assignGroupField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            assignGroupField.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.targetGroup"));
            assignGroupField.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.targetGroup.tooltip"));
            controller.AssignGroupField = assignGroupField;

            PanelButton addToGroupBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            addToGroupBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.addUserToGroup"));
            addToGroupBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.addUserToGroup.tooltip"));
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
            removeFromGroupBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.removeUserFromGroup.tooltip"));
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
            nodeGroupField.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.nodeGroup.tooltip"));
            controller.NodeGroupField = nodeGroupField;

            PanelTextField nodeValueField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            nodeValueField.Descriptor.SetTitle(BasisLocalization.Get("menu.individualPlayer.permissionNode"));
            nodeValueField.Descriptor.SetTooltip(BasisLocalization.Get("menu.individualPlayer.permissionNode.tooltip"));
            controller.NodeValueField = nodeValueField;

            PanelButton addNodeBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            addNodeBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.addNodeToGroup"));
            addNodeBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.addNodeToGroup.tooltip"));
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
            removeNodeBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.removeNodeFromGroup.tooltip"));
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
            parentGroupField.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.childGroup.tooltip"));
            controller.ParentGroupField = parentGroupField;

            PanelTextField parentNameField = PanelTextField.CreateNewEntry(adminGroup.ContentParent);
            parentNameField.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.parentGroup"));
            parentNameField.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.parentGroup.tooltip"));
            controller.ParentNameField = parentNameField;

            PanelButton addParentBtn = PanelButton.CreateNew(adminGroup.ContentParent);
            addParentBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.permissions.addParentToGroup"));
            addParentBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.addParentToGroup.tooltip"));
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
            removeParentBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.permissions.removeParentFromGroup.tooltip"));
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
            // Gate the whole section bar (not just the group) so non-admins don't see an
            // empty "Admin Actions" bar.
            controller.AdminGroupRoot = adminToggle.gameObject;

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(adminToggle, adminGroup, false, null);

            // Initially hide the admin section until we know if the user is admin.
            adminToggle.gameObject.SetActive(false);
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
                BasisNetworkModeration.OnPermissionsReceived -= OnPermissionsReceived;
                BasisNetworkModeration.OnPermissionsReceived += OnPermissionsReceived;
                SettingsProviderAdminTab.OnPlayerUuidSelected -= OnPlayerUuidSelected;
                SettingsProviderAdminTab.OnPlayerUuidSelected += OnPlayerUuidSelected;

                if (BasisNetworkModeration.LastPermissionSnapshot != null)
                    RebuildDisplay(BasisNetworkModeration.LastPermissionSnapshot);
                BasisNetworkModeration.RequestPermissions();
            }

            private void OnDisable()
            {
                BasisNetworkModeration.OnPermissionsReceived -= OnPermissionsReceived;
                SettingsProviderAdminTab.OnPlayerUuidSelected -= OnPlayerUuidSelected;
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

                // Build group display. Instantiate while the section is active so each entry's
                // Awake runs now and keeps the title we set below; a collapsed (inactive) section
                // defers Awake until expand, which re-applies the prefab's default ("Press"/blank).
                GroupsGroup.SetDescription(BasisLocalization.Get("settings.perm.groupsOnServer", snapshot.Groups.Count));
                bool groupsWasActive = GroupsGroup.gameObject.activeSelf;
                GroupsGroup.gameObject.SetActive(true);
                foreach (var group in snapshot.Groups)
                {
                    PanelElementDescriptor entry =
                        PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, GroupsParent);
                    entry.SetTitle(group.Name);

                    string nodesStr = group.Nodes.Count > 0 ? string.Join(", ", group.Nodes) : BasisLocalization.Get("settings.perm.none");
                    string parentsStr = group.Parents.Count > 0 ? string.Join(", ", group.Parents) : BasisLocalization.Get("settings.perm.none");
                    entry.SetDescription(BasisLocalization.Get("settings.perm.groupEntry", nodesStr, parentsStr));

                    _groupEntries.Add(entry.gameObject);

                    // Admin: quick actions per group
                    if (BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsEdit))
                    {
                        PanelButton fillBtn = PanelButton.CreateNew(entry.ContentParent);
                        fillBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.perm.title.select"));
                        fillBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.perm.title.select.group.description"));
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

                GroupsGroup.gameObject.SetActive(groupsWasActive);

                // Build user display (same active-while-building requirement as groups above).
                UsersGroup.SetDescription(BasisLocalization.Get("settings.perm.usersWithEntries", snapshot.Users.Count));
                bool usersWasActive = UsersGroup.gameObject.activeSelf;
                UsersGroup.gameObject.SetActive(true);
                foreach (var user in snapshot.Users)
                {
                    PanelElementDescriptor entry =
                        PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, UsersParent);

                    // Try to resolve display name from connected players
                    string displayName = ResolveDisplayName(user.Uuid);
                    entry.SetTitle(displayName != null ? $"{displayName} ({ShortenUuid(user.Uuid)})" : user.Uuid);

                    string groupsStr = user.Groups.Count > 0 ? string.Join(", ", user.Groups) : BasisLocalization.Get("settings.perm.default");
                    string nodesStr = user.Nodes.Count > 0 ? string.Join(", ", user.Nodes) : BasisLocalization.Get("settings.perm.none");
                    entry.SetDescription(BasisLocalization.Get("settings.perm.userEntry", groupsStr, nodesStr));

                    _userEntries.Add(entry.gameObject);

                    // Admin: fill UUID into assign field
                    if (BasisNetworkManagement.LocalPermissions.Contains(PermNodes.PermissionsEdit))
                    {
                        PanelButton fillBtn = PanelButton.CreateNew(entry.ContentParent);
                        fillBtn.Descriptor.SetTitle(BasisLocalization.Get("settings.perm.title.select"));
                        fillBtn.Descriptor.SetTooltip(BasisLocalization.Get("settings.perm.title.select.user.description"));
                        string uuid = user.Uuid;
                        fillBtn.OnClicked += () =>
                        {
                            SetFieldText(AssignUuidField, uuid);
                        };
                        _userEntries.Add(fillBtn.gameObject);
                    }
                }

                UsersGroup.gameObject.SetActive(usersWasActive);

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
