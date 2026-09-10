using System;
using System.Collections.Generic;
using System.Text;
using Basis.BasisUI;
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
            GetWindow<BasisHandHeldCameraTestWindow>("Hand-Held Camera").Show();
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
                EditorGUILayout.HelpBox("Enter Play Mode and bring a camera out in game (Props > Photo Camera) to test it here.", MessageType.Info);
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
            EditorGUILayout.LabelField("Setup", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                Status("Platform has a desktop window", BasisHandHeldCamera.IsDirectToScreenSupported,
                    "Direct To Screen is only offered on platforms with a monitor to draw on.");

                UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
                if (asset == null)
                {
                    EditorGUILayout.HelpBox("No URP pipeline asset is active.", MessageType.Error);
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Pipeline asset", asset.name);
                    if (GUILayout.Button("Select", GUILayout.Width(60f))) Selection.activeObject = asset;
                }

                int index = BasisCameraDirectToScreenOutput.FindRendererWithFeature(asset);
                if (index >= 0)
                {
                    ScriptableRendererData data = asset.rendererDataList[index];
                    Status($"Direct To Screen renderer: #{index} {data.name}", true,
                        "The screen camera renders through this renderer, which carries BasisCameraDirectToScreenFeature.");
                }
                else
                {
                    Status("Direct To Screen renderer: none on this pipeline", false,
                        "No renderer carries BasisCameraDirectToScreenFeature. The pass is enqueued by hand on the default renderer instead; add DirectToScreenRenderer to the pipeline asset's renderer list for the intended path.");
                }
            }
        }

        // ---------- Device: the hot-swap the mode has to survive ----------

        private static void DrawDevice()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Device", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                string mode = BasisDeviceManagement.StaticCurrentMode;
                bool inVR = BasisDeviceManagement.IsCurrentModeVR();
                bool inDesktop = BasisDeviceManagement.IsUserInDesktop();
                EditorGUILayout.LabelField("Mode", $"{mode}{(inVR ? " (VR)" : inDesktop ? " (desktop)" : string.Empty)}");

                BasisDeviceManagement manager = BasisDeviceManagement.Instance;
                if (manager == null)
                {
                    EditorGUILayout.LabelField("No BasisDeviceManagement instance.");
                    return;
                }
                if (manager.IsSoftSwapped) EditorGUILayout.LabelField("Soft-swapped from", manager.AutoSwapPreviousVRMode);

                // Desktop hands the monitor back; VR takes it over again. Both from here, so the
                // round trip can be tested without leaving the editor.
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(inDesktop))
                    {
                        if (GUILayout.Button("Switch to Desktop")) SwitchMode(manager, BasisConstants.Desktop);
                    }

                    bool canReturnToVR = manager.IsSoftSwapped && !string.IsNullOrEmpty(manager.AutoSwapPreviousVRMode);
                    using (new EditorGUI.DisabledScope(inVR))
                    {
                        if (canReturnToVR)
                        {
                            if (GUILayout.Button($"Back to {manager.AutoSwapPreviousVRMode}")) SwitchMode(manager, manager.AutoSwapPreviousVRMode);
                        }
                        else
                        {
                            if (GUILayout.Button("Switch to OpenXR")) SwitchMode(manager, BasisConstants.OpenXRLoader);
                            if (GUILayout.Button("Switch to OpenVR")) SwitchMode(manager, BasisConstants.OpenVRLoader);
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
            EditorGUILayout.LabelField("Cameras", EditorStyles.boldLabel);

            IReadOnlyList<BasisHandHeldCamera> cameras = BasisHandHeldCameraRegistry.Cameras;
            if (cameras.Count == 0)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.HelpBox("No hand-held camera is out. Bring one out in game: Props > Photo Camera.", MessageType.Info);
                }
                return null;
            }

            string[] names = new string[cameras.Count];
            for (int Index = 0; Index < cameras.Count; Index++)
            {
                BasisHandHeldCamera entry = cameras[Index];
                names[Index] = entry != null
                    ? $"{Index + 1}. {entry.gameObject.name}{(entry.IsCameraHidden ? " (hidden)" : string.Empty)}"
                    : $"{Index + 1}. (destroyed)";
            }

            _selected = Mathf.Clamp(_selected, 0, cameras.Count - 1);
            using (new EditorGUI.IndentLevelScope())
            {
                _selected = EditorGUILayout.Popup("Camera", _selected, names);
            }

            BasisHandHeldCamera camera = cameras[_selected];
            return camera != null ? camera : null;
        }

        private static void DrawCameraState(BasisHandHeldCamera camera)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Camera", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Select")) Selection.activeGameObject = camera.gameObject;
                    if (GUILayout.Button("Take Photo")) camera.CapturePhoto();
                    if (GUILayout.Button(camera.IsCameraHidden ? "Show Prop" : "Hide Prop")) camera.SetCameraHidden(!camera.IsCameraHidden);
                }

                EditorGUILayout.LabelField("Mode / body",
                    $"{camera.CameraMode} / {camera.BodyTraits.Kind}{(camera.BodyAllowsLiveFeed ? string.Empty : " (no output socket)")}");
                EditorGUILayout.LabelField("Capture size", $"{camera.captureWidth} x {camera.captureHeight}");

                UnityCamera capture = camera.captureCamera;
                bool rendering = capture != null && capture.enabled;
                Status(rendering ? "Capture camera rendering" : "Capture camera not rendering", rendering,
                    capture == null
                        ? "This camera has no capture camera."
                        : rendering
                            ? "The capture camera is enabled for this frame."
                            : "Gated off: nothing is showing the feed, or the render-rate cap skipped this frame.");

                EditorGUILayout.LabelField("Feed consumers", DescribeConsumers(camera));

                bool capped = BasisSettingsDefaults.LimitHandHeldCameraRate.RawValue;
                EditorGUILayout.LabelField("Render-rate cap", capped
                    ? $"{BasisSettingsDefaults.HandHeldCameraRenderHz.RawValue:0} Hz{(camera.IsDirectToScreenPresenting ? " (lifted while presenting)" : string.Empty)}"
                    : "off");

                RenderTexture feed = camera.PreviewTexture;
                EditorGUILayout.LabelField("Feed texture", DescribeTexture(feed));
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

            if (camera.IsCameraHidden) Add("prop hidden but live");
            if (camera.IsWebStreamActive) Add("web stream");
            if (camera.IsVideoOutputActive) Add("video output");
            if (camera.IsGifRecording) Add("GIF recording");
            if (camera.IsVideoRecording) Add("video recording");
            if (camera.IsDirectToScreenPresenting) Add("Direct To Screen");
            return consumers.Length > 0 ? consumers.ToString() : "viewfinder only";
        }

        private static string DescribeTexture(RenderTexture texture)
        {
            if (texture == null) return "none";
            return $"{texture.width} x {texture.height}, {texture.graphicsFormat}, {Mathf.Max(1, texture.antiAliasing)}x MSAA, depth {texture.depthStencilFormat}{(texture.IsCreated() ? string.Empty : " (not created)")}";
        }

        private static void DrawFeedPreview(Texture feed)
        {
            Rect area = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(10f, FeedPreviewHeight, GUILayout.ExpandWidth(true)));
            EditorGUI.DrawRect(area, Color.black);
            if (feed == null)
            {
                EditorGUI.LabelField(area, "no feed", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            Rect fitted = BasisCameraDirectToScreenPass.FitViewport(feed.width, feed.height, area);
            if (fitted.width < 1f || fitted.height < 1f) return;
            EditorGUI.DrawPreviewTexture(fitted, feed, null, ScaleMode.StretchToFill);
        }

        // ---------- Direct To Screen ----------

        private void DrawDirectToScreen(BasisHandHeldCamera camera)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Direct To Screen", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                bool enabled = EditorGUILayout.Toggle("Enabled", camera.DirectToScreen);
                if (enabled != camera.DirectToScreen) camera.SetDirectToScreen(enabled);

                BasisCameraDirectToScreenFit fit = (BasisCameraDirectToScreenFit)EditorGUILayout.EnumPopup("Fit", camera.DirectToScreenFit);
                if (fit != camera.DirectToScreenFit) camera.SetDirectToScreenFit(fit);
                using (new EditorGUI.DisabledScope(fit != BasisCameraDirectToScreenFit.Fit && fit != BasisCameraDirectToScreenFit.Fill))
                {
                    Vector2 alignment = camera.DirectToScreenAlignment;
                    float horizontal = EditorGUILayout.Slider("Horizontal", alignment.x, 0f, 1f);
                    float vertical = EditorGUILayout.Slider("Vertical", alignment.y, 0f, 1f);
                    if (horizontal != alignment.x || vertical != alignment.y) camera.SetDirectToScreenAlignment(horizontal, vertical);
                }
                if (camera.DirectToScreenFeedFollowsWindow)
                {
                    EditorGUILayout.LabelField("Feed", $"following the window: {DescribeTexture(camera.PreviewTexture)}");
                }

                BasisCameraDirectToScreenState state = camera.DirectToScreenState;
                bool settled = state == BasisCameraDirectToScreenState.Off || state == BasisCameraDirectToScreenState.Presenting;
                Status($"State: {state}", settled, DescribeState(state));

                BasisCameraDirectToScreenOutput output = BasisCameraDirectToScreenOutput.Presenting;
                if (camera.IsDirectToScreenPresenting && output != null)
                {
                    DrawPresenting(output);
                }
                else if (output != null)
                {
                    Transform holder = output.transform.parent;
                    EditorGUILayout.LabelField("Window held by", holder != null ? holder.name : output.name);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Re-evaluate")) camera.RefreshDirectToScreen();
                    _showMonitorPreview = GUILayout.Toggle(_showMonitorPreview, "Monitor preview", GUI.skin.button);
                }

                if (_showMonitorPreview)
                {
                    EditorGUILayout.LabelField("What the monitor shows while presenting, at the game window's aspect:", EditorStyles.miniLabel);
                    DrawMonitorPreview(camera);
                }
            }
        }

        private static void DrawPresenting(BasisCameraDirectToScreenOutput output)
        {
            UnityCamera screen = output.ScreenCamera;
            if (screen == null)
            {
                Status("Screen camera missing", false, "The output has no camera to draw through.");
                return;
            }

            UniversalAdditionalCameraData data = screen.GetUniversalAdditionalCameraData();
            EditorGUILayout.LabelField("Screen camera",
                $"{(screen.enabled ? "enabled" : "DISABLED")}, depth {screen.depth:0}, XR rendering {(data.allowXRRendering ? "on" : "off")}, HDR {(screen.allowHDR ? "on" : "off")}, HDR output {(data.allowHDROutput ? "allowed" : "off")}");

            UnityCamera player = BasisLocalCameraDriver.CameraInstance;
            if (player != null)
            {
                Status($"Renders after the player camera (depth {player.depth:0})", screen.depth > player.depth,
                    "URP draws the headset mirror at the end of the player camera's stack; the screen camera has to draw after it to cover it.");
            }

            Status(output.IsUsingFallbackPass ? "Pass: enqueued by hand (fallback)" : "Pass: renderer feature", !output.IsUsingFallbackPass,
                output.IsUsingFallbackPass
                    ? "No renderer on the pipeline carries the feature, so the pass is enqueued directly on the default renderer. It works, but see Setup."
                    : "The pass is added by BasisCameraDirectToScreenFeature on the Direct To Screen renderer.");

            BasisCameraDirectToScreenPassInfo last = BasisCameraDirectToScreenPass.LastRecorded;
            int age = Time.frameCount - last.Frame;
            bool drawing = last.Frame >= 0 && age <= 2;
            Status(drawing
                    ? $"Drawing to the window (frame {last.Frame})"
                    : last.Frame < 0 ? "Not drawing: the pass has never run" : $"Not drawing: the pass last ran {age} frames ago",
                drawing,
                drawing
                    ? "The blit pass recorded on this frame or the one before."
                    : "Presenting, but the blit pass is not recording. Check the Console for a render graph error and the Setup section above.");

            if (last.Frame < 0) return;
            EditorGUILayout.LabelField("Source", $"{last.SourceWidth} x {last.SourceHeight}, {last.SourceFormat}, {last.SourceSamples}x MSAA");
            EditorGUILayout.LabelField("Target", $"{last.TargetWidth} x {last.TargetHeight}, {last.TargetFormat}, {last.TargetSamples}x MSAA");
            EditorGUILayout.LabelField("Viewport", $"{last.Viewport.width:0} x {last.Viewport.height:0} at ({last.Viewport.x:0}, {last.Viewport.y:0})");
            EditorGUILayout.LabelField("Fit", $"{last.Fit}, feed window {last.ScaleBias.x:0.###} x {last.ScaleBias.y:0.###} from ({last.ScaleBias.z:0.###}, {last.ScaleBias.w:0.###})");
        }

        private static string DescribeState(BasisCameraDirectToScreenState state)
        {
            switch (state)
            {
                case BasisCameraDirectToScreenState.Presenting:
                    return "The feed is being drawn over the game window in place of the headset mirror.";
                case BasisCameraDirectToScreenState.WaitingForVR:
                    return "Switched on, but in desktop mode the window is already the operator's own view. It takes the window over on the next switch into VR.";
                case BasisCameraDirectToScreenState.NoOutputSocket:
                    return "Switched on, but the fitted body has no output socket. Fit a digital body on the Presets tab.";
                case BasisCameraDirectToScreenState.Unsupported:
                    return "This platform has no desktop window to draw to.";
                default:
                    return "The mode is switched off.";
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
                EditorGUI.LabelField(window, "no feed", EditorStyles.centeredGreyMiniLabel);
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
