#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;

public class BasisHeightDriverWindow : EditorWindow
{
    private const string Pref_RepaintWhilePlaying = "BasisHeightDriverWindow.RepaintWhilePlaying";
    private const string Pref_AutoRepaintInterval = "BasisHeightDriverWindow.AutoRepaintInterval";

    private Vector2 _scroll;
    private bool _repaintWhilePlaying;
    private float _autoRepaintInterval = 0.15f;
    private double _nextRepaintTime;

    [MenuItem("Basis/Calibration/Basis Height Driver")]
    public static void Open()
    {
        var w = GetWindow<BasisHeightDriverWindow>("Basis Height Driver");
        w.minSize = new Vector2(560, 600);
        w.Show();
    }

    private void OnEnable()
    {
        _repaintWhilePlaying = EditorPrefs.GetBool(Pref_RepaintWhilePlaying, true);
        _autoRepaintInterval = EditorPrefs.GetFloat(Pref_AutoRepaintInterval, 0.15f);

        EditorApplication.playModeStateChanged += _ => Repaint();
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        EditorPrefs.SetBool(Pref_RepaintWhilePlaying, _repaintWhilePlaying);
        EditorPrefs.SetFloat(Pref_AutoRepaintInterval, _autoRepaintInterval);

        EditorApplication.update -= OnEditorUpdate;
    }

    private void OnEditorUpdate()
    {
        if (!_repaintWhilePlaying) return;
        if (!EditorApplication.isPlaying) return;

        var t = EditorApplication.timeSinceStartup;
        if (t >= _nextRepaintTime)
        {
            _nextRepaintTime = t + Mathf.Max(0.02f, _autoRepaintInterval);
            Repaint();
        }
    }

    private void OnGUI()
    {
        DrawHeader();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        DrawRuntimeStatus();
        EditorGUILayout.Space(6);

        DrawActions();
        EditorGUILayout.Space(10);

        DrawAllData();
        EditorGUILayout.Space(10);

        DrawQuickTools();

        EditorGUILayout.EndScrollView();
    }

    private void DrawHeader()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("BasisHeightDriver Debug Panel", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _repaintWhilePlaying = EditorGUILayout.ToggleLeft("Repaint while playing", _repaintWhilePlaying, GUILayout.Width(170));
                EditorGUILayout.LabelField("Interval (sec)", GUILayout.Width(80));
                _autoRepaintInterval = EditorGUILayout.Slider(_autoRepaintInterval, 0.02f, 1.0f);

                if (GUILayout.Button("Repaint Now", GUILayout.Width(110)))
                    Repaint();
            }
        }
    }

    private void DrawRuntimeStatus()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

            EditorGUILayout.LabelField("Play Mode", EditorApplication.isPlaying ? "Playing" : "Not Playing");

            var lp = BasisLocalPlayer.Instance;
            EditorGUILayout.LabelField("BasisLocalPlayer.Instance", lp ? "Present" : "NULL");

            string device = "Unknown";
            try { device = BasisDeviceManagement.IsUserInDesktop() ? "Desktop" : "VR/Other"; }
            catch { /* ignore */ }
            EditorGUILayout.LabelField("Device", device);
        }
    }

    private void DrawActions()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

            GUI.enabled = EditorApplication.isPlaying;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Capture Player Height"))
                    BasisHeightDriver.CapturePlayerHeight();

                if (GUILayout.Button("Capture Avatar Height (T-Pose)"))
                    BasisHeightDriver.CaptureAvatarHeightDuringTpose();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Apply Scale & Height"))
                    BasisHeightDriver.ApplyScaleAndHeight();

                if (GUILayout.Button("On Avatar FB Calibration"))
                    BasisHeightDriver.OnAvatarFBCalibration();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                var mode = SMModuleCalibration.HeightMode;
                var newMode = (BasisSelectedHeightMode)EditorGUILayout.EnumPopup("HeightMode", mode);
                if (newMode != mode)
                    SMModuleCalibration.HeightMode = newMode;

                if (GUILayout.Button("Revaluate Unscaled", GUILayout.Width(150)))
                    BasisHeightDriver.RevaluateUnscaledHeight(newMode);

                if (GUILayout.Button("Choose Height", GUILayout.Width(120)))
                    BasisHeightDriver.ChooseHeightToUse(newMode);
            }

            GUI.enabled = true;
        }
    }

    private void DrawAllData()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Data", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "These are live static values. Editing here mutates the driver immediately.\n" +
                "Red tint = NaN/Inf/<=0 (suspicious for meters/ratios).",
                MessageType.Info
            );

            DrawSection("Constants / Toggles", () =>
            {
                EditorGUILayout.LabelField("FallbackHeightInMeters", BasisHeightDriver.FallbackHeightInMeters.ToString("0.###"));

                BasisHeightDriver.AdditionalPlayerHeight =
                    EditorGUILayout.FloatField("AdditionalPlayerHeight", BasisHeightDriver.AdditionalPlayerHeight);

                BasisHeightDriver.AppliedUpScale =
                    EditorGUILayout.FloatField("AppliedUpScale", BasisHeightDriver.AppliedUpScale);

                BasisHeightDriver.ScaledToMatchValue =
                    EditorGUILayout.FloatField("ScaledToMatchValue", BasisHeightDriver.ScaledToMatchValue);

                BasisHeightDriver.DeviceScale =
                    EditorGUILayout.FloatField("DeviceScale", BasisHeightDriver.DeviceScale);
            });

            DrawSection("Player Metrics", () =>
            {
                BasisHeightDriver.PlayerEyeHeight = FloatFieldWarn("PlayerEyeHeight", BasisHeightDriver.PlayerEyeHeight);
                BasisHeightDriver.PlayerArmSpan = FloatFieldWarn("PlayerArmSpan", BasisHeightDriver.PlayerArmSpan);
            });

            DrawSection("Avatar Metrics", () =>
            {
                BasisHeightDriver.AvatarEyeHeight = FloatFieldWarn("AvatarEyeHeight", BasisHeightDriver.AvatarEyeHeight);
                BasisHeightDriver.AvatarArmSpan = FloatFieldWarn("AvatarArmSpan", BasisHeightDriver.AvatarArmSpan);
            });

            DrawSection("Selected Scaled", () =>
            {
                BasisHeightDriver.SelectedScaledPlayerHeight =
                    FloatFieldWarn("SelectedScaledPlayerHeight", BasisHeightDriver.SelectedScaledPlayerHeight);

                BasisHeightDriver.SelectedScaledAvatarHeight =
                    FloatFieldWarn("SelectedScaledAvatarHeight", BasisHeightDriver.SelectedScaledAvatarHeight);
            });

            DrawSection("Selected Unscaled", () =>
            {
                BasisHeightDriver.SelectedUnScaledPlayerHeight =
                    FloatFieldWarn("SelectedUnScaledPlayerHeight", BasisHeightDriver.SelectedUnScaledPlayerHeight);

                BasisHeightDriver.SelectedUnScaledAvatarHeight =
                    FloatFieldWarn("SelectedUnScaledAvatarHeight", BasisHeightDriver.SelectedUnScaledAvatarHeight);
            });

            DrawSection("Ratios", () =>
            {
                BasisHeightDriver.PlayerToAvatarRatioScaled =
                    FloatFieldWarn("PlayerToAvatarRatioScaled", BasisHeightDriver.PlayerToAvatarRatioScaled);

                BasisHeightDriver.AvatarToPlayerRatioScaled =
                    FloatFieldWarn("AvatarToPlayerRatioScaled", BasisHeightDriver.AvatarToPlayerRatioScaled);

                BasisHeightDriver.PlayerToDefaultRatioScaledWithAvatarScale =
                    FloatFieldWarn("PlayerToDefaultRatioScaled", BasisHeightDriver.PlayerToDefaultRatioScaledWithAvatarScale);

                BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale =
                    FloatFieldWarn("AvatarToDefaultRatioScaled", BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale);
            });
        }
    }

    private void DrawQuickTools()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Quick Tools", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Copy Snapshot to Clipboard"))
                {
                    EditorGUIUtility.systemCopyBuffer = DumpSnapshot();
                }

                if (GUILayout.Button("Log Snapshot"))
                {
                    Debug.Log(DumpSnapshot());
                }
            }
        }
    }

    private static void DrawSection(string title, System.Action body)
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            body?.Invoke();
        }
    }

    private static float FloatFieldWarn(string label, float value)
    {
        var old = GUI.color;

        if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
            GUI.color = new Color(1f, 0.75f, 0.75f);

        float v = EditorGUILayout.FloatField(label, value);
        GUI.color = old;
        return v;
    }

    private static string DumpSnapshot()
    {
        return
            "=== BasisHeightDriver Snapshot ===\n" +
            $"AdditionalPlayerHeight: {BasisHeightDriver.AdditionalPlayerHeight:0.######}\n" +
            $"AppliedUpScale: {BasisHeightDriver.AppliedUpScale:0.######}\n" +
            $"ScaledToMatchValue: {BasisHeightDriver.ScaledToMatchValue:0.######}\n" +
            $"DeviceScale: {BasisHeightDriver.DeviceScale:0.######}\n" +
            $"PlayerEyeHeight: {BasisHeightDriver.PlayerEyeHeight:0.######}\n" +
            $"PlayerArmSpan: {BasisHeightDriver.PlayerArmSpan:0.######}\n" +
            $"AvatarEyeHeight: {BasisHeightDriver.AvatarEyeHeight:0.######}\n" +
            $"AvatarArmSpan: {BasisHeightDriver.AvatarArmSpan:0.######}\n" +
            $"SelectedScaledPlayerHeight: {BasisHeightDriver.SelectedScaledPlayerHeight:0.######}\n" +
            $"SelectedScaledAvatarHeight: {BasisHeightDriver.SelectedScaledAvatarHeight:0.######}\n" +
            $"SelectedUnScaledPlayerHeight: {BasisHeightDriver.SelectedUnScaledPlayerHeight:0.######}\n" +
            $"SelectedUnScaledAvatarHeight: {BasisHeightDriver.SelectedUnScaledAvatarHeight:0.######}\n" +
            $"PlayerToAvatarRatioScaled: {BasisHeightDriver.PlayerToAvatarRatioScaled:0.######}\n" +
            $"AvatarToPlayerRatioScaled: {BasisHeightDriver.AvatarToPlayerRatioScaled:0.######}\n" +
            $"PlayerToDefaultRatioScaled: {BasisHeightDriver.PlayerToDefaultRatioScaledWithAvatarScale:0.######}\n" +
            $"AvatarToDefaultRatioScaled: {BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale:0.######}\n" +
            $"HeightMode (SMModuleCalibration): {SMModuleCalibration.HeightMode}\n";
    }
}
#endif
