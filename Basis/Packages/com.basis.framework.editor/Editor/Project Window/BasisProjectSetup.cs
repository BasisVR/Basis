#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering; // just for GraphicsDeviceType enums if needed

public partial class BasisProjectSetup : EditorWindow
{
    // ─────────────────────────── Enums & constants ───────────────────────────

    private enum PlatformChoice { Windows, Linux, Android }
    private enum FirstRunKind { None = 0, Avatar = 1, World = 2, Project = 3 }
    private enum Tab { QuickStart, BuildModules, PlatformQuality, PlayXR, Docs, Scenes, About }

    // Persisted prefs
    private const string PREF_LAST_PLATFORM = "BasisPlatformSwitcher_LastPlatform";
    private const string PREF_HAS_OPENED = "BasisPlatformSwitcher_HasOpened";
    private const string PREF_FIRST_RUN_KIND = "BasisPlatformSwitcher_FirstRunKind";
    private const string PREF_TAB = "BasisProjectSetup_Tab";
    // Docs foldouts
    private const string FOLD_DOCS_ASMDEF = "Basis_Fold_DOCS_Asmdef";
    private const string FOLD_DOCS_SNIPPETS = "Basis_Fold_DOCS_Snippets";

    // External docs
    private const string UNITY_ASMDEF_DOCS = "https://docs.unity3d.com/Manual/ScriptCompilationAssemblyDefinitionFiles.html";
    // Foldout prefs
    private const string FOLD_QS_FIRST_RUN = "Basis_Fold_QS_FirstRun";
    private const string FOLD_QS_FUNDING = "Basis_Fold_QS_Funding";
    private const string FOLD_BUILD_STATUS = "Basis_Fold_Build_Status";
    private const string FOLD_PQ_APPLY = "Basis_Fold_PQ_Apply";
    private const string FOLD_PLAY_KEYS = "Basis_Fold_PLAY_Keys";
    private const string FOLD_PLAY_CONTROL = "Basis_Fold_PLAY_Control";
    private const string FOLD_DOCS_INLINE = "Basis_Fold_DOCS_Inline";
    private const string FOLD_SCENES_LIST = "Basis_Fold_SCENES_List";
    private const string FOLD_ABOUT_INFO = "Basis_Fold_ABOUT_Info";

    // SessionState keys
    private const string SESSION_SHOW_FIRST_NOTICE = "BasisPlatformSwitcher_ShowFirstNotice";
    private const string SESSION_NEED_MODULE_RECHECK = "BasisPlatformSwitcher_NeedModuleRecheck";

    // Links
    private const string BASIS_SITE = "https://basisvr.org/";
    private const string BASIS_GETTING_STARTED = "https://docs.basisvr.org";
    private const string BASIS_AVATARS = "https://docs.basisvr.org/en/docs/avatar";
    private const string BASIS_WORLDS = "https://docs.basisvr.org/en/docs/world";
    private const string BASIS_DONATE = "https://opencollective.com/basis";
    private const string UNITY_HUB_ADD_MODULES = "https://docs.unity3d.com/hub/manual/AddModules.html";

    // Package id to remove on Linux (optional hygiene)
    private const string META_XR_CORE_PKG = "com.meta.xr.sdk.core";

    // Logo (Packages path)
    private const string BASIS_LOGO_PATH = "Packages/com.basis.sdk/Textures/BasisLogoTemp.png";
    private Texture2D _basisLogo;

    // Basis default scenes (adjust as needed)
    private const string SCENE_INIT = "Packages/com.basis.framework/Scenes/initialization.unity";
    private const string SCENE_DEMO = "Packages/com.basis.examples/Scenes/DemoScene.unity";
    private const string SCENE_INTERACTABLES = "Packages/com.basis.examples/Scenes/InteractablesScene.unity";

    // Cached scene assets
    private SceneAsset _sceneInit;
    private SceneAsset _sceneDemo;
    private SceneAsset _sceneInteractables;

    // UI state
    private Tab _tab;
    private PlatformChoice _choice;
    private bool _showFirstRunNotice;
    private FirstRunKind _firstRunKind;

    // Enforcement
    private bool _enforceIl2cpp = true;

    // Cached module checks (session)
    private bool? _hasWin;
    private bool? _hasLinux;
    private bool? _hasAndroid;

    private bool? _hasIl2cppStandalone;
    private bool? _hasIl2cppAndroid;

    // Quality presets — must match indices in BasisQualitySettingsGuard
    private const int QUALITY_DESKTOP = 0;
    private const int QUALITY_ANDROID = 1;

    // Package manager state
    private bool? _metaXrInstalled;          // null = unknown (scanning), true/false known
    private ListRequest _pkgListReq;          // scanning
    private RemoveRequest _pkgRemoveReq;      // removing
    private string _pkgStatus;                // short status string

    // Copy toast
    private double _copiedToastUntil;

    // ─────────────────────────── Menu & lifecycle ───────────────────────────

    [MenuItem("Basis/ProjectSetup")]
    public static void ShowWindow()
    {
        var window = GetWindow<BasisProjectSetup>("Basis Project Setup");
        window.minSize = new Vector2(640, 520);
        window.Show();
    }

    private void OnEnable()
    {
        _tab = (Tab)EditorPrefs.GetInt(PREF_TAB, (int)Tab.QuickStart);
        _choice = (PlatformChoice)EditorPrefs.GetInt(PREF_LAST_PLATFORM, (int)PlatformChoice.Windows);

        if (!EditorPrefs.GetBool(PREF_HAS_OPENED, false))
            EditorPrefs.SetBool(PREF_HAS_OPENED, true);

        _showFirstRunNotice = SessionState.GetBool(SESSION_SHOW_FIRST_NOTICE, false);
        if (_showFirstRunNotice) SessionState.EraseBool(SESSION_SHOW_FIRST_NOTICE);

        _firstRunKind = (FirstRunKind)EditorPrefs.GetInt(PREF_FIRST_RUN_KIND, (int)FirstRunKind.None);

        if (SessionState.GetBool(SESSION_NEED_MODULE_RECHECK, true))
        {
            RecheckBuildModulesAndBackends();
            SessionState.SetBool(SESSION_NEED_MODULE_RECHECK, false);
        }

        BeginPackageScanIfNeeded();
        EditorApplication.update += PollPackageOperations;

        LoadLogoIfNeeded();

        _sceneInit = LoadSceneAsset(SCENE_INIT);
        _sceneDemo = LoadSceneAsset(SCENE_DEMO);
        _sceneInteractables = LoadSceneAsset(SCENE_INTERACTABLES);
    }

    private void OnDisable()
    {
        EditorApplication.update -= PollPackageOperations;
        EditorApplication.update -= Repaint;
    }

    // ────────────────────────────── IMGUI root ──────────────────────────────

    private void OnGUI()
    {
        EditorGUILayout.Space(2);
        DrawHeader();
        EditorGUILayout.Space(6);

        if (_showFirstRunNotice)
        {
            EditorGUILayout.HelpBox(
                "First time here! Choose what you’re setting up, verify build modules (including IL2CPP), and pick your target platform before building or pressing Play.",
                MessageType.Warning);
        }

        var newTab = (Tab)GUILayout.Toolbar((int)_tab,
            new[] { "Quick Start", "Build & Modules", "Platform & Quality", "Play & XR", "Docs", "Scenes", "About" });
        if (newTab != _tab)
        {
            _tab = newTab;
            EditorPrefs.SetInt(PREF_TAB, (int)_tab);
        }

        EditorGUILayout.Space(6);
        switch (_tab)
        {
            case Tab.QuickStart: DrawTab_QuickStart(); break;
            case Tab.BuildModules: DrawTab_BuildModules(); break;
            case Tab.PlatformQuality: DrawTab_PlatformQuality(); break;
            case Tab.PlayXR: DrawTab_PlayXR(); break;
            case Tab.Docs: DrawTab_Docs(); break;
            case Tab.Scenes: DrawTab_Scenes(); break;
            case Tab.About: DrawTab_About(); break;
        }

        GUILayout.FlexibleSpace();
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(90))) Close();
        }

        if (GUI.changed)
        {
            EditorPrefs.SetInt(PREF_LAST_PLATFORM, (int)_choice);
        }
    }

    // ───────────────────────────────── TABS ─────────────────────────────────

    private void DrawTab_QuickStart()
    {
        FoldoutBox("First Run & Docs", FOLD_QS_FIRST_RUN, () =>
        {
            EditorGUILayout.LabelField("Jump straight into the right docs.", EditorStyles.wordWrappedLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Avatar Docs")) Application.OpenURL(BASIS_AVATARS);
                if (GUILayout.Button("World Docs")) Application.OpenURL(BASIS_WORLDS);
                if (GUILayout.Button("Getting Started")) Application.OpenURL(BASIS_GETTING_STARTED);
                if (GUILayout.Button("basisvr.org")) Application.OpenURL(BASIS_SITE);
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
                    "Install Windows, Linux, and Android Build Support via Unity Hub. Use IL2CPP for best compatibility (required on Android).",
                    MessageType.Info);
            }
        });

        if (_firstRunKind == FirstRunKind.Avatar || _firstRunKind == FirstRunKind.World || _firstRunKind == FirstRunKind.Project)
        {
            EditorGUILayout.Space(6);
            DrawFirstRunSteps(); // 🔧 new
        }

        FoldoutBox("How We Are Funded", FOLD_QS_FUNDING, () =>
        {
            EditorGUILayout.LabelField(
                "BasisVR is sustained by community donations and collaborations. Funds are pooled to solve shared problems (networking, embodiment, tooling).",
                EditorStyles.wordWrappedLabel);
#if UNITY_2021_2_OR_NEWER
            if (EditorGUILayout.LinkButton("Support Basis on Open Collective")) Application.OpenURL(BASIS_DONATE);
#else
            if (GUILayout.Button("Support Basis on Open Collective", EditorStyles.linkLabel)) Application.OpenURL(BASIS_DONATE);
#endif
        });
    }
    private void DrawFirstRunSteps()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            var header = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            var body = new GUIStyle(EditorStyles.wordWrappedLabel);

            switch (_firstRunKind)
            {
                case FirstRunKind.Avatar:
                    EditorGUILayout.LabelField("Avatar setup — do this:", header);
                    EditorGUILayout.LabelField(
                        "1) Add the component “BasisAvatar” to your avatar root.\n" +
                        "2) Set viewpoint/eye height as needed.\n" +
                        "3) Enter Play and sanity check movement/teleport. by clicking test in editor", body);

                    EditorGUILayout.Space(4);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Add BasisAvatar to selected"))
                            AddComponentToSelectionOrWarn("BasisAvatar");

#if UNITY_2021_2_OR_NEWER
                        if (EditorGUILayout.LinkButton("Avatar Docs")) Application.OpenURL(BASIS_AVATARS);
#else
                    if (GUILayout.Button("Avatar Docs", EditorStyles.linkLabel)) Application.OpenURL(BASIS_AVATARS);
#endif
                    }
                    break;

                case FirstRunKind.World:
                    EditorGUILayout.LabelField("World setup — do this:", header);
                    EditorGUILayout.LabelField(
                        "1) Add the component “BasisScene” to a root scene object.\n" +
                        "2) Set spawn points and scene options in Inspector.\n" +
                        "3) Test desktop play, then enter XR with F10/F11.", body);

                    EditorGUILayout.Space(4);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Add BasisScene to selected / create root"))
                            AddBasisSceneWithFallback();

#if UNITY_2021_2_OR_NEWER
                        if (EditorGUILayout.LinkButton("World Docs")) Application.OpenURL(BASIS_WORLDS);
#else
                    if (GUILayout.Button("World Docs", EditorStyles.linkLabel)) Application.OpenURL(BASIS_WORLDS);
#endif
                    }
                    break;

                case FirstRunKind.Project:
                    EditorGUILayout.LabelField("Project basics — quick notes:", header);
                    EditorGUILayout.LabelField(
                        "• Avatar: add “BasisAvatar” to your avatar root.\n" +
                        "• World: add “BasisScene” to a scene root object.\n" +
                        "• Prop: add “BasisProp” to any GameObject you want to behave as a prop.", body);

                    EditorGUILayout.Space(4);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Add BasisAvatar to selected"))
                            AddComponentToSelectionOrWarn("BasisAvatar");

                        if (GUILayout.Button("Add BasisScene to selected / create root"))
                            AddBasisSceneWithFallback();

                        if (GUILayout.Button("Add BasisProp to selected"))
                            AddComponentToSelectionOrWarn("BasisProp");
                    }
                    break;
            }
        }
    }

    // 🔧 NEW: add BasisScene with a good fallback
    private void AddBasisSceneWithFallback()
    {
        var go = Selection.activeGameObject;
        if (go == null)
        {
            // make a clean root if nothing is selected
            go = new GameObject("BasisSceneRoot");
            Undo.RegisterCreatedObjectUndo(go, "Create BasisSceneRoot");
            Selection.activeGameObject = go;
        }

        if (!TryAddComponentByName(go, "BasisScene"))
        {
            EditorUtility.DisplayDialog(
                "Type not found",
                "Couldn’t find a component type named “BasisScene”.\n\n" +
                "Make sure the Basis packages are imported and the type name matches.",
                "OK");
        }
    }

    // 🔧 NEW: add a component by simple type name, using reflection over loaded assemblies
    private void AddComponentToSelectionOrWarn(string simpleTypeName)
    {
        var go = Selection.activeGameObject;
        if (go == null)
        {
            EditorUtility.DisplayDialog("No selection",
                "Select a GameObject in the Hierarchy first.", "OK");
            return;
        }

        if (!TryAddComponentByName(go, simpleTypeName))
        {
            EditorUtility.DisplayDialog(
                "Type not found",
                $"Couldn’t find a component type named “{simpleTypeName}”.\n\n" +
                "If this type lives in a namespace, the reflection scanner tries all assemblies; " +
                "verify the component exists and the package is imported.",
                "OK");
        }
    }

    // 🔧 NEW: reflection-based add; searches all assemblies for a matching class name
    private bool TryAddComponentByName(GameObject go, string simpleTypeName)
    {
        if (go == null || string.IsNullOrEmpty(simpleTypeName)) return false;

        var type = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                Type[] types = Type.EmptyTypes;
                try { types = a.GetTypes(); } catch { /* ignore reflection type load issues */ }
                return types;
            })
            .FirstOrDefault(t =>
                t != null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(Component).IsAssignableFrom(t) &&
                t.Name == simpleTypeName);

        if (type == null) return false;

        Undo.AddComponent(go, type);
        ShowTinyNotification($"{simpleTypeName} added to “{go.name}”.");
        return true;
    }

    // 🔧 NEW: little toast on the window titlebar
    private void ShowTinyNotification(string msg)
    {
        var wnd = GetWindow<BasisProjectSetup>();
        wnd.ShowNotification(new GUIContent(msg));
        EditorApplication.delayCall += () => wnd.RemoveNotification();
    }
    private void DrawTab_BuildModules()
    {
        DrawLinuxMetaXrNotice();

        FoldoutBox("Build Targets / Modules / IL2CPP", FOLD_BUILD_STATUS, () =>
        {
            DrawStatusChips();

            EditorGUILayout.Space(4);
            if (!_hasWin.HasValue || !_hasLinux.HasValue || !_hasAndroid.HasValue ||
                !_hasIl2cppStandalone.HasValue || !_hasIl2cppAndroid.HasValue)
            {
                RecheckBuildModulesAndBackendsRow();
            }
            else
            {
                DrawModuleAndBackendStatusRow();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Build Settings")) EditorWindow.GetWindow(typeof(BuildPlayerWindow));
#if UNITY_2021_2_OR_NEWER
                if (EditorGUILayout.LinkButton("How to add modules in Unity Hub")) Application.OpenURL(UNITY_HUB_ADD_MODULES);
#else
                if (GUILayout.Button("How to add modules in Unity Hub", EditorStyles.linkLabel)) Application.OpenURL(UNITY_HUB_ADD_MODULES);
#endif
            }
        });
    }

    private void DrawTab_PlatformQuality()
    {
        FoldoutBox("Platform & Quality", FOLD_PQ_APPLY, () =>
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPlatformRadio(PlatformChoice.Windows, "Windows");
                DrawPlatformRadio(PlatformChoice.Linux, "Linux");
                DrawPlatformRadio(PlatformChoice.Android, "Android (Quest)");
            }

            _enforceIl2cpp = EditorGUILayout.ToggleLeft(
                "Enforce IL2CPP scripting backend when applying", _enforceIl2cpp);

            EditorGUILayout.HelpBox("Quality presets: 1 = Desktop (Windows/Linux), 2 = Android/Quest", MessageType.None);

            bool modulesOk = AreRequiredModulesOkForCurrentSelection();
            using (new EditorGUI.DisabledScope(!modulesOk && _enforceIl2cpp))
            {
                string label = modulesOk ? "Apply & Switch Platform" : "Apply & Switch Platform (modules missing)";
                if (GUILayout.Button(label))
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
                    }
                }
            }
        });
    }

    private void DrawTab_PlayXR()
    {
        FoldoutBox("Play Mode & XR Basics", FOLD_PLAY_KEYS, () =>
        {
            EditorGUILayout.LabelField(
                "Entering Play in any scene will load Basis. The boot path is marked with " +
                "[RuntimeInitializeOnLoadMethod(AfterSceneLoad)] and instantiates the Addressable prefab “BasisFramework”.\n\n" +
                "XR hotkeys during Play:\n• F10 → OpenVR (SteamVR)\n• F11 → OpenXR",
                EditorStyles.wordWrappedLabel);
        });

        FoldoutBox("Control Boot / XR Startup", FOLD_PLAY_CONTROL, () =>
        {
            EditorGUILayout.LabelField(
                "Two common flows:\n" +
                "1) Stop Basis from booting (good for plain-Unity testing).\n" +
                "2) Desktop-first: let Basis boot, but don’t auto-enter XR; opt in via F10/F11.",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Boot Sequence Toggle"))
                {
                    var t = FindTypeByName("Basis.Scripts.Boot_Sequence.BootManagerEditor");
                    if (t != null)
                    {
                        t.GetMethod("ShowWindow", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(
                            "Boot Sequence",
                            "Couldn’t find BootManagerEditor. Make sure it’s in an Editor assembly.",
                            "OK");
                    }
                }
            }
        });
    }

    private void DrawTab_Docs()
    {
        FoldoutBox("In-Editor API Docs", FOLD_DOCS_INLINE, () =>
        {
            EditorGUILayout.LabelField(
                "Every Basis component ships its own API notes right in the Inspector. " +
                "Select a Basis component and expand the foldout named “Basis API Reference”.",
                EditorStyles.wordWrappedLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
#if UNITY_2021_2_OR_NEWER
                if (EditorGUILayout.LinkButton("Unity: Assembly Definition Files")) Application.OpenURL(UNITY_ASMDEF_DOCS);
#else
            if (GUILayout.Button("Unity: Assembly Definition Files", EditorStyles.linkLabel)) Application.OpenURL(UNITY_ASMDEF_DOCS);
#endif
#if UNITY_2021_2_OR_NEWER
                if (EditorGUILayout.LinkButton("Basis: Getting Started")) Application.OpenURL(BASIS_GETTING_STARTED);
#else
            if (GUILayout.Button("Basis: Getting Started", EditorStyles.linkLabel)) Application.OpenURL(BASIS_GETTING_STARTED);
#endif
            }
        });

        FoldoutBox("Assembly Definitions (asmdef) — How to hook into Basis", FOLD_DOCS_ASMDEF, () =>
        {
            EditorGUILayout.LabelField(
                "Keep compile times lean and dependencies explicit. Put your gameplay in a runtime asmdef, and editor tools in an Editor-only asmdef.",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Runtime asmdef (template)", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Create Assets/YourGame/YourGame.Runtime.asmdef and add a reference to the Basis runtime assembly that contains BasisLocalPlayer.",
                    EditorStyles.wordWrappedLabel);

                ReadOnlyCodeWithCopy(AsmdefRuntimeTemplate);

                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Editor asmdef (template)", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Create Assets/YourGame/Editor/YourGame.Editor.asmdef, tick Include Platforms → Editor, and reference YourGame.Runtime (+ any Basis Editor assemblies you need).",
                    EditorStyles.wordWrappedLabel);

                ReadOnlyCodeWithCopy(AsmdefEditorTemplate);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Tip: To find the exact Basis assembly to reference, click any Basis script and check its Assembly Definition in the Inspector. Add only what you need.",
                MessageType.Info);
        });

        FoldoutBox("Basis Snippets — Local player, teleport, events", FOLD_DOCS_SNIPPETS, () =>
        {
            EditorGUILayout.LabelField(
                "Paste these into your runtime assembly (the one that references Basis). Adjust namespaces to match your Basis package.",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(4);

            // Calculate a sensible height so the scroll is visible but not cramped
            float maxHeight = Mathf.Clamp(position.height - 220f, 220f, 900f);

            // 🔽 Scrollable area for long snippet lists
            using (var sv = new EditorGUILayout.ScrollViewScope(_docsSnippetsScroll, GUILayout.MaxHeight(maxHeight)))
            {
                _docsSnippetsScroll = sv.scrollPosition;

                DrawSnippet("Teleport on keypress", Snippet_DevTeleport);
                DrawSnippet("Wait for Basis then teleport", Snippet_WaitForBasis);
                DrawSnippet("Listen for spawn/teleport events", Snippet_ListenForSpawn);
                DrawSnippet("Get any tracked point once", Snippet_GetTrackedPointOnce);
                DrawSnippet("Follow a tracked role (world space)", Snippet_FollowTrackedRole);
                DrawSnippet("Follow a tracked role via BasisNetworkPlayer", Snippet_FollowTrackedRoleViaNet);
                DrawSnippet("Play haptics on a role", Snippet_Haptics);
                DrawSnippet("Detect if user is in VR", Snippet_IsUserInVR);
            }
        });
    }

    // NEW: scroll state for the Snippets foldout
    private Vector2 _docsSnippetsScroll;
    private void DrawSnippet(string title, string code)
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            ReadOnlyCodeWithCopy(code);
        }
    }
    private void DrawTab_Scenes()
    {
        FoldoutBox("Initial Scene & Build Setup", FOLD_SCENES_LIST, DrawInitialSceneAndBuildSetup);
    }

    private void DrawTab_About()
    {
        FoldoutBox("About Basis", FOLD_ABOUT_INFO, () =>
        {
            EditorGUILayout.LabelField(
                "Creator-First, Creative Freedom — Basis helps you stand up VR projects quickly.\n" +
                "Open-Source (MIT). Strong systems for networking, input, and presence.",
                EditorStyles.wordWrappedLabel);
#if UNITY_2021_2_OR_NEWER
            if (EditorGUILayout.LinkButton("Visit basisvr.org")) Application.OpenURL(BASIS_SITE);
#else
            if (GUILayout.Button("Visit basisvr.org", EditorStyles.linkLabel)) Application.OpenURL(BASIS_SITE);
#endif
        });
    }
    private const string AsmdefRuntimeTemplate = @"{
  ""name"": ""YourGame.Runtime"",
    ""references"": [
        ""GUID:c6d9b466725956a45955440904ff0491"",
        ""GUID:8c9aa0e006f5b5347af5c5470971dfae"",
        ""GUID:75469ad4d38634e559750d17036d5f7c"",
        ""GUID:2684ea0d564097444a05d23355ff46a1""
    ],
  ""includePlatforms"": [],
  ""excludePlatforms"": [],
  ""allowUnsafeCode"": false,
  ""overrideReferences"": false,
  ""precompiledReferences"": [],
  ""autoReferenced"": true,
  ""defineConstraints"": [],
  ""versionDefines"": [],
  ""noEngineReferences"": false
}";

    private const string AsmdefEditorTemplate = @"{
  ""name"": ""YourGame.Editor"",
    ""references"": [
        ""GUID:c6d9b466725956a45955440904ff0491"",
        ""GUID:8c9aa0e006f5b5347af5c5470971dfae"",
        ""GUID:75469ad4d38634e559750d17036d5f7c"",
        ""GUID:2684ea0d564097444a05d23355ff46a1""
    ],
  ""includePlatforms"": [ ""Editor"" ],
  ""excludePlatforms"": [],
  ""allowUnsafeCode"": false,
  ""overrideReferences"": false,
  ""precompiledReferences"": [],
  ""autoReferenced"": true,
  ""defineConstraints"": [ ""UNITY_EDITOR"" ],
  ""versionDefines"": [],
  ""noEngineReferences"": false
}";

    // ───────────────────── C# snippets ─────────────────────
    private const string Snippet_GetTrackedPointOnce = @"using UnityEngine;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;

public class SampleGetTrackedPointOnce : MonoBehaviour
{
    // Pick any role defined in BasisBoneTrackedRole (Head, LeftHand, RightHand, etc.)
    [SerializeField] private BasisBoneTrackedRole role = BasisBoneTrackedRole.RightHand;

    void Start()
    {
        var lp = BasisLocalPlayer.Instance;
        if (lp == null || lp.LocalBoneDriver == null)
        {
            Debug.LogWarning(""Local player not ready"");
            return;
        }

        if (lp.LocalBoneDriver.FindBone(out BasisLocalBoneControl bone, role))
        {
            // Either pull the calibrated payload struct…
            var data = bone.OutgoingWorldData; // position / rotation in world space
            Debug.Log($""[{role}] pos={data.position} rot={data.rotation.eulerAngles}"");

            // …or extract as raw values:
            Vector3 pos = data.position;
            Quaternion rot = data.rotation;

            // Example: move this GameObject to the tracked point
            transform.SetPositionAndRotation(pos, rot);
        }
        else
        {
            Debug.LogWarning($""No tracked device/bone found for role {role}"");
        }
    }
}";
    private const string Snippet_FollowTrackedRole = @"using UnityEngine;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;

public class FollowTrackedRole : MonoBehaviour
{
    [SerializeField] private BasisBoneTrackedRole role = BasisBoneTrackedRole.RightHand;
    [SerializeField] private bool matchRotation = true;

    private BasisLocalBoneControl _control;

    void OnEnable()
    {
        TryResolve();
        // Simple retry in case Basis boots a tick later
        if (_control == null) InvokeRepeating(nameof(TryResolve), 0.25f, 0.25f);
    }

    void OnDisable()
    {
        CancelInvoke(nameof(TryResolve));
        _control = null;
    }

    void LateUpdate()
    {
        if (_control == null) return;

        var d = _control.OutgoingWorldData; // world-space pose
        transform.position = d.position;
        if (matchRotation) transform.rotation = d.rotation;
    }

    private void TryResolve()
    {
        var lp = BasisLocalPlayer.Instance;
        if (lp != null && lp.LocalBoneDriver != null &&
            lp.LocalBoneDriver.FindBone(out BasisLocalBoneControl c, role))
        {
            _control = c;
            CancelInvoke(nameof(TryResolve));
            // Optional: Debug.Log($""Resolved tracked role {role} to {_control.name}"");
        }
    }
}";
    private const string Snippet_FollowTrackedRoleViaNet = @"using UnityEngine;
using Basis.Scripts.Networking.NetworkedAvatar;

public class FollowTrackedRoleViaNetworkPlayer : MonoBehaviour
{
    [SerializeField] private BasisBoneTrackedRole role = BasisBoneTrackedRole.RightHand;
    [SerializeField] private bool matchRotation = true;

    void LateUpdate()
    {
        var me = BasisNetworkPlayer.LocalPlayer;
        if (me != null && me.GetTrackingData(role, out Vector3 pos, out Quaternion rot))
        {
            transform.position = pos;
            if (matchRotation) transform.rotation = rot;
        }
    }
}";

    private const string Snippet_Haptics = @"using UnityEngine;
using Basis.Scripts.Networking.NetworkedAvatar;

public class HapticOnKey : MonoBehaviour
{
    [SerializeField] private BasisBoneTrackedRole role = BasisBoneTrackedRole.RightHand;

    [Header(""Haptic Params"")]
    [SerializeField, Range(0f, 1f)] private float amplitude = 0.5f;
    [SerializeField, Range(0f, 1f)] private float frequency = 0.5f;
    [SerializeField] private float duration = 0.25f; // SteamVR honors duration; OpenXR may approximate

    [Header(""Keybinding"")]
    [SerializeField] private KeyCode triggerKey = KeyCode.H;

    void Update()
    {
        if (Input.GetKeyDown(triggerKey))
        {
            var me = BasisNetworkPlayer.LocalPlayer;
            if (me != null)
            {
                me.PlayHaptic(role, duration, amplitude, frequency);
                // Optional: Debug.Log($""Haptic sent to {role} (amp={amplitude}, freq={frequency}, dur={duration})"");
            }
        }
    }
}";
    private const string Snippet_IsUserInVR = @"using UnityEngine;
using Basis.Scripts.Device_Management;

public class IsUserInVrExample : MonoBehaviour
{
    void Start()
    {
        bool inVr = BasisDeviceManagement.IsUserInDesktop() == false;
        Debug.Log(""User in VR? "" + inVr);
    }
}";

    private const string Snippet_DevTeleport = @"using UnityEngine;
// using Basis; // Adjust namespace

public class DevTeleport : MonoBehaviour
{
    [SerializeField] private Transform target; // optional

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.T))
        {
            var lp = BasisLocalPlayer.Instance;
            if (lp == null) return;

            Vector3 pos = target ? target.position : new Vector3(0, 1.6f, 0);
            Quaternion rot = target ? target.rotation : Quaternion.identity;

            lp.Teleport(pos, rot);
        }
    }
}";

    private const string Snippet_WaitForBasis = @"using UnityEngine;
using Basis.Scripts.BasisSdk.Players;

public class DoThingWhenLocalPlayerReady : MonoBehaviour
{
    void OnEnable()
    {
        // already bootstrapped?
        if (BasisLocalPlayer.PlayerReady && BasisLocalPlayer.Instance != null)
        {
            HandleReady(BasisLocalPlayer.Instance);
            return;
        }

        // otherwise wait for the callback, then unhook
        BasisLocalPlayer.OnLocalPlayerCreatedAndReady += OnReadyOnce;
    }

    void OnDisable()
    {
        BasisLocalPlayer.OnLocalPlayerCreatedAndReady -= OnReadyOnce;
    }

    private void OnReadyOnce()
    {
        BasisLocalPlayer.OnLocalPlayerCreatedAndReady -= OnReadyOnce;
        HandleReady(BasisLocalPlayer.Instance);
    }

    private void HandleReady(BasisLocalPlayer lp)
    {
        if (lp == null) return;
        Debug.Log(""Local player ready: "" + lp.name);
        lp.Teleport(new Vector3(0, 1.6f, 0), Quaternion.identity);
    }
}
";

    private const string Snippet_ListenForSpawn = @"using UnityEngine;
// using Basis;

public class ListenForLocalSpawn : MonoBehaviour
{
    private BasisLocalPlayer _lp;

    void OnEnable() { TryHook(); }
    void OnDisable() { TryUnhook(); }

    void TryHook()
    {
        _lp = BasisLocalPlayer.Instance;
        if (_lp != null)
        {
            _lp.OnSpawnedEvent += OnLocalSpawned;
        }
        else
        {
            InvokeRepeating(nameof(WaitAndHook), 0.25f, 0.25f);
        }
    }

    void WaitAndHook()
    {
        _lp = BasisLocalPlayer.Instance;
        if (_lp != null)
        {
            CancelInvoke(nameof(WaitAndHook));
            _lp.OnSpawnedEvent += OnLocalSpawned;
        }
    }

    void TryUnhook()
    {
        if (_lp != null) _lp.OnSpawnedEvent -= OnLocalSpawned;
    }

    void OnLocalSpawned()
    {
        Debug.Log(""Local player spawned or teleported."");
    }
}";
    // ────────────────────────────── Section UI ──────────────────────────────

    private void DrawHeader()
    {
        var rect = GUILayoutUtility.GetRect(10, 86, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, new Color32(16, 15, 39, 255)); // #100f27
        var accent = new Rect(rect.x, rect.y, rect.width, 4);
        EditorGUI.DrawRect(accent, new Color32(239, 18, 55, 255)); // #ef1237

        float ppp = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
        float pad = 12f;
        float logoSize = 56f * ppp;

        var title = new Rect(rect.x + pad, rect.y + 10, rect.width - (logoSize + pad * 2f), 28);
        var subtitle = new Rect(rect.x + pad, rect.y + 40, rect.width - (logoSize + pad * 2f), 40);

        var tStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, normal = { textColor = Color.white } };
        var subtitleColor = EditorGUIUtility.isProSkin ? new Color(0.85f, 0.85f, 0.9f) : new Color(0.15f, 0.15f, 0.2f);
        var sStyle = new GUIStyle(EditorStyles.label) { fontSize = 11, wordWrap = true, normal = { textColor = subtitleColor } };

        GUI.Label(title, "Basis Project Wizard", tStyle);
        GUI.Label(subtitle, "Creator-First • Creative Freedom\nOpen-Source (MIT) • Networking • Input • Presence", sStyle);

        if (_basisLogo != null)
        {
            var logoRect = new Rect(rect.xMax - logoSize - pad, rect.y + (rect.height - logoSize) * 0.5f, logoSize, logoSize);
            var border = new Rect(logoRect.x - 2, logoRect.y - 2, logoRect.width + 4, logoRect.height + 4);
            EditorGUI.DrawRect(border, new Color(1, 1, 1, 0.05f));
            GUI.DrawTexture(logoRect, _basisLogo, ScaleMode.ScaleToFit, true);
        }
    }

    private void FoldoutBox(string title, string prefKey, Action body)
    {
        bool open = EditorPrefs.GetBool(prefKey, true);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            var newOpen = EditorGUILayout.BeginFoldoutHeaderGroup(open, title);
            if (newOpen != open) EditorPrefs.SetBool(prefKey, newOpen);
            if (newOpen) { EditorGUILayout.Space(2); body?.Invoke(); EditorGUILayout.Space(2); }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }
    }

    private void DrawFirstRunRadio(FirstRunKind value, string label)
    {
        bool isSelected = _firstRunKind == value;
        if (GUILayout.Toggle(isSelected, label, EditorStyles.radioButton)) _firstRunKind = value;
    }

    private void DrawPlatformRadio(PlatformChoice value, string label)
    {
        bool isSelected = _choice == value;
        if (GUILayout.Toggle(isSelected, label, EditorStyles.radioButton)) _choice = value;
    }

    private void DrawInitialSceneAndBuildSetup()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Initial Scene & Build Setup", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);
            DrawSceneRow("Initialization", SCENE_INIT, ref _sceneInit, makeFirst: true);
            DrawSceneRow("Demo Scene", SCENE_DEMO, ref _sceneDemo);
            DrawSceneRow("Interactables Scene", SCENE_INTERACTABLES, ref _sceneInteractables);
        }
    }

    private void DrawSceneRow(string label, string path, ref SceneAsset cached, bool makeFirst = false)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(label, GUILayout.Width(130));
            EditorGUILayout.SelectableLabel(path, EditorStyles.textField, GUILayout.Height(16));

            GUI.enabled = ScenePathExists(path);
            if (GUILayout.Button("Open", GUILayout.Width(70)))
            {
                if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    EditorSceneManager.OpenScene(path);
            }
            GUI.enabled = true;
        }
    }
    private void DrawStatusChips()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawChip("Windows", _hasWin, "Editor module installed");
            DrawChip("Linux", _hasLinux, "Editor module installed");
            DrawChip("Android", _hasAndroid, "Editor module installed");
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawChip("IL2CPP Standalone", _hasIl2cppStandalone, "Scripting backend available");
            DrawChip("IL2CPP Android", _hasIl2cppAndroid, "Scripting backend available");
            if (!string.IsNullOrEmpty(_pkgStatus))
                GUILayout.Label($"Pkg: {_pkgStatus}", EditorStyles.miniLabel);
        }

        if (EditorApplication.timeSinceStartup < _copiedToastUntil)
        {
            EditorGUILayout.HelpBox("Copied to clipboard.", MessageType.None);
        }
    }

    private void DrawChip(string label, bool? ok, string tooltip = null)
    {
        var style = new GUIStyle(EditorStyles.miniButtonMid);
        Color col;
        string txt;

        if (!ok.HasValue) { col = new Color(0.5f, 0.5f, 0.5f, 0.2f); txt = $"{label}: ?"; }
        else if (ok.Value) { col = new Color(0.2f, 0.6f, 0.2f, 0.25f); txt = $"{label}: ✓"; }
        else { col = new Color(0.8f, 0.2f, 0.2f, 0.25f); txt = $"{label}: ✕"; }

        var content = new GUIContent(txt, tooltip ?? label);
        var r = GUILayoutUtility.GetRect(content, style, GUILayout.Width(150));
        EditorGUI.DrawRect(r, col);
        GUI.Label(r, content, EditorStyles.miniBoldLabel);
    }

    private void ReadOnlyCodeWithCopy(string code)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUILayout.VerticalScope())
            {
                var prev = GUI.enabled; GUI.enabled = false;
                var style = new GUIStyle(EditorStyles.textArea) { fontSize = 11 };
                EditorGUILayout.TextArea(code, style, GUILayout.MinHeight(64));
                GUI.enabled = prev;
            }

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(60)))
            {
                GUILayout.Space(4);
                if (GUILayout.Button("Copy", GUILayout.Height(24)))
                {
                    EditorGUIUtility.systemCopyBuffer = code;
                    _copiedToastUntil = EditorApplication.timeSinceStartup + 1.25;
                    EditorApplication.update -= Repaint;
                    EditorApplication.update += Repaint;
                }
                GUILayout.FlexibleSpace();
            }
        }
    }

    // ─────────────────────────── Utility helpers ───────────────────────────

    private static bool IsBuildTargetSupported(BuildTarget target)
    {
        try
        {
            // Most reliable public API
            return BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target);
        }
        catch { return false; }
    }

    private static bool SupportsScriptingBackend(BuildTargetGroup group, ScriptingImplementation impl)
    {
        // Public API doesn’t expose “available backends”, so we reflect internal ModuleManager.
        try
        {
            var mbt = typeof(BuildTargetGroup);
#if UNITY_2021_2_OR_NEWER
            // NamedBuildTarget is a thing, but we can still probe by group for availability
#endif
            var moduleManagerType = Type.GetType("UnityEditor.Modules.ModuleManager, UnityEditor.dll");
            if (moduleManagerType != null)
            {
                var mi = moduleManagerType.GetMethod("GetScriptingImplementations", BindingFlags.Static | BindingFlags.NonPublic);
                if (mi != null)
                {
                    var result = mi.Invoke(null, new object[] { group }) as Array;
                    if (result != null)
                    {
                        foreach (var x in result)
                        {
                            if ((int)x == (int)impl) return true;
                        }
                        return false;
                    }
                }
            }
        }
        catch { /* swallow */ }

        // Fallback heuristics:
        if (group == BuildTargetGroup.Android) return true;          // Android installs usually ship IL2CPP
        if (group == BuildTargetGroup.Standalone) return true;       // Common on dev machines
        return false;
    }
    private static Type FindTypeByName(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName, throwOnError: false);
            if (t != null) return t;
        }
        return null;
    }
}
#endif
