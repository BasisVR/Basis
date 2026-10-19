#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Basis.Editor.Localization;
using BasisPermissions;
using static BasisPermissions.PermissionManager;

public sealed class BasisServerPermissionsTab : BasisEditorTabPage
{
    public override string Title => BasisEditorLocalization.Get("editor.serverPermissions.title");
    public override string Subtitle =>
        BasisEditorLocalization.Get("editor.serverPermissions.subtitle");

    // -------- UI state --------
    private Vector2 _leftScroll;
    private Vector2 _rightScroll;

    private enum Tab { Users, Groups }
    private Tab _tab = Tab.Users;

    // Selection
    private string _selectedUserUuid = "";
    private string _selectedGroupName = "";

    // Inputs
    private string _xmlPath = "permissions.xml";

    private string _newUserUuid = "";
    private string _newGroupName = "";

    private string _newUserNode = "";
    private string _newUserGroup = "";

    private string _newGroupNode = "";
    private string _newGroupParent = "";

    // Search/filter
    private string _userFilter = "";
    private string _groupFilter = "";

    // Effective perms foldouts
    private bool _showEffectiveAllowed = true;
    private bool _showEffectiveDenied = true;
    private string _effectiveFilter = "";

    private string _quickCheckNode = "";
    private bool _quickCheckResult = false;
    private bool _quickCheckRan = false;

    public override void OnEnable()
    {
        // Try to sync path from manager if already set
        try { _xmlPath = PermissionIntegration.Manager.GetXmlPath(); }
        catch { /* ignore */ }
    }

    public override void Draw()
    {
        DrawTopBar();

        EditorGUILayout.Space(6);

        using (new EditorGUILayout.HorizontalScope())
        {
            DrawLeftPane();
            GUILayout.Space(8);
            DrawRightPane();
        }
    }

    private void DrawTopBar()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _xmlPath = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.serverPermissions.xmlPath"), _xmlPath);

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.serverPermissions.browse"), GUILayout.Width(90)))
                {
                    BrowseForXml();
                }

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.serverPermissions.setPath"), GUILayout.Width(90)))
                {
                    SafeCall(() =>
                    {
                        var abs = ResolveToAbsolutePath(_xmlPath);
                        PermissionIntegration.Manager.SetXmlPath(abs);
                        Debug.Log($"[Permissions] Set path: {_xmlPath} -> {abs}");
                    });
                }

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.load"), GUILayout.Width(70)))
                {
                    SafeCall(() =>
                    {
                        var abs = ResolveToAbsolutePath(_xmlPath);
                        if (!EnsureFileExistsOrDialog(abs)) return;

                        PermissionIntegration.Manager.LoadFromXml(abs);
                        Debug.Log($"[Permissions] Loaded: {abs}");
                    });
                }

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.save"), GUILayout.Width(70)))
                {
                    SafeCall(() =>
                    {
                        var abs = ResolveToAbsolutePath(_xmlPath);

                        var dir = System.IO.Path.GetDirectoryName(abs);
                        if (!string.IsNullOrWhiteSpace(dir) && !System.IO.Directory.Exists(dir))
                            System.IO.Directory.CreateDirectory(dir);

                        PermissionIntegration.Manager.SaveToXml(abs);
                        Debug.Log($"[Permissions] Saved: {abs}");
                    });
                }

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.serverPermissions.saveDebounced"), GUILayout.Width(130)))
                {
                    SafeCall(() =>
                    {
                        // Debounced saver probably uses manager's internal path.
                        // So ensure it's set to a real absolute path first.
                        var abs = ResolveToAbsolutePath(_xmlPath);
                        PermissionIntegration.Manager.SetXmlPath(abs);
                        PermissionIntegration.Manager.SaveToXmlDebounced();
                        Debug.Log($"[Permissions] Save debounced (path={abs})");
                    });
                }

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.serverPermissions.ensureDefaults"), GUILayout.Width(120)))
                {
                    SafeCall(() => PermissionIntegration.Manager.EnsureDefaults());
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _tab = (Tab)GUILayout.Toolbar((int)_tab, new[] { BasisEditorLocalization.Get("editor.serverPermissions.users"), BasisEditorLocalization.Get("editor.serverPermissions.groups") }, GUILayout.Height(22));

                GUILayout.FlexibleSpace();

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.refresh"), GUILayout.Width(90)))
                {
                    Host.Repaint();
                }
            }
        }
    }

    // -------- PATH FIXES --------

    private static string ResolveToAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        path = path.Trim();

        // Already absolute?
        if (System.IO.Path.IsPathRooted(path))
            return System.IO.Path.GetFullPath(path);

        // If Assets-relative, resolve against project root.
        if (path.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
        {
            string projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, path));
        }

        // Otherwise treat as relative to project root (e.g. "permissions.xml")
        {
            string projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, path));
        }
    }

    private static bool EnsureFileExistsOrDialog(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.permissionsWindow.title"), BasisEditorLocalization.Get("editor.serverPermissions.noXmlPath"), BasisEditorLocalization.Get("editor.common.ok"));
            return false;
        }

        if (!System.IO.File.Exists(absolutePath))
        {
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.permissionsWindow.title"), BasisEditorLocalization.Get("editor.serverPermissions.xmlNotFound", absolutePath), BasisEditorLocalization.Get("editor.common.ok"));
            return false;
        }

        return true;
    }

    private void BrowseForXml()
    {
        // Start browsing from current value if possible, otherwise from Assets folder.
        string startDir = Application.dataPath;

        try
        {
            if (!string.IsNullOrWhiteSpace(_xmlPath))
            {
                var abs = ResolveToAbsolutePath(_xmlPath);
                if (System.IO.File.Exists(abs))
                    startDir = System.IO.Path.GetDirectoryName(abs);
            }
        }
        catch { /* ignore */ }

        string chosen = EditorUtility.OpenFilePanel(BasisEditorLocalization.Get("editor.serverPermissions.selectXml"), startDir, "xml");
        if (string.IsNullOrEmpty(chosen))
            return;

        if (!chosen.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.serverPermissions.invalidFile"), BasisEditorLocalization.Get("editor.serverPermissions.selectXmlFile"), BasisEditorLocalization.Get("editor.common.ok"));
            return;
        }

        // Store a nice relative path if inside project, otherwise absolute.
        string projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
        string fullChosen = System.IO.Path.GetFullPath(chosen);

        if (fullChosen.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            string rel = fullChosen.Substring(projectRoot.Length + 1).Replace("\\", "/");
            _xmlPath = rel;
        }
        else
        {
            _xmlPath = fullChosen;
        }

        // Always init using an absolute path so IO is unambiguous.
        var absInit = ResolveToAbsolutePath(_xmlPath);
        SafeCall(() =>
        {
            PermissionIntegration.Init(absInit);
            // Also set manager path so Load/Save/Debounced are consistent.
            PermissionIntegration.Manager.SetXmlPath(absInit);
        });

        Debug.Log($"[Permissions] Browse selected: {_xmlPath} -> {absInit}");

        GUI.FocusControl(null);
        Host.Repaint();
    }

    // -------- UI PANES --------

    private void DrawLeftPane()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(340)))
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_tab == Tab.Users) DrawUsersList();
                else DrawGroupsList();
            }
        }
    }

    private void DrawUsersList()
    {
        var snapshot = SafeSnapshot();

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.users"), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
        }

        _userFilter = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.serverPermissions.filter"), _userFilter);

        using (new EditorGUILayout.HorizontalScope())
        {
            _newUserUuid = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.serverPermissions.newUuid"), _newUserUuid);
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.create"), GUILayout.Width(80)))
            {
                var uuid = _newUserUuid?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(uuid))
                {
                    SafeCall(() => PermissionIntegration.Manager.GetOrCreateUser(uuid));
                    _selectedUserUuid = uuid;
                    _newUserUuid = "";
                }
            }
        }

        EditorGUILayout.Space(4);

        var uuids = snapshot.Users.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Where(u => string.IsNullOrWhiteSpace(_userFilter) || u.IndexOf(_userFilter, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll, GUILayout.Height(Host.position.height - 210));

        foreach (var uuid in uuids)
        {
            bool selected = string.Equals(uuid, _selectedUserUuid, StringComparison.OrdinalIgnoreCase);
            using (new EditorGUILayout.HorizontalScope())
            {
                var style = selected ? EditorStyles.toolbarButton : EditorStyles.miniButton;
                if (GUILayout.Button(uuid, style))
                {
                    _selectedUserUuid = uuid;
                    GUI.FocusControl(null);
                }
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawGroupsList()
    {
        var snapshot = SafeSnapshot();

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.groups"), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
        }

        _groupFilter = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.serverPermissions.filter"), _groupFilter);

        using (new EditorGUILayout.HorizontalScope())
        {
            _newGroupName = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.serverPermissions.newGroup"), _newGroupName);
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.create"), GUILayout.Width(80)))
            {
                var name = _newGroupName?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(name))
                {
                    SafeCall(() => PermissionIntegration.Manager.GetOrCreateGroup(name));
                    _selectedGroupName = name;
                    _newGroupName = "";
                }
            }
        }

        EditorGUILayout.Space(4);

        var names = snapshot.Groups.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Where(g => string.IsNullOrWhiteSpace(_groupFilter) || g.IndexOf(_groupFilter, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll, GUILayout.Height(Host.position.height - 210));

        foreach (var name in names)
        {
            bool selected = string.Equals(name, _selectedGroupName, StringComparison.OrdinalIgnoreCase);
            using (new EditorGUILayout.HorizontalScope())
            {
                var style = selected ? EditorStyles.toolbarButton : EditorStyles.miniButton;
                if (GUILayout.Button(name, style))
                {
                    _selectedGroupName = name;
                    GUI.FocusControl(null);
                }
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawRightPane()
    {
        using (new EditorGUILayout.VerticalScope())
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            if (_tab == Tab.Users) DrawUserDetails();
            else DrawGroupDetails();
        }
    }

    private void DrawUserDetails()
    {
        if (string.IsNullOrWhiteSpace(_selectedUserUuid))
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.selectUser"), EditorStyles.boldLabel);
            return;
        }

        if (!PermissionIntegration.Manager.TryGetUser(_selectedUserUuid, out var user))
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.userNotFound", _selectedUserUuid), EditorStyles.boldLabel);
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.user"), EditorStyles.boldLabel, GUILayout.Width(60));
            EditorGUILayout.SelectableLabel(user.Uuid, EditorStyles.textField, GUILayout.Height(18));
        }

        EditorGUILayout.Space(6);

        using (var scroll = new EditorGUILayout.ScrollViewScope(_rightScroll))
        {
            _rightScroll = scroll.scrollPosition;

            DrawUserGroups(user);
            EditorGUILayout.Space(10);
            DrawUserNodes(user);
            EditorGUILayout.Space(10);
            DrawEffectivePermissions(user.Uuid);
        }
    }

    private void DrawUserGroups(PermissionUser user)
    {
        GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.groups"), EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            _newUserGroup = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.serverPermissions.addToGroup"), _newUserGroup);
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.add"), GUILayout.Width(80)))
            {
                var g = _newUserGroup?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(g))
                {
                    SafeCall(() => PermissionIntegration.Manager.AddUserToGroup(user.Uuid, g));
                    _newUserGroup = "";
                }
            }
        }

        if (user.Groups.Count == 0)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.serverPermissions.noGroups"), MessageType.Info);
            return;
        }

        foreach (var g in user.Groups.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(g);
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.remove"), GUILayout.Width(80)))
                {
                    SafeCall(() => PermissionIntegration.Manager.RemoveUserFromGroup(user.Uuid, g));
                    GUI.FocusControl(null);
                    break;
                }
            }
        }
    }

    private void DrawUserNodes(PermissionUser user)
    {
        GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.userNodes"), EditorStyles.boldLabel);
        BasisEditorUI.Readout(BasisEditorLocalization.Get("editor.serverPermissions.denyHintUser"));

        using (new EditorGUILayout.HorizontalScope())
        {
            _newUserNode = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.serverPermissions.addNode"), _newUserNode);
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.add"), GUILayout.Width(80)))
            {
                var n = _newUserNode?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(n))
                {
                    SafeCall(() => PermissionIntegration.Manager.AddUserNode(user.Uuid, n));
                    _newUserNode = "";
                }
            }
        }

        if (user.Nodes.Count == 0)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.serverPermissions.noUserNodes"), MessageType.Info);
            return;
        }

        foreach (var n in user.Nodes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawNodePill(n);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.remove"), GUILayout.Width(80)))
                {
                    SafeCall(() => PermissionIntegration.Manager.RemoveUserNode(user.Uuid, n));
                    GUI.FocusControl(null);
                    break;
                }
            }
        }
    }

    private void DrawEffectivePermissions(string uuid)
    {
        GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.effectiveRules"), EditorStyles.boldLabel);
        _effectiveFilter = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.serverPermissions.filter"), _effectiveFilter);

        var allowed = SafeCallRet(() => PermissionIntegration.Manager.GetAllAllowedRules(uuid)) ?? Array.Empty<string>();
        var denied = SafeCallRet(() => PermissionIntegration.Manager.GetAllDeniedRules(uuid)) ?? Array.Empty<string>();

        bool Pass(string s) =>
            string.IsNullOrWhiteSpace(_effectiveFilter) ||
            s.IndexOf(_effectiveFilter, StringComparison.OrdinalIgnoreCase) >= 0;

        _showEffectiveAllowed = EditorGUILayout.Foldout(_showEffectiveAllowed, BasisEditorLocalization.Get("editor.serverPermissions.allowed", allowed.Count), true);
        if (_showEffectiveAllowed)
        {
            foreach (var a in allowed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Where(Pass))
                EditorGUILayout.LabelField(a);
        }

        EditorGUILayout.Space(4);

        _showEffectiveDenied = EditorGUILayout.Foldout(_showEffectiveDenied, BasisEditorLocalization.Get("editor.serverPermissions.denied", denied.Count), true);
        if (_showEffectiveDenied)
        {
            foreach (var d in denied.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Where(Pass))
                EditorGUILayout.LabelField(d);
        }

        EditorGUILayout.Space(6);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.checkNode"), GUILayout.Width(80));
            string test = EditorGUILayout.TextField("", _quickCheckNode);
            if (test != _quickCheckNode) _quickCheckNode = test;

            if (GUILayout.Button(BasisEditorLocalization.Get("editor.serverPermissions.has"), GUILayout.Width(60)))
            {
                _quickCheckResult = SafeCallRet(() => PermissionIntegration.Manager.Has(uuid, _quickCheckNode));
                _quickCheckRan = true;
            }
        }

        if (_quickCheckRan)
        {
            EditorGUILayout.HelpBox(
                _quickCheckResult ? BasisEditorLocalization.Get("editor.serverPermissions.allowResult") : BasisEditorLocalization.Get("editor.serverPermissions.denyResult"),
                _quickCheckResult ? MessageType.Info : MessageType.Warning);
        }
    }

    private void DrawGroupDetails()
    {
        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.selectGroup"), EditorStyles.boldLabel);
            return;
        }

        if (!PermissionIntegration.Manager.TryGetGroup(_selectedGroupName, out var group))
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.groupNotFound", _selectedGroupName), EditorStyles.boldLabel);
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.group"), EditorStyles.boldLabel, GUILayout.Width(60));
            EditorGUILayout.SelectableLabel(group.Name, EditorStyles.textField, GUILayout.Height(18));
        }

        EditorGUILayout.Space(6);

        using (var scroll = new EditorGUILayout.ScrollViewScope(_rightScroll))
        {
            _rightScroll = scroll.scrollPosition;

            DrawGroupParents(group);
            EditorGUILayout.Space(10);
            DrawGroupNodes(group);
        }
    }

    private void DrawGroupParents(PermissionGroup group)
    {
        GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.parents"), EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            _newGroupParent = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.serverPermissions.addParent"), _newGroupParent);
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.add"), GUILayout.Width(80)))
            {
                var p = _newGroupParent?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(p))
                {
                    SafeCall(() => PermissionIntegration.Manager.AddGroupParent(group.Name, p));
                    _newGroupParent = "";
                }
            }
        }

        if (group.Parents.Count == 0)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.serverPermissions.noParents"), MessageType.Info);
            return;
        }

        foreach (var p in group.Parents.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(p);
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.remove"), GUILayout.Width(80)))
                {
                    SafeCall(() => PermissionIntegration.Manager.RemoveGroupParent(group.Name, p));
                    GUI.FocusControl(null);
                    break;
                }
            }
        }
    }

    private void DrawGroupNodes(PermissionGroup group)
    {
        GUILayout.Label(BasisEditorLocalization.Get("editor.serverPermissions.groupNodes"), EditorStyles.boldLabel);
        BasisEditorUI.Readout(BasisEditorLocalization.Get("editor.serverPermissions.denyHintGroup"));

        using (new EditorGUILayout.HorizontalScope())
        {
            _newGroupNode = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.serverPermissions.addNode"), _newGroupNode);
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.add"), GUILayout.Width(80)))
            {
                var n = _newGroupNode?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(n))
                {
                    SafeCall(() => PermissionIntegration.Manager.AddGroupNode(group.Name, n));
                    _newGroupNode = "";
                }
            }
        }

        if (group.Nodes.Count == 0)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.serverPermissions.noGroupNodes"), MessageType.Info);
            return;
        }

        foreach (var n in group.Nodes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawNodePill(n);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.remove"), GUILayout.Width(80)))
                {
                    SafeCall(() => PermissionIntegration.Manager.RemoveGroupNode(group.Name, n));
                    GUI.FocusControl(null);
                    break;
                }
            }
        }
    }

    private static void DrawNodePill(string node)
    {
        bool deny = node.StartsWith("-", StringComparison.Ordinal);
        var old = GUI.color;
        if (deny) GUI.color = new Color(1f, 0.8f, 0.8f);
        else GUI.color = new Color(0.8f, 1f, 0.8f);

        GUILayout.Label(node, EditorStyles.textField);
        GUI.color = old;
    }

    // -------- helpers --------

    private static PermissionStore SafeSnapshot()
    {
        try { return PermissionIntegration.Manager.Snapshot(); }
        catch (Exception e)
        {
            Debug.LogError(e); // IMPORTANT: don't hide the error
            return new PermissionStore();
        }
    }

    private static void SafeCall(Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            Debug.LogError(e);
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.serverPermissions.errorTitle"), e.Message, BasisEditorLocalization.Get("editor.common.ok"));
        }
    }

    private static T SafeCallRet<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception e)
        {
            Debug.LogError(e);
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.serverPermissions.errorTitle"), e.Message, BasisEditorLocalization.Get("editor.common.ok"));
            return default;
        }
    }
}
#endif
