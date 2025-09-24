#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System;
using System.Reflection;

// NEW: package manager APIs
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

public class BasisPlatformSwitcher : EditorWindow
{
    private enum PlatformChoice { Windows, Linux, Android }
    private enum FirstRunKind { None = 0, Avatar = 1, World = 2, Project = 3 }

    // EditorPrefs keys
    private const string PREF_LAST_PLATFORM = "BasisPlatformSwitcher_LastPlatform";
    private const string PREF_HAS_OPENED = "BasisPlatformSwitcher_HasOpened";
    private const string PREF_FIRST_RUN_KIND = "BasisPlatformSwitcher_FirstRunKind";

    // SessionState keys
    private const string SESSION_SHOW_FIRST_NOTICE = "BasisPlatformSwitcher_ShowFirstNotice";
    private const string SESSION_NEED_MODULE_RECHECK = "BasisPlatformSwitcher_NeedModuleRecheck";

    // Links
    private const string BASIS_SITE = "https://basisvr.org/";
    private const string BASIS_DOCS = "https://docs.basisvr.org/";
    private const string BASIS_GETTING_STARTED = "https://docs.basisvr.org/docs/getting-started/";
    private const string BASIS_AVATARS = "https://docs.basisvr.org/docs/avatars/";
    private const string BASIS_WORLDS = "https://docs.basisvr.org/docs/worlds/";
    private const string BASIS_DONATE = "https://opencollective.com/basis";
    private const string UNITY_HUB_ADD_MODULES = "https://docs.unity3d.com/hub/manual/AddModules.html";

    // NEW: Package id we want gone on Linux
    private const string META_XR_CORE_PKG = "com.meta.xr.sdk.core";

    // --- Logo (Packages path) ---
    private const string BASIS_LOGO_PATH = "Packages/com.basis.sdk/Textures/BasisLogoTemp.png";
    private Texture2D _basisLogo;

    // UI state
    private PlatformChoice _choice;
    private bool _showFirstRunNotice;
    private FirstRunKind _firstRunKind;

    // Enforce IL2CPP when applying
    private bool _enforceIl2cpp = true;

    // Cached module checks (session only)
    private bool? _hasWin;
    private bool? _hasLinux;
    private bool? _hasAndroid;

    private bool? _hasIl2cppStandalone; // Windows/Linux share Standalone group for backend availability
    private bool? _hasIl2cppAndroid;

    // Quality presets (1=Desktop, 2=Quest/Android, 3=Headless)
    private const int QUALITY_DESKTOP = 1;
    private const int QUALITY_ANDROID = 2;

    // NEW: package manager state
    private bool? _metaXrInstalled;        // null = unknown, true/false = known
    private ListRequest _pkgListReq;        // scanning
    private RemoveRequest _pkgRemoveReq;    // removing
    private string _pkgStatus;              // short status string

    [MenuItem("Basis/ProjectSetup")]
    public static void ShowWindow()
    {
        var window = GetWindow<BasisPlatformSwitcher>("Basis Project Setup");
        window.minSize = new Vector2(560, 500);
        window.Show();
    }

    // --- Auto-open once for brand new users ---
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

    private void OnEnable()
    {
        _choice = (PlatformChoice)EditorPrefs.GetInt(PREF_LAST_PLATFORM, (int)PlatformChoice.Windows);

        if (!EditorPrefs.GetBool(PREF_HAS_OPENED, false))
            EditorPrefs.SetBool(PREF_HAS_OPENED, true);

        _showFirstRunNotice = SessionState.GetBool(SESSION_SHOW_FIRST_NOTICE, false);
        if (_showFirstRunNotice) SessionState.EraseInt(SESSION_SHOW_FIRST_NOTICE);

        _firstRunKind = (FirstRunKind)EditorPrefs.GetInt(PREF_FIRST_RUN_KIND, (int)FirstRunKind.None);

        if (SessionState.GetBool(SESSION_NEED_MODULE_RECHECK, true))
        {
            RecheckBuildModulesAndBackends();
            SessionState.SetBool(SESSION_NEED_MODULE_RECHECK, false);
        }

        // NEW: kick off a package scan on Linux
        BeginPackageScanIfNeeded();
        EditorApplication.update += PollPackageOperations;

        // NEW: cache logo
        LoadLogoIfNeeded();
    }

    private void OnDisable()
    {
        EditorApplication.update -= PollPackageOperations;
    }

    private void OnGUI()
    {
        EditorGUILayout.Space();
        DrawHeader();
        EditorGUILayout.Space(8);

        if (_showFirstRunNotice)
        {
            EditorGUILayout.HelpBox(
                "First time here! Choose what you’re setting up, verify build modules (including IL2CPP), " +
                "and pick your target platform before building or pressing Play.",
                MessageType.Warning);
        }

        // === Documentation (FIRST) + First-run selector ===
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Documentation", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Jump straight into the right docs.", EditorStyles.wordWrappedLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Avatar Docs")) Application.OpenURL(BASIS_AVATARS);
                if (GUILayout.Button("World Docs")) Application.OpenURL(BASIS_WORLDS);
                if (GUILayout.Button("Project – Getting Started")) Application.OpenURL(BASIS_GETTING_STARTED);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("What are you setting up today?", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawFirstRunRadio(FirstRunKind.Avatar, "Avatar");
                DrawFirstRunRadio(FirstRunKind.World, "World");
                DrawFirstRunRadio(FirstRunKind.Project, "Project");
            }

            if (GUI.changed)
                EditorPrefs.SetInt(PREF_FIRST_RUN_KIND, (int)_firstRunKind);

            if (_firstRunKind == FirstRunKind.Avatar || _firstRunKind == FirstRunKind.World)
            {
                EditorGUILayout.HelpBox(
                    "For Avatars/Worlds you should install Windows, Linux, and Android Build Support via Unity Hub.\n" +
                    "Use IL2CPP for best compatibility/performance (required on Android).",
                    MessageType.Info);
            }
        }

        EditorGUILayout.Space();

        // === LINUX-ONLY NOTICE: Meta XR Core removal ===
        DrawLinuxMetaXrNotice();

        // === Build Module + IL2CPP Check ===
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Build Targets / Modules / IL2CPP", EditorStyles.boldLabel);

            if (!_hasWin.HasValue || !_hasLinux.HasValue || !_hasAndroid.HasValue || !_hasIl2cppStandalone.HasValue || !_hasIl2cppAndroid.HasValue)
                RecheckBuildModulesAndBackendsRow();
            else
                DrawModuleAndBackendStatusRow();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Build Settings")) EditorWindow.GetWindow(typeof(BuildPlayerWindow));
#if UNITY_2021_2_OR_NEWER
                if (EditorGUILayout.LinkButton("How to add modules in Unity Hub")) Application.OpenURL(UNITY_HUB_ADD_MODULES);
#else
                if (GUILayout.Button("How to add modules in Unity Hub", EditorStyles.linkLabel)) Application.OpenURL(UNITY_HUB_ADD_MODULES);
#endif
            }
        }

        EditorGUILayout.Space();

        // === Platform & Quality ===
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Platform & Quality", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPlatformRadio(PlatformChoice.Windows, "Windows (Standalone)");
                DrawPlatformRadio(PlatformChoice.Linux, "Linux (Standalone)");
                DrawPlatformRadio(PlatformChoice.Android, "Android (Quest)");
            }

            _enforceIl2cpp = EditorGUILayout.ToggleLeft(
                "Enforce IL2CPP scripting backend when applying",
                _enforceIl2cpp);

            EditorGUILayout.HelpBox("Quality presets: 1 = Desktop (Windows/Linux), 2 = Android/Quest", MessageType.None);

            bool modulesOk = AreRequiredModulesOkForCurrentSelection();
            using (new EditorGUI.DisabledScope(!modulesOk && _enforceIl2cpp))
            {
                if (GUILayout.Button(modulesOk ? "Apply & Switch Platform" : "Apply & Switch Platform (modules missing)"))
                {
                    if (!modulesOk && _enforceIl2cpp)
                    {
                        EditorUtility.DisplayDialog(
                            "Missing Modules / IL2CPP",
                            "Required build modules or IL2CPP are missing for the current selection. See the warnings above.",
                            "Got it");
                    }
                    else
                    {
                        ApplyPlatformAndQuality(_choice, _enforceIl2cpp);
                        _showFirstRunNotice = false;
                    }
                }
            }
        }

        EditorGUILayout.Space();

        // === About Basis ===
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("About Basis", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Creator-First, Creative Freedom — Basis lets you set up your own VR games with ease.\n" +
                "Open-Source (MIT). Designed for creators. Strong systems for networking, user input, and user presence.",
                EditorStyles.wordWrappedLabel);
        }

        EditorGUILayout.Space();

        // === Funding / Donations ===
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("How We Are Funded", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "BasisVR is sustained by a mix of community donations and collaborations with companies who build on the framework. " +
                "Funds are pooled to tackle shared problems (e.g., networking, embodiment, tooling) that benefit everyone.",
                EditorStyles.wordWrappedLabel);
#if UNITY_2021_2_OR_NEWER
            if (EditorGUILayout.LinkButton("Support Basis on Open Collective")) Application.OpenURL(BASIS_DONATE);
#else
            if (GUILayout.Button("Support Basis on Open Collective", EditorStyles.linkLabel)) Application.OpenURL(BASIS_DONATE);
#endif
        }

        GUILayout.FlexibleSpace();
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close")) Close();
        }

        if (GUI.changed)
        {
            EditorPrefs.SetInt(PREF_LAST_PLATFORM, (int)_choice);
        }
    }

    // === Validation helper: are required modules present for the current selection? ===
    private bool AreRequiredModulesOkForCurrentSelection()
    {
        // Bail if we haven’t checked yet
        if (!_hasWin.HasValue || !_hasLinux.HasValue || !_hasAndroid.HasValue ||
            !_hasIl2cppStandalone.HasValue || !_hasIl2cppAndroid.HasValue)
            return false;

        // Avatar/World setups require all three platforms + IL2CPP
        if (_firstRunKind == FirstRunKind.Avatar || _firstRunKind == FirstRunKind.World)
        {
            return _hasWin == true && _hasLinux == true && _hasAndroid == true
                && _hasIl2cppStandalone == true && _hasIl2cppAndroid == true;
        }

        // Project setups depend on which platform they’ve chosen in the radio buttons
        switch (_choice)
        {
            case PlatformChoice.Windows:
            case PlatformChoice.Linux:
                return _hasWin == true || _hasLinux == true
                    ? _hasIl2cppStandalone == true
                    : false;

            case PlatformChoice.Android:
                return _hasAndroid == true && _hasIl2cppAndroid == true;

            default:
                return false;
        }
    }

    // === Header ===
    private void DrawHeader()
    {
        // Height slightly bigger to give the logo breathing room
        var rect = GUILayoutUtility.GetRect(10, 86, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, new Color32(16, 15, 39, 255)); // #100f27
        var accent = new Rect(rect.x, rect.y, rect.width, 4);
        EditorGUI.DrawRect(accent, new Color32(239, 18, 55, 255)); // #ef1237

        // Safe padding and DPI scaling
        float ppp = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
        float pad = 12f;
        float logoSize = 56f * ppp; // renders crisply on HiDPI

        // Title/subtitle area leaves space for the logo on the right
        var title = new Rect(rect.x + pad, rect.y + 10, rect.width - (logoSize + pad * 2f), 28);
        var subtitle = new Rect(rect.x + pad, rect.y + 40, rect.width - (logoSize + pad * 2f), 40);

        var tStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 16,
            normal = { textColor = Color.white }
        };

        var subtitleColor = EditorGUIUtility.isProSkin
            ? new Color(0.85f, 0.85f, 0.9f)
            : new Color(0.15f, 0.15f, 0.2f);

        var sStyle = new GUIStyle(EditorStyles.label)
        {
            fontSize = 11,
            wordWrap = true,
            normal = { textColor = subtitleColor }
        };

        GUI.Label(title, "Basis Project Wizard", tStyle);
        GUI.Label(subtitle,
            "Creator-First • Creative Freedom\n" +
            "Open-Source (MIT) • Networking • Input • Presence",
            sStyle);

        // Draw the logo on the right; keep square ratio
        if (_basisLogo != null)
        {
            var logoRect = new Rect(
                rect.xMax - logoSize - pad,
                rect.y + (rect.height - logoSize) * 0.5f,
                logoSize,
                logoSize);

            // Slight inset border for pop without extra art
            var border = new Rect(logoRect.x - 2, logoRect.y - 2, logoRect.width + 4, logoRect.height + 4);
            EditorGUI.DrawRect(border, new Color(1, 1, 1, 0.05f));

            // Keep aspect & alpha
            GUI.DrawTexture(logoRect, _basisLogo, ScaleMode.ScaleToFit, true);
        }
    }

    // === Radios ===
    private void DrawFirstRunRadio(FirstRunKind value, string label)
    {
        var isSelected = _firstRunKind == value;
        if (GUILayout.Toggle(isSelected, label, EditorStyles.radioButton))
            _firstRunKind = value;
    }

    private void DrawPlatformRadio(PlatformChoice value, string label)
    {
        var isSelected = _choice == value;
        if (GUILayout.Toggle(isSelected, label, EditorStyles.radioButton))
            _choice = value;
    }

    // === Apply platform + quality (+ optional IL2CPP enforce) ===
    private void ApplyPlatformAndQuality(PlatformChoice choice, bool enforceIl2cpp)
    {
        BuildTargetGroup group;
        BuildTarget target;
        int desiredQuality;

        switch (choice)
        {
            case PlatformChoice.Android:
                group = BuildTargetGroup.Android;
                target = BuildTarget.Android;
                desiredQuality = QUALITY_ANDROID;
                break;

            case PlatformChoice.Linux:
                group = BuildTargetGroup.Standalone;
                target = BuildTarget.StandaloneLinux64;
                desiredQuality = QUALITY_DESKTOP;
                break;

            case PlatformChoice.Windows:
            default:
                group = BuildTargetGroup.Standalone;
                target = BuildTarget.StandaloneWindows64;
                desiredQuality = QUALITY_DESKTOP;
                break;
        }

#if UNITY_2021_2_OR_NEWER
        if (group == BuildTargetGroup.Standalone)
        {
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
        }
#endif

        // Optional: enforce IL2CPP (Android strongly recommended/required for shipping)
        if (enforceIl2cpp)
        {
            if (!SupportsIl2cpp(group))
            {
                EditorUtility.DisplayDialog(
                    "IL2CPP Not Available",
                    $"IL2CPP scripting backend is not available for {group}. " +
                    $"Install the appropriate *Build Support (IL2CPP)* module via Unity Hub.",
                    "OK");
                return;
            }

            try
            {
                SetScriptingBackendSafe(group, ScriptingImplementation.IL2CPP);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "Failed to Set IL2CPP",
                    "Tried to set IL2CPP but Unity reported an error:\n" + ex.Message,
                    "OK");
                return;
            }
        }

        if (EditorUserBuildSettings.activeBuildTarget != target)
            EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);

        SetQualitySafe(desiredQuality);

        var subSuffix = (group == BuildTargetGroup.Standalone);

        var backend = PlayerSettings.GetScriptingBackend(group);
        EditorUtility.DisplayDialog(
            "Platform Applied",
            $"Switched to: {group}/{target}{subSuffix}\n" +
            $"Quality: {desiredQuality}\n" +
            $"Scripting Backend: {backend}",
            "Nice");
    }

    private static void SetQualitySafe(int index)
    {
        index = Mathf.Clamp(index, 0, Mathf.Max(0, QualitySettings.names.Length - 1));
        QualitySettings.SetQualityLevel(index, true);
    }

    // === Build module + IL2CPP checks ===
    private void RecheckBuildModulesAndBackends()
    {
        // Module install checks
        _hasWin = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
        _hasLinux = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
        _hasAndroid = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);

        // IL2CPP availability checks
        _hasIl2cppStandalone = SupportsIl2cpp(BuildTargetGroup.Standalone);
        _hasIl2cppAndroid = SupportsIl2cpp(BuildTargetGroup.Android);
    }

    private void RecheckBuildModulesAndBackendsRow()
    {
        RecheckBuildModulesAndBackends();
        DrawModuleAndBackendStatusRow();
    }

    private void DrawModuleAndBackendStatusRow()
    {
        // Modules row
        EditorGUILayout.LabelField("Installed Build Modules:", EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        DrawBadge("Windows", _hasWin == true);
        DrawBadge("Linux", _hasLinux == true);
        DrawBadge("Android", _hasAndroid == true);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Re-check")) RecheckBuildModulesAndBackends();
        EditorGUILayout.EndHorizontal();

        // IL2CPP row
        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("IL2CPP Availability:", EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        DrawBadge("Standalone (Win/Linux)", _hasIl2cppStandalone == true);
        DrawBadge("Android", _hasIl2cppAndroid == true);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        // Contextual warnings
        bool needsAllThree = (_firstRunKind == FirstRunKind.Avatar || _firstRunKind == FirstRunKind.World);
        if (needsAllThree)
        {
            if (!(_hasWin == true && _hasLinux == true && _hasAndroid == true))
            {
                EditorGUILayout.HelpBox(
                    "Avatar/World setup: install Windows, Linux, and Android Build Support via Unity Hub.",
                    MessageType.Warning);
            }
            if (!(_hasIl2cppStandalone == true && _hasIl2cppAndroid == true))
            {
                EditorGUILayout.HelpBox(
                    "Avatar/World setup: IL2CPP must be available for Standalone and Android. " +
                    "Install *Build Support (IL2CPP)* modules in Unity Hub.",
                    MessageType.Warning);
            }
        }
        else
        {
            if (_choice == PlatformChoice.Android && _hasAndroid != true)
                EditorGUILayout.HelpBox("Android Build Support is missing. Install it in Unity Hub to build for Quest.", MessageType.Error);

            if (_choice == PlatformChoice.Android && _hasIl2cppAndroid != true && _enforceIl2cpp)
                EditorGUILayout.HelpBox("Android IL2CPP is not available. Install Android Build Support (includes IL2CPP) in Unity Hub.", MessageType.Error);
        }

        // Linux Editor friendly note
        if (Application.platform == RuntimePlatform.LinuxEditor)
        {
            EditorGUILayout.HelpBox(
                "Running in Linux Editor. Some platform toolchains may not be available on this OS; " +
                "the badges reflect what’s actually installed here.",
                MessageType.None);
        }
    }

    private void DrawBadge(string label, bool ok)
    {
        var prev = GUI.color;
        GUI.color = ok ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.6f, 0.6f);
        GUILayout.Label(ok ? $"✓ {label}" : $"✕ {label}", EditorStyles.helpBox, GUILayout.MinWidth(140));
        GUI.color = prev;
    }

    // === IL2CPP helpers ===
    private static bool SupportsIl2cpp(BuildTargetGroup group)
    {
        // Prefer direct API if present; otherwise use reflection to stay compatible
        try
        {
            // Unity exposes PlayerSettings.GetAvailableScriptingBackends(BuildTargetGroup)
            var backends = GetAvailableScriptingBackendsSafe(group);
            foreach (var b in backends)
            {
                if (b == ScriptingImplementation.IL2CPP)
                    return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private static ScriptingImplementation[] GetAvailableScriptingBackendsSafe(BuildTargetGroup group)
    {
        // Try direct call first (most modern Unity versions)
        var direct = typeof(PlayerSettings).GetMethod(
            "GetAvailableScriptingBackends",
            BindingFlags.Public | BindingFlags.Static);
        if (direct != null)
        {
            return (ScriptingImplementation[])direct.Invoke(null, new object[] { group });
        }

        // Fallback: some older versions expose it as internal; try reflection anyway
        var any = typeof(PlayerSettings).GetMethod(
            "GetAvailableScriptingBackends",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (any != null)
        {
            return (ScriptingImplementation[])any.Invoke(null, new object[] { group });
        }

        // Last resort: assume Mono + IL2CPP exist for Android, unknown for Standalone
        if (group == BuildTargetGroup.Android)
            return new[] { ScriptingImplementation.Mono2x, ScriptingImplementation.IL2CPP };

        return new[] { PlayerSettings.GetScriptingBackend(group) }; // best-effort probe
    }

    private static void SetScriptingBackendSafe(BuildTargetGroup group, ScriptingImplementation impl)
    {
        // Don’t write if it’s already set
        if (PlayerSettings.GetScriptingBackend(group) == impl) return;

        // Guard: only set if actually supported
        var backends = GetAvailableScriptingBackendsSafe(group);
        bool supported = Array.Exists(backends, b => b == impl);
        if (!supported)
            throw new InvalidOperationException($"IL2CPP not supported for {group} on this Editor install.");

        PlayerSettings.SetScriptingBackend(group, impl);
    }

    // =======================
    // NEW: Linux + Meta XR removal helpers
    // =======================
    private void BeginPackageScanIfNeeded()
    {
        if (Application.platform != RuntimePlatform.LinuxEditor)
        {
            _metaXrInstalled = false; // irrelevant on non-Linux, don’t bother scanning
            return;
        }

        // Only start a scan if we don't already know
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
                // Unknown state, re-scan
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

            // Show current status line
            var status = string.IsNullOrEmpty(_pkgStatus) ? "Ready" : _pkgStatus;
            EditorGUILayout.LabelField(status, EditorStyles.miniLabel);

            // If we don't yet know, show a scan button
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
                    GUI.enabled = _pkgRemoveReq == null; // disable button while removing
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

    // === Logo loader ===
    private void LoadLogoIfNeeded()
    {
#if UNITY_EDITOR
        if (_basisLogo != null) return;

        // Try to load from package path
        _basisLogo = AssetDatabase.LoadAssetAtPath<Texture2D>(BASIS_LOGO_PATH);

        // Fallback: use a built-in icon so header still looks sane
        if (_basisLogo == null)
        {
            var icon = EditorGUIUtility.IconContent("d_UnityLogo");
            _basisLogo = icon?.image as Texture2D;
        }
#endif
    }
}
#endif
