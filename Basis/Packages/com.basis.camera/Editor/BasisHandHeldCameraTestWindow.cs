using System;
using System.Collections.Generic;
using System.Text;
using Basis.BasisUI;
using Basis.Editor.Localization;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal;
using UnityCamera = UnityEngine.Camera;

// Not Basis.Camera.* — a namespace segment named Camera shadows UnityEngine.Camera.
namespace Basis.HandHeldCamera.Editor
{
    /// <summary>
    /// A test bench for the hand-held camera: which cameras are out, what each one is rendering and
    /// why, a live look at its feed, and whether Direct To Screen is really putting that feed on the
    /// monitor — with the device switch that hands the monitor back and takes it over again.
    ///
    /// <para>
    /// The Direct To Screen check is not inferred from the setting: the blit pass records what it
    /// drew on every frame it runs, and the window reads that back, so "presenting" and "actually
    /// drawing" are shown as two different facts. Out of play mode only the setup half applies — the
    /// platform and whether the pipeline carries the renderer feature.
    /// </para>
    /// </summary>
    public sealed class BasisHandHeldCameraTestWindow : EditorWindow
    {
        private const float FeedPreviewHeight = 180f;
        private const double RepaintInterval = 1.0 / 30.0;

        private Vector2 _scroll;
        private int _selected;
        private double _lastRepaint;
        private bool _showMonitorPreview = true;

        [MenuItem("Basis/Debug/Hand-Held Camera", false, 625)]
        public static void Open()
        {
            GetWindow<BasisHandHeldCameraTestWindow>(BasisEditorLocalization.Get("editor.camera.testWindow.title")).Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        // The feed is a live picture, so the window follows it at a rate a preview reads as live
        // rather than the inspector's few ticks a second. Nothing to follow out of play mode.
        private void OnEditorUpdate()
        {
            if (!Application.isPlaying) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastRepaint < RepaintInterval) return;
            _lastRepaint = now;
            Repaint();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawSetup();

            if (!Application.isPlaying)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.camera.testWindow.enterPlayMode"), MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawDevice();

            BasisHandHeldCamera camera = DrawCameraSelector();
            if (camera != null)
            {
                DrawCameraState(camera);
                DrawDirectToScreen(camera);
            }

            EditorGUILayout.EndScrollView();
        }

        // ---------- Setup: what has to be true before play mode ----------

        private static void DrawSetup()
        {
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.setup"), EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                Status(BasisEditorLocalization.Get("editor.camera.testWindow.platformDesktop"), BasisCameraDirectToScreen.IsSupported,
                    BasisEditorLocalization.Get("editor.camera.testWindow.platformDesktop.tooltip"));

                UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
                if (asset == null)
                {
                    EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.camera.testWindow.noPipelineAsset"), MessageType.Error);
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.pipelineAsset"), asset.name);
                    if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.select"), GUILayout.Width(60f))) Selection.activeObject = asset;
                }

                int index = BasisCameraDirectToScreenOutput.FindRendererWithFeature(asset);
                if (index >= 0)
                {
                    ScriptableRendererData data = asset.rendererDataList[index];
                    Status(BasisEditorLocalization.Get("editor.camera.testWindow.d2sRenderer", index, data.name), true,
                        BasisEditorLocalization.Get("editor.camera.testWindow.d2sRenderer.tooltip"));
                }
                else
                {
                    Status(BasisEditorLocalization.Get("editor.camera.testWindow.d2sRendererNone"), false,
                        BasisEditorLocalization.Get("editor.camera.testWindow.d2sRendererNone.tooltip"));
                }
            }
        }

        // ---------- Device: the hot-swap the mode has to survive ----------

        private static void DrawDevice()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.device"), EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                string mode = BasisDeviceManagement.StaticCurrentMode;
                bool inVR = BasisDeviceManagement.IsCurrentModeVR();
                bool inDesktop = BasisDeviceManagement.IsUserInDesktop();
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.common.mode"), inVR ? BasisEditorLocalization.Get("editor.camera.testWindow.modeVr", mode) : inDesktop ? BasisEditorLocalization.Get("editor.camera.testWindow.modeDesktop", mode) : $"{mode}");

                BasisDeviceManagement manager = BasisDeviceManagement.Instance;
                if (manager == null)
                {
                    EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.noDeviceManagement"));
                    return;
                }
                if (manager.IsSoftSwapped) EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.softSwappedFrom"), manager.AutoSwapPreviousVRMode);

                // Desktop hands the monitor back; VR takes it over again. Both from here, so the
                // round trip can be tested without leaving the editor.
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(inDesktop))
                    {
                        if (GUILayout.Button(BasisEditorLocalization.Get("editor.camera.testWindow.switchToDesktop"))) SwitchMode(manager, BasisConstants.Desktop);
                    }

                    bool canReturnToVR = manager.IsSoftSwapped && !string.IsNullOrEmpty(manager.AutoSwapPreviousVRMode);
                    using (new EditorGUI.DisabledScope(inVR))
                    {
                        if (canReturnToVR)
                        {
                            if (GUILayout.Button(BasisEditorLocalization.Get("editor.camera.testWindow.backTo", manager.AutoSwapPreviousVRMode))) SwitchMode(manager, manager.AutoSwapPreviousVRMode);
                        }
                        else
                        {
                            if (GUILayout.Button(BasisEditorLocalization.Get("editor.camera.testWindow.switchToOpenXr"))) SwitchMode(manager, BasisConstants.OpenXRLoader);
                            if (GUILayout.Button(BasisEditorLocalization.Get("editor.camera.testWindow.switchToOpenVr"))) SwitchMode(manager, BasisConstants.OpenVRLoader);
                        }
                    }
                }
            }
        }

        private static async void SwitchMode(BasisDeviceManagement manager, string mode)
        {
            try
            {
                await manager.SwitchSetMode(mode);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // ---------- Cameras ----------

        private BasisHandHeldCamera DrawCameraSelector()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.cameras"), EditorStyles.boldLabel);

            IReadOnlyList<BasisHandHeldCamera> cameras = BasisHandHeldCameraRegistry.Cameras;
            if (cameras.Count == 0)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.HelpBox(BasisEditorLocalization.Get("editor.camera.testWindow.noCameraOut"), MessageType.Info);
                }
                return null;
            }

            string[] names = new string[cameras.Count];
            for (int Index = 0; Index < cameras.Count; Index++)
            {
                BasisHandHeldCamera entry = cameras[Index];
                names[Index] = entry != null
                    ? entry.IsCameraHidden
                        ? BasisEditorLocalization.Get("editor.camera.testWindow.cameraEntryHidden", Index + 1, entry.gameObject.name)
                        : $"{Index + 1}. {entry.gameObject.name}"
                    : BasisEditorLocalization.Get("editor.camera.testWindow.cameraEntryDestroyed", Index + 1);
            }

            _selected = Mathf.Clamp(_selected, 0, cameras.Count - 1);
            using (new EditorGUI.IndentLevelScope())
            {
                _selected = EditorGUILayout.Popup(BasisEditorLocalization.Get("editor.camera.testWindow.camera"), _selected, names);
            }

            BasisHandHeldCamera camera = cameras[_selected];
            return camera != null ? camera : null;
        }

        private static void DrawCameraState(BasisHandHeldCamera camera)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.camera"), EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(BasisEditorLocalization.Get("editor.common.select"))) Selection.activeGameObject = camera.gameObject;
                    if (GUILayout.Button(BasisEditorLocalization.Get("editor.camera.testWindow.takePhoto"))) camera.CapturePhoto();
                    if (GUILayout.Button(camera.IsCameraHidden ? BasisEditorLocalization.Get("editor.camera.testWindow.showProp") : BasisEditorLocalization.Get("editor.camera.testWindow.hideProp"))) camera.SetCameraHidden(!camera.IsCameraHidden);
                }

                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.modeBody"),
                    camera.BodyAllowsLiveFeed
                        ? $"{camera.CameraMode} / {camera.BodyTraits.Kind}"
                        : BasisEditorLocalization.Get("editor.camera.testWindow.modeBodyNoSocket", camera.CameraMode, camera.BodyTraits.Kind));
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.captureSize"), $"{camera.captureWidth} x {camera.captureHeight}");

                UnityCamera capture = camera.captureCamera;
                bool rendering = capture != null && capture.enabled;
                Status(rendering ? BasisEditorLocalization.Get("editor.camera.testWindow.captureRendering") : BasisEditorLocalization.Get("editor.camera.testWindow.captureNotRendering"), rendering,
                    capture == null
                        ? BasisEditorLocalization.Get("editor.camera.testWindow.captureMissing.tooltip")
                        : rendering
                            ? BasisEditorLocalization.Get("editor.camera.testWindow.captureRendering.tooltip")
                            : BasisEditorLocalization.Get("editor.camera.testWindow.captureNotRendering.tooltip"));

                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.feedConsumers"), DescribeConsumers(camera));

                bool capped = BasisSettingsDefaults.LimitHandHeldCameraRate.RawValue;
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.renderRateCap"), capped
                    ? camera.IsDirectToScreenPresenting
                        ? BasisEditorLocalization.Get("editor.camera.testWindow.renderRateCapLifted", BasisSettingsDefaults.HandHeldCameraRenderHz.RawValue)
                        : $"{BasisSettingsDefaults.HandHeldCameraRenderHz.RawValue:0} Hz"
                    : BasisEditorLocalization.Get("editor.camera.testWindow.off"));

                RenderTexture feed = camera.PreviewTexture;
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.feedTexture"), DescribeTexture(feed));
                DrawFeedPreview(feed);
            }
        }

        private static string DescribeConsumers(BasisHandHeldCamera camera)
        {
            StringBuilder consumers = new StringBuilder();
            void Add(string name)
            {
                if (consumers.Length > 0) consumers.Append(", ");
                consumers.Append(name);
            }

            if (camera.IsCameraHidden) Add(BasisEditorLocalization.Get("editor.camera.testWindow.consumerHidden"));
            if (camera.IsWebStreamActive) Add(BasisEditorLocalization.Get("editor.camera.testWindow.consumerWebStream"));
            if (camera.IsVideoOutputActive) Add(BasisEditorLocalization.Get("editor.camera.testWindow.consumerVideoOutput"));
            if (camera.IsGifRecording) Add(BasisEditorLocalization.Get("editor.camera.testWindow.consumerGif"));
            if (camera.IsVideoRecording) Add(BasisEditorLocalization.Get("editor.camera.testWindow.consumerVideoRecording"));
            if (camera.IsDirectToScreenPresenting) Add(BasisEditorLocalization.Get("editor.camera.testWindow.directToScreen"));
            return consumers.Length > 0 ? consumers.ToString() : BasisEditorLocalization.Get("editor.camera.testWindow.consumerViewfinder");
        }

        private static string DescribeTexture(RenderTexture texture)
        {
            if (texture == null) return BasisEditorLocalization.Get("editor.camera.testWindow.textureNone");
            return texture.IsCreated()
                ? BasisEditorLocalization.Get("editor.camera.testWindow.textureInfo", texture.width, texture.height, texture.graphicsFormat, Mathf.Max(1, texture.antiAliasing), texture.depthStencilFormat)
                : BasisEditorLocalization.Get("editor.camera.testWindow.textureInfoNotCreated", texture.width, texture.height, texture.graphicsFormat, Mathf.Max(1, texture.antiAliasing), texture.depthStencilFormat);
        }

        private static void DrawFeedPreview(Texture feed)
        {
            Rect area = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(10f, FeedPreviewHeight, GUILayout.ExpandWidth(true)));
            EditorGUI.DrawRect(area, Color.black);
            if (feed == null)
            {
                EditorGUI.LabelField(area, BasisEditorLocalization.Get("editor.camera.testWindow.noFeed"), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            Rect fitted = BasisCameraDirectToScreenPass.FitViewport(feed.width, feed.height, area);
            if (fitted.width < 1f || fitted.height < 1f) return;
            EditorGUI.DrawPreviewTexture(fitted, feed, null, ScaleMode.StretchToFill);
        }

        // ---------- Direct To Screen ----------

        private static string[] FitOptions => new[]
        {
            BasisEditorLocalization.Get("editor.camera.testWindow.fit"),
            BasisEditorLocalization.Get("editor.camera.testWindow.fitFill"),
            BasisEditorLocalization.Get("editor.camera.testWindow.fitStretch"),
            BasisEditorLocalization.Get("editor.camera.testWindow.fitMatchWindow"),
        };

        private void DrawDirectToScreen(BasisHandHeldCamera camera)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.directToScreen"), EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                bool enabled = EditorGUILayout.Toggle(BasisEditorLocalization.Get("editor.common.enabled"), camera.DirectToScreen);
                if (enabled != camera.DirectToScreen) camera.SetDirectToScreen(enabled);

                int display = EditorGUILayout.IntSlider(BasisLocalization.Get("camera.directToScreen.display"), camera.DirectToScreenDisplay + 1, 1, BasisCameraDirectToScreen.MaxDisplays) - 1;
                if (display != camera.DirectToScreenDisplay) camera.SetDirectToScreenDisplay(display);

                BasisCameraDirectToScreenFit fit = (BasisCameraDirectToScreenFit)EditorGUILayout.Popup(BasisEditorLocalization.Get("editor.camera.testWindow.fit"), (int)camera.DirectToScreenFit, FitOptions);
                if (fit != camera.DirectToScreenFit) camera.SetDirectToScreenFit(fit);
                using (new EditorGUI.DisabledScope(fit != BasisCameraDirectToScreenFit.Fit && fit != BasisCameraDirectToScreenFit.Fill))
                {
                    Vector2 alignment = camera.DirectToScreenAlignment;
                    float horizontal = EditorGUILayout.Slider(BasisEditorLocalization.Get("editor.camera.testWindow.horizontal"), alignment.x, 0f, 1f);
                    float vertical = EditorGUILayout.Slider(BasisEditorLocalization.Get("editor.camera.testWindow.vertical"), alignment.y, 0f, 1f);
                    if (horizontal != alignment.x || vertical != alignment.y) camera.SetDirectToScreenAlignment(horizontal, vertical);
                }
                if (camera.DirectToScreenFeedFollowsWindow)
                {
                    EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.feed"), BasisEditorLocalization.Get("editor.camera.testWindow.feedFollowing", DescribeTexture(camera.PreviewTexture)));
                }

                BasisCameraDirectToScreenState state = camera.DirectToScreenState;
                bool settled = state == BasisCameraDirectToScreenState.Off || state == BasisCameraDirectToScreenState.Presenting;
                Status(BasisEditorLocalization.Get("editor.camera.testWindow.state", state), settled, DescribeState(state, camera.DirectToScreenDisplay));

                BasisCameraDirectToScreenOutput output = BasisCameraDirectToScreenOutput.Presenting;
                if (camera.IsDirectToScreenPresenting && output != null)
                {
                    DrawPresenting(output);
                }
                else if (output != null)
                {
                    Transform holder = output.transform.parent;
                    EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.windowHeldBy"), holder != null ? holder.name : output.name);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(BasisEditorLocalization.Get("editor.camera.testWindow.reevaluate"))) camera.RefreshDirectToScreen();
                    _showMonitorPreview = GUILayout.Toggle(_showMonitorPreview, BasisEditorLocalization.Get("editor.camera.testWindow.monitorPreview"), GUI.skin.button);
                }

                if (_showMonitorPreview)
                {
                    EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.monitorPreviewNote"), EditorStyles.miniLabel);
                    DrawMonitorPreview(camera);
                }
            }
        }

        private static void DrawPresenting(BasisCameraDirectToScreenOutput output)
        {
            UnityCamera screen = output.ScreenCamera;
            if (screen == null)
            {
                Status(BasisEditorLocalization.Get("editor.camera.testWindow.screenCameraMissing"), false, BasisEditorLocalization.Get("editor.camera.testWindow.screenCameraMissing.tooltip"));
                return;
            }

            UniversalAdditionalCameraData data = screen.GetUniversalAdditionalCameraData();
            string on = BasisEditorLocalization.Get("editor.camera.testWindow.on");
            string off = BasisEditorLocalization.Get("editor.camera.testWindow.off");
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.screenCamera"),
                BasisEditorLocalization.Get("editor.camera.testWindow.screenCameraInfo",
                    screen.enabled ? BasisEditorLocalization.Get("editor.camera.testWindow.screenEnabled") : BasisEditorLocalization.Get("editor.camera.testWindow.screenDisabled"),
                    screen.depth, data.allowXRRendering ? on : off, screen.allowHDR ? on : off,
                    data.allowHDROutput ? BasisEditorLocalization.Get("editor.camera.testWindow.allowed") : off));

            UnityCamera player = BasisLocalCameraDriver.CameraInstance;
            if (player != null)
            {
                Status(BasisEditorLocalization.Get("editor.camera.testWindow.rendersAfterPlayer", player.depth), screen.depth > player.depth,
                    BasisEditorLocalization.Get("editor.camera.testWindow.rendersAfterPlayer.tooltip"));
            }

            // The final blit lands on the camera's pixel rect: a rect smaller than the window leaves
            // the headset mirror showing round the feed, which is what a camera still aimed at both
            // eyes does while XR runs.
            Rect rect = screen.pixelRect;
            bool covers = Mathf.Approximately(rect.width, Screen.width) && Mathf.Approximately(rect.height, Screen.height) && rect.x <= 0f && rect.y <= 0f;
            Status(BasisEditorLocalization.Get("editor.camera.testWindow.coversWindow", rect.width, rect.height, rect.x, rect.y, Screen.width, Screen.height, screen.stereoTargetEye), covers,
                covers
                    ? BasisEditorLocalization.Get("editor.camera.testWindow.coversWindow.tooltip")
                    : BasisEditorLocalization.Get("editor.camera.testWindow.coversWindow.tooltipPartial"));

            Status(output.IsUsingFallbackPass ? BasisEditorLocalization.Get("editor.camera.testWindow.passFallback") : BasisEditorLocalization.Get("editor.camera.testWindow.passFeature"), !output.IsUsingFallbackPass,
                output.IsUsingFallbackPass
                    ? BasisEditorLocalization.Get("editor.camera.testWindow.passFallback.tooltip")
                    : BasisEditorLocalization.Get("editor.camera.testWindow.passFeature.tooltip"));

            BasisCameraDirectToScreenPassInfo last = BasisCameraDirectToScreenPass.LastRecorded;
            int age = Time.frameCount - last.Frame;
            bool drawing = last.Frame >= 0 && age <= 2;
            Status(drawing
                    ? BasisEditorLocalization.Get("editor.camera.testWindow.drawing", last.Frame)
                    : last.Frame < 0 ? BasisEditorLocalization.Get("editor.camera.testWindow.notDrawingNever") : BasisEditorLocalization.Get("editor.camera.testWindow.notDrawingAge", age),
                drawing,
                drawing
                    ? BasisEditorLocalization.Get("editor.camera.testWindow.drawing.tooltip")
                    : BasisEditorLocalization.Get("editor.camera.testWindow.notDrawingHelp"));

            if (last.Frame < 0) return;
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.common.source"), $"{last.SourceWidth} x {last.SourceHeight}, {last.SourceFormat}, {last.SourceSamples}x MSAA");
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.common.target"), $"{last.TargetWidth} x {last.TargetHeight}, {last.TargetFormat}, {last.TargetSamples}x MSAA");
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.viewport"), BasisEditorLocalization.Get("editor.camera.testWindow.viewportInfo", last.Viewport.width, last.Viewport.height, last.Viewport.x, last.Viewport.y));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.camera.testWindow.fit"), BasisEditorLocalization.Get("editor.camera.testWindow.fitInfo", last.Fit, last.ScaleBias.x, last.ScaleBias.y, last.ScaleBias.z, last.ScaleBias.w));
        }

        private static string DescribeState(BasisCameraDirectToScreenState state, int display)
        {
            switch (state)
            {
                case BasisCameraDirectToScreenState.Presenting:
                    return BasisEditorLocalization.Get("editor.camera.testWindow.stateHelpPresenting");
                case BasisCameraDirectToScreenState.WaitingForVR:
                    return BasisEditorLocalization.Get("editor.camera.testWindow.stateHelpWaitingForVr");
                case BasisCameraDirectToScreenState.NoOutputSocket:
                    return BasisEditorLocalization.Get("editor.camera.testWindow.stateHelpNoOutputSocket");
                case BasisCameraDirectToScreenState.Unsupported:
                    return BasisEditorLocalization.Get("editor.camera.testWindow.stateHelpUnsupported");
                case BasisCameraDirectToScreenState.DisplayNotConnected:
                    return BasisLocalization.Get("camera.directToScreen.displayNotConnected", display + 1);
                default:
                    return BasisEditorLocalization.Get("editor.camera.testWindow.stateHelpOff");
            }
        }

        private static void DrawMonitorPreview(BasisHandHeldCamera camera)
        {
            // The game window's own aspect, so the bars the monitor would show are the bars shown here.
            int windowWidth = Mathf.Max(1, Screen.width);
            int windowHeight = Mathf.Max(1, Screen.height);
            float available = Mathf.Max(100f, EditorGUIUtility.currentViewWidth - 40f);
            float height = Mathf.Clamp(available * windowHeight / windowWidth, 60f, 400f);

            Rect area = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(10f, height, GUILayout.ExpandWidth(true)));
            Rect window = BasisCameraDirectToScreenPass.FitViewport(windowWidth, windowHeight, area);
            if (window.width < 1f || window.height < 1f) return;

            EditorGUI.DrawRect(window, Color.black);
            Texture feed = camera.PreviewTexture;
            if (feed == null)
            {
                EditorGUI.LabelField(window, BasisEditorLocalization.Get("editor.camera.testWindow.noFeed"), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            // The pass's own placement, so the fit and alignment on the monitor are the ones shown
            // here. IMGUI counts y down from the top and a viewport up from the bottom: the same
            // gap, measured from the other edge.
            BasisCameraDirectToScreenPlacement placement = BasisCameraDirectToScreenPass.Place(camera.DirectToScreenFit, feed.width, feed.height, window, camera.DirectToScreenAlignment);
            if (placement.IsEmpty) return;
            Rect viewport = placement.Viewport;
            viewport.y = window.y + (window.yMax - viewport.yMax);
            Vector4 crop = placement.ScaleBias;
            GUI.DrawTextureWithTexCoords(viewport, feed, new Rect(crop.z, crop.w, crop.x, crop.y), false);
        }

        // ---------- Shared ----------

        private static readonly Color OkColour = new Color(0.65f, 1f, 0.65f);
        private static readonly Color ProblemColour = new Color(1f, 0.65f, 0.65f);

        private static void Status(string label, bool ok, string tooltip)
        {
            Color previous = GUI.color;
            GUI.color = ok ? OkColour : ProblemColour;
            EditorGUILayout.LabelField(new GUIContent((ok ? "[ok] " : "[!!] ") + label, tooltip));
            GUI.color = previous;
        }
    }
}
