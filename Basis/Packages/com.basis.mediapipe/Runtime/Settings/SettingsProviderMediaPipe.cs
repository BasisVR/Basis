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
                BasisMediaPipeManagement.Instance.ApplySettings();
            };

            PanelElementDescriptor settingsGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            settingsGroup.SetTitle(string.Empty);
            content = settingsGroup.ContentParent;

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
                BasisMediaPipeManagement.Instance.SetCamera(choice);
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
                    BasisMediaPipeManagement.Instance.ReloadCamera();
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
                    BasisMediaPipeManagement.Instance.ReloadCamera();
                    BasisMediaPipeManagement.Instance.ApplySettings();
                }
            };

            void AddFeatureToggle(string title, string description, BasisSettingsBinding<bool> binding)
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

            void AddTuningToggle(string title, string description, BasisSettingsBinding<bool> binding)
            {
                PanelToggle toggle = PanelToggle.CreateNewEntry(content);
                toggle.Descriptor.SetTitle(title);
                toggle.Descriptor.SetDescription(description);
                toggle.SetValueWithoutNotify(binding.RawValue);
                toggle.OnValueChanged += value =>
                {
                    binding.SetValue(value);
                    BasisMediaPipeManagement.Instance.ApplyTuning();
                    BasisMediaPipeManagement.Instance.ApplySettings();
                };
            }

            AddFeatureToggle("Face & Eyes", "Track facial expressions, blink and gaze.", BasisMediaPipeSettings.EnableFace);
            AddFeatureToggle("Hands & Fingers", "Track finger curl and splay.", BasisMediaPipeSettings.EnableHands);
            AddFeatureToggle("Head Rotation", "Your avatar's head turns, nods and tilts to follow your real head. The camera stays on the mouse.", BasisMediaPipeSettings.EnableHeadRotation);
            AddFeatureToggle("Head Position", "Your avatar's head shifts to follow your real head movement.", BasisMediaPipeSettings.EnableHeadPosition);
            AddFeatureToggle("Arm Tracking (experimental)", "Move your avatar's arms to match your real arms, retargeted from the pose skeleton (turns on the pose model; extra CPU).", BasisMediaPipeSettings.EnableHandTracking);
            AddTuningToggle(BasisLocalization.Get("settings.mediapipe.armElbowPoleExperimental"), BasisLocalization.Get("settings.mediapipe.armElbowPoleExperimental.description"), BasisMediaPipeSettings.EnableArmElbowPole);
            AddTuningToggle(BasisLocalization.Get("settings.mediapipe.handRotation"), BasisLocalization.Get("settings.mediapipe.handRotation.description"), BasisMediaPipeSettings.HandRotation);
            AddTuningToggle(BasisLocalization.Get("settings.mediapipe.rejectGlitches"), BasisLocalization.Get("settings.mediapipe.rejectGlitches.description"), BasisMediaPipeSettings.RejectGlitches);
            AddFeatureToggle("Body Lean/Twist", "Your avatar's chest leans, twists and sways with your torso. Uses the pose model (extra CPU). Set the amount with Chest Motion below.", BasisMediaPipeSettings.EnableBody);
            AddFeatureToggle("Mirror Camera", "Flip the camera horizontally (selfie view).", BasisMediaPipeSettings.Mirror);
            AddFeatureToggle(BasisLocalization.Get("settings.mediapipe.lowLightBoost"), BasisLocalization.Get("settings.mediapipe.lowLightBoost.description"), BasisMediaPipeSettings.LowLightBoost);

            AddFeatureToggle("Swap Hands", "Fix left/right hands if they are reversed.", BasisMediaPipeSettings.SwapHands);
            AddTuningToggle(BasisLocalization.Get("settings.mediapipe.invertBlink"), BasisLocalization.Get("settings.mediapipe.invertBlink.description"), BasisMediaPipeSettings.InvertBlink);
            AddTuningToggle(BasisLocalization.Get("settings.mediapipe.invertHeadYaw"), BasisLocalization.Get("settings.mediapipe.invertHeadYaw.description"), BasisMediaPipeSettings.InvertHeadYaw);
            AddTuningToggle(BasisLocalization.Get("settings.mediapipe.invertHeadPitch"), BasisLocalization.Get("settings.mediapipe.invertHeadPitch.description"), BasisMediaPipeSettings.InvertHeadPitch);
            AddTuningToggle(BasisLocalization.Get("settings.mediapipe.invertHeadRoll"), BasisLocalization.Get("settings.mediapipe.invertHeadRoll.description"), BasisMediaPipeSettings.InvertHeadRoll);

            void AddSmoothingSlider(string title, BasisSettingsBinding<float> binding)
            {
                PanelSlider slider = PanelSlider.CreateNew(content);
                slider.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 1f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
                slider.Descriptor.SetTitle(title);
                slider.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.smoothing.description"));
                slider.SetValueWithoutNotify(binding.RawValue);
                slider.OnValueChanged += value =>
                {
                    binding.SetValue(value);
                    BasisMediaPipeManagement.Instance.ApplyTuning();
                    BasisMediaPipeManagement.Instance.ApplySettings();
                };
            }

            AddSmoothingSlider(BasisLocalization.Get("settings.mediapipe.headSmoothing"), BasisMediaPipeSettings.HeadSmoothing);
            AddSmoothingSlider(BasisLocalization.Get("settings.mediapipe.faceSmoothing"), BasisMediaPipeSettings.FaceSmoothing);
            AddSmoothingSlider(BasisLocalization.Get("settings.mediapipe.handSmoothing"), BasisMediaPipeSettings.HandSmoothing);
            AddSmoothingSlider(BasisLocalization.Get("settings.mediapipe.fingerSmoothing"), BasisMediaPipeSettings.FingerSmoothing);

            PanelSlider chestMotion = PanelSlider.CreateNew(content);
            chestMotion.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 1.5f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            chestMotion.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.chestMotion"));
            chestMotion.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.chestMotion.description"));
            chestMotion.SetValueWithoutNotify(BasisMediaPipeSettings.ChestMotion.RawValue);
            chestMotion.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.ChestMotion.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };


            PanelSlider elbowRest = PanelSlider.CreateNew(content);
            elbowRest.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 1f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            elbowRest.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.elbowRestBias"));
            elbowRest.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.elbowRestBias.description"));
            elbowRest.SetValueWithoutNotify(BasisMediaPipeSettings.ElbowRestBias.RawValue);
            elbowRest.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.ElbowRestBias.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };

            PanelSlider headAnchor = PanelSlider.CreateNew(content);
            headAnchor.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 1f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            headAnchor.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.armHeadAnchor"));
            headAnchor.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.armHeadAnchor.description"));
            headAnchor.SetValueWithoutNotify(BasisMediaPipeSettings.ArmHeadAnchor.RawValue);
            headAnchor.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.ArmHeadAnchor.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };

            PanelSlider headPosition = PanelSlider.CreateNew(content);
            headPosition.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 3f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            headPosition.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.headPositionStrength"));
            headPosition.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.headPositionStrength.description"));
            headPosition.SetValueWithoutNotify(BasisMediaPipeSettings.HeadPositionStrength.RawValue);
            headPosition.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.HeadPositionStrength.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
                BasisMediaPipeManagement.Instance.ApplySettings();
            };

            PanelSlider headRotation = PanelSlider.CreateNew(content);
            headRotation.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 3f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            headRotation.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.headRotationStrength"));
            headRotation.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.headRotationStrength.description"));
            headRotation.SetValueWithoutNotify(BasisMediaPipeSettings.HeadRotationStrength.RawValue);
            headRotation.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.HeadRotationStrength.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };

            PanelSlider headHeight = PanelSlider.CreateNew(content);
            headHeight.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = -0.25f, SliderMax = 0.25f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Meters });
            headHeight.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.headHeightTrim"));
            headHeight.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.headHeightTrim.description"));
            headHeight.SetValueWithoutNotify(BasisMediaPipeSettings.HeadHeight.RawValue);
            headHeight.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.HeadHeight.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };

            AddTuningToggle(BasisLocalization.Get("settings.mediapipe.tongueExperimental"), BasisLocalization.Get("settings.mediapipe.tongueExperimental.description"), BasisMediaPipeSettings.EnableTongue);

            PanelSlider tongueStrength = PanelSlider.CreateNew(content);
            tongueStrength.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 3f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            tongueStrength.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.tongueStrength"));
            tongueStrength.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.tongueStrength.description"));
            tongueStrength.SetValueWithoutNotify(BasisMediaPipeSettings.TongueStrength.RawValue);
            tongueStrength.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.TongueStrength.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };

            PanelButton calibrate = PanelButton.CreateNew(content);
            calibrate.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.calibrateHeadLookForward"));
            calibrate.Descriptor.SetDescription(BasisLocalization.Get("settings.mediapipe.calibrateHeadLookForward.description"));
            calibrate.OnClicked += () =>
            {
                BasisMediaPipeManagement.Instance.CalibrateHead();
                BasisMediaPipeManagement.Instance.ApplySettings();
            };

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
                }
                tabDescriptor?.ForceRebuild();
            });
        }
    }
}
