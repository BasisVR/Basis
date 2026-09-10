using System.Collections.Generic;
using System.Linq;
using Basis.BasisUI;
using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>
    /// Adds the webcam tracking controls (enable, camera selection, per-feature toggles, calibrate)
    /// as a section inside the framework's Tracker Settings tab via
    /// SettingsProvider.TrackerSettingsExtraBuilder.
    /// </summary>
    public static class SettingsProviderMediaPipe
    {
        private static readonly List<string> PoseModelIds = new List<string> { BasisMediaPipeConfig.PoseModelLite, BasisMediaPipeConfig.PoseModelFull, BasisMediaPipeConfig.PoseModelHeavy };

        [RuntimeInitializeOnLoadMethod]
        private static void Register()
        {
            SettingsProvider.TrackerSettingsExtraBuilder = BuildSection;
        }

        private static void BuildSection(RectTransform parent)
        {
            PanelElementDescriptor tabDescriptor = parent.GetComponentInParent<PanelElementDescriptor>(true);

            // Whole webcam section collapses under one bar.
            PanelSectionToggle webcamToggle = PanelSectionToggle.CreateNewEntry(parent);
            webcamToggle.SetTitle(BasisLocalization.Get("settings.mediapipe.webcamTracking"));
            int webcamStart = parent.childCount;

            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            group.SetDescription(BasisLocalization.Get("settings.mediapipe.webcamTracking.description"));
            var content = group.ContentParent;

            PanelToggle enableToggle = PanelToggle.CreateNewEntry(content);
            enableToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.enableWebcamTracking"));
            enableToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.enableWebcamTracking.description"));
            enableToggle.SetValueWithoutNotify(BasisMediaPipeSettings.Enable.RawValue);
            enableToggle.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.Enable.SetValue(value);
                BasisMediaPipeManagement.Instance.SetEnabled(value);
            };

            PanelElementDescriptor settingsGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            settingsGroup.SetTitle(string.Empty);
            content = settingsGroup.ContentParent;

            // Live camera feed with the landmarks drawn over it, plus a plain-language camera state. The view
            // only costs anything while this page is open (it drives itself off the frame clock in OnEnable).
            MediaPipePreviewView preview = MediaPipePreviewView.Create(content);
            PanelElementDescriptor cameraState = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, content);
            cameraState.SetBackgroundVisible(false);
            cameraState.SetTitle(BasisLocalization.Get("settings.mediapipe.status"));
            cameraState.SetDescription(BasisLocalization.Get(BasisMediaPipeManagement.Instance != null ? BasisMediaPipeManagement.Instance.StatusKey : "settings.mediapipe.status.off"));

            PanelToggle previewToggle = PanelToggle.CreateNewEntry(content);
            previewToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.preview"));
            previewToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.preview.description"));
            previewToggle.SetValueWithoutNotify(BasisMediaPipeSettings.ShowPreview.RawValue);
            preview.gameObject.SetActive(BasisMediaPipeSettings.ShowPreview.RawValue);
            previewToggle.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.ShowPreview.SetValue(value);
                preview.gameObject.SetActive(value);
                settingsGroup.ForceRebuild();
                tabDescriptor?.ForceRebuild();
            };

            PanelDropdown cameraDropdown = PanelDropdown.CreateNewEntry(content);
            cameraDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.camera"));
            cameraDropdown.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.camera.description"));
            List<string> deviceNames = BasisMediaPipeCamera.EnumerateDevices().Select(d => d.name).ToList();
            if (deviceNames.Count == 0) deviceNames.Add("(no cameras found)");
            cameraDropdown.AssignEntries(deviceNames);
            string currentCamera = BasisMediaPipeSettings.Camera.RawValue;
            if (!string.IsNullOrEmpty(currentCamera) && deviceNames.Contains(currentCamera))
            {
                cameraDropdown.SetValueWithoutNotify(currentCamera);
            }
            cameraDropdown.OnValueChanged += choice =>
            {
                BasisMediaPipeSettings.Camera.SetValue(choice);
                BasisMediaPipeManagement.Instance.ApplySettings();
            };

            List<string> resolutions = new List<string> { "320 x 240", "640 x 480", "960 x 540", "1280 x 720" };
            PanelDropdown resolutionDropdown = PanelDropdown.CreateNewEntry(content);
            resolutionDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.cameraResolution"));
            resolutionDropdown.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.cameraResolution.description"));
            resolutionDropdown.AssignEntries(resolutions);
            string currentResolution = $"{BasisMediaPipeSettings.ResolutionWidth.RawValue} x {BasisMediaPipeSettings.ResolutionHeight.RawValue}";
            if (resolutions.Contains(currentResolution)) resolutionDropdown.SetValueWithoutNotify(currentResolution);
            resolutionDropdown.OnValueChanged += choice =>
            {
                string[] parts = choice.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int rw) && int.TryParse(parts[1].Trim(), out int rh))
                {
                    BasisMediaPipeSettings.ResolutionWidth.SetValue(rw);
                    BasisMediaPipeSettings.ResolutionHeight.SetValue(rh);
                    BasisMediaPipeManagement.Instance.ApplySettings();
                }
            };

            List<string> frameRates = new List<string> { "15", "30", "60" };
            PanelDropdown fpsDropdown = PanelDropdown.CreateNewEntry(content);
            fpsDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.cameraFrameRate"));
            fpsDropdown.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.cameraFrameRate.description"));
            fpsDropdown.AssignEntries(frameRates);
            string currentFrameRate = BasisMediaPipeSettings.CameraFps.RawValue.ToString();
            if (frameRates.Contains(currentFrameRate)) fpsDropdown.SetValueWithoutNotify(currentFrameRate);
            fpsDropdown.OnValueChanged += choice =>
            {
                if (int.TryParse(choice, out int fps))
                {
                    BasisMediaPipeSettings.CameraFps.SetValue(fps);
                    BasisMediaPipeManagement.Instance.ApplySettings();
                }
            };

            PanelDropdown poseModelDropdown = PanelDropdown.CreateNewEntry(content);
            poseModelDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.poseModel"));
            poseModelDropdown.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.poseModel.description"));
            poseModelDropdown.AssignEntries(PoseModelIds, PoseModelIds.Select(id => BasisLocalization.Get("settings.mediapipe.poseModel." + id)).ToList());
            poseModelDropdown.SetValueWithoutNotify(BasisMediaPipeConfig.NormalizePoseModel(BasisMediaPipeSettings.PoseModel.RawValue));
            poseModelDropdown.OnValueChanged += choice =>
            {
                BasisMediaPipeSettings.PoseModel.SetValue(BasisMediaPipeConfig.NormalizePoseModel(choice));
                BasisMediaPipeManagement.Instance.ApplySettings();
            };

            // Toggles reach the running pipeline through ApplySettings, which now reconfigures in place: the
            // camera keeps running and the models swap on their own threads.
            void AddToggle(string title, string description, BasisSettingsBinding<bool> binding)
            {
                PanelToggle toggle = PanelToggle.CreateNewEntry(content);
                toggle.Descriptor.SetTitle(title);
                toggle.Descriptor.SetDescription(description);
                toggle.SetValueWithoutNotify(binding.RawValue);
                toggle.OnValueChanged += value =>
                {
                    binding.SetValue(value);
                    BasisMediaPipeManagement.Instance.ApplySettings();
                };
            }

            AddToggle("Face & Eyes", "Track facial expressions, blink and gaze.", BasisMediaPipeSettings.EnableFace);
            AddToggle("Hands & Fingers", "Track finger curl and splay.", BasisMediaPipeSettings.EnableHands);
            AddToggle("Head Rotation", "Your avatar's head turns, nods and tilts to follow your real head. The camera stays on the mouse.", BasisMediaPipeSettings.EnableHeadRotation);
            AddToggle("Head Position", "Your avatar's head shifts to follow your real head movement.", BasisMediaPipeSettings.EnableHeadPosition);
            AddToggle("Arm Tracking (experimental)", "Move your avatar's arms to match your real arms, retargeted from the pose skeleton (turns on the pose model; extra CPU).", BasisMediaPipeSettings.EnableHandTracking);
            AddToggle(BasisLocalization.Get("settings.mediapipe.armElbowPoleExperimental"), BasisLocalization.Get("settings.mediapipe.armElbowPoleExperimental.description"), BasisMediaPipeSettings.EnableArmElbowPole);
            AddToggle(BasisLocalization.Get("settings.mediapipe.handRotation"), BasisLocalization.Get("settings.mediapipe.handRotation.description"), BasisMediaPipeSettings.HandRotation);
            AddToggle(BasisLocalization.Get("settings.mediapipe.rejectGlitches"), BasisLocalization.Get("settings.mediapipe.rejectGlitches.description"), BasisMediaPipeSettings.RejectGlitches);
            AddToggle("Body Lean/Twist", "Your avatar's chest leans, twists, sways and shifts with your torso. Uses the pose model (extra CPU). Set the amount with Chest Motion below.", BasisMediaPipeSettings.EnableBody);
            AddToggle("Mirror Camera", "Flip the camera horizontally (selfie view).", BasisMediaPipeSettings.Mirror);
            AddToggle(BasisLocalization.Get("settings.mediapipe.lowLightBoost"), BasisLocalization.Get("settings.mediapipe.lowLightBoost.description"), BasisMediaPipeSettings.LowLightBoost);
            AddToggle(BasisLocalization.Get("settings.mediapipe.cameraFpsAuto"), BasisLocalization.Get("settings.mediapipe.cameraFpsAuto.description"), BasisMediaPipeSettings.CameraFpsAuto);

            AddToggle("Swap Hands", "Fix left/right hands if they are reversed.", BasisMediaPipeSettings.SwapHands);
            AddToggle(BasisLocalization.Get("settings.mediapipe.invertBlink"), BasisLocalization.Get("settings.mediapipe.invertBlink.description"), BasisMediaPipeSettings.InvertBlink);
            AddToggle(BasisLocalization.Get("settings.mediapipe.invertHeadYaw"), BasisLocalization.Get("settings.mediapipe.invertHeadYaw.description"), BasisMediaPipeSettings.InvertHeadYaw);
            AddToggle(BasisLocalization.Get("settings.mediapipe.invertHeadPitch"), BasisLocalization.Get("settings.mediapipe.invertHeadPitch.description"), BasisMediaPipeSettings.InvertHeadPitch);
            AddToggle(BasisLocalization.Get("settings.mediapipe.invertHeadRoll"), BasisLocalization.Get("settings.mediapipe.invertHeadRoll.description"), BasisMediaPipeSettings.InvertHeadRoll);

            // Sliders only touch converter tuning, so they apply on the spot without going near the camera.
            void AddSlider(string title, string description, BasisSettingsBinding<float> binding, float min, float max, ValueDisplayMode mode = ValueDisplayMode.Percentage)
            {
                PanelSlider slider = PanelSlider.CreateNew(content);
                slider.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = min, SliderMax = max, DecimalPlaces = 2, DisplayMode = mode });
                slider.Descriptor.SetTitle(title);
                slider.Descriptor.SetDescription(description);
                slider.SetValueWithoutNotify(binding.RawValue);
                slider.OnValueChanged += value =>
                {
                    binding.SetValue(value);
                    BasisMediaPipeManagement.Instance.ApplyTuning();
                };
            }

            string smoothing = BasisLocalization.Get("settings.mediapipe.smoothing.description");
            AddSlider(BasisLocalization.Get("settings.mediapipe.headSmoothing"), smoothing, BasisMediaPipeSettings.HeadSmoothing, 0f, 1f);
            AddSlider(BasisLocalization.Get("settings.mediapipe.faceSmoothing"), smoothing, BasisMediaPipeSettings.FaceSmoothing, 0f, 1f);
            AddSlider(BasisLocalization.Get("settings.mediapipe.handSmoothing"), smoothing, BasisMediaPipeSettings.HandSmoothing, 0f, 1f);
            AddSlider(BasisLocalization.Get("settings.mediapipe.fingerSmoothing"), smoothing, BasisMediaPipeSettings.FingerSmoothing, 0f, 1f);
            AddSlider(BasisLocalization.Get("settings.mediapipe.gazeStrength"), BasisLocalization.Get("settings.mediapipe.gazeStrength.description"), BasisMediaPipeSettings.GazeStrength, 0.25f, 3f);
            AddSlider(BasisLocalization.Get("settings.mediapipe.chestMotion"), BasisLocalization.Get("settings.mediapipe.chestMotion.description"), BasisMediaPipeSettings.ChestMotion, 0f, 1.5f);
            AddSlider(BasisLocalization.Get("settings.mediapipe.elbowRestBias"), BasisLocalization.Get("settings.mediapipe.elbowRestBias.description"), BasisMediaPipeSettings.ElbowRestBias, 0f, 1f);
            AddSlider(BasisLocalization.Get("settings.mediapipe.armHeadAnchor"), BasisLocalization.Get("settings.mediapipe.armHeadAnchor.description"), BasisMediaPipeSettings.ArmHeadAnchor, 0f, 1f);
            AddSlider(BasisLocalization.Get("settings.mediapipe.headPositionStrength"), BasisLocalization.Get("settings.mediapipe.headPositionStrength.description"), BasisMediaPipeSettings.HeadPositionStrength, 0f, 3f);
            AddSlider(BasisLocalization.Get("settings.mediapipe.headRotationStrength"), BasisLocalization.Get("settings.mediapipe.headRotationStrength.description"), BasisMediaPipeSettings.HeadRotationStrength, 0f, 3f);
            AddSlider(BasisLocalization.Get("settings.mediapipe.headHeightTrim"), BasisLocalization.Get("settings.mediapipe.headHeightTrim.description"), BasisMediaPipeSettings.HeadHeight, -0.25f, 0.25f, ValueDisplayMode.Meters);

            AddToggle(BasisLocalization.Get("settings.mediapipe.tongueExperimental"), BasisLocalization.Get("settings.mediapipe.tongueExperimental.description"), BasisMediaPipeSettings.EnableTongue);
            AddSlider(BasisLocalization.Get("settings.mediapipe.tongueStrength"), BasisLocalization.Get("settings.mediapipe.tongueStrength.description"), BasisMediaPipeSettings.TongueStrength, 0f, 3f);

            PanelButton calibrate = PanelButton.CreateNew(content);
            calibrate.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.calibrateHeadLookForward"));
            calibrate.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.calibrateHeadLookForward.description"));
            calibrate.OnClicked += () => BasisMediaPipeManagement.Instance.CalibrateHead();

            PanelElementDescriptor diagnostics = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, content);
            diagnostics.SetBackgroundVisible(false);
            diagnostics.SetTitle(BasisLocalization.Get("settings.mediapipe.diagnostics"));
            diagnostics.SetDescription(BasisLocalization.Get("settings.mediapipe.diagnostics.description"));

            PanelElementDescriptor statusField = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, diagnostics.ContentParent);
            statusField.SetTitle(BasisLocalization.Get("settings.mediapipe.status"));
            statusField.SetDescription(BasisLocalization.Get("settings.mediapipe.status.description"));

            PanelButton refresh = PanelButton.CreateNew(diagnostics.ContentParent);
            refresh.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.refresh"));
            refresh.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.refresh.description"));

            void RefreshStatus()
            {
                BasisMediaPipeManagement manager = BasisMediaPipeManagement.Instance;
                statusField.SetDescription(manager != null ? manager.DiagnosticsText() : "Not started.");
            }

            refresh.OnClicked += RefreshStatus;
            RefreshStatus();
            preview.StatusChanged += key =>
            {
                cameraState.SetDescription(BasisLocalization.Get(key));
                RefreshStatus();
            };

            void RefreshWebcamSettingsVisibility(bool on)
            {
                settingsGroup.SetActive(on);
                settingsGroup.ForceRebuild();
                tabDescriptor?.ForceRebuild();
            }
            RefreshWebcamSettingsVisibility(BasisMediaPipeSettings.Enable.RawValue);
            enableToggle.OnValueChanged += RefreshWebcamSettingsVisibility;

            PanelSectionToggleHelpers.FinalizeFlatSectionFromIndex(webcamToggle, parent, webcamStart, false, visible =>
            {
                // Expanding re-shows both rows; re-apply the enable gate over the settings.
                if (visible)
                {
                    RefreshWebcamSettingsVisibility(BasisMediaPipeSettings.Enable.RawValue);
                    preview.gameObject.SetActive(BasisMediaPipeSettings.ShowPreview.RawValue);
                }
                tabDescriptor?.ForceRebuild();
            });
        }
    }
}
