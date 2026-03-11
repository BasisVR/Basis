#if UNITY_EDITOR
using System;
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

    [MenuItem("Basis/Avatar Recorder")]
    public static void ShowWindow()
    {
        var window = GetWindow<BasisAvatarRecorderWindow>("Avatar Recorder");
        window.minSize = new Vector2(450, 300);
        window.RefreshFileList();
    }

    private void OnEnable()
    {
        RefreshFileList();
    }

    private void OnGUI()
    {
        DrawRecordingControls();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Recordings on Disk", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", GUILayout.Width(80)))
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
        GUILayout.Label("Avatar Recorder", EditorStyles.boldLabel);

        EditorGUILayout.LabelField("Status:",
            BasisAvatarRecorder.IsRecording ? "Recording" : "Idle");

        EditorGUILayout.Space(4);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = !BasisAvatarRecorder.IsRecording;
            if (GUILayout.Button("Start Recording", GUILayout.Height(28)))
            {
                BasisAvatarRecorder.StartRecording();
                RefreshFileList();
            }

            GUI.enabled = BasisAvatarRecorder.IsRecording;
            if (GUILayout.Button("Stop Recording", GUILayout.Height(28)))
            {
                BasisAvatarRecorder.StopRecording();
                RefreshFileList();
            }

            GUI.enabled = true;
        }

        string dir = GetRecordingsDirectory();

        EditorGUILayout.HelpBox(
            $"Files are written under:\n{dir}",
            MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Open Recordings Folder", GUILayout.Width(180)))
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
                "No recordings found yet.\nRecord something and hit Refresh.",
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
                string info = $"{name}  |  {sizeStr}  |  {frames} frames";

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

        EditorGUILayout.LabelField("Selected Recording", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Path:", fi.FullName);
        EditorGUILayout.LabelField("Size:", EditorUtility.FormatBytes(bytes));
        EditorGUILayout.LabelField("Frames:", _frameCount.ToString());

        // Total duration from intervals, if available
        float totalTime = 0f;
        if (_intervals != null && _intervals.Length == _frameCount)
        {
            for (int i = 0; i < _intervals.Length; i++)
                totalTime += _intervals[i];
        }
        EditorGUILayout.LabelField("Total Duration:", $"{totalTime:F3} seconds");

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Reload Data", GUILayout.Width(100)))
            {
                LoadSelectedFileData(force: true);
            }

            if (GUILayout.Button("Open Containing Folder", GUILayout.Width(170)))
            {
                EditorUtility.RevealInFinder(fi.FullName);
            }

            if (GUILayout.Button("Delete File", GUILayout.Width(100)))
            {
                bool confirm = EditorUtility.DisplayDialog(
                    "Delete Recording",
                    $"Are you sure you want to delete:\n\n{fi.Name}",
                    "Delete", "Cancel");

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
        EditorGUILayout.LabelField("Visualisation (by frame index)", EditorStyles.boldLabel);

        _graphScroll = EditorGUILayout.BeginScrollView(_graphScroll);

        // Position graph
        DrawSectionHeader("Position (X / Y / Z)");
        Rect posRect = GUILayoutUtility.GetRect(10, 80, GUILayout.ExpandWidth(true));
        DrawCurveBackground(posRect);
        DrawPositionCurves(posRect);

        // Rotation graph
        DrawSectionHeader("Rotation Y (degrees from Quaternion)");
        Rect rotRect = GUILayoutUtility.GetRect(10, 80, GUILayout.ExpandWidth(true));
        DrawCurveBackground(rotRect);
        DrawSingleCurve(rotRect, _rotY, Color.yellow);

        // Scale graph
        DrawSectionHeader("Scale");
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
