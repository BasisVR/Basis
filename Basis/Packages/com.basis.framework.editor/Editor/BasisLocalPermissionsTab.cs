#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Editor.Localization;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using UnityEditor;
using UnityEngine;
using static SerializableBasis;

public sealed class BasisLocalPermissionsTab : BasisEditorTabPage
{
    public override string Title => BasisEditorLocalization.Get("editor.localPermissions.title");
    public override string Subtitle =>
        BasisEditorLocalization.Get("editor.localPermissions.subtitle");

    private Vector2 _scroll;
    private string _filter = "";
    private string _checkNode = "";
    private bool _checkRan;
    private bool _checkResult;
    private bool _showBitsetSection = true;
    private bool _showExtrasSection = true;

    public override void OnEnable()
    {
        BasisNetworkManagement.OnlocalPermissionsChanged += OnPermissionsChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    public override void OnDisable()
    {
        BasisNetworkManagement.OnlocalPermissionsChanged -= OnPermissionsChanged;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
    }

    private void OnPermissionsChanged()
    {
        Host.Repaint();
    }

    private void OnPlayModeChanged(PlayModeStateChange state)
    {
        Host.Repaint();
    }

    public override void Draw()
    {
        if (!EditorApplication.isPlaying)
        {
            BasisEditorUI.Help(BasisEditorLocalization.Get("editor.localPermissions.enterPlayMode"), MessageType.Info);
            return;
        }

        HashSet<string> perms = BasisNetworkManagement.LocalPermissions;
        ServerMetaDataMessage smdm = BasisNetworkManagement.ServerMetaDataMessage;

        DrawHeader(perms);
        EditorGUILayout.Space(4);
        DrawQuickCheck(perms);
        EditorGUILayout.Space(4);
        DrawPermissionsList(perms, smdm);
    }

    private void DrawHeader(HashSet<string> perms)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            int total = perms != null ? perms.Count : 0;
            bool isAdmin = perms != null && perms.Contains("*");

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(BasisEditorLocalization.Get("editor.localPermissions.total", total), EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (isAdmin)
                {
                    var old = GUI.color;
                    GUI.color = new Color(1f, 0.9f, 0.5f);
                    GUILayout.Label(BasisEditorLocalization.Get("editor.localPermissions.admin"), EditorStyles.boldLabel);
                    GUI.color = old;
                }
            }

            _filter = EditorGUILayout.TextField(BasisEditorLocalization.Get("editor.localPermissions.filter"), _filter);
        }
    }

    private void DrawQuickCheck(HashSet<string> perms)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUILayout.Label(BasisEditorLocalization.Get("editor.localPermissions.permissionCheck"), EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _checkNode = EditorGUILayout.TextField(_checkNode);
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.localPermissions.check"), GUILayout.Width(60)))
                {
                    _checkRan = true;
                    _checkResult = HasPermission(perms, _checkNode);
                }
            }

            if (_checkRan)
            {
                EditorGUILayout.HelpBox(
                    _checkResult ? BasisEditorLocalization.Get("editor.localPermissions.allowed") : BasisEditorLocalization.Get("editor.localPermissions.denied"),
                    _checkResult ? MessageType.Info : MessageType.Warning);
            }
        }
    }

    private void DrawPermissionsList(HashSet<string> perms, ServerMetaDataMessage smdm)
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        // Bitset (known) permissions
        HashSet<string> bitsetPerms = null;
        HashSet<string> extraPerms = null;

        if (smdm.PermissionsBitset != null && smdm.PermissionsBitset.Length > 0)
        {
            bitsetPerms = PermissionBitsetMap.Decode(smdm.PermissionsBitset, null);
        }

        if (smdm.ExtraPermissions != null && smdm.ExtraPermissions.Length > 0)
        {
            extraPerms = new HashSet<string>(smdm.ExtraPermissions, StringComparer.OrdinalIgnoreCase);
        }

        bool hasBitset = bitsetPerms != null && bitsetPerms.Count > 0;
        bool hasExtras = extraPerms != null && extraPerms.Count > 0;

        if (hasBitset)
        {
            int bitsetCount = bitsetPerms.Count;
            _showBitsetSection = EditorGUILayout.Foldout(_showBitsetSection,
                BasisEditorLocalization.Get("editor.localPermissions.knownPermissions", bitsetCount, smdm.PermissionsBitset.Length), true);

            if (_showBitsetSection)
            {
                EditorGUI.indentLevel++;
                foreach (string node in bitsetPerms.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!PassesFilter(node)) continue;
                    DrawPermissionRow(node);
                }
                EditorGUI.indentLevel--;
            }
        }

        if (hasExtras)
        {
            _showExtrasSection = EditorGUILayout.Foldout(_showExtrasSection,
                BasisEditorLocalization.Get("editor.localPermissions.extraPermissions", extraPerms.Count), true);

            if (_showExtrasSection)
            {
                EditorGUI.indentLevel++;
                foreach (string node in extraPerms.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!PassesFilter(node)) continue;
                    DrawPermissionRow(node);
                }
                EditorGUI.indentLevel--;
            }
        }

        if (!hasBitset && !hasExtras)
        {
            // Flat list fallback
            if (perms == null || perms.Count == 0)
            {
                BasisEditorUI.Help(BasisEditorLocalization.Get("editor.localPermissions.noneReceived"), MessageType.Info);
            }
            else
            {
                foreach (string node in perms.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!PassesFilter(node)) continue;
                    DrawPermissionRow(node);
                }
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawPermissionRow(string node)
    {
        var old = GUI.color;
        GUI.color = node == "*" ? new Color(1f, 0.9f, 0.5f) : new Color(0.8f, 1f, 0.8f);
        EditorGUILayout.LabelField(node, EditorStyles.textField);
        GUI.color = old;
    }

    private bool PassesFilter(string node)
    {
        return string.IsNullOrWhiteSpace(_filter) ||
               node.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Client-side permission check with wildcard climbing (mirrors EffectivePermissions.Has).
    /// </summary>
    private static bool HasPermission(HashSet<string> perms, string node)
    {
        if (perms == null || string.IsNullOrWhiteSpace(node)) return false;

        node = node.Trim();
        if (perms.Contains(node)) return true;

        // Climb wildcards: a.b.c -> a.b.* -> a.* -> *
        int idx = node.Length;
        while (true)
        {
            idx = node.LastIndexOf('.', idx - 1);
            if (idx < 0) break;
            if (perms.Contains(node.Substring(0, idx) + ".*")) return true;
        }

        return perms.Contains("*");
    }
}
#endif
