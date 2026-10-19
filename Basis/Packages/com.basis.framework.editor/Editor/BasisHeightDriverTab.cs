#if UNITY_EDITOR
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;

public sealed class BasisHeightDriverTab : BasisEditorTabPage
{
        public override string Title => BasisEditorLocalization.Get("editor.heightDriver.title");
        public override string Subtitle =>
            BasisEditorLocalization.Get("editor.heightDriver.subtitle");

    private const string Pref_RepaintWhilePlaying = "BasisHeightDriverWindow.RepaintWhilePlaying";
    private const string Pref_AutoRepaintInterval = "BasisHeightDriverWindow.AutoRepaintInterval";

    private Vector2 _scroll;
    private bool _repaintWhilePlaying;
    private float _autoRepaintInterval = 0.15f;
    private double _nextRepaintTime;

    public override void OnEnable()
    {
        _repaintWhilePlaying = EditorPrefs.GetBool(Pref_RepaintWhilePlaying, true);
        _autoRepaintInterval = EditorPrefs.GetFloat(Pref_AutoRepaintInterval, 0.15f);

        EditorApplication.playModeStateChanged += _ => Host.Repaint();
        EditorApplication.update += OnEditorUpdate;
    }

    public override void OnDisable()
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
            Host.Repaint();
        }
    }

    public override void Draw()
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
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.heightDriver.debugPanelTitle"));

            using (new EditorGUILayout.HorizontalScope())
            {
                _repaintWhilePlaying = EditorGUILayout.ToggleLeft(BasisEditorLocalization.Get("editor.heightDriver.repaintWhilePlaying"), _repaintWhilePlaying, GUILayout.Width(170));
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.heightDriver.interval"), GUILayout.Width(80));
                _autoRepaintInterval = EditorGUILayout.Slider(_autoRepaintInterval, 0.02f, 1.0f);

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.heightDriver.repaintNow"), GUILayout.Width(110)))
                    Host.Repaint();
            }
        }
    }

    private void DrawRuntimeStatus()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.heightDriver.runtimeStatusSection"));

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.heightDriver.playMode"), EditorApplication.isPlaying ? BasisEditorLocalization.Get("editor.heightDriver.playing") : BasisEditorLocalization.Get("editor.heightDriver.notPlaying"));

            var lp = BasisLocalPlayer.Instance;
            EditorGUILayout.LabelField("BasisLocalPlayer.Instance", lp ? BasisEditorLocalization.Get("editor.heightDriver.present") : "NULL");

            string device = BasisEditorLocalization.Get("editor.common.unknown");
            try { device = BasisDeviceManagement.IsUserInDesktop() ? BasisEditorLocalization.Get("editor.heightDriver.desktop") : BasisEditorLocalization.Get("editor.heightDriver.vrOther"); }
            catch { /* ignore */ }
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.heightDriver.device"), device);
        }
    }

    private void DrawActions()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.heightDriver.actionsSection"));

            GUI.enabled = EditorApplication.isPlaying;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.heightDriver.capturePlayerHeight")))
                    BasisHeightDriver.CapturePlayerHeight();

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.heightDriver.captureAvatarHeight")))
                    BasisHeightDriver.CaptureAvatarHeightDuringTpose();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.heightDriver.applyScaleAndHeight")))
                    BasisHeightDriver.ApplyScaleAndHeight();

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.heightDriver.onAvatarFbCalibration")))
                    BasisHeightDriver.OnAvatarFBCalibration();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                var mode = SMModuleCalibration.HeightMode;
                var newMode = (BasisSelectedHeightMode)EditorGUILayout.EnumPopup("HeightMode", mode);
                if (newMode != mode)
                    SMModuleCalibration.HeightMode = newMode;

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.heightDriver.revaluateUnscaled"), GUILayout.Width(150)))
                    BasisHeightDriver.RevaluateUnscaledHeight(newMode);

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.heightDriver.chooseHeight"), GUILayout.Width(120)))
                    BasisHeightDriver.ChooseHeightToUse(newMode);
            }

            GUI.enabled = true;
        }
    }

    private void DrawAllData()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.heightDriver.dataSection"));
            EditorGUILayout.HelpBox(
                BasisEditorLocalization.Get("editor.heightDriver.dataHelp"),
                MessageType.Info
            );

            DrawSection(BasisEditorLocalization.Get("editor.heightDriver.sectionConstants"), () =>
            {
                EditorGUILayout.LabelField("FallbackHeightInMeters", BasisHeightDriver.FallbackHeightInMeters.ToString("0.###"));

                BasisHeightDriver.AppliedUpScale =
                    EditorGUILayout.FloatField("AppliedUpScale", BasisHeightDriver.AppliedUpScale);

                BasisHeightDriver.ScaledToMatchValue =
                    EditorGUILayout.FloatField("ScaledToMatchValue", BasisHeightDriver.ScaledToMatchValue);

                BasisHeightDriver.DeviceScale =
                    EditorGUILayout.FloatField("DeviceScale", BasisHeightDriver.DeviceScale);
            });

            DrawSection(BasisEditorLocalization.Get("editor.heightDriver.sectionPlayerMetrics"), () =>
            {
                BasisHeightDriver.PlayerEyeHeight = FloatFieldWarn("PlayerEyeHeight", BasisHeightDriver.PlayerEyeHeight);
                BasisHeightDriver.PlayerArmSpan = FloatFieldWarn("PlayerArmSpan", BasisHeightDriver.PlayerArmSpan);
            });

            DrawSection(BasisEditorLocalization.Get("editor.heightDriver.sectionAvatarMetrics"), () =>
            {
                BasisHeightDriver.AvatarEyeHeight = FloatFieldWarn("AvatarEyeHeight", BasisHeightDriver.AvatarEyeHeight);
                BasisHeightDriver.AvatarArmSpan = FloatFieldWarn("AvatarArmSpan", BasisHeightDriver.AvatarArmSpan);
            });

            DrawSection(BasisEditorLocalization.Get("editor.heightDriver.sectionSelectedScaled"), () =>
            {
                BasisHeightDriver.SelectedScaledPlayerHeight =
                    FloatFieldWarn("SelectedScaledPlayerHeight", BasisHeightDriver.SelectedScaledPlayerHeight);

                BasisHeightDriver.SelectedScaledAvatarHeight =
                    FloatFieldWarn("SelectedScaledAvatarHeight", BasisHeightDriver.SelectedScaledAvatarHeight);
            });

            DrawSection(BasisEditorLocalization.Get("editor.heightDriver.sectionSelectedUnscaled"), () =>
            {
                BasisHeightDriver.SelectedUnScaledPlayerHeight =
                    FloatFieldWarn("SelectedUnScaledPlayerHeight", BasisHeightDriver.SelectedUnScaledPlayerHeight);

                BasisHeightDriver.SelectedUnScaledAvatarHeight =
                    FloatFieldWarn("SelectedUnScaledAvatarHeight", BasisHeightDriver.SelectedUnScaledAvatarHeight);
            });

            DrawSection(BasisEditorLocalization.Get("editor.heightDriver.sectionRatios"), () =>
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
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.heightDriver.quickToolsSection"));

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.heightDriver.copySnapshot")))
                {
                    EditorGUIUtility.systemCopyBuffer = DumpSnapshot();
                }

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.heightDriver.logSnapshot")))
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
