using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    public class CalibrationProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new CalibrationProvider());
        }

        public override string Title => BasisLocalization.Get("menu.provider.calibration");
        public override string IconAddress => AddressableAssets.Sprites.Calibrate;
        public override int Order => 50;

        public override bool Hidden => false;

        private readonly Dictionary<BasisInput, Action> _triggerDelegates = new();

        private BasisInput _leftHand;
        private BasisInput _rightHand;

        private bool _leftPressed;
        private bool _rightPressed;
        private bool _calibrated;

        // Pitch calibration state
        private enum PitchCalibrationStep
        {
            None,
            WaitingForUp,
            WaitingForDown,
            WaitingForForward
        }
        private PitchCalibrationStep _pitchStep = PitchCalibrationStep.None;
        private float _pitchUpY;
        private float _pitchDownY;

        public PanelButton Button;
        public PanelElementDescriptor HeightDescription;
        private PanelButton _pitchToggleButton;
        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.Instance.ActiveMenu.ReleaseInstance();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                new BasisMenuPanel.PanelData
                {
                    Title = this.Title,
                    PanelSize = new Vector2(440, 720),
                    PanelPosition = new Vector3(530, -150, 0),
                },
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            RectTransform container = panel.Descriptor.ContentParent;

            PanelElementDescriptor layout = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVertical, container);
            container = layout.ContentParent;

            Button = PanelButton.CreateNew(PanelButton.ButtonStyles.Default, container);
            Button.OnClicked += Calibrate;
            Button.Descriptor.SetTitle(BasisLocalization.Get("calibration.calibrate"));

            HeightDescription = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            HeightDescription.SetTitle(BasisLocalization.Get("calibration.additionalHeight"));
            HeightDescription.SetDescription($"{BasisHeightDriver.AdditionalPlayerHeight:F2}");

            var Description = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            Description.SetTitle(BasisLocalization.Get("calibration.pullTriggers"));

            var MinusButton = PanelButton.CreateNew(Description.ContentParent);
            MinusButton.OnClicked += DecreasePlayerSize;
            MinusButton.Descriptor.SetTitle(BasisLocalization.Get("calibration.decreaseHeight"));

            var PlusButton = PanelButton.CreateNew(Description.ContentParent);
            PlusButton.OnClicked += IncreasePlayerSize;
            PlusButton.Descriptor.SetTitle(BasisLocalization.Get("calibration.increaseHeight"));

            // Pitch calibration toggle
            _pitchToggleButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Default, container);
            _pitchToggleButton.OnClicked += TogglePitchCalibration;
            UpdatePitchToggleLabel();

            // Navigate to Body Tracking settings
            var bodyTrackingSettingsButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Default, container);
            bodyTrackingSettingsButton.Descriptor.SetTitle(BasisLocalization.Get("calibration.bodyTrackingSettings"));
            bodyTrackingSettingsButton.OnClicked += () => SettingsProvider.OpenBodyTrackingTab();

            // Reset Calibration (restores defaults for calibration-only state, including hidden pitch data)
            var resetButton = PanelButton.CreateNew(PanelButton.ButtonStyles.Default, container);
            resetButton.Descriptor.SetTitle(BasisLocalization.Get("calibration.reset"));
            resetButton.Descriptor.SetDescription(BasisLocalization.Get("calibration.resetDescription"));
            resetButton.OnClicked += PromptResetCalibration;
        }

        private void PromptResetCalibration()
        {
            BasisMainMenu.Instance.OpenDialogue(
                BasisLocalization.Get("calibration.reset"),
                BasisLocalization.Get("calibration.resetConfirm"),
                BasisLocalization.Get("ui.reset"),
                BasisLocalization.Get("ui.cancel"),
                value =>
                {
                    if (!value)
                    {
                        return;
                    }

                    ResetCalibration();
                });
        }

        private void ResetCalibration()
        {
            // Pitch calibration toggle (binding + module-static used by Calibrate())
            BasisSettingsDefaults.PitchCalibration.ResetToDefault();
            SMModuleCalibration.PitchCalibrationEnabled = BasisSettingsDefaults.PitchCalibration.RawValue;

            // Captured pitch calibration result (hidden backend state)
            BasisHeightDriver.HasPitchCalibratedHeight = false;
            BasisHeightDriver.PitchCalibratedEyeHeight = BasisHeightDriver.FallbackHeightInMeters;

            // Per-user additional height adjustment
            BasisHeightDriver.AdditionalPlayerHeight = 0f;
            BasisHeightDriver.ApplyScaleAndHeight();

            // Refresh on-screen labels for the controls we just reset
            HeightDescription.SetDescription($"{BasisHeightDriver.AdditionalPlayerHeight:F2}");
            UpdatePitchToggleLabel();
        }
        /// <summary>
        /// tracker balls
        /// </summary>
        public void IncreasePlayerSize()
        {
            BasisHeightDriver.AdditionalPlayerHeight += 0.1f;
            ApplyAndUpdateUI();
        }
        public void DecreasePlayerSize()
        {
            BasisHeightDriver.AdditionalPlayerHeight -= 0.1f;
            ApplyAndUpdateUI();
        }
        public void ApplyAndUpdateUI()
        {
            HeightDescription.SetDescription($"{BasisHeightDriver.AdditionalPlayerHeight:F2}");
            BasisHeightDriver.ApplyScaleAndHeight();
        }

        private void TogglePitchCalibration()
        {
            SMModuleCalibration.PitchCalibrationEnabled = !SMModuleCalibration.PitchCalibrationEnabled;
            UpdatePitchToggleLabel();
        }

        private void UpdatePitchToggleLabel()
        {
            if (_pitchToggleButton != null)
            {
                string state = BasisLocalization.Get(SMModuleCalibration.PitchCalibrationEnabled ? "ui.on" : "ui.off");
                _pitchToggleButton.Descriptor.SetTitle(BasisLocalization.Get("calibration.pitchLabel", state));
            }
        }

        public void Calibrate()
        {
            if (BasisLocalAvatarDriver.CurrentlyTposing)
            {
                return;
            }

            var localplayer = BasisLocalPlayer.Instance;
            BasisUINeedsVisibleTrackers.Instance.Add(localplayer);
            // kept because you had it (even if unused)
            var localBoneDriver = localplayer.LocalBoneDriver;

            _calibrated = false;
            _leftPressed = false;
            _rightPressed = false;

            if (SMModuleCalibration.PitchCalibrationEnabled && !SMModuleSitStand.IsSteatedMode)
            {
                // Start pitch calibration flow: look up → look down → look forward
                _pitchStep = PitchCalibrationStep.WaitingForUp;
                Button.Descriptor.SetTitle(BasisLocalization.Get("calibration.pitch.up"));
                SubscribeToTriggers();
            }
            else
            {
                // Standard single-pose calibration — clear any stale pitch data
                _pitchStep = PitchCalibrationStep.None;
                BasisHeightDriver.HasPitchCalibratedHeight = false;
                Button.Descriptor.SetTitle(BasisLocalization.Get("calibration.calibrating"));
                localplayer.LocalAvatarDriver.PutAvatarIntoTPose();
                SubscribeToTriggers();
            }
        }

        private void SubscribeToTriggers()
        {
            UnsubscribeAll();

            bool hasLeft = BasisDeviceManagement.Instance.FindDevice(out BasisInput leftHand, BasisBoneTrackedRole.LeftHand);
            bool hasRight = BasisDeviceManagement.Instance.FindDevice(out BasisInput rightHand, BasisBoneTrackedRole.RightHand);

            if (hasLeft && hasRight)
            {
                _leftHand = leftHand;
                _rightHand = rightHand;
                Subscribe(_leftHand, () => OnTriggerChanged(_leftHand));
                Subscribe(_rightHand, () => OnTriggerChanged(_rightHand));
            }
            else
            {
                foreach (BasisInput device in BasisDeviceManagement.Instance.AllInputDevices)
                {
                    Subscribe(device, () => OnTriggerChanged(device));
                }
            }
        }

        private void Subscribe(BasisInput device, Action handler)
        {
            _triggerDelegates[device] = handler;
            device.CurrentInputState.OnTriggerChanged += handler;
        }

        private void UnsubscribeAll()
        {
            foreach (KeyValuePair<BasisInput, Action> entry in _triggerDelegates)
            {
                entry.Key.CurrentInputState.OnTriggerChanged -= entry.Value;
            }

            _triggerDelegates.Clear();

            _leftHand = null;
            _rightHand = null;
        }

        private void OnTriggerChanged(BasisInput device)
        {
            if (_calibrated)
                return;

            float trigger = device.CurrentInputState.Trigger;

            // If we have both hands, require BOTH triggers pressed
            if (_leftHand != null && _rightHand != null)
            {
                if (device == _leftHand)
                    _leftPressed = (trigger >= 0.9f);

                if (device == _rightHand)
                    _rightPressed = (trigger >= 0.9f);

                if (_leftPressed && _rightPressed)
                    OnTriggersConfirmed();

                return;
            }

            // Fallback: any device trigger pressed
            if (trigger >= 0.9f)
            {
                OnTriggersConfirmed();
            }
        }

        private void OnTriggersConfirmed()
        {
            if (_calibrated)
                return;

            switch (_pitchStep)
            {
                case PitchCalibrationStep.WaitingForUp:
                    _pitchUpY = BasisLocalHeightCalculator.CaptureHMDHeightSample();
                    if (_pitchUpY <= 0f)
                    {
                        // No device, fall back to standard calibration
                        BasisDebug.LogWarning("Pitch calibration: no HMD for up sample, falling back to standard.", BasisDebug.LogTag.Avatar);
                        StartStandardCalibration();
                        return;
                    }
                    _pitchStep = PitchCalibrationStep.WaitingForDown;
                    Button.Descriptor.SetTitle(BasisLocalization.Get("calibration.pitch.down"));
                    // Reset trigger state for next step
                    _leftPressed = false;
                    _rightPressed = false;
                    break;

                case PitchCalibrationStep.WaitingForDown:
                    _pitchDownY = BasisLocalHeightCalculator.CaptureHMDHeightSample();
                    if (_pitchDownY <= 0f)
                    {
                        BasisDebug.LogWarning("Pitch calibration: no HMD for down sample, falling back to standard.", BasisDebug.LogTag.Avatar);
                        StartStandardCalibration();
                        return;
                    }
                    _pitchStep = PitchCalibrationStep.WaitingForForward;
                    Button.Descriptor.SetTitle(BasisLocalization.Get("calibration.pitch.forward"));
                    _leftPressed = false;
                    _rightPressed = false;
                    break;

                case PitchCalibrationStep.WaitingForForward:
                    float forwardY = BasisLocalHeightCalculator.CaptureHMDHeightSample();
                    if (forwardY <= 0f)
                    {
                        BasisDebug.LogWarning("Pitch calibration: no HMD for forward sample, falling back to standard.", BasisDebug.LogTag.Avatar);
                        StartStandardCalibration();
                        return;
                    }
                    // Compute corrected height and store it
                    float corrected = BasisLocalHeightCalculator.ComputePitchCalibratedHeight(_pitchUpY, _pitchDownY, forwardY);
                    BasisHeightDriver.PitchCalibratedEyeHeight = corrected;
                    BasisHeightDriver.HasPitchCalibratedHeight = true;
                    _pitchStep = PitchCalibrationStep.None;
                    // Now proceed with standard full-body calibration using the corrected height
                    StartStandardCalibration();
                    break;

                case PitchCalibrationStep.None:
                default:
                    CalibrateOnce();
                    break;
            }
        }

        private void StartStandardCalibration()
        {
            _pitchStep = PitchCalibrationStep.None;
            Button.Descriptor.SetTitle(BasisLocalization.Get("calibration.calibrating"));
            BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();
            // Reset trigger state so they need to press again for final calibration
            _leftPressed = false;
            _rightPressed = false;
            // Subscribe fresh for the final trigger press
            SubscribeToTriggers();
        }

        private void CalibrateOnce()
        {
            if (_calibrated)
                return;

            _calibrated = true;

            UnsubscribeAll();
            BasisAvatarIKStageCalibration.FullBodyCalibration();
            BasisUINeedsVisibleTrackers.Instance.Remove(BasisLocalPlayer.Instance);
            Button.Descriptor.SetTitle(BasisLocalization.Get("calibration.calibrate"));
        }

        public override void OnButtonCreated(PanelButton button)
        {
            base.OnButtonCreated(button);
            BasisDeviceManagement.OnBootModeChanged += BootModeChanged;
            BasisSettingsDefaults.EnableFBT.OnChanged += FBTToggleChanged;
            BoundButton.OnInstanceReleased += () =>
            {
                BasisDeviceManagement.OnBootModeChanged -= BootModeChanged;
                BasisSettingsDefaults.EnableFBT.OnChanged -= FBTToggleChanged;
            };
            EvaluateButtonVisibility();
        }

        private void BootModeChanged(string _) => EvaluateButtonVisibility();
        private void FBTToggleChanged(bool _) => EvaluateButtonVisibility();

        private void EvaluateButtonVisibility()
        {
            bool inVR = !BasisDeviceManagement.IsUserInDesktop();
            bool fbtEnabled = BasisSettingsDefaults.EnableFBT.RawValue;
            BoundButton.gameObject.SetActive(inVR && fbtEnabled);
        }
    }
}
