using Basis;
using UnityEngine;

namespace Basis.BasisUI.HandHeldCamera
{
    /// <summary>
    /// The Photogrammetry Path section on the Recording tab, sibling to the live Photogrammetry
    /// section above it: record a route by walking it once, then replay it to shoot one deliberate
    /// still per recorded point. Shares the record button/status plumbing with every other
    /// recorder on this tab — the state lives on the camera, which keeps recording or replaying
    /// with the panel closed.
    /// </summary>
    public partial class BasisHandHeldCameraPanelProvider
    {
        private PanelSectionToggle _photogrammetryPathSection;
        private PanelElementDescriptor _photogrammetryPathGroup;
        private PanelButton _photogrammetryPathRecordButton;
        private PanelElementDescriptor _photogrammetryPathStatus;
        private PanelButton _photogrammetryPathClearButton;
        private PanelSlider _photogrammetryPathSettleSlider;
        private PanelButton _photogrammetryPathReplayButton;
        private PanelElementDescriptor _photogrammetryPathReplayStatus;

        private string _lastPhotogrammetryPathButtonLabel;
        private string _lastPhotogrammetryPathStatusText;
        private float _lastPhotogrammetryPathSettle = float.NaN;
        private bool? _lastPhotogrammetryPathClearInteractable;
        private string _lastPhotogrammetryPathReplayButtonLabel;
        private string _lastPhotogrammetryPathReplayStatusText;
        private bool? _lastPhotogrammetryPathRecordInteractable;
        private bool? _lastPhotogrammetryPathReplayInteractable;

        private void BuildPhotogrammetryPathGroup(RectTransform parent)
        {
            _photogrammetryPathSection = PanelSectionToggle.CreateNewEntry(parent);
            _photogrammetryPathGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _photogrammetryPathSection, parent, BasisLocalization.Get("camera.photogrammetryPath"), false);
            RectTransform content = _photogrammetryPathGroup.ContentParent;

            RectTransform recordRow = PanelElementDescriptor.BuildActionRow(content, "CameraPhotogrammetryPathRecordRow");
            _photogrammetryPathRecordButton = PanelButton.CreateNew(recordRow);
            _photogrammetryPathRecordButton.Descriptor.SetTitle(BasisLocalization.Get("camera.photogrammetryPath.record"));
            _photogrammetryPathRecordButton.OnClicked += OnPhotogrammetryPathRecordClicked;

            _photogrammetryPathStatus = BuildRecordingStatusCard(content, "camera.photogrammetryPath.status", "camera.photogrammetryPath.status.empty");

            RectTransform clearRow = PanelElementDescriptor.BuildActionRow(content, "CameraPhotogrammetryPathClearRow");
            _photogrammetryPathClearButton = PanelButton.CreateNew(clearRow);
            _photogrammetryPathClearButton.Descriptor.SetTitle(BasisLocalization.Get("camera.photogrammetryPath.clear"));
            _photogrammetryPathClearButton.OnClicked += OnPhotogrammetryPathClearClicked;

            BasisHandHeldCameraUI.CameraSettings defaults = new BasisHandHeldCameraUI.CameraSettings();

            _photogrammetryPathSettleSlider = PanelSlider.CreateNew(content);
            _photogrammetryPathSettleSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.photogrammetryPath.settle"),
                BasisCameraRecordingLimits.MinPhotogrammetryPathSettleSeconds, BasisCameraRecordingLimits.MaxPhotogrammetryPathSettleSeconds,
                false, 1, ValueDisplayMode.Raw));
            _photogrammetryPathSettleSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.photogrammetryPath.settle.description"));
            _photogrammetryPathSettleSlider.SetResetDefault(defaults.photogrammetryPathSettleSeconds);
            _photogrammetryPathSettleSlider.OnValueChanged = v => _activeCamera?.SetPhotogrammetryPathSettleSeconds(v);

            RectTransform replayRow = PanelElementDescriptor.BuildActionRow(content, "CameraPhotogrammetryPathReplayRow");
            _photogrammetryPathReplayButton = PanelButton.CreateNew(replayRow);
            _photogrammetryPathReplayButton.Descriptor.SetTitle(BasisLocalization.Get("camera.photogrammetryPath.replay.record"));
            _photogrammetryPathReplayButton.OnClicked += OnPhotogrammetryPathReplayClicked;

            _photogrammetryPathReplayStatus = BuildRecordingStatusCard(
                content, "camera.photogrammetryPath.replay.status", "camera.photogrammetryPath.replay.status.idle");

            if (BasisCameraPhotoFolder.CanOpen)
            {
                RectTransform folderRow = PanelElementDescriptor.BuildActionRow(content, "CameraPhotogrammetryPathFolderRow");
                PanelButton openFolderButton = PanelButton.CreateNew(folderRow);
                openFolderButton.Descriptor.SetTitle(BasisLocalization.Get("camera.openPhotosFolder"));
                openFolderButton.OnClicked += () => BasisCameraPhotoFolder.Open();
            }
        }

        private void OnPhotogrammetryPathRecordClicked()
        {
            if (_activeCamera == null) return;

            if (_activeCamera.IsRecordingPhotogrammetryPath)
            {
                _activeCamera.StopRecordingPhotogrammetryPath();
            }
            else
            {
                _activeCamera.StartRecordingPhotogrammetryPath();
            }

            _lastPhotogrammetryPathButtonLabel = null;
            _lastPhotogrammetryPathStatusText = null;
            TickPhotogrammetryPathSection();
        }

        private void OnPhotogrammetryPathClearClicked()
        {
            _activeCamera?.ClearPhotogrammetryPath();
            _lastPhotogrammetryPathStatusText = null;
            TickPhotogrammetryPathSection();
        }

        private void OnPhotogrammetryPathReplayClicked()
        {
            if (_activeCamera == null) return;

            if (_activeCamera.IsReplayingPhotogrammetryPath)
            {
                _activeCamera.StopPhotogrammetryPathReplay();
            }
            else
            {
                _activeCamera.StartPhotogrammetryPathReplay();
            }

            _lastPhotogrammetryPathReplayButtonLabel = null;
            _lastPhotogrammetryPathReplayStatusText = null;
            TickPhotogrammetryPathSection();
        }

        /// <summary>Seeds the Photogrammetry Path controls from a camera the panel just bound.</summary>
        private void SeedPhotogrammetryPathControls()
        {
            if (_activeCamera == null) return;

            _photogrammetryPathSettleSlider?.SetValueWithoutNotify(_activeCamera.PhotogrammetryPathSettleSeconds);
            _lastPhotogrammetryPathSettle = _activeCamera.PhotogrammetryPathSettleSeconds;
            _lastPhotogrammetryPathClearInteractable = null;
            _lastPhotogrammetryPathRecordInteractable = null;
            _lastPhotogrammetryPathReplayInteractable = null;

            _lastPhotogrammetryPathButtonLabel = null;
            _lastPhotogrammetryPathStatusText = null;
            _lastPhotogrammetryPathReplayButtonLabel = null;
            _lastPhotogrammetryPathReplayStatusText = null;
            TickPhotogrammetryPathSection();
        }

        /// <summary>Per-tick sync, edge-gated so an unchanged value never restarts a widget's tweens.</summary>
        private void TickPhotogrammetryPathSection()
        {
            if (_activeCamera == null || _photogrammetryPathRecordButton == null) return;

            SyncSlider(_photogrammetryPathSettleSlider, _activeCamera.PhotogrammetryPathSettleSeconds, ref _lastPhotogrammetryPathSettle);

            BasisCameraRecordingState state = _activeCamera.PhotogrammetryState;
            bool recording = _activeCamera.IsRecordingPhotogrammetryPath;
            bool replaying = _activeCamera.IsReplayingPhotogrammetryPath;
            string recordLabel = BasisLocalization.Get(recording ? "camera.photogrammetryPath.stop" : "camera.photogrammetryPath.record");
            if (recordLabel != _lastPhotogrammetryPathButtonLabel)
            {
                _lastPhotogrammetryPathButtonLabel = recordLabel;
                _photogrammetryPathRecordButton.Descriptor.SetTitle(recordLabel);
            }

            bool canRecord = recording || state == BasisCameraRecordingState.Idle;
            if (_lastPhotogrammetryPathRecordInteractable != canRecord)
            {
                _lastPhotogrammetryPathRecordInteractable = canRecord;
                _photogrammetryPathRecordButton.SetInteractable(canRecord);
            }

            int count = _activeCamera.PhotogrammetryPathCount;
            string statusText = recording
                ? BasisLocalization.Get("camera.photogrammetryPath.status.recording", count)
                : count > 0
                    ? BasisLocalization.Get("camera.photogrammetryPath.status.ready", count,
                        FormatDuration(count * _activeCamera.PhotogrammetryPathSettleSeconds))
                    : BasisLocalization.Get("camera.photogrammetryPath.status.empty");
            if (statusText != _lastPhotogrammetryPathStatusText)
            {
                _lastPhotogrammetryPathStatusText = statusText;
                _photogrammetryPathStatus?.SetDescription(statusText);
            }

            bool canClear = count > 0;
            if (_lastPhotogrammetryPathClearInteractable != canClear)
            {
                _lastPhotogrammetryPathClearInteractable = canClear;
                _photogrammetryPathClearButton?.SetInteractable(canClear);
            }

            if (state == BasisCameraRecordingState.Recording && !replaying)
            {
                string busyLabel = BasisLocalization.Get("camera.photogrammetryPath.replay.record");
                if (busyLabel != _lastPhotogrammetryPathReplayButtonLabel)
                {
                    _lastPhotogrammetryPathReplayButtonLabel = busyLabel;
                    _photogrammetryPathReplayButton.Descriptor.SetTitle(busyLabel);
                }
                if (_lastPhotogrammetryPathReplayInteractable != false)
                {
                    _lastPhotogrammetryPathReplayInteractable = false;
                    _photogrammetryPathReplayButton.SetInteractable(false);
                }

                string busyStatus = BasisLocalization.Get("camera.photogrammetryPath.replay.status.liveBusy");
                if (busyStatus != _lastPhotogrammetryPathReplayStatusText)
                {
                    _lastPhotogrammetryPathReplayStatusText = busyStatus;
                    _photogrammetryPathReplayStatus?.SetDescription(busyStatus);
                }
                return;
            }

            TickRecordingControls(
                state, float.PositiveInfinity,
                _activeCamera.PhotogrammetryFramesCaptured, _activeCamera.PhotogrammetryFramesEncoded,
                clipNumber: 0,
                _activeCamera.LastPhotogrammetryFileName, _activeCamera.LastPhotogrammetryFailure,
                "camera.photogrammetryPath.replay", _photogrammetryPathReplayButton, _photogrammetryPathReplayStatus,
                ref _lastPhotogrammetryPathReplayButtonLabel, ref _lastPhotogrammetryPathReplayStatusText, ref _lastPhotogrammetryPathReplayInteractable,
                canStart: count > 0 && !recording);
        }

        /// <summary>A render's rough lower bound is point count times settle time — encode time between points is normally absorbed, not added.</summary>
        private static string FormatDuration(float totalSeconds)
        {
            int seconds = Mathf.CeilToInt(totalSeconds);
            if (seconds < 60) return $"{seconds}s";
            return $"{(totalSeconds / 60f):0.#}m";
        }

        private void ClearPhotogrammetryPathReferences()
        {
            _photogrammetryPathSection = null;
            _photogrammetryPathGroup = null;
            _photogrammetryPathRecordButton = null;
            _photogrammetryPathStatus = null;
            _photogrammetryPathClearButton = null;
            _photogrammetryPathSettleSlider = null;
            _photogrammetryPathReplayButton = null;
            _photogrammetryPathReplayStatus = null;
            _lastPhotogrammetryPathButtonLabel = null;
            _lastPhotogrammetryPathStatusText = null;
            _lastPhotogrammetryPathSettle = float.NaN;
            _lastPhotogrammetryPathClearInteractable = null;
            _lastPhotogrammetryPathReplayButtonLabel = null;
            _lastPhotogrammetryPathReplayStatusText = null;
            _lastPhotogrammetryPathRecordInteractable = null;
            _lastPhotogrammetryPathReplayInteractable = null;
        }
    }
}
