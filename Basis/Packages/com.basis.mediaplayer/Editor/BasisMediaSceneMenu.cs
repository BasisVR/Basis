using Basis.Editor.Localization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The media player's two scene menus, which exist for different jobs.
///
/// <b>Test Scene</b> builds a scene for a pass in TESTING.md. It replaces what
/// is open, instantiates the prefab the rows call for, fills a source in and
/// turns both captures on, so a pass starts by walking around rather than by
/// assembling a scene.
///
/// <b>Insert Player</b> is the authoring path: the same prefab dropped into a
/// scene someone is building, exactly as it ships. No capture, no diagnostics
/// component and no source: a world's player is not an instrument, and a
/// fixture path from this machine would not resolve on anyone else's.
///
/// Both instantiate the shipped prefabs, so what either drops in the scene is
/// what a world author would have built by hand.
/// </summary>
public static class BasisMediaSceneMenu
{
    /// <summary>The scene Basis boots into, and the only one that arranges a
    /// floor and a spawn.</summary>
    const string InitializationScene = "Packages/com.basis.framework/Scenes/initialization.unity";

    const string StereoPrefab =
        "Packages/com.basis.mediaplayer/Prefabs/MediaPlayerStreaming.prefab";

    const string SurroundPrefab =
        "Packages/com.basis.mediaplayer/Prefabs/MediaPlayerMultiChannelStreaming.prefab";

    /// <summary>Straight in front of a spawned user, at the distance that puts
    /// the screen comfortably in view with the surround rig's speakers landing
    /// where they belong.</summary>
    static readonly Vector3 FirstPlayer = new Vector3(0f, 0f, 3.75f);

    /// <summary>Players after the first step out along +X, so walking that way
    /// changes which are nearest. Wide enough that the governor's hysteresis
    /// is not being asked to resolve a tie at every step.</summary>
    const float SpacingMetres = 10f;

    [MenuItem("Basis/Tools/Media Player/Test Scene/One Player (Stereo)", false, 100)]
    static void BuildOneStereo() => Build(1, StereoPrefab, "stereo");

    [MenuItem("Basis/Tools/Media Player/Test Scene/One Player (7.1)", false, 101)]
    static void BuildOneSurround() => Build(1, SurroundPrefab, "7.1");

    /// <summary>Four against a default cap of three, so one is dormant from the
    /// first frame and the cap has something to do at startup.</summary>
    [MenuItem("Basis/Tools/Media Player/Test Scene/Four Players (Stereo)", false, 102)]
    static void BuildFourStereo() => Build(4, StereoPrefab, "stereo");

    [MenuItem("Basis/Tools/Media Player/Test Scene/Four Players (7.1)", false, 103)]
    static void BuildFourSurround() => Build(4, SurroundPrefab, "7.1");

    [MenuItem("Basis/Tools/Media Player/Insert Player (existing scene)/Stereo", false, 120)]
    static void InsertStereo() => Insert(StereoPrefab);

    [MenuItem("Basis/Tools/Media Player/Insert Player (existing scene)/Multi-Channel", false, 121)]
    static void InsertMultiChannel() => Insert(SurroundPrefab);

    /// <summary>
    /// Replaces the open scene with a pass-ready one. Nothing is saved: the
    /// scene is left dirty for you to keep or discard, and every object is
    /// registered for undo.
    /// </summary>
    static void Build(int count, string prefabPath, string arrangement)
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        GameObject prefab = LoadPrefab(prefabPath);
        if (prefab == null) return;

        // On the initialization scene rather than an empty one: that is what
        // spawns the local player onto a collision floor, and without it there
        // is nobody to walk.
        Scene scene;
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(InitializationScene) != null)
        {
            scene = EditorSceneManager.OpenScene(InitializationScene, OpenSceneMode.Single);
        }
        else
        {
            // A project that does not carry the framework still gets a usable
            // scene; it just has no floor and nobody to walk, so the session
            // cap cannot really be exercised in it.
            scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Debug.LogWarning(
                "[BasisMedia] initialization scene not found; built on an empty scene instead. " +
                "There is no spawn or floor here, so the distance-based rows cannot be tested.");
        }

        EnsureDirectionalLight(scene);

        string[] clips = TestClipUrls();
        string fixture = DefaultFixtureUrl();
        GameObject first = null;
        for (int i = 0; i < count; i++)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = count > 1 ? $"MediaPlayer Test {i + 1}" : "MediaPlayer Test";
            // Every player keeps the first one's orientation, so standing at
            // (i * spacing, 0, 0) reproduces the same head-on arrangement.
            instance.transform.position = FirstPlayer + new Vector3(i * SpacingMetres, 0f, 0f);

            if (instance.GetComponentInChildren<BasisMediaPlayer>() is { } player)
            {
                // One clip each where they are to hand, so which player is
                // which is obvious on screen and by ear; the bundled fixture
                // otherwise, which is short enough that the walking rows
                // cannot really be run against it.
                string url = clips.Length > 0 ? clips[i % clips.Length] : fixture;
                if (!string.IsNullOrEmpty(url)) player.url = url;
                FitCapture(player, count > 1 ? i + 1 : 0);
                EditorUtility.SetDirty(player);
            }

            Undo.RegisterCreatedObjectUndo(instance, "Build Basis Media test scene");
            first ??= instance;
        }

        Selection.activeGameObject = first;
        EditorSceneManager.MarkSceneDirty(scene);

        int assigned = Mathf.Min(count, clips.Length);
        string where = assigned > 0
            ? $"Pre-filled with {assigned} lettered test clip{(assigned == 1 ? "" : "s")}."
            : string.IsNullOrEmpty(fixture)
                ? "Set a URL on each, then enter play mode."
                : "Pre-filled with the engine's 6 s A/V fixture, too "
                  + "short for the rows that need walking. See TESTING.md.";
        Debug.Log($"[BasisMedia] test scene built with {count} {arrangement} player(s). {where} " +
                  "Both captures are on: the per-frame one writes as it goes, the engine's " +
                  $"lands when each session ends. Look in {Application.persistentDataPath}. " +
                  "The scene is unsaved on purpose: discard it when you are done, or save it " +
                  "if the pass needs a build.");
    }

    /// <summary>
    /// Drops a player into the scene already open, as the prefab ships: no
    /// capture, and no URL, because this one goes into somebody's world rather
    /// than into a pass.
    /// </summary>
    static void Insert(string prefabPath)
    {
        GameObject prefab = LoadPrefab(prefabPath);
        if (prefab == null) return;

        Scene scene = SceneManager.GetActiveScene();
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        // Where the scene view is looking, as Unity's own object menu does, so
        // it lands in view rather than at the origin of a scene built elsewhere.
        if (SceneView.lastActiveSceneView is { } view)
            instance.transform.position = view.pivot;

        Undo.RegisterCreatedObjectUndo(instance, "Insert Basis media player");
        Selection.activeGameObject = instance;
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"[BasisMedia] inserted {prefab.name}. Set its URL to something, and add a " +
                  "Basis Media Player Diagnostics component if you want a capture of the run.");
    }

    static GameObject LoadPrefab(string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
            EditorUtility.DisplayDialog("Basis Media", BasisEditorLocalization.Get("editor.mediaPlayer.sceneMenu.loadFailed", path), BasisEditorLocalization.Get("editor.common.ok"));
        return prefab;
    }

    /// <summary>
    /// Turn both captures on: the engine's own view of the session, and the
    /// per-frame one recording what the engine cannot see from the inside.
    /// Nobody remembers to switch them on before the run that turns out to be
    /// interesting.
    ///
    /// The diagnostics component is added here rather than carried on the
    /// prefab because it is a development instrument, not part of a media
    /// player. That is also why the insert path above leaves it off.
    ///
    /// Both default to a single fixed filename under persistentDataPath, so
    /// several players in one scene would write over each other. `ordinal`
    /// numbers them when there is more than one, and leaves the default names
    /// alone when there is not.
    /// </summary>
    static void FitCapture(BasisMediaPlayer player, int ordinal)
    {
        player.engineCapture = true;
        player.engineCaptureFileName = ordinal > 0 ? $"BasisMediaEngine-{ordinal}.csv" : "";
        // Sessions cycle in these scenes (dormancy, waking, re-opens) and
        // each one writes on close, so replacing would leave only the last.
        player.engineCaptureAppend = true;

        var diagnostics = player.GetComponent<BasisMediaPlayerDiagnostics>()
                          ?? Undo.AddComponent<BasisMediaPlayerDiagnostics>(player.gameObject);
        diagnostics.LogPathOverride = ordinal > 0 ? $"BasisMediaFrames-{ordinal}.csv" : "";
        diagnostics.AppendBetweenSessions = true;
        EditorUtility.SetDirty(diagnostics);
    }

    /// <summary>The surround rig is unlit, but the rest of the scene is not;
    /// initialization ships no light of its own.</summary>
    static void EnsureDirectionalLight(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var existing in root.GetComponentsInChildren<Light>(includeInactive: true))
            {
                if (existing.type == LightType.Directional) return;
            }
        }

        var go = new GameObject("Directional Light");
        var light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        Undo.RegisterCreatedObjectUndo(go, "Build Basis Media test scene");
    }

    /// <summary>
    /// A lettered clip per player: a minute each of one letter, one colour,
    /// one genre of music and a running timecode. They make the session-cap
    /// rows readable: which player is dormant is visible at a glance, whether
    /// one resumed where it would have been is legible off the timecode, and
    /// flapping is audible without looking at anything.
    ///
    /// Shipped under `Native~/fixtures/captest`, so the pass needs no setup.
    /// Missing ones are skipped, so a partial set still helps.
    /// </summary>
    static string[] TestClipUrls()
    {
        string dir = FixtureDirectory();
        if (string.IsNullOrEmpty(dir)) return System.Array.Empty<string>();
        dir = Path.Combine(dir, "captest");
        var found = new System.Collections.Generic.List<string>(4);
        foreach (string letter in new[] { "A", "B", "C", "D" })
        {
            string path = Path.GetFullPath(Path.Combine(dir, letter + ".mp4"));
            if (File.Exists(path)) found.Add(path);
        }
        return found.ToArray();
    }

    /// <summary>The engine's A/V fixture where the package sits in a checkout
    /// carrying it; empty otherwise. A local file, so a pass needs no network
    /// and no stream server.</summary>
    static string DefaultFixtureUrl()
    {
        string dir = FixtureDirectory();
        if (string.IsNullOrEmpty(dir)) return string.Empty;
        string fixture = Path.GetFullPath(Path.Combine(dir, "h264-aac-640x360-30fps.mp4"));
        return File.Exists(fixture) ? fixture : string.Empty;
    }

    /// <summary>Asked of the package manager rather than spelled out, so a
    /// folder rename does not quietly turn this into an empty string.</summary>
    static string FixtureDirectory()
    {
        string packagePath = UnityEditor.PackageManager.PackageInfo
            .FindForAssembly(typeof(BasisMediaSceneMenu).Assembly)?.assetPath;
        return string.IsNullOrEmpty(packagePath)
            ? string.Empty
            : Path.Combine(Path.GetFullPath(packagePath), "Native~", "fixtures");
    }
}
