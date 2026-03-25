#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using UnityEditor;
using UnityEngine;
using static SerializableBasis;

public sealed class LocalPermissionsEditorWindow : EditorWindow
{
    private Vector2 _scroll;
    private string _filter = "";
    private string _checkNode = "";
    private bool _checkRan;
    private bool _checkResult;
    private bool _showBitsetSection = true;
    private bool _showExtrasSection = true;

    [MenuItem("Basis/Local Permissions")]
    public static void Open()
    {
        var w = GetWindow<LocalPermissionsEditorWindow>("Local Permissions");
        w.minSize = new Vector2(400, 300);
        w.Show();
    }

    private void OnEnable()
    {
        BasisNetworkManagement.OnlocalPermissionsChanged += OnPermissionsChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private void OnDisable()
    {
        BasisNetworkManagement.OnlocalPermissionsChanged -= OnPermissionsChanged;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
    }

    private void OnPermissionsChanged()
    {
        Repaint();
    }

    private void OnPlayModeChanged(PlayModeStateChange state)
    {
        Repaint();
    }

    private void OnGUI()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode and connect to a server to see local permissions.", MessageType.Info);
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
                GUILayout.Label($"Permissions: {total}", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (isAdmin)
                {
                    var old = GUI.color;
                    GUI.color = new Color(1f, 0.9f, 0.5f);
                    GUILayout.Label("ADMIN (*)", EditorStyles.boldLabel);
                    GUI.color = old;
                }
            }

            _filter = EditorGUILayout.TextField("Filter", _filter);
        }
    }

    private void DrawQuickCheck(HashSet<string> perms)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUILayout.Label("Permission Check", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _checkNode = EditorGUILayout.TextField(_checkNode);
                if (GUILayout.Button("Check", GUILayout.Width(60)))
                {
                    _checkRan = true;
                    _checkResult = HasPermission(perms, _checkNode);
                }
            }

            if (_checkRan)
            {
                EditorGUILayout.HelpBox(
                    _checkResult ? "ALLOWED" : "DENIED",
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
                $"Known Permissions ({bitsetCount}) - {smdm.PermissionsBitset.Length} bytes on wire", true);

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
                $"Extra Permissions ({extraPerms.Count})", true);

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
                EditorGUILayout.HelpBox("No permissions received from server.", MessageType.Info);
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
