using Basis;
using UnityEngine;

namespace Basis.BasisUI.HandHeldCamera
{
    /// <summary>
    /// The Photogrammetry section on the Recording tab: start a session and the camera keeps
    /// capturing low-resolution stills, tagged with their pose, as it moves or turns past the
    /// configured thresholds. Shares the record button/status plumbing with the GIF and video
    /// sections above it, and the same "state lives on the camera, not the panel" rule — a session
    /// keeps running with the panel closed.
    /// </summary>
    public partial class BasisHandHeldCameraPanelProvider
    {
        private PanelSectionToggle _photogrammetrySection;
        private PanelElementDescriptor _photogrammetryGroup;
        private PanelButton _photogrammetryRecordButton;
        private PanelElementDescriptor _photogrammetryStatus;
        private PanelSlider _photogrammetryDistanceSlider;
        private PanelSlider _photogrammetryAngleSlider;
        private PanelDropdown _photogrammetryResolutionDropdown;
        private PanelButton _photogrammetryCaptureNowButton;

        private string _lastPhotogrammetryButtonLabel;
        private string _lastPhotogrammetryStatusText;
        private float _lastPhotogrammetryDistance = float.NaN;
        private float _lastPhotogrammetryAngle = float.NaN;
        private int _lastPhotogrammetryWidth = -1;
        private bool? _lastPhotogrammetryCaptureNowInteractable;

        private void BuildPhotogrammetryGroup(RectTransform parent)
        {
            _photogrammetrySection = PanelSectionToggle.CreateNewEntry(parent);
            _photogrammetryGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _photogrammetrySection, parent, BasisLocalization.Get("camera.photogrammetry"), false);
            RectTransform content = _photogrammetryGroup.ContentParent;

            RectTransform recordRow = PanelElementDescriptor.BuildActionRow(content, "CameraPhotogrammetryRecordRow");
            _photogrammetryRecordButton = PanelButton.CreateNew(recordRow);
            _photogrammetryRecordButton.Descriptor.SetTitle(BasisLocalization.Get("camera.photogrammetry.record"));
            _photogrammetryRecordButton.OnClicked += OnPhotogrammetryRecordClicked;

            _photogrammetryStatus = BuildRecordingStatusCard(content, "camera.photogrammetry.status", "camera.photogrammetry.status.idle");

            BasisHandHeldCameraUI.CameraSettings defaults = new BasisHandHeldCameraUI.CameraSettings();

            _photogrammetryDistanceSlider = PanelSlider.CreateNew(content);
            _photogrammetryDistanceSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.photogrammetry.distance"),
                BasisHandHeldCamera.MinPhotogrammetryDistanceMeters, BasisHandHeldCamera.MaxPhotogrammetryDistanceMeters,
                false, 2, ValueDisplayMode.Meters));
            _photogrammetryDistanceSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.photogrammetry.distance.description"));
            _photogrammetryDistanceSlider.SetResetDefault(defaults.photogrammetryDistanceMeters);
            _photogrammetryDistanceSlider.OnValueChanged = v => _activeCamera?.SetPhotogrammetryDistance(v);

            _photogrammetryAngleSlider = PanelSlider.CreateNew(content);
            _photogrammetryAngleSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.photogrammetry.angle"),
                BasisHandHeldCamera.MinPhotogrammetryAngleDegrees, BasisHandHeldCamera.MaxPhotogrammetryAngleDegrees,
                true, 0, ValueDisplayMode.Degrees));
            _photogrammetryAngleSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.photogrammetry.angle.description"));
            _photogrammetryAngleSlider.SetResetDefault(defaults.photogrammetryAngleDegrees);
            _photogrammetryAngleSlider.OnValueChanged = v => _activeCamera?.SetPhotogrammetryAngle(v);

            _photogrammetryResolutionDropdown = PanelDropdown.CreateNewEntry(content);
            _photogrammetryResolutionDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.photogrammetry.resolution"));
            _photogrammetryResolutionDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.photogrammetry.resolution.description"));
            _photogrammetryResolutionDropdown.AssignEntries(new System.Collections.Generic.List<string> { "480p", "720p" });
            _photogrammetryResolutionDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _photogrammetryResolutionDropdown == null) return;
                int index = _photogrammetryResolutionDropdown.Index;
                if (index >= 0 && index < BasisHandHeldCamera.PhotogrammetryWidthPresets.Length)
                {
                    _activeCamera.SetPhotogrammetryWidth(BasisHandHeldCamera.PhotogrammetryWidthPresets[index]);
                }
            };

            RectTransform captureNowRow = PanelElementDescriptor.BuildActionRow(content, "CameraPhotogrammetryCaptureNowRow");
            _photogrammetryCaptureNowButton = PanelButton.CreateNew(captureNowRow);
            _photogrammetryCaptureNowButton.Descriptor.SetTitle(BasisLocalization.Get("camera.photogrammetry.captureNow"));
            _photogrammetryCaptureNowButton.Descriptor.SetTooltip(BasisLocalization.Get("camera.photogrammetry.captureNow.description"));
            _photogrammetryCaptureNowButton.OnClicked += () => _activeCamera?.CapturePhotogrammetryFrameNow();

            if (BasisHandHeldCamera.CanOpenPhotosFolder)
            {
                RectTransform folderRow = PanelElementDescriptor.BuildActionRow(content, "CameraPhotogrammetryFolderRow");
                PanelButton openFolderButton = PanelButton.CreateNew(folderRow);
                openFolderButton.Descriptor.SetTitle(BasisLocalization.Get("camera.openPhotosFolder"));
                openFolderButton.OnClicked += () => BasisHandHeldCamera.OpenPhotosFolder();
            }
        }

        private void OnPhotogrammetryRecordClicked()
        {
            if (_activeCamera == null) return;

            if (_activeCamera.PhotogrammetryState == BasisCameraRecordingState.Recording)
            {
                _activeCamera.StopPhotogrammetrySession();
            }
            else if (_activeCamera.PhotogrammetryState == BasisCameraRecordingState.Idle)
            {
                _activeCamera.StartPhotogrammetrySession();
            }

            // The click moved the state out from under the caches; repaint on the next tick.
            _lastPhotogrammetryButtonLabel = null;
            _lastPhotogrammetryStatusText = null;
            TickPhotogrammetrySection();
        }

        /// <summary>Seeds the Photogrammetry controls from a camera the panel just bound.</summary>
        private void SeedPhotogrammetryControls()
        {
            if (_activeCamera == null) return;

            _photogrammetryDistanceSlider?.SetValueWithoutNotify(_activeCamera.PhotogrammetryDistanceMeters);
            _photogrammetryAngleSlider?.SetValueWithoutNotify(_activeCamera.PhotogrammetryAngleDegrees);
            _lastPhotogrammetryDistance = _activeCamera.PhotogrammetryDistanceMeters;
            _lastPhotogrammetryAngle = _activeCamera.PhotogrammetryAngleDegrees;
            _lastPhotogrammetryWidth = -1;
            SyncPhotogrammetryResolutionDropdown(_activeCamera.PhotogrammetryWidth, ref _lastPhotogrammetryWidth);
            _lastPhotogrammetryCaptureNowInteractable = null;

            _lastPhotogrammetryButtonLabel = null;
            _lastPhotogrammetryStatusText = null;
            TickPhotogrammetrySection();
        }

        /// <summary>
        /// Per-tick sync. A session carries on with the panel closed or on another tab, so
        /// everything here follows the camera, edge-gated so an unchanged value never restarts a
        /// widget's tweens.
        /// </summary>
        private void TickPhotogrammetrySection()
        {
            if (_activeCamera == null || _photogrammetryRecordButton == null) return;

            SyncSlider(_photogrammetryDistanceSlider, _activeCamera.PhotogrammetryDistanceMeters, ref _lastPhotogrammetryDistance);
            SyncSlider(_photogrammetryAngleSlider, _activeCamera.PhotogrammetryAngleDegrees, ref _lastPhotogrammetryAngle);
            SyncPhotogrammetryResolutionDropdown(_activeCamera.PhotogrammetryWidth, ref _lastPhotogrammetryWidth);

            bool canCaptureNow = _activeCamera.PhotogrammetryState == BasisCameraRecordingState.Recording;
            if (_lastPhotogrammetryCaptureNowInteractable != canCaptureNow)
            {
                _lastPhotogrammetryCaptureNowInteractable = canCaptureNow;
                _photogrammetryCaptureNowButton?.SetInteractable(canCaptureNow);
            }

            TickRecordingControls(
                _activeCamera.PhotogrammetryState, float.PositiveInfinity,
                _activeCamera.PhotogrammetryFramesCaptured, _activeCamera.PhotogrammetryFramesEncoded,
                clipNumber: 0,
                _activeCamera.LastPhotogrammetryFileName, _activeCamera.LastPhotogrammetryFailure,
                "camera.photogrammetry", _photogrammetryRecordButton, _photogrammetryStatus,
                ref _lastPhotogrammetryButtonLabel, ref _lastPhotogrammetryStatusText);
        }

        private void ClearPhotogrammetryReferences()
        {
            _photogrammetrySection = null;
            _photogrammetryGroup = null;
            _photogrammetryRecordButton = null;
            _photogrammetryStatus = null;
            _photogrammetryDistanceSlider = null;
            _photogrammetryAngleSlider = null;
            _photogrammetryResolutionDropdown = null;
            _photogrammetryCaptureNowButton = null;
            _lastPhotogrammetryButtonLabel = null;
            _lastPhotogrammetryStatusText = null;
            _lastPhotogrammetryDistance = float.NaN;
            _lastPhotogrammetryAngle = float.NaN;
            _lastPhotogrammetryWidth = -1;
            _lastPhotogrammetryCaptureNowInteractable = null;
        }

        /// <summary>
        /// Shows the camera's width as the nearest of the two friendly presets. Never moves a
        /// list that is open under the user's pointer, matching <see cref="SyncWidthDropdown"/>.
        /// </summary>
        private void SyncPhotogrammetryResolutionDropdown(int width, ref int cached)
        {
            PanelDropdown dropdown = _photogrammetryResolutionDropdown;
            if (dropdown == null || width == cached) return;
            if (dropdown.DropdownComponent != null && dropdown.DropdownComponent.IsExpanded) return;

            cached = width;

            int[] presets = BasisHandHeldCamera.PhotogrammetryWidthPresets;
            int nearest = 0;
            for (int index = 1; index < presets.Length; index++)
            {
                if (Mathf.Abs(presets[index] - width) < Mathf.Abs(presets[nearest] - width)) nearest = index;
            }
            dropdown.SetValueWithoutNotify(nearest == 0 ? "480p" : "720p");
        }
    }
}
