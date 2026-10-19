using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Plays a source end to end and grades the run against the documented
/// bands, so "does it still work" is a command rather than a person
/// watching a quad.
///
/// Interactive: <c>Basis > Tools > Media Player > Run Smoke Test</c>.
///
/// Batch:
/// <code>
/// Unity -batchmode -projectPath &lt;project&gt; -logFile - \
///       -executeMethod BasisMediaSmokeTest.RunBatch
/// </code>
/// with optional environment: <c>BASIS_SMOKE_URL</c> (defaults to the
/// repo fixture), <c>BASIS_SMOKE_SECONDS</c>, <c>BASIS_SMOKE_STRICT_HOLDS</c>,
/// <c>BASIS_SMOKE_LIVE</c> (a live source is not expected to end).
/// Exit code 0 on a pass, 1 on a fail, so CI reads it directly.
///
/// Entering play mode reloads the domain, so the run's state lives in
/// <see cref="SessionState"/> and the timer re-hooks itself on the far
/// side.
/// </summary>
public static class BasisMediaSmokeTest
{
    const string PendingKey = "BasisMedia.Smoke.Pending";
    const string BatchKey = "BasisMedia.Smoke.Batch";
    const string DeadlineKey = "BasisMedia.Smoke.Deadline";
    const string SecondsKey = "BasisMedia.Smoke.Seconds";
    const string StrictKey = "BasisMedia.Smoke.Strict";
    const string LiveKey = "BasisMedia.Smoke.Live";
    const string CaptureKey = "BasisMedia.Smoke.Capture";

    [MenuItem("Basis/Tools/Media Player/Run Smoke Test", false, 503)]
    public static void RunInteractive() => Run(batch: false);

    public static void RunBatch() => Run(batch: true);

    static void Run(bool batch)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Report("already in play mode; leave it first", batch, pass: false);
            return;
        }

        string url = Environment.GetEnvironmentVariable("BASIS_SMOKE_URL");
        if (string.IsNullOrEmpty(url)) url = DefaultFixture();
        if (string.IsNullOrEmpty(url))
        {
            Report("no source: set BASIS_SMOKE_URL, or run from a checkout that has the engine fixtures", batch, pass: false);
            return;
        }

        double seconds = ParseDouble("BASIS_SMOKE_SECONDS", 20);
        bool strict = Truthy(Environment.GetEnvironmentVariable("BASIS_SMOKE_STRICT_HOLDS"));
        bool live = Truthy(Environment.GetEnvironmentVariable("BASIS_SMOKE_LIVE"));
        string capture = Path.Combine(Application.persistentDataPath, "BasisMediaSmoke.csv");

        // The graded scene replaces whatever is open, so offer to save it
        // first and take a cancel as "don't run". After the checks above, so a
        // run that was going to refuse anyway does not ask. Not in batch mode:
        // there is nobody to ask, and the dialog would be auto-answered.
        if (!batch && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        BuildScene(url, capture, live);

        SessionState.SetBool(PendingKey, true);
        SessionState.SetBool(BatchKey, batch);
        SessionState.SetBool(StrictKey, strict);
        SessionState.SetBool(LiveKey, live);
        SessionState.SetFloat(SecondsKey, (float)seconds);
        SessionState.SetString(CaptureKey, capture);
        SessionState.SetString(DeadlineKey,
            (EditorApplication.timeSinceStartup + seconds).ToString("R"));

        Debug.Log($"[BasisMedia] smoke test: {BasisMediaUrlRouter.Redact(url)} for {seconds:F0}s → {capture}");
        EditorApplication.EnterPlaymode();
    }

    /// <summary>Re-hooks the timer after the domain reload that entering
    /// play mode causes.</summary>
    [InitializeOnLoadMethod]
    static void Resume()
    {
        if (!SessionState.GetBool(PendingKey, false)) return;
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        if (!SessionState.GetBool(PendingKey, false))
        {
            EditorApplication.update -= Tick;
            return;
        }
        if (!Application.isPlaying) return; // still entering

        double deadline = double.TryParse(SessionState.GetString(DeadlineKey, "0"), out double parsed) ? parsed : 0;
        BasisMediaPlayer player = BasisMediaPlayerRegistry.Count > 0
            ? BasisMediaPlayerRegistry.Players[0]
            : null;

        // Finish early once a finite source has ended; there is nothing
        // more to learn by waiting out the clock.
        bool ended = !SessionState.GetBool(LiveKey, false)
                     && player != null && player.State == BmState.Ended;
        if (EditorApplication.timeSinceStartup < deadline && !ended) return;

        EditorApplication.update -= Tick;
        SessionState.SetBool(PendingKey, false);

        var diagnostics = player != null ? player.GetComponent<BasisMediaPlayerDiagnostics>() : null;
        diagnostics?.StopLogging();

        string capture = SessionState.GetString(CaptureKey, "");
        bool batch = SessionState.GetBool(BatchKey, false);
        var bands = new BasisMediaSmokeBands
        {
            ExpectEnded = !SessionState.GetBool(LiveKey, false),
            EnforceHoldShare = SessionState.GetBool(StrictKey, false),
        };

        BasisMediaSmokeReport report;
        try
        {
            report = BasisMediaSmokeGrader.Grade(ReadCapture(capture), bands);
        }
        catch (Exception e)
        {
            Report($"the capture could not be read: {e.Message}", batch, pass: false);
            return;
        }

        EditorApplication.ExitPlaymode();
        Report(report.Summarise(), batch, report.Passed);
    }

    static IReadOnlyList<string> ReadCapture(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<string>();
        // The logger holds the file open; read alongside it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        string line;
        while ((line = reader.ReadLine()) != null) lines.Add(line);
        return lines;
    }

    static void Report(string body, bool batch, bool pass)
    {
        if (pass) Debug.Log($"[BasisMedia] {body}");
        else Debug.LogError($"[BasisMedia] {body}");
        if (batch) EditorApplication.Exit(pass ? 0 : 1);
    }

    static void BuildScene(string url, string capturePath, bool live)
    {
        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        const float screenWidth = 16f / 9f * 2f;
        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = "Smoke Screen";
        screen.transform.localScale = new Vector3(screenWidth, 2f, 1f);
        screen.transform.position = new Vector3(0f, 0f, 4f);
        UnityEngine.Object.DestroyImmediate(screen.GetComponent<Collider>());
        Shader shader = Shader.Find("Basis/Media Player Video")
                        ?? Shader.Find("Universal Render Pipeline/Unlit")
                        ?? Shader.Find("Unlit/Texture");
        if (shader != null) screen.GetComponent<MeshRenderer>().sharedMaterial = new Material(shader);

        var host = new GameObject("Smoke Player");
        var player = host.AddComponent<BasisMediaPlayer>();
        player.url = url;
        player.playOnStart = true;
        player.liveness = live ? BmLiveness.Live : BmLiveness.Vod;
        // A local fixture resolves to a loopback-ish path; the engine's own
        // gate is what this opts out of, deliberately and only here.
        player.allowLocalAddresses = true;

        // The audio bands grade what actually reached a speaker, so the run
        // needs a real output set rather than a bare AudioSource. Stereo:
        // the fixture is stereo, and a surround rig would leave six of its
        // eight outputs silent by design and nothing to measure.
        BasisMediaAudioRig.AddStereoOutput(host);

        var output = host.AddComponent<BasisVideoMaterialOutput>();
        output.Player = player;
        output.TargetRenderer = screen.GetComponent<MeshRenderer>();
        output.TexturePropertyName = "_BaseMap";

        var diagnostics = host.AddComponent<BasisMediaPlayerDiagnostics>();
        diagnostics.AutoStart = true;
        diagnostics.AppendBetweenSessions = false;
        diagnostics.LogPathOverride = Path.GetFileName(capturePath);
    }

    /// <summary>The engine's A/V fixture when the package sits in a
    /// checkout that carries it; empty otherwise.</summary>
    static string DefaultFixture()
    {
        // Asked of the package manager rather than spelled out, so a folder
        // rename does not quietly turn this into an empty string.
        string package = UnityEditor.PackageManager.PackageInfo
            .FindForAssembly(typeof(BasisMediaSmokeTest).Assembly)?.assetPath;
        if (string.IsNullOrEmpty(package)) return string.Empty;
        package = Path.GetFullPath(package);
        string fixture = Path.GetFullPath(
            Path.Combine(package, "Native~", "fixtures", "h264-aac-640x360-30fps.mp4"));
        return File.Exists(fixture) ? fixture : string.Empty;
    }

    static double ParseDouble(string variable, double fallback)
        => double.TryParse(Environment.GetEnvironmentVariable(variable), out double value) && value > 0
            ? value
            : fallback;

    static bool Truthy(string value)
        => !string.IsNullOrEmpty(value)
           && !value.Equals("0", StringComparison.Ordinal)
           && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
}
