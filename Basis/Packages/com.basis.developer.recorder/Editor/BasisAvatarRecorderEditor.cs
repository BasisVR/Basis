#if UNITY_EDITOR
using Basis.Editor.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class BasisAvatarRecorderWindow : EditorWindow
{
    private const string RecordingsFolderName = "AvatarRecordings";

    // Use the runtime recorder's constants so the format can't drift
    private const int MuscleCount = BasisAvatarRecorder.MuscleCount;

    private Vector2 _listScroll;
    private Vector2 _graphScroll;

    private FileInfo[] _recordingFiles = Array.Empty<FileInfo>();
    private int _selectedIndex = -1;

    // Parsed data for the current file
    private string _loadedPath;
    private int _frameCount;

    private float[] _intervals; // IntervalSeconds per frame
    private Vector3[] _positions;
    private float[] _rotY;      // rotation around Y (in degrees)
    private float[] _scales;

    [MenuItem("Basis/Avatar/Recorder", false, 140)]
    public static void ShowWindow()
    {
        var window = GetWindow<BasisAvatarRecorderWindow>(BasisEditorLocalization.Get("editor.recorder.window.title"));
        window.minSize = new Vector2(450, 300);
        window.RefreshFileList();
    }

    private void OnEnable()
    {
        RefreshFileList();
    }

    private void OnGUI()
    {
        BasisEditorUI.Header(BasisEditorLocalization.Get("editor.recorder.window.title"),
            BasisEditorLocalization.Get("editor.recorder.window.subtitle"));

        DrawRecordingControls();

        EditorGUILayout.Space();
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.recorder.window.recordingsOnDisk"));

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.refresh"), GUILayout.Width(80)))
            {
                RefreshFileList();
            }
        }

        EditorGUILayout.Space();
        DrawRecordingList();

        EditorGUILayout.Space();
        DrawSelectedFileDetails();

        EditorGUILayout.Space();
        DrawGraphs();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Top: start/stop + status + open folder
    // ─────────────────────────────────────────────────────────────────────────────

    private void DrawRecordingControls()
    {
        GUILayout.Label(BasisEditorLocalization.Get("editor.recorder.window.title"), EditorStyles.boldLabel);

        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.recorder.window.status"), BasisAvatarRecorderDriver.State.ToString());

        EditorGUILayout.Space(4);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = !BasisAvatarRecorderDriver.IsBusy;
            if (BasisEditorUI.PrimaryButton(BasisEditorLocalization.Get("editor.recorder.window.startRecording"), 28f))
            {
                BasisAvatarRecorderDriver.RequestStart(0f, false, 0f);
                RefreshFileList();
            }

            GUI.enabled = BasisAvatarRecorderDriver.IsBusy;
            if (BasisEditorUI.PrimaryButton(BasisEditorLocalization.Get("editor.recorder.window.stopRecording"), 28f))
            {
                BasisAvatarRecorderDriver.RequestStop();
                RefreshFileList();
            }

            GUI.enabled = true;
        }

        string dir = GetRecordingsDirectory();

        EditorGUILayout.HelpBox(
            BasisEditorLocalization.Get("editor.recorder.window.filesWrittenUnder", dir),
            MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.recorder.window.loadAndConvert"), GUILayout.Width(180)))
            {
                LoadAndConvert();
            }
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.recorder.window.openRecordingsFolder"), GUILayout.Width(180)))
            {
                EnsureDirectoryExists(dir);
                EditorUtility.RevealInFinder(dir);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Middle: file list
    // ─────────────────────────────────────────────────────────────────────────────

    private void DrawRecordingList()
    {
        if (_recordingFiles == null || _recordingFiles.Length == 0)
        {
            EditorGUILayout.HelpBox(
                BasisEditorLocalization.Get("editor.recorder.window.noRecordings"),
                MessageType.Info);
            return;
        }

        _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.Height(120));

        for (int i = 0; i < _recordingFiles.Length; i++)
        {
            var fi = _recordingFiles[i];
            if (fi == null || !fi.Exists)
                continue;

            long bytes = 0;
            try { bytes = fi.Length; } catch { }

            int frames = BasisAvatarRecorder.BytesPerFrame > 0
                ? (int)(bytes / BasisAvatarRecorder.BytesPerFrame)
                : 0;

            using (new EditorGUILayout.HorizontalScope())
            {
                bool isSelected = (i == _selectedIndex);
                bool newSelected = GUILayout.Toggle(isSelected, GUIContent.none, GUILayout.Width(20));
                if (newSelected && !isSelected)
                {
                    _selectedIndex = i;
                    LoadSelectedFileData();
                }

                string name = fi.Name;
                string sizeStr = EditorUtility.FormatBytes(bytes);
                string info = BasisEditorLocalization.Get("editor.recorder.window.fileInfo", name, sizeStr, frames);

                EditorGUILayout.LabelField(info);
            }
        }

        EditorGUILayout.EndScrollView();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Selected file info + open folder + delete
    // ─────────────────────────────────────────────────────────────────────────────

    private void DrawSelectedFileDetails()
    {
        if (!HasValidSelection())
            return;

        var fi = _recordingFiles[_selectedIndex];
        long bytes = 0;
        try { bytes = fi.Length; } catch { }

        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.recorder.window.selectedRecording"));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.recorder.window.pathLabel"), fi.FullName);
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.recorder.window.sizeLabel"), EditorUtility.FormatBytes(bytes));
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.recorder.window.framesLabel"), _frameCount.ToString());

        // Total duration from intervals, if available
        float totalTime = 0f;
        if (_intervals != null && _intervals.Length == _frameCount)
        {
            for (int i = 0; i < _intervals.Length; i++)
                totalTime += _intervals[i];
        }
        EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.recorder.window.totalDuration"), BasisEditorLocalization.Get("editor.recorder.window.durationSeconds", totalTime));

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.recorder.window.reloadData"), GUILayout.Width(100)))
            {
                LoadSelectedFileData(force: true);
            }

            if (GUILayout.Button(BasisEditorLocalization.Get("editor.recorder.window.openContainingFolder"), GUILayout.Width(170)))
            {
                EditorUtility.RevealInFinder(fi.FullName);
            }

            if (GUILayout.Button(BasisEditorLocalization.Get("editor.recorder.window.convertToAnimationClip"), GUILayout.Width(200)))
            {
                ConvertSelectedToAnimationClip();
            }

            if (GUILayout.Button(BasisEditorLocalization.Get("editor.recorder.window.deleteFile"), GUILayout.Width(100)))
            {
                bool confirm = EditorUtility.DisplayDialog(
                    BasisEditorLocalization.Get("editor.recorder.window.deleteRecording"),
                    BasisEditorLocalization.Get("editor.recorder.window.deleteConfirm", fi.Name),
                    BasisEditorLocalization.Get("editor.common.delete"), BasisEditorLocalization.Get("editor.common.cancel"));

                if (confirm)
                {
                    try
                    {
                        fi.Delete();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Failed to delete recording: {e}");
                    }

                    RefreshFileList();
                    return;
                }
            }

            GUILayout.FlexibleSpace();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Graphs (position, rotation, scale)
    // ─────────────────────────────────────────────────────────────────────────────

    private void DrawGraphs()
    {
        if (!HasValidSelection() || _frameCount <= 0 ||
            _positions == null || _rotY == null || _scales == null)
        {
            return;
        }

        EditorGUILayout.Space();
        BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.recorder.window.visualisation"));

        _graphScroll = EditorGUILayout.BeginScrollView(_graphScroll);

        // Position graph
        DrawSectionHeader(BasisEditorLocalization.Get("editor.recorder.window.graphPosition"));
        Rect posRect = GUILayoutUtility.GetRect(10, 80, GUILayout.ExpandWidth(true));
        DrawCurveBackground(posRect);
        DrawPositionCurves(posRect);

        // Rotation graph
        DrawSectionHeader(BasisEditorLocalization.Get("editor.recorder.window.graphRotation"));
        Rect rotRect = GUILayoutUtility.GetRect(10, 80, GUILayout.ExpandWidth(true));
        DrawCurveBackground(rotRect);
        DrawSingleCurve(rotRect, _rotY, Color.yellow);

        // Scale graph
        DrawSectionHeader(BasisEditorLocalization.Get("editor.recorder.window.graphScale"));
        Rect scaleRect = GUILayoutUtility.GetRect(10, 60, GUILayout.ExpandWidth(true));
        DrawCurveBackground(scaleRect);
        DrawSingleCurve(scaleRect, _scales, Color.cyan);

        EditorGUILayout.EndScrollView();
    }

    private void DrawSectionHeader(string label)
    {
        var style = new GUIStyle(EditorStyles.miniBoldLabel)
        {
            margin = new RectOffset(4, 4, 6, 2)
        };
        EditorGUILayout.LabelField(label, style);
    }

    private void DrawCurveBackground(Rect rect)
    {
        EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f, 1f));

        Handles.color = new Color(1f, 1f, 1f, 0.1f);
        int verticalLines = 10;
        for (int i = 0; i <= verticalLines; i++)
        {
            float x = Mathf.Lerp(rect.x, rect.xMax, i / (float)verticalLines);
            Handles.DrawLine(new Vector2(x, rect.yMin), new Vector2(x, rect.yMax));
        }

        // Center horizontal line
        Handles.color = new Color(1f, 1f, 1f, 0.15f);
        float midY = (rect.yMin + rect.yMax) * 0.5f;
        Handles.DrawLine(new Vector2(rect.x, midY), new Vector2(rect.xMax, midY));
    }

    private void DrawPositionCurves(Rect rect)
    {
        float[] xs = new float[_frameCount];
        float[] ys = new float[_frameCount];
        float[] zs = new float[_frameCount];

        for (int i = 0; i < _frameCount; i++)
        {
            xs[i] = _positions[i].x;
            ys[i] = _positions[i].y;
            zs[i] = _positions[i].z;
        }

        DrawSingleCurve(rect, xs, Color.red);
        DrawSingleCurve(rect, ys, Color.green);
        DrawSingleCurve(rect, zs, Color.blue);
    }

    private void DrawSingleCurve(Rect rect, float[] values, Color color)
    {
        if (values == null || values.Length == 0)
            return;

        int count = values.Length;
        if (count < 2)
            return;

        float min = values[0];
        float max = values[0];
        for (int i = 1; i < count; i++)
        {
            if (values[i] < min) min = values[i];
            if (values[i] > max) max = values[i];
        }

        if (Mathf.Approximately(min, max))
        {
            min -= 0.5f;
            max += 0.5f;
        }

        int maxPoints = 500;
        int step = Mathf.Max(1, count / maxPoints);

        Handles.color = color;
        Vector3 prev = Vector3.zero;
        bool hasPrev = false;

        for (int i = 0; i < count; i += step)
        {
            float t = i / (float)(count - 1);
            float normalized = Mathf.InverseLerp(min, max, values[i]);
            float x = Mathf.Lerp(rect.x, rect.xMax, t);
            float y = Mathf.Lerp(rect.yMax, rect.yMin, normalized);
            Vector3 current = new Vector3(x, y, 0);

            if (hasPrev)
            {
                Handles.DrawLine(prev, current);
            }

            prev = current;
            hasPrev = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Data loading
    // ─────────────────────────────────────────────────────────────────────────────

    private bool HasValidSelection()
    {
        return _recordingFiles != null &&
               _recordingFiles.Length > 0 &&
               _selectedIndex >= 0 &&
               _selectedIndex < _recordingFiles.Length &&
               _recordingFiles[_selectedIndex] != null &&
               _recordingFiles[_selectedIndex].Exists;
    }

    private void LoadSelectedFileData(bool force = false)
    {
        if (!HasValidSelection())
            return;

        var fi = _recordingFiles[_selectedIndex];
        string path = fi.FullName;

        if (!force && _loadedPath == path && _positions != null)
            return; // already loaded

        try
        {
            long bytes = fi.Length;
            if (bytes <= 0 || bytes % BasisAvatarRecorder.BytesPerFrame != 0)
            {
                Debug.LogWarning(
                    $"File size is not a multiple of BytesPerFrame: {bytes} " +
                    $"(BytesPerFrame={BasisAvatarRecorder.BytesPerFrame})");
            }

            _frameCount = BasisAvatarRecorder.BytesPerFrame > 0
                ? (int)(bytes / BasisAvatarRecorder.BytesPerFrame)
                : 0;

            if (_frameCount <= 0)
            {
                Debug.LogWarning("No frames detected in file.");
                ClearLoadedData();
                return;
            }

            _intervals = new float[_frameCount];
            _positions = new Vector3[_frameCount];
            _rotY = new float[_frameCount];
            _scales = new float[_frameCount];

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs))
            {
                for (int i = 0; i < _frameCount; i++)
                {
                    // IntervalSeconds (new first float)
                    float interval = br.ReadSingle();
                    _intervals[i] = interval;

                    // Rotation (Quaternion)
                    float rx = br.ReadSingle();
                    float ry = br.ReadSingle();
                    float rz = br.ReadSingle();
                    float rw = br.ReadSingle();
                    Quaternion rot = new Quaternion(rx, ry, rz, rw);

                    // Position
                    float px = br.ReadSingle();
                    float py = br.ReadSingle();
                    float pz = br.ReadSingle();
                    _positions[i] = new Vector3(px, py, pz);

                    // Muscles (skip)
                    for (int m = 0; m < MuscleCount; m++)
                    {
                        br.ReadSingle();
                    }

                    // Scale
                    float s = br.ReadSingle();
                    _scales[i] = s;

                    // Store Y rotation (degrees)
                    _rotY[i] = rot.eulerAngles.y;
                }
            }

            _loadedPath = path;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load avatar recording: {e}");
            ClearLoadedData();
        }

        Repaint();
    }

    private void ClearLoadedData()
    {
        _loadedPath = null;
        _frameCount = 0;

        _intervals = null;
        _positions = null;
        _rotY = null;
        _scales = null;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // File list refresh / helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private void RefreshFileList()
    {
        try
        {
            string directory = GetRecordingsDirectory();

            if (!Directory.Exists(directory))
            {
                _recordingFiles = Array.Empty<FileInfo>();
                _selectedIndex = -1;
                ClearLoadedData();
                return;
            }

            var dirInfo = new DirectoryInfo(directory);
            _recordingFiles = dirInfo.GetFiles("AvatarRecord_*.dat", SearchOption.TopDirectoryOnly);

            Array.Sort(_recordingFiles,
                (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc)); // newest first

            if (_recordingFiles.Length == 0)
            {
                _selectedIndex = -1;
                ClearLoadedData();
            }
            else if (_selectedIndex < 0 || _selectedIndex >= _recordingFiles.Length)
            {
                _selectedIndex = 0;
                LoadSelectedFileData(force: true);
            }
            else
            {
                LoadSelectedFileData(force: true);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to refresh avatar recordings: {e}");
            _recordingFiles = Array.Empty<FileInfo>();
            _selectedIndex = -1;
            ClearLoadedData();
        }

        Repaint();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Conversion to a humanoid AnimationClip
    // ─────────────────────────────────────────────────────────────────────────────

    private void ConvertSelectedToAnimationClip()
    {
        if (!HasValidSelection())
            return;
        ConvertFileToAnimationClip(_recordingFiles[_selectedIndex].FullName);
    }

    private void LoadAndConvert()
    {
        string startDir = GetRecordingsDirectory();
        EnsureDirectoryExists(startDir);

        string path = EditorUtility.OpenFilePanel(BasisEditorLocalization.Get("editor.recorder.window.selectRecording"), startDir, "dat");
        if (!string.IsNullOrEmpty(path))
            ConvertFileToAnimationClip(path);
    }

    private void ConvertFileToAnimationClip(string path)
    {
        if (!File.Exists(path))
        {
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.recorder.window.convertToAnimationClip"), BasisEditorLocalization.Get("editor.recorder.window.fileNotFound", path), BasisEditorLocalization.Get("editor.common.ok"));
            return;
        }

        long bytes;
        try { bytes = new FileInfo(path).Length; }
        catch (Exception e) { Debug.LogError($"Failed to read recording size: {e}"); return; }

        int frameCount = (int)(bytes / BasisAvatarRecorder.BytesPerFrame);
        if (frameCount <= 0)
        {
            EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.recorder.window.convertToAnimationClip"), BasisEditorLocalization.Get("editor.recorder.window.noFrames"), BasisEditorLocalization.Get("editor.common.ok"));
            return;
        }

        int muscleCount = MuscleCount;

        float[] times = new float[frameCount];
        float[] rootTx = new float[frameCount];
        float[] rootTy = new float[frameCount];
        float[] rootTz = new float[frameCount];
        float[] rootQx = new float[frameCount];
        float[] rootQy = new float[frameCount];
        float[] rootQz = new float[frameCount];
        float[] rootQw = new float[frameCount];
        float[][] muscles = new float[muscleCount][];
        for (int m = 0; m < muscleCount; m++)
            muscles[m] = new float[frameCount];

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            float t = 0f;
            for (int i = 0; i < frameCount; i++)
            {
                float interval = br.ReadSingle();
                if (i > 0) t += interval;
                times[i] = t;

                rootQx[i] = br.ReadSingle();
                rootQy[i] = br.ReadSingle();
                rootQz[i] = br.ReadSingle();
                rootQw[i] = br.ReadSingle();

                rootTx[i] = br.ReadSingle();
                rootTy[i] = br.ReadSingle();
                rootTz[i] = br.ReadSingle();

                for (int m = 0; m < muscleCount; m++)
                    muscles[m][i] = br.ReadSingle();

                br.ReadSingle();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to read recording for conversion: {e}");
            return;
        }

        string defaultName = Path.GetFileNameWithoutExtension(path);
        string savePath = EditorUtility.SaveFilePanelInProject(
            BasisEditorLocalization.Get("editor.recorder.window.saveClipTitle"),
            defaultName,
            "anim",
            BasisEditorLocalization.Get("editor.recorder.window.saveClipMessage"));

        if (string.IsNullOrEmpty(savePath))
            return;

        AnimationClip clip = new AnimationClip();

        string[] muscleNames = BuildMuscleCurveNames();
        for (int m = 0; m < muscleCount && m < muscleNames.Length; m++)
            clip.SetCurve("", typeof(Animator), muscleNames[m], BuildCurve(times, muscles[m]));

        clip.SetCurve("", typeof(Animator), "RootT.x", BuildCurve(times, rootTx));
        clip.SetCurve("", typeof(Animator), "RootT.y", BuildCurve(times, rootTy));
        clip.SetCurve("", typeof(Animator), "RootT.z", BuildCurve(times, rootTz));
        clip.SetCurve("", typeof(Animator), "RootQ.x", BuildCurve(times, rootQx));
        clip.SetCurve("", typeof(Animator), "RootQ.y", BuildCurve(times, rootQy));
        clip.SetCurve("", typeof(Animator), "RootQ.z", BuildCurve(times, rootQz));
        clip.SetCurve("", typeof(Animator), "RootQ.w", BuildCurve(times, rootQw));

        float duration = times[frameCount - 1];
        clip.frameRate = duration > 0f ? Mathf.Clamp(Mathf.Round((frameCount - 1) / duration), 1f, 240f) : 60f;
        clip.EnsureQuaternionContinuity();

        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(savePath);
        if (existing != null)
        {
            EditorUtility.CopySerialized(clip, existing);
            AssetDatabase.SaveAssets();
        }
        else
        {
            AssetDatabase.CreateAsset(clip, savePath);
            AssetDatabase.SaveAssets();
        }
        AssetDatabase.Refresh();

        var saved = AssetDatabase.LoadAssetAtPath<AnimationClip>(savePath);
        EditorGUIUtility.PingObject(saved);
        Debug.Log($"Converted {frameCount} frames to humanoid AnimationClip: {savePath}");
    }

    private static AnimationCurve BuildCurve(float[] times, float[] values)
    {
        int n = times.Length;
        Keyframe[] keys = new Keyframe[n];
        for (int i = 0; i < n; i++)
        {
            float inTangent = 0f;
            float outTangent = 0f;

            if (i > 0)
            {
                float dt = times[i] - times[i - 1];
                if (dt > 1e-6f) inTangent = (values[i] - values[i - 1]) / dt;
            }
            if (i < n - 1)
            {
                float dt = times[i + 1] - times[i];
                if (dt > 1e-6f) outTangent = (values[i + 1] - values[i]) / dt;
            }

            keys[i] = new Keyframe(times[i], values[i], inTangent, outTangent);
        }
        return new AnimationCurve(keys);
    }

    private static string[] BuildMuscleCurveNames()
    {
        string[] names = (string[])HumanTrait.MuscleName.Clone();

        Dictionary<string, string> fingerMap = new Dictionary<string, string>();
        string[] sides = { "Left", "Right" };
        string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
        foreach (string side in sides)
        {
            foreach (string finger in fingers)
            {
                fingerMap[$"{side} {finger} 1 Stretched"] = $"{side}Hand.{finger}.1 Stretched";
                fingerMap[$"{side} {finger} Spread"] = $"{side}Hand.{finger}.Spread";
                fingerMap[$"{side} {finger} 2 Stretched"] = $"{side}Hand.{finger}.2 Stretched";
                fingerMap[$"{side} {finger} 3 Stretched"] = $"{side}Hand.{finger}.3 Stretched";
            }
        }

        for (int i = 0; i < names.Length; i++)
        {
            if (fingerMap.TryGetValue(names[i], out string mapped))
                names[i] = mapped;
        }
        return names;
    }

    private string GetRecordingsDirectory()
    {
        return Path.Combine(Application.persistentDataPath, RecordingsFolderName);
    }

    private void EnsureDirectoryExists(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
#endif
