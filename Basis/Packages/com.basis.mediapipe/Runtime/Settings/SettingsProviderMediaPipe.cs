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
            webcamToggle.SetTitle("Webcam Tracking");
            int webcamStart = parent.childCount;

            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            group.SetDescription("Drive your avatar's face, eyes, fingers and hands from a webcam (MediaPipe). Requires the MediaPipe Unity Plugin and its models (see package README).");
            var content = group.ContentParent;

            PanelToggle enableToggle = PanelToggle.CreateNewEntry(content);
            enableToggle.Descriptor.SetTitle("Enable Webcam Tracking");
            enableToggle.Descriptor.SetDescription("Turn webcam tracking on or off.");
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
            cameraDropdown.Descriptor.SetTitle("Camera");
            cameraDropdown.Descriptor.SetDescription("Select which webcam to use.");
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
            resolutionDropdown.Descriptor.SetTitle("Camera Resolution");
            resolutionDropdown.Descriptor.SetDescription("Higher = sharper tracking but more CPU.");
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
            fpsDropdown.Descriptor.SetTitle("Camera Frame Rate");
            fpsDropdown.Descriptor.SetDescription("Requested webcam frames per second.");
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
            AddTuningToggle("Arm Elbow Pole (experimental)", "Steer the elbow with a pose-driven pole. May interact with full-body tracker calibration; leave off unless arms are tracking well first.", BasisMediaPipeSettings.EnableArmElbowPole);
            AddTuningToggle("Hand Rotation", "Off keeps the wrist aligned to the forearm (position only) to avoid noisy webcam wrist rotation.", BasisMediaPipeSettings.HandRotation);
            AddFeatureToggle("Body Lean/Twist", "Your avatar's chest leans, twists and sways with your torso. Uses the pose model (extra CPU). Set the amount with Chest Motion below.", BasisMediaPipeSettings.EnableBody);
            AddFeatureToggle("Mirror Camera", "Flip the camera horizontally (selfie view).", BasisMediaPipeSettings.Mirror);

            AddFeatureToggle("Swap Hands", "Fix left/right hands if they are reversed.", BasisMediaPipeSettings.SwapHands);
            AddTuningToggle("Invert Blink", "Fix blink if the eyes close when you open them.", BasisMediaPipeSettings.InvertBlink);
            AddTuningToggle("Invert Head Yaw", "Fix the head turn (left/right) direction.", BasisMediaPipeSettings.InvertHeadYaw);
            AddTuningToggle("Invert Head Pitch", "Fix the head nod (up/down) direction.", BasisMediaPipeSettings.InvertHeadPitch);
            AddTuningToggle("Invert Head Roll", "Fix the head tilt (ear-to-shoulder) direction.", BasisMediaPipeSettings.InvertHeadRoll);

            void AddSmoothingSlider(string title, BasisSettingsBinding<float> binding)
            {
                PanelSlider slider = PanelSlider.CreateNew(content);
                slider.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 1f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
                slider.Descriptor.SetTitle(title);
                slider.Descriptor.SetDescription("Higher = smoother but more latency.");
                slider.SetValueWithoutNotify(binding.RawValue);
                slider.OnValueChanged += value =>
                {
                    binding.SetValue(value);
                    BasisMediaPipeManagement.Instance.ApplyTuning();
                    BasisMediaPipeManagement.Instance.ApplySettings();
                };
            }

            AddSmoothingSlider("Head Smoothing", BasisMediaPipeSettings.HeadSmoothing);
            AddSmoothingSlider("Face Smoothing", BasisMediaPipeSettings.FaceSmoothing);
            AddSmoothingSlider("Hand Smoothing", BasisMediaPipeSettings.HandSmoothing);
            AddSmoothingSlider("Finger Smoothing", BasisMediaPipeSettings.FingerSmoothing);

            PanelSlider chestMotion = PanelSlider.CreateNew(content);
            chestMotion.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 1.5f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            chestMotion.Descriptor.SetTitle("Chest Motion");
            chestMotion.Descriptor.SetDescription("How much your torso lean, twist and sway carries into the avatar's chest. Needs Body Lean/Twist on. Below 100% reads as a suggestion of movement rather than a copy of it.");
            chestMotion.SetValueWithoutNotify(BasisMediaPipeSettings.ChestMotion.RawValue);
            chestMotion.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.ChestMotion.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };


            PanelSlider elbowRest = PanelSlider.CreateNew(content);
            elbowRest.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 1f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            elbowRest.Descriptor.SetTitle("Elbow Rest Bias");
            elbowRest.Descriptor.SetDescription("Pulls the elbow toward hanging naturally. The camera guesses elbow depth badly and the error shows up as the elbow riding too high, so raise this if your elbows wing out; lower it to trust the camera.");
            elbowRest.SetValueWithoutNotify(BasisMediaPipeSettings.ElbowRestBias.RawValue);
            elbowRest.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.ElbowRestBias.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };

            PanelSlider headAnchor = PanelSlider.CreateNew(content);
            headAnchor.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 1f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            headAnchor.Descriptor.SetTitle("Arm Head Anchor");
            headAnchor.Descriptor.SetDescription("Lines a raised hand up with the avatar's head rather than just scaling by arm length. Lower it if hands sit too high when you reach up.");
            headAnchor.SetValueWithoutNotify(BasisMediaPipeSettings.ArmHeadAnchor.RawValue);
            headAnchor.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.ArmHeadAnchor.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };

            PanelSlider headPosition = PanelSlider.CreateNew(content);
            headPosition.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 3f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            headPosition.Descriptor.SetTitle("Head Position Strength");
            headPosition.Descriptor.SetDescription("How much your head movement shifts the avatar's head position.");
            headPosition.SetValueWithoutNotify(BasisMediaPipeSettings.HeadPositionStrength.RawValue);
            headPosition.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.HeadPositionStrength.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
                BasisMediaPipeManagement.Instance.ApplySettings();
            };

            PanelSlider headRotation = PanelSlider.CreateNew(content);
            headRotation.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 3f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            headRotation.Descriptor.SetTitle("Head Rotation Strength");
            headRotation.Descriptor.SetDescription("Amplifies how far the avatar's head turns/nods relative to your real head.");
            headRotation.SetValueWithoutNotify(BasisMediaPipeSettings.HeadRotationStrength.RawValue);
            headRotation.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.HeadRotationStrength.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };

            PanelSlider headHeight = PanelSlider.CreateNew(content);
            headHeight.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = -0.25f, SliderMax = 0.25f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Meters });
            headHeight.Descriptor.SetTitle("Head Height Trim");
            headHeight.Descriptor.SetDescription("Fine vertical adjustment. The head is auto-fit to your avatar's eyes; nudge this only if needed.");
            headHeight.SetValueWithoutNotify(BasisMediaPipeSettings.HeadHeight.RawValue);
            headHeight.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.HeadHeight.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };

            AddTuningToggle("Tongue (experimental)", "Estimate tongue-out from the mouth interior (webcam heuristic).", BasisMediaPipeSettings.EnableTongue);

            PanelSlider tongueStrength = PanelSlider.CreateNew(content);
            tongueStrength.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = 0f, SliderMax = 3f, DecimalPlaces = 2, DisplayMode = ValueDisplayMode.Percentage });
            tongueStrength.Descriptor.SetTitle("Tongue Strength");
            tongueStrength.Descriptor.SetDescription("How strongly tongue-out is detected.");
            tongueStrength.SetValueWithoutNotify(BasisMediaPipeSettings.TongueStrength.RawValue);
            tongueStrength.OnValueChanged += value =>
            {
                BasisMediaPipeSettings.TongueStrength.SetValue(value);
                BasisMediaPipeManagement.Instance.ApplyTuning();
            };

            PanelButton calibrate = PanelButton.CreateNew(content);
            calibrate.Descriptor.SetTitle("Calibrate Head (look forward)");
            calibrate.Descriptor.SetDescription("Face the screen straight on, then click to set your neutral head pose.");
            calibrate.OnClicked += () =>
            {
                BasisMediaPipeManagement.Instance.CalibrateHead();
                BasisMediaPipeManagement.Instance.ApplySettings();
            };

            PanelElementDescriptor diagnostics = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, content);
            diagnostics.SetBackgroundVisible(false);
            diagnostics.SetTitle("Diagnostics");
            diagnostics.SetDescription("Live webcam tracking state.");

            PanelElementDescriptor statusField = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, diagnostics.ContentParent);
            statusField.SetTitle("Status");
            statusField.SetDescription("(press Refresh)");

            PanelButton refresh = PanelButton.CreateNew(diagnostics.ContentParent);
            refresh.Descriptor.SetTitle("Refresh");
            refresh.Descriptor.SetDescription("Poll the current tracking state.");

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
