using Basis;
using UnityEngine;

namespace Basis.BasisUI.HandHeldCamera
{
    /// <summary>
    /// The video section on the Image tab, directly under the GIF section it shares its
    /// plumbing with: record the camera feed to a Motion-JPEG AVI beside the photos. Distinct
    /// from Output's live streaming — this writes a file to keep, that publishes a feed.
    /// </summary>
    public partial class BasisHandHeldCameraPanelProvider
    {
        private PanelSectionToggle _videoSection;
        private PanelElementDescriptor _videoGroup;
        private PanelButton _videoRecordButton;
        private PanelElementDescriptor _videoStatus;
        private PanelToggle _videoTimeLimitToggle;
        private PanelToggle _videoAutoNewClipToggle;
        private PanelSlider _videoDurationSlider;
        private PanelSlider _videoRecordFrameRateSlider;
        private PanelDropdown _videoSizeDropdown;
        private PanelSlider _videoQualitySlider;

        private string _lastVideoButtonLabel;
        private string _lastVideoStatusText;
        private bool? _lastVideoInteractable;
        private bool? _lastVideoTimeLimit;
        private bool? _lastVideoAutoNewClip;
        private float _lastVideoDuration = float.NaN;
        private float _lastVideoFrameRate = float.NaN;
        private int _lastVideoWidth = -1;
        private float _lastVideoQuality = float.NaN;

        private void BuildVideoGroup(RectTransform parent)
        {
            _videoSection = PanelSectionToggle.CreateNewEntry(parent);
            _videoGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _videoSection, parent, BasisLocalization.Get("camera.video"), false);
            RectTransform content = _videoGroup.ContentParent;

            RectTransform recordRow = PanelElementDescriptor.BuildActionRow(content, "CameraVideoRecordRow");
            _videoRecordButton = PanelButton.CreateNew(recordRow);
            _videoRecordButton.Descriptor.SetTitle(BasisLocalization.Get("camera.video.record"));
            _videoRecordButton.OnClicked += OnVideoRecordClicked;

            _videoStatus = BuildRecordingStatusCard(content, "camera.video.status", "camera.video.status.idle");

            BasisHandHeldCameraUI.CameraSettings defaults = new BasisHandHeldCameraUI.CameraSettings();

            _videoTimeLimitToggle = PanelToggle.CreateNewEntry(content);
            _videoTimeLimitToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.video.timeLimit"));
            _videoTimeLimitToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.video.timeLimit.description"));
            _videoTimeLimitToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.VideoRecordingTimeLimit = v;
                RefreshVideoLimitVisibility();
            };

            _videoDurationSlider = PanelSlider.CreateNew(content);
            _videoDurationSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.video.length"),
                BasisCameraRecordingLimits.MinVideoDurationSeconds, BasisCameraRecordingLimits.MaxVideoDurationSeconds,
                true, 0, ValueDisplayMode.Raw));
            _videoDurationSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.video.length.description"));
            _videoDurationSlider.SetResetDefault(defaults.videoDurationSeconds);
            _videoDurationSlider.OnValueChanged = v => _activeCamera?.SetVideoRecordingDuration(v);

            _videoAutoNewClipToggle = PanelToggle.CreateNewEntry(content);
            _videoAutoNewClipToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.video.autoNewClip"));
            _videoAutoNewClipToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.video.autoNewClip.description"));
            _videoAutoNewClipToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.VideoContinuousClips = v;
            };

            _videoRecordFrameRateSlider = PanelSlider.CreateNew(content);
            _videoRecordFrameRateSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.video.frameRate"),
                BasisCameraRecordingLimits.MinVideoFrameRate, BasisCameraRecordingLimits.MaxVideoFrameRate,
                true, 0, ValueDisplayMode.Hz));
            _videoRecordFrameRateSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.video.frameRate.description"));
            _videoRecordFrameRateSlider.SetResetDefault(defaults.videoFrameRate);
            _videoRecordFrameRateSlider.OnValueChanged = v => _activeCamera?.SetVideoRecordingFrameRate((int)v);

            _videoSizeDropdown = PanelDropdown.CreateNewEntry(content);
            _videoSizeDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.video.size"));
            _videoSizeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.video.size.description"));
            _videoSizeDropdown.AssignEntries(BuildWidthLabels(BasisCameraRecordingLimits.VideoWidthPresets));
            _videoSizeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _videoSizeDropdown == null) return;
                int index = _videoSizeDropdown.Index;
                if (index >= 0 && index < BasisCameraRecordingLimits.VideoWidthPresets.Length)
                {
                    _activeCamera.SetVideoRecordingWidth(BasisCameraRecordingLimits.VideoWidthPresets[index]);
                }
            };

            _videoQualitySlider = PanelSlider.CreateNew(content);
            _videoQualitySlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.video.quality"),
                BasisCameraRecordingLimits.MinVideoQuality, BasisCameraRecordingLimits.MaxVideoQuality,
                true, 0, ValueDisplayMode.Raw));
            _videoQualitySlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.video.quality.description"));
            _videoQualitySlider.SetResetDefault(defaults.videoQuality);
            _videoQualitySlider.OnValueChanged = v => _activeCamera?.SetVideoRecordingQuality((int)v);

            if (BasisCameraPhotoFolder.CanOpen)
            {
                RectTransform folderRow = PanelElementDescriptor.BuildActionRow(content, "CameraVideoFolderRow");
                PanelButton openFolderButton = PanelButton.CreateNew(folderRow);
                openFolderButton.Descriptor.SetTitle(BasisLocalization.Get("camera.openPhotosFolder"));
                openFolderButton.OnClicked += () => BasisCameraPhotoFolder.Open();
            }
        }

        private void OnVideoRecordClicked()
        {
            if (_activeCamera == null) return;

            if (_activeCamera.VideoRecordingState == BasisCameraRecordingState.Recording)
            {
                _activeCamera.StopVideoRecording();
            }
            else if (_activeCamera.VideoRecordingState == BasisCameraRecordingState.Idle)
            {
                _activeCamera.StartVideoRecording();
            }

            _lastVideoButtonLabel = null;
            _lastVideoStatusText = null;
            TickVideoSection();
        }

        /// <summary>Seeds the video controls from a camera the panel just bound.</summary>
        private void SeedVideoControls()
        {
            if (_activeCamera == null) return;

            _videoDurationSlider?.SetValueWithoutNotify(_activeCamera.VideoRecordingDurationSeconds);
            _videoRecordFrameRateSlider?.SetValueWithoutNotify(_activeCamera.VideoRecordingFrameRate);
            _videoQualitySlider?.SetValueWithoutNotify(_activeCamera.VideoRecordingQuality);
            _lastVideoDuration = _activeCamera.VideoRecordingDurationSeconds;
            _lastVideoFrameRate = _activeCamera.VideoRecordingFrameRate;
            _lastVideoQuality = _activeCamera.VideoRecordingQuality;
            _lastVideoWidth = -1;
            SyncWidthDropdown(_videoSizeDropdown, BasisCameraRecordingLimits.VideoWidthPresets, _activeCamera.VideoRecordingWidth, ref _lastVideoWidth);
            _lastVideoTimeLimit = null;
            _lastVideoAutoNewClip = null;
            SyncToggle(_videoTimeLimitToggle, _activeCamera.VideoRecordingTimeLimit, ref _lastVideoTimeLimit);
            SyncToggle(_videoAutoNewClipToggle, _activeCamera.VideoContinuousClips, ref _lastVideoAutoNewClip);
            RefreshVideoLimitVisibility();

            _lastVideoButtonLabel = null;
            _lastVideoStatusText = null;
            _lastVideoInteractable = null;
            TickVideoSection();
        }

        /// <summary>
        /// The length slider and the auto-new-clip toggle only exist while there is a limit for
        /// them to act on — the one sets it, the other says what reaching it does.
        /// </summary>
        private void RefreshVideoLimitVisibility()
        {
            if (_activeCamera == null) return;

            bool limited = _activeCamera.VideoRecordingTimeLimit;
            bool changed = SetRowActive(_videoDurationSlider, limited);
            changed |= SetRowActive(_videoAutoNewClipToggle, limited);
            if (!changed) return;

            RefreshSearch();
            ForceLayoutRebuild(_videoGroup);
        }

        /// <summary>Shows or hides one row, reporting whether that was a change worth a rebuild.</summary>
        private static bool SetRowActive(PanelComponent row, bool active)
        {
            if (row == null || row.gameObject.activeSelf == active) return false;
            row.gameObject.SetActive(active);
            return true;
        }

        /// <summary>Per-tick sync, same shape and reasoning as the GIF section's.</summary>
        private void TickVideoSection()
        {
            if (_activeCamera == null || _videoRecordButton == null) return;

            SyncSlider(_videoDurationSlider, _activeCamera.VideoRecordingDurationSeconds, ref _lastVideoDuration);
            SyncSlider(_videoRecordFrameRateSlider, _activeCamera.VideoRecordingFrameRate, ref _lastVideoFrameRate);
            SyncSlider(_videoQualitySlider, _activeCamera.VideoRecordingQuality, ref _lastVideoQuality);
            SyncWidthDropdown(_videoSizeDropdown, BasisCameraRecordingLimits.VideoWidthPresets, _activeCamera.VideoRecordingWidth, ref _lastVideoWidth);
            SyncToggle(_videoTimeLimitToggle, _activeCamera.VideoRecordingTimeLimit, ref _lastVideoTimeLimit);
            SyncToggle(_videoAutoNewClipToggle, _activeCamera.VideoContinuousClips, ref _lastVideoAutoNewClip);
            RefreshVideoLimitVisibility();

            TickRecordingControls(
                _activeCamera.VideoRecordingState, _activeCamera.VideoSecondsRemaining,
                _activeCamera.VideoFramesCaptured, _activeCamera.VideoFramesEncoded,
                _activeCamera.VideoClipNumber,
                _activeCamera.LastVideoFileName, _activeCamera.LastVideoFailure,
                "camera.video", _videoRecordButton, _videoStatus,
                ref _lastVideoButtonLabel, ref _lastVideoStatusText, ref _lastVideoInteractable);
        }

        private void ClearVideoReferences()
        {
            _videoSection = null;
            _videoGroup = null;
            _videoRecordButton = null;
            _videoStatus = null;
            _videoTimeLimitToggle = null;
            _lastVideoTimeLimit = null;
            _videoAutoNewClipToggle = null;
            _lastVideoAutoNewClip = null;
            _videoDurationSlider = null;
            _videoRecordFrameRateSlider = null;
            _videoSizeDropdown = null;
            _videoQualitySlider = null;
            _lastVideoButtonLabel = null;
            _lastVideoStatusText = null;
            _lastVideoInteractable = null;
            _lastVideoDuration = float.NaN;
            _lastVideoFrameRate = float.NaN;
            _lastVideoWidth = -1;
            _lastVideoQuality = float.NaN;
        }
    }
}
