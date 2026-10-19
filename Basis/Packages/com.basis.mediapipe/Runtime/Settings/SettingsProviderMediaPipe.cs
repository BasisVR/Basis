using Unity.Scripting.LifecycleManagement;
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
    [AutoStaticsCleanup]
    public static partial class SettingsProviderMediaPipe
    {
        private static readonly List<string> PoseModelIds = new List<string> { BasisMediaPipeConfig.PoseModelLite, BasisMediaPipeConfig.PoseModelFull, BasisMediaPipeConfig.PoseModelHeavy };

        [RuntimeInitializeOnLoadMethod]
        private static void Register()
        {
            SettingsProvider.TrackerSettingsExtraBuilder = BuildSection;
            SettingsProvider.AddPageResetAction(SettingsProviderTrackerSettings.TabKey, ResetDefaults);
        }

        private static void ResetDefaults()
        {
            bool wasEnabled = BasisMediaPipeSettings.Enable.RawValue;
            BasisMediaPipeSettings.Enable.ResetToDefault();
            BasisMediaPipeSettings.EnableFace.ResetToDefault();
            BasisMediaPipeSettings.EnableHands.ResetToDefault();
            BasisMediaPipeSettings.EnableHeadRotation.ResetToDefault();
            BasisMediaPipeSettings.EnableHeadPosition.ResetToDefault();
            BasisMediaPipeSettings.EnableHandTracking.ResetToDefault();
            BasisMediaPipeSettings.EnableBody.ResetToDefault();
            BasisMediaPipeSettings.ShowPreview.ResetToDefault();
            BasisMediaPipeSettings.ResolutionWidth.ResetToDefault();
            BasisMediaPipeSettings.ResolutionHeight.ResetToDefault();
            BasisMediaPipeSettings.CameraFps.ResetToDefault();
            BasisMediaPipeSettings.CameraFpsAuto.ResetToDefault();
            BasisMediaPipeSettings.LowLightBoost.ResetToDefault();
            BasisMediaPipeSettings.Mirror.ResetToDefault();
            BasisMediaPipeSettings.RejectGlitches.ResetToDefault();
            BasisMediaPipeSettings.PoseModel.ResetToDefault();
            BasisMediaPipeSettings.HeadRotationStrength.ResetToDefault();
            BasisMediaPipeSettings.HeadPositionStrength.ResetToDefault();
            BasisMediaPipeSettings.HeadHeight.ResetToDefault();
            BasisMediaPipeSettings.InvertHeadYaw.ResetToDefault();
            BasisMediaPipeSettings.InvertHeadPitch.ResetToDefault();
            BasisMediaPipeSettings.InvertHeadRoll.ResetToDefault();
            BasisMediaPipeSettings.GazeStrength.ResetToDefault();
            BasisMediaPipeSettings.InvertBlink.ResetToDefault();
            BasisMediaPipeSettings.EnableTongue.ResetToDefault();
            BasisMediaPipeSettings.TongueStrength.ResetToDefault();
            BasisMediaPipeSettings.EnableArmElbowPole.ResetToDefault();
            BasisMediaPipeSettings.HandRotation.ResetToDefault();
            BasisMediaPipeSettings.ArmHeadAnchor.ResetToDefault();
            BasisMediaPipeSettings.ChestMotion.ResetToDefault();

            BasisMediaPipeManagement manager = BasisMediaPipeManagement.Instance;
            if (manager == null) return;
            if (wasEnabled != BasisMediaPipeSettings.Enable.RawValue) manager.SetEnabled(BasisMediaPipeSettings.Enable.RawValue);
            manager.ApplySettings();
        }

        private static void BuildSection(RectTransform parent)
        {
            void RebuildFrom(RectTransform changed) => PanelElementDescriptor.RebuildLayoutChain(changed, parent);

            PanelSectionToggle webcamToggle = PanelSectionToggle.CreateNewEntry(parent);
            PanelElementDescriptor webcamGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                webcamToggle, parent, BasisLocalization.Get("settings.mediapipe.webcamTracking"), showGroupTitle: false);
            webcamToggle.Descriptor?.SetTooltip(BasisLocalization.Get("settings.mediapipe.webcamTracking.description"));
            RectTransform webcamContent = webcamGroup.ContentParent;

            PanelToggle enableToggle = PanelToggle.CreateNewEntry(webcamContent);
            enableToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.enableWebcamTracking"));
            enableToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.mediapipe.enableWebcamTracking.description"));
            enableToggle.AssignBinding(BasisMediaPipeSettings.Enable);
            enableToggle.OnValueChanged += value => BasisMediaPipeManagement.Instance.SetEnabled(value);

            PanelElementDescriptor enabledGroup = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, webcamContent);
            if (enabledGroup.Header != null) enabledGroup.Header.gameObject.SetActive(false);
            enabledGroup.SetBackgroundVisible(false);
            RectTransform content = enabledGroup.ContentParent;

            MediaPipePreviewView preview = MediaPipePreviewView.Create(content);
            preview.gameObject.SetActive(BasisMediaPipeSettings.ShowPreview.RawValue);
            PanelElementDescriptor cameraState = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, content);
            cameraState.SetBackgroundVisible(false);
            cameraState.SetTitle(BasisLocalization.Get("settings.mediapipe.status"));
            cameraState.SetDescription(BasisLocalization.Get(BasisMediaPipeManagement.Instance != null ? BasisMediaPipeManagement.Instance.StatusKey : "settings.mediapipe.status.off"));

            PanelDropdown cameraDropdown = PanelDropdown.CreateNewEntry(content);
            cameraDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.camera"));
            cameraDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.mediapipe.camera.description"));
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

            void AddToggle(RectTransform target, string key, BasisSettingsBinding<bool> binding)
            {
                PanelToggle toggle = PanelToggle.CreateNewEntry(target);
                toggle.Descriptor.SetTitle(BasisLocalization.Get(key));
                toggle.Descriptor.SetTooltip(BasisLocalization.Get(key + ".description"));
                toggle.AssignBinding(binding);
                toggle.OnValueChanged += _ => BasisMediaPipeManagement.Instance.ApplySettings();
            }

            void AddSlider(RectTransform target, string key, BasisSettingsBinding<float> binding, float min, float max, ValueDisplayMode mode = ValueDisplayMode.Percentage)
            {
                PanelSlider slider = PanelSlider.CreateNew(target);
                slider.SetSliderSettings(new PanelSlider.SliderSettings { SliderMin = min, SliderMax = max, DecimalPlaces = 2, DisplayMode = mode });
                slider.Descriptor.SetTitle(BasisLocalization.Get(key));
                slider.Descriptor.SetTooltip(BasisLocalization.Get(key + ".description"));
                slider.AssignBinding(binding);
                slider.OnValueChanged += _ => BasisMediaPipeManagement.Instance.ApplyTuning();
            }

            void AddDropdown(RectTransform target, string key, List<string> entries, string current, string resetDefault, System.Action<string> onChanged)
            {
                PanelDropdown dropdown = PanelDropdown.CreateNewEntry(target);
                dropdown.Descriptor.SetTitle(BasisLocalization.Get(key));
                dropdown.Descriptor.SetTooltip(BasisLocalization.Get(key + ".description"));
                dropdown.AssignEntries(entries);
                if (entries.Contains(current)) dropdown.SetValueWithoutNotify(current);
                dropdown.SetResetDefault(resetDefault);
                dropdown.OnValueChanged += onChanged;
            }

            AddToggle(content, "settings.mediapipe.faceEyes", BasisMediaPipeSettings.EnableFace);
            AddToggle(content, "settings.mediapipe.handsFingers", BasisMediaPipeSettings.EnableHands);
            AddToggle(content, "settings.mediapipe.headRotation", BasisMediaPipeSettings.EnableHeadRotation);
            AddToggle(content, "settings.mediapipe.headPosition", BasisMediaPipeSettings.EnableHeadPosition);
            AddToggle(content, "settings.mediapipe.armTracking", BasisMediaPipeSettings.EnableHandTracking);
            AddToggle(content, "settings.mediapipe.bodyLeanTwist", BasisMediaPipeSettings.EnableBody);

            PanelButton calibrate = PanelButton.CreateNew(content);
            calibrate.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.calibrateHeadLookForward"));
            calibrate.Descriptor.SetTooltip(BasisLocalization.Get("settings.mediapipe.calibrateHeadLookForward.description"));
            calibrate.OnClicked += () => BasisMediaPipeManagement.Instance.CalibrateHead();

            PanelSectionToggle advancedToggle = PanelSectionToggle.CreateNewEntry(content);
            PanelElementDescriptor advancedGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                advancedToggle, content, BasisLocalization.Get("ui.advanced"), showGroupTitle: false);
            RectTransform advanced = advancedGroup.ContentParent;

            PanelSectionToggle cameraSection = null, headFaceSection = null, armsBodySection = null, diagnosticsSection = null;
            cameraSection = PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(advanced, BasisLocalization.Get("settings.mediapipe.camera"), () =>
            {
                PanelToggle previewToggle = PanelToggle.CreateNewEntry(advanced);
                previewToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.preview"));
                previewToggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.mediapipe.preview.description"));
                previewToggle.AssignBinding(BasisMediaPipeSettings.ShowPreview);
                previewToggle.OnValueChanged += value =>
                {
                    preview.gameObject.SetActive(value);
                    RebuildFrom(content);
                };

                AddDropdown(advanced, "settings.mediapipe.cameraResolution", new List<string> { "320 x 240", "640 x 480", "960 x 540", "1280 x 720" },
                    $"{BasisMediaPipeSettings.ResolutionWidth.RawValue} x {BasisMediaPipeSettings.ResolutionHeight.RawValue}",
                    $"{BasisMediaPipeSettings.ResolutionWidth.DefaultValue.GetDefault()} x {BasisMediaPipeSettings.ResolutionHeight.DefaultValue.GetDefault()}", choice =>
                {
                    string[] parts = choice.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int rw) && int.TryParse(parts[1].Trim(), out int rh))
                    {
                        BasisMediaPipeSettings.ResolutionWidth.SetValue(rw);
                        BasisMediaPipeSettings.ResolutionHeight.SetValue(rh);
                        BasisMediaPipeManagement.Instance.ApplySettings();
                    }
                });
                AddDropdown(advanced, "settings.mediapipe.cameraFrameRate", new List<string> { "15", "30", "60" }, BasisMediaPipeSettings.CameraFps.RawValue.ToString(), BasisMediaPipeSettings.CameraFps.DefaultValue.GetDefault().ToString(), choice =>
                {
                    if (int.TryParse(choice, out int fps))
                    {
                        BasisMediaPipeSettings.CameraFps.SetValue(fps);
                        BasisMediaPipeManagement.Instance.ApplySettings();
                    }
                });
                AddToggle(advanced, "settings.mediapipe.cameraFpsAuto", BasisMediaPipeSettings.CameraFpsAuto);
                AddToggle(advanced, "settings.mediapipe.lowLightBoost", BasisMediaPipeSettings.LowLightBoost);
                AddToggle(advanced, "settings.mediapipe.mirrorCamera", BasisMediaPipeSettings.Mirror);
                AddToggle(advanced, "settings.mediapipe.rejectGlitches", BasisMediaPipeSettings.RejectGlitches);

                PanelDropdown poseModelDropdown = PanelDropdown.CreateNewEntry(advanced);
                poseModelDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.poseModel"));
                poseModelDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.mediapipe.poseModel.description"));
                poseModelDropdown.AssignEntries(PoseModelIds, PoseModelIds.Select(id => BasisLocalization.Get("settings.mediapipe.poseModel." + id)).ToList());
                poseModelDropdown.AssignBinding(BasisMediaPipeSettings.PoseModel);
                poseModelDropdown.SetValueWithoutNotify(BasisMediaPipeConfig.NormalizePoseModel(BasisMediaPipeSettings.PoseModel.RawValue));
                poseModelDropdown.OnValueChanged += choice =>
                {
                    BasisMediaPipeManagement.Instance.ApplySettings();
                };
            }, false, _ => PanelSectionToggleHelpers.RebuildSectionLayout(cameraSection, parent));

            headFaceSection = PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(advanced, BasisLocalization.Get("settings.mediapipe.section.headFace"), () =>
            {
                AddSlider(advanced, "settings.mediapipe.headRotationStrength", BasisMediaPipeSettings.HeadRotationStrength, 0f, 3f);
                AddSlider(advanced, "settings.mediapipe.headPositionStrength", BasisMediaPipeSettings.HeadPositionStrength, 0f, 3f);
                AddSlider(advanced, "settings.mediapipe.headHeightTrim", BasisMediaPipeSettings.HeadHeight, -0.25f, 0.25f, ValueDisplayMode.Meters);
                AddToggle(advanced, "settings.mediapipe.invertHeadYaw", BasisMediaPipeSettings.InvertHeadYaw);
                AddToggle(advanced, "settings.mediapipe.invertHeadPitch", BasisMediaPipeSettings.InvertHeadPitch);
                AddToggle(advanced, "settings.mediapipe.invertHeadRoll", BasisMediaPipeSettings.InvertHeadRoll);
                AddSlider(advanced, "settings.mediapipe.gazeStrength", BasisMediaPipeSettings.GazeStrength, 0.25f, 3f);
                AddToggle(advanced, "settings.mediapipe.invertBlink", BasisMediaPipeSettings.InvertBlink);
                AddToggle(advanced, "settings.mediapipe.tongueExperimental", BasisMediaPipeSettings.EnableTongue);
                AddSlider(advanced, "settings.mediapipe.tongueStrength", BasisMediaPipeSettings.TongueStrength, 0f, 3f);
            }, false, _ => PanelSectionToggleHelpers.RebuildSectionLayout(headFaceSection, parent));

            armsBodySection = PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(advanced, BasisLocalization.Get("settings.mediapipe.section.armsBody"), () =>
            {
                AddToggle(advanced, "settings.mediapipe.armElbowPole", BasisMediaPipeSettings.EnableArmElbowPole);
                AddToggle(advanced, "settings.mediapipe.handRotation", BasisMediaPipeSettings.HandRotation);
                AddSlider(advanced, "settings.mediapipe.armHeadAnchor", BasisMediaPipeSettings.ArmHeadAnchor, 0f, 1f);
                AddSlider(advanced, "settings.mediapipe.chestMotion", BasisMediaPipeSettings.ChestMotion, 0f, 1.5f);
            }, false, _ => PanelSectionToggleHelpers.RebuildSectionLayout(armsBodySection, parent));

            PanelElementDescriptor statusField = null;
            void RefreshStatus()
            {
                if (statusField == null) return;
                BasisMediaPipeManagement manager = BasisMediaPipeManagement.Instance;
                statusField.SetDescription(manager != null ? manager.DiagnosticsText() : BasisLocalization.Get("settings.mediapipe.diagnostics.notStarted"));
                if (statusField.gameObject.activeInHierarchy) RebuildFrom(statusField.rectTransform);
            }
            diagnosticsSection = PanelSectionToggleHelpers.CreateCollapsibleBoxedSection(advanced, BasisLocalization.Get("settings.mediapipe.diagnostics"), () =>
            {
                statusField = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, advanced);
                statusField.SetBackgroundVisible(false);
                statusField.SetTitle(BasisLocalization.Get("settings.mediapipe.status"));
                statusField.SetDescription(BasisLocalization.Get("settings.mediapipe.status.description"));

                PanelButton refresh = PanelButton.CreateNew(advanced);
                refresh.Descriptor.SetTitle(BasisLocalization.Get("settings.mediapipe.refresh"));
                refresh.Descriptor.SetTooltip(BasisLocalization.Get("settings.mediapipe.diagnostics.description") + "\n" + BasisLocalization.Get("settings.mediapipe.refresh.description"));
                refresh.OnClicked += RefreshStatus;
            }, false, _ => PanelSectionToggleHelpers.RebuildSectionLayout(diagnosticsSection, parent));

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(advancedToggle, advancedGroup, false, visible => RebuildFrom(visible ? advanced : content));

            RefreshStatus();
            preview.StatusChanged += key =>
            {
                cameraState.SetDescription(BasisLocalization.Get(key));
                if (cameraState.gameObject.activeInHierarchy) RebuildFrom(cameraState.rectTransform);
                RefreshStatus();
            };

            void RefreshWebcamSettingsVisibility(bool on)
            {
                enabledGroup.SetActive(on);
                RebuildFrom(on ? content : webcamContent);
            }
            RefreshWebcamSettingsVisibility(BasisMediaPipeSettings.Enable.RawValue);
            enableToggle.OnValueChanged += RefreshWebcamSettingsVisibility;

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(webcamToggle, webcamGroup, false, visible =>
            {
                if (visible)
                {
                    preview.gameObject.SetActive(BasisMediaPipeSettings.ShowPreview.RawValue);
                    RefreshWebcamSettingsVisibility(BasisMediaPipeSettings.Enable.RawValue);
                }
                else
                {
                    RebuildFrom(parent);
                }
            });
        }
    }
}
