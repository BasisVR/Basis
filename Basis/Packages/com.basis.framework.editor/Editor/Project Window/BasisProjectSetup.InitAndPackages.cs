#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System;
using UnityEditor.PackageManager;

public partial class BasisProjectSetup : EditorWindow
{
    // Auto-open once for brand new users
    [InitializeOnLoadMethod]
    private static void AutoShowOnFirstUse()
    {
        if (!EditorPrefs.GetBool(PREF_HAS_OPENED, false))
        {
            EditorApplication.update += OpenOnceOnLoad;
            SessionState.SetBool(SESSION_SHOW_FIRST_NOTICE, true);
            SessionState.SetBool(SESSION_NEED_MODULE_RECHECK, true);
        }
    }

    private static void OpenOnceOnLoad()
    {
        EditorApplication.update -= OpenOnceOnLoad;
        ShowWindow();
    }
    // =======================
    // Linux + Meta XR removal helpers
    // =======================
    private void BeginPackageScanIfNeeded()
    {
        if (Application.platform != RuntimePlatform.LinuxEditor)
        {
            _metaXrInstalled = false; // irrelevant on non-Linux
            return;
        }

        if (_metaXrInstalled.HasValue) return;

        try
        {
            _pkgStatus = "Scanning packages…";
            _pkgListReq = Client.List(true);
        }
        catch (Exception ex)
        {
            _pkgStatus = "Package scan failed: " + ex.Message;
            _metaXrInstalled = null;
        }
    }

    private void PollPackageOperations()
    {
        // List in progress?
        if (_pkgListReq != null && _pkgListReq.IsCompleted)
        {
            if (_pkgListReq.Status == StatusCode.Success)
            {
                bool found = false;
                foreach (var p in _pkgListReq.Result)
                {
                    if (string.Equals(p.name, META_XR_CORE_PKG, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                _metaXrInstalled = found;
                _pkgStatus = found ? "Detected com.meta.xr.sdk.core" : "Meta XR Core not installed";
            }
            else
            {
                _metaXrInstalled = null;
                _pkgStatus = "Package scan error: " + _pkgListReq?.Error?.message;
            }

            _pkgListReq = null;
            Repaint();
        }

        // Remove in progress?
        if (_pkgRemoveReq != null && _pkgRemoveReq.IsCompleted)
        {
            if (_pkgRemoveReq.Status == StatusCode.Success)
            {
                _pkgStatus = "Removed com.meta.xr.sdk.core";
                _metaXrInstalled = false;
            }
            else
            {
                _pkgStatus = "Remove failed: " + _pkgRemoveReq?.Error?.message;
                _metaXrInstalled = null;
                BeginPackageScanIfNeeded();
            }

            _pkgRemoveReq = null;
            Repaint();
        }
    }

    private void DrawLinuxMetaXrNotice()
    {
        if (Application.platform != RuntimePlatform.LinuxEditor)
            return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Linux Compatibility Check", EditorStyles.boldLabel);

            var status = string.IsNullOrEmpty(_pkgStatus) ? "Ready" : _pkgStatus;
            EditorGUILayout.LabelField(status, EditorStyles.miniLabel);

            if (!_metaXrInstalled.HasValue)
            {
                EditorGUILayout.HelpBox(
                    "Scanning for the Meta XR Core package (com.meta.xr.sdk.core)… some Oculus/Meta SDKs are not supported on Linux and can break imports.",
                    MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Re-scan Packages")) { _metaXrInstalled = null; BeginPackageScanIfNeeded(); }
                }
                return;
            }

            if (_metaXrInstalled.Value)
            {
                EditorGUILayout.HelpBox(
                    "You’re on Linux and the project contains Meta XR Core (com.meta.xr.sdk.core).\n" +
                    "This package is not supported in Linux Editor and commonly causes import/compile issues.\n" +
                    "It’s recommended to remove it here, then re-add it later from Windows if needed.",
                    MessageType.Warning);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = _pkgRemoveReq == null;
                    if (GUILayout.Button("Remove com.meta.xr.sdk.core"))
                    {
                        try
                        {
                            _pkgStatus = "Removing com.meta.xr.sdk.core…";
                            _pkgRemoveReq = Client.Remove(META_XR_CORE_PKG);
                        }
                        catch (Exception ex)
                        {
                            _pkgStatus = "Remove start failed: " + ex.Message;
                        }
                    }
                    GUI.enabled = true;

                    if (GUILayout.Button("Re-scan Packages"))
                    {
                        _metaXrInstalled = null;
                        BeginPackageScanIfNeeded();
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Linux check: Meta XR Core (com.meta.xr.sdk.core) is not installed. You’re good.",
                    MessageType.None);

                if (GUILayout.Button("Re-scan Packages"))
                {
                    _metaXrInstalled = null;
                    BeginPackageScanIfNeeded();
                }
            }
        }
    }

    // Logo + scene asset helpers
    private void LoadLogoIfNeeded()
    {
#if UNITY_EDITOR
        if (_basisLogo != null) return;

        _basisLogo = AssetDatabase.LoadAssetAtPath<Texture2D>(BASIS_LOGO_PATH);

        if (_basisLogo == null)
        {
            var icon = EditorGUIUtility.IconContent("d_UnityLogo");
            _basisLogo = icon?.image as Texture2D;
        }
#endif
    }

    private static SceneAsset LoadSceneAsset(string path)
    {
        return AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
    }

    private static bool ScenePathExists(string path)
    {
        return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path));
    }
}
#endif
