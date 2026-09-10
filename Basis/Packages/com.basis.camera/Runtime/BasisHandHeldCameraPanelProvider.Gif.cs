using Basis;
using UnityEngine;

namespace Basis.BasisUI.HandHeldCamera
{
    /// <summary>
    /// The GIF section on the Image tab: record a short clip of the camera feed straight to a
    /// shareable animated GIF. The controls write through the camera's clamped setters, and the
    /// tick keeps the button and status honest about a recording the panel did not start — the
    /// state lives on the camera, which keeps recording with the panel closed. The button and
    /// status plumbing is shared with the video section below it, which records the same way.
    /// </summary>
    public partial class BasisHandHeldCameraPanelProvider
    {
        private PanelSectionToggle _gifSection;
        private PanelElementDescriptor _gifGroup;
        private PanelButton _gifRecordButton;
        private PanelElementDescriptor _gifStatus;
        private PanelSlider _gifDurationSlider;
        private PanelSlider _gifFrameRateSlider;
        private PanelDropdown _gifSizeDropdown;
        private PanelToggle _gifLoopToggle;
        private PanelToggle _gifDitherToggle;

        private string _lastGifButtonLabel;
        private string _lastGifStatusText;
        private bool? _lastGifInteractable;
        private float _lastGifDuration = float.NaN;
        private float _lastGifFrameRate = float.NaN;
        private int _lastGifWidth = -1;
        private bool? _lastGifLoop;
        private bool? _lastGifDither;

        private void BuildGifGroup(RectTransform parent)
        {
            _gifSection = PanelSectionToggle.CreateNewEntry(parent);
            _gifGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                _gifSection, parent, BasisLocalization.Get("camera.gif"), false);
            RectTransform content = _gifGroup.ContentParent;

            RectTransform recordRow = PanelElementDescriptor.BuildActionRow(content, "CameraGifRecordRow");
            _gifRecordButton = PanelButton.CreateNew(recordRow);
            _gifRecordButton.Descriptor.SetTitle(BasisLocalization.Get("camera.gif.record"));
            _gifRecordButton.OnClicked += OnGifRecordClicked;

            _gifStatus = BuildRecordingStatusCard(content, "camera.gif.status", "camera.gif.status.idle");

            BasisHandHeldCameraUI.CameraSettings defaults = new BasisHandHeldCameraUI.CameraSettings();

            _gifDurationSlider = PanelSlider.CreateNew(content);
            _gifDurationSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.gif.length"),
                BasisCameraRecordingLimits.MinGifDurationSeconds, BasisCameraRecordingLimits.MaxGifDurationSeconds,
                true, 0, ValueDisplayMode.Raw));
            _gifDurationSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.gif.length.description"));
            _gifDurationSlider.SetResetDefault(defaults.gifDurationSeconds);
            _gifDurationSlider.OnValueChanged = v => _activeCamera?.SetGifDuration(v);

            _gifFrameRateSlider = PanelSlider.CreateNew(content);
            _gifFrameRateSlider.SetSliderSettings(PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get("camera.gif.frameRate"),
                BasisCameraRecordingLimits.MinGifFrameRate, BasisCameraRecordingLimits.MaxGifFrameRate,
                true, 0, ValueDisplayMode.Hz));
            _gifFrameRateSlider.Descriptor.SetTooltip(BasisLocalization.Get("camera.gif.frameRate.description"));
            _gifFrameRateSlider.SetResetDefault(defaults.gifFrameRate);
            _gifFrameRateSlider.OnValueChanged = v => _activeCamera?.SetGifFrameRate((int)v);

            _gifSizeDropdown = PanelDropdown.CreateNewEntry(content);
            _gifSizeDropdown.Descriptor.SetTitle(BasisLocalization.Get("camera.gif.size"));
            _gifSizeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("camera.gif.size.description"));
            _gifSizeDropdown.AssignEntries(BuildWidthLabels(BasisCameraRecordingLimits.GifWidthPresets));
            _gifSizeDropdown.OnValueChanged = _ =>
            {
                if (_activeCamera == null || _gifSizeDropdown == null) return;
                int index = _gifSizeDropdown.Index;
                if (index >= 0 && index < BasisCameraRecordingLimits.GifWidthPresets.Length)
                {
                    _activeCamera.SetGifWidth(BasisCameraRecordingLimits.GifWidthPresets[index]);
                }
            };

            _gifLoopToggle = PanelToggle.CreateNewEntry(content);
            _gifLoopToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.gif.loop"));
            _gifLoopToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.gif.loop.description"));
            _gifLoopToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.GifLoop = v;
            };

            _gifDitherToggle = PanelToggle.CreateNewEntry(content);
            _gifDitherToggle.Descriptor.SetTitle(BasisLocalization.Get("camera.gif.dither"));
            _gifDitherToggle.Descriptor.SetTooltip(BasisLocalization.Get("camera.gif.dither.description"));
            _gifDitherToggle.OnValueChanged = v =>
            {
                if (_activeCamera != null) _activeCamera.GifDither = v;
            };

            if (BasisCameraPhotoFolder.CanOpen)
            {
                RectTransform folderRow = PanelElementDescriptor.BuildActionRow(content, "CameraGifFolderRow");
                PanelButton openFolderButton = PanelButton.CreateNew(folderRow);
                openFolderButton.Descriptor.SetTitle(BasisLocalization.Get("camera.openPhotosFolder"));
                openFolderButton.OnClicked += () => BasisCameraPhotoFolder.Open();
            }
        }

        private void OnGifRecordClicked()
        {
            if (_activeCamera == null) return;

            if (_activeCamera.GifState == BasisCameraRecordingState.Recording)
            {
                _activeCamera.StopGifRecording();
            }
            else if (_activeCamera.GifState == BasisCameraRecordingState.Idle)
            {
                _activeCamera.StartGifRecording();
            }

            // The click moved the state out from under the caches; repaint on the next tick.
            _lastGifButtonLabel = null;
            _lastGifStatusText = null;
            TickGifSection();
        }

        /// <summary>Seeds the GIF controls from a camera the panel just bound.</summary>
        private void SeedGifControls()
        {
            if (_activeCamera == null) return;

            _gifDurationSlider?.SetValueWithoutNotify(_activeCamera.GifDurationSeconds);
            _gifFrameRateSlider?.SetValueWithoutNotify(_activeCamera.GifFrameRate);
            _lastGifDuration = _activeCamera.GifDurationSeconds;
            _lastGifFrameRate = _activeCamera.GifFrameRate;
            SyncToggle(_gifLoopToggle, _activeCamera.GifLoop, ref _lastGifLoop);
            SyncToggle(_gifDitherToggle, _activeCamera.GifDither, ref _lastGifDither);
            _lastGifWidth = -1;
            SyncWidthDropdown(_gifSizeDropdown, BasisCameraRecordingLimits.GifWidthPresets, _activeCamera.GifWidth, ref _lastGifWidth);

            _lastGifButtonLabel = null;
            _lastGifStatusText = null;
            _lastGifInteractable = null;
            TickGifSection();
        }

        /// <summary>
        /// Per-tick sync. A recording carries on with the panel closed or on another tab, and a
        /// mode apply can move the sliders from underneath — so everything here follows the
        /// camera, edge-gated so an unchanged value never restarts a widget's tweens.
        /// </summary>
        private void TickGifSection()
        {
            if (_activeCamera == null || _gifRecordButton == null) return;

            SyncSlider(_gifDurationSlider, _activeCamera.GifDurationSeconds, ref _lastGifDuration);
            SyncSlider(_gifFrameRateSlider, _activeCamera.GifFrameRate, ref _lastGifFrameRate);
            SyncToggle(_gifLoopToggle, _activeCamera.GifLoop, ref _lastGifLoop);
            SyncToggle(_gifDitherToggle, _activeCamera.GifDither, ref _lastGifDither);
            SyncWidthDropdown(_gifSizeDropdown, BasisCameraRecordingLimits.GifWidthPresets, _activeCamera.GifWidth, ref _lastGifWidth);

            TickRecordingControls(
                _activeCamera.GifState, _activeCamera.GifSecondsRemaining,
                _activeCamera.GifFramesCaptured, _activeCamera.GifFramesEncoded,
                clipNumber: 0,
                _activeCamera.LastGifFileName, _activeCamera.LastGifFailure,
                "camera.gif", _gifRecordButton, _gifStatus,
                ref _lastGifButtonLabel, ref _lastGifStatusText, ref _lastGifInteractable);
        }

        private void ClearGifReferences()
        {
            _gifSection = null;
            _gifGroup = null;
            _gifRecordButton = null;
            _gifStatus = null;
            _gifDurationSlider = null;
            _gifFrameRateSlider = null;
            _gifSizeDropdown = null;
            _gifLoopToggle = null;
            _gifDitherToggle = null;
            _lastGifButtonLabel = null;
            _lastGifStatusText = null;
            _lastGifInteractable = null;
            _lastGifDuration = float.NaN;
            _lastGifFrameRate = float.NaN;
            _lastGifWidth = -1;
            _lastGifLoop = null;
            _lastGifDither = null;
        }

        // ---- shared between the GIF and video sections ----------------------------------

        /// <summary>Text-only status card: the icon slot and control reservation both go, or the description wraps into a sliver.</summary>
        private static PanelElementDescriptor BuildRecordingStatusCard(RectTransform parent, string titleKey, string idleKey)
        {
            PanelElementDescriptor card = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            card.SetTitle(BasisLocalization.Get(titleKey));
            card.SetDescription(string.IsNullOrEmpty(idleKey) ? string.Empty : BasisLocalization.Get(idleKey));
            if (card.IconBackground != null) card.IconBackground.SetActive(false);
            ReleaseControlSlot(card);
            return card;
        }

        private static System.Collections.Generic.List<string> BuildWidthLabels(int[] presets)
        {
            var labels = new System.Collections.Generic.List<string>(presets.Length);
            for (int Index = 0; Index < presets.Length; Index++)
            {
                labels.Add($"{presets[Index]} px");
            }
            return labels;
        }

        /// <summary>
        /// Shows the camera's width on a preset dropdown, as the nearest preset. Never moves a
        /// list that is open under the user's pointer; the cache only advances on a successful
        /// write, so the value is re-read the moment it closes.
        /// </summary>
        private static void SyncWidthDropdown(PanelDropdown dropdown, int[] presets, int width, ref int cached)
        {
            if (dropdown == null || width == cached) return;
            if (dropdown.DropdownComponent != null && dropdown.DropdownComponent.IsExpanded) return;

            cached = width;

            int nearest = 0;
            for (int Index = 1; Index < presets.Length; Index++)
            {
                if (Mathf.Abs(presets[Index] - width) < Mathf.Abs(presets[nearest] - width))
                {
                    nearest = Index;
                }
            }
            dropdown.SetValueWithoutNotify($"{presets[nearest]} px");
        }

        /// <summary>
        /// The record button and status card for one clip recorder, edge-gated. Both recorders
        /// speak the same states, so the only thing that differs is the key prefix their
        /// wording lives under. <paramref name="clipNumber"/> is zero for a recording that is a
        /// single clip, and which clip is being taken for one that rolls into new ones.
        /// </summary>
        private void TickRecordingControls(
            BasisCameraRecordingState state,
            float secondsRemaining,
            int framesCaptured,
            int framesEncoded,
            int clipNumber,
            string lastFileName,
            string lastFailure,
            string keyPrefix,
            PanelButton recordButton,
            PanelElementDescriptor statusCard,
            ref string lastButtonLabel,
            ref string lastStatusText,
            ref bool? lastInteractable,
            bool canStart = true)
        {
            string buttonLabel;
            string statusText;
            bool interactable = true;

            switch (state)
            {
                case BasisCameraRecordingState.Recording:
                    // No countdown where there is nothing for it to count down to: an unlimited
                    // recording, and a run of clips, both end when the button is pressed. The
                    // countdown to the next roll would read as a countdown to the end.
                    buttonLabel = clipNumber > 0 || float.IsPositiveInfinity(secondsRemaining)
                        ? BasisLocalization.Get(keyPrefix + ".stopUnlimited")
                        : BasisLocalization.Get(keyPrefix + ".stop", Mathf.CeilToInt(secondsRemaining));
                    statusText = clipNumber > 0
                        ? BasisLocalization.Get(keyPrefix + ".status.recordingClip", clipNumber, framesCaptured)
                        : BasisLocalization.Get(keyPrefix + ".status.recording", framesCaptured);
                    break;

                case BasisCameraRecordingState.Saving:
                    buttonLabel = BasisLocalization.Get(keyPrefix + ".saving");
                    statusText = BasisLocalization.Get(keyPrefix + ".status.saving", framesEncoded, framesCaptured);
                    interactable = false;
                    break;

                default:
                    buttonLabel = BasisLocalization.Get(keyPrefix + ".record");
                    interactable = canStart;
                    if (BasisNetworkModeration.CameraCaptureBlockedLocally)
                    {
                        statusText = BasisLocalization.Get(keyPrefix + ".status.blocked");
                        interactable = false;
                    }
                    else if (lastFailure != null)
                    {
                        statusText = BasisLocalization.Get(keyPrefix + ".status.failed", lastFailure);
                    }
                    else if (lastFileName != null)
                    {
                        statusText = BasisLocalization.Get(keyPrefix + ".status.saved", lastFileName);
                    }
                    else
                    {
                        statusText = BasisLocalization.Get(keyPrefix + ".status.idle");
                    }
                    break;
            }

            if (buttonLabel != lastButtonLabel)
            {
                lastButtonLabel = buttonLabel;
                recordButton.Descriptor.SetTitle(buttonLabel);
            }

            if (interactable != lastInteractable)
            {
                lastInteractable = interactable;
                recordButton.SetInteractable(interactable);
            }

            if (statusText != lastStatusText)
            {
                lastStatusText = statusText;
                statusCard?.SetDescription(statusText);
            }
        }
    }
}
