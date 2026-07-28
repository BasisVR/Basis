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
        public override int Order => 70;

        public override bool Hidden => false;

        private readonly Dictionary<BasisInput, Action> _triggerDelegates = new();

        private BasisInput _leftHand;
        private BasisInput _rightHand;

        private bool _leftPressed;
        private bool _rightPressed;
        private bool _calibrated;

        public PanelButton Button;
        private PanelElementDescriptor _reportGroup;
        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                new BasisMenuPanel.PanelData
                {
                    Title = this.Title,
                    PanelSize = new Vector2(587, 1025),
                    PanelPosition = new Vector3(456, 25, 0),
                },
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            panel.OnInstanceReleased += CancelActiveCalibration;

            RectTransform container = panel.Descriptor.ContentParent;

            PanelElementDescriptor layout = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.ScrollViewVertical, container);
            container = layout.ContentParent;

            Button = PanelButton.CreateNew(PanelButton.ButtonStyles.Default, container);
            Button.OnClicked += OnCalibrateButtonClicked;
            Button.Descriptor.SetTitle(BasisLocalization.Get("calibration.calibrate"));
            Button.Descriptor.SetTooltip(BasisLocalization.Get("calibration.calibrate.tooltip"));

            // See-through calibration mirror (implementation registers from the examples assembly):
            // shows only your avatar + calibration visuals, and unlike the pinned Personal Mirror it
            // spawns without closing the menu. Off by default.
            if (BasisCalibrationMirrorService.Available)
            {
                IBasisCalibrationMirror mirror = BasisCalibrationMirrorService.Provider;

                if (BasisSettingsDefaults.CalibrationMirror.RawValue && !mirror.IsUp)
                {
                    mirror.Summon();
                }

                var mirrorToggle = PanelToggle.CreateNewEntry(container);
                mirrorToggle.Descriptor.SetTitle(BasisLocalization.Get("calibration.mirror"));
                mirrorToggle.Descriptor.SetTooltip(BasisLocalization.Get("calibration.mirror.tooltip"));
                mirrorToggle.SetValueWithoutNotify(mirror.IsUp);

                mirrorToggle.OnValueChanged += value =>
                {
                    if (value)
                    {
                        mirror.Summon();
                    }
                    else
                    {
                        mirror.Hide();
                    }
                    BasisSettingsDefaults.CalibrationMirror.SetValue(mirror.IsUp);
                    mirrorToggle.SetValueWithoutNotify(mirror.IsUp);
                };
            }

            // Calibration quality report — filled in after a calibration completes.
            _reportGroup = null;
            if (BasisSettingsDefaults.DevShowCalibrationDebug.RawValue)
            {
                _reportGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                _reportGroup.SetTitle("Calibration Report");
                _reportGroup.SetDescription(BasisCalibrationQualityReport.HasReport ? BasisCalibrationQualityReport.Summary : "Calibrate to see a quality report.");
            }

            // Calibration modes (moved here from Body Tracking settings): seated/standing, avatar scaling, spine lock.
            var seatedModeDropdown = PanelDropdown.CreateNewEntry(container);
            seatedModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.seatedMode"));
            seatedModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.seatedMode.tooltip"));
            seatedModeDropdown.AssignLocalizedEntries(
                new List<string> { SettingsProviderIK.SeatedMode_Standing, SettingsProviderIK.SeatedMode_Seated },
                new List<string> { "settings.bodyTracking.seatedMode.standing", "settings.bodyTracking.seatedMode.seated" });
            seatedModeDropdown.AssignBinding(BasisSettingsDefaults.SitStand);

            var scalingModeDropdown = PanelDropdown.CreateNewEntry(container);
            scalingModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.ikMode"));
            scalingModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.ikMode.tooltip"));
            scalingModeDropdown.AssignLocalizedEntries(
                new List<string> { "Auto", "Eye Height", "Arm Distance" },
                new List<string> { "settings.bodyTracking.ikMode.auto", "settings.bodyTracking.ikMode.eyeHeight", "settings.bodyTracking.ikMode.armDistance" });
            scalingModeDropdown.AssignBinding(BasisSettingsDefaults.IKMode);

            // Arm To Height Ratio: scale by a percentage between the two measurements instead of a single
            // scaling mode. Overrides the Avatar Scaling Mode dropdown while enabled (VR only).
            var armToHeightToggle = PanelToggle.CreateNewEntry(container);
            armToHeightToggle.Descriptor.SetTitle("Arm To Height Ratio");
            armToHeightToggle.Descriptor.SetTooltip(
                "Scale the avatar by a percentage between your two measurements instead of a single scaling mode: " +
                "0% uses eye height, 100% uses arm distance, and values outside that range keep going in the " +
                "same direction. Overrides Avatar Scaling Mode while enabled.");
            armToHeightToggle.AssignBinding(BasisSettingsDefaults.EnableArmToHeightBlend);

            var armToHeightSlider = PanelSlider.CreateAndBind(
                container,
                PanelSlider.SliderSettings.Advanced("Arm To Height Ratio",
                    BasisCalibrationMath.ArmToHeightBlendMin, BasisCalibrationMath.ArmToHeightBlendMax,
                    false, 2, ValueDisplayMode.percentageFromZero),
                BasisSettingsDefaults.ArmToHeightBlend);

            var spineLockModeDropdown = PanelDropdown.CreateNewEntry(container);
            spineLockModeDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.spineLockMode"));
            spineLockModeDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.bodyTracking.spineLockMode.tooltip"));
            spineLockModeDropdown.AssignLocalizedEntries(
                new List<string> { "Lock Hips", "Lock Head", "Lock Both" },
                new List<string> { "settings.bodyTracking.spineLock.hips", "settings.bodyTracking.spineLock.head", "settings.bodyTracking.spineLock.both" });
            spineLockModeDropdown.AssignBinding(BasisSettingsDefaults.IKLockMode);

            // Slim calibration panel: inset each dropdown control's left edge so its label isn't squished.
            NarrowDropdownForPanel(seatedModeDropdown);
            NarrowDropdownForPanel(scalingModeDropdown);
            NarrowDropdownForPanel(spineLockModeDropdown);

            // Avatar Scaling Mode is moot in seated mode (a fixed height is used) and while the
            // Arm To Height Ratio blend replaces it, so disable it there.
            void UpdateScalingModeInteractable()
            {
                bool isSeated = seatedModeDropdown.DropdownComponent.options[seatedModeDropdown.DropdownComponent.value].text == SettingsProviderIK.SeatedMode_Seated;
                bool blendActive = BasisSettingsDefaults.EnableArmToHeightBlend.RawValue;
                scalingModeDropdown.SetInteractable(!isSeated && !blendActive,
                    isSeated ? BasisLocalization.Get("settings.bodyTracking.ikMode.disabledSeated")
                    : blendActive ? "Disabled while Arm To Height Ratio is enabled." : null);
            }
            seatedModeDropdown.OnValueChanged += _ => UpdateScalingModeInteractable();
            UpdateScalingModeInteractable();

            if (armToHeightSlider != null)
            {
                armToHeightSlider.Descriptor.SetTooltip(
                    "Percentage between the two measurements: 0% scales by eye height, 100% by arm distance. " +
                    "Negative pushes past eye height, above 100% pushes past arm distance.");
                armToHeightSlider.gameObject.SetActive(BasisSettingsDefaults.EnableArmToHeightBlend.RawValue);
            }
            armToHeightToggle.OnValueChanged += enabled =>
            {
                if (armToHeightSlider != null)
                {
                    armToHeightSlider.gameObject.SetActive(enabled);
                }
                UpdateScalingModeInteractable();
                layout.ForceRebuild();
            };

            // Lock-in guides toggle (shrinking spheres + foot-forward guide while calibrating).
            if (BasisSettingsDefaults.DevShowCalibrationDebug.RawValue)
            {
                var lockInGuidesToggle = PanelToggle.CreateNewEntry(container);
                lockInGuidesToggle.Descriptor.SetTitle(BasisLocalization.Get("calibration.lockInGuides"));
                lockInGuidesToggle.Descriptor.SetTooltip(BasisLocalization.Get("calibration.lockInGuides.tooltip"));
                lockInGuidesToggle.SetValueWithoutNotify(BasisCalibrationLockInVisualizer.Enabled);
                lockInGuidesToggle.OnValueChanged += value => BasisCalibrationLockInVisualizer.Enabled = value;
            }

            // Avatar scale
            var customScaleToggle = PanelToggle.CreateNewEntry(container);
            customScaleToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.bodyTracking.customScale"));
            customScaleToggle.Descriptor.SetTooltip(BasisLocalization.Get("calibration.customScale.tooltip"));
            customScaleToggle.AssignBinding(BasisSettingsDefaults.CustomScale);

            var avatarScaleSlider = PanelSlider.CreateAndBind(
                container,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.bodyTracking.avatarHeightScale"), 0.1f, 5f, false, 2, ValueDisplayMode.Meters),
                BasisSettingsDefaults.SelectedScale);
            if (avatarScaleSlider != null)
            {
                avatarScaleSlider.Descriptor.SetTooltip(FormatScaleMeters(avatarScaleSlider.Value));
                avatarScaleSlider.OnValueChanged += value => avatarScaleSlider.Descriptor.SetTooltip(FormatScaleMeters(value));
                avatarScaleSlider.gameObject.SetActive(BasisSettingsDefaults.CustomScale.RawValue);
                customScaleToggle.OnValueChanged += visible =>
                {
                    avatarScaleSlider.gameObject.SetActive(visible);
                    layout.ForceRebuild();
                };
            }
        }

        private static string FormatScaleMeters(float meters) => meters.ToString("0.##") + " m";

        // The dropdown control prefab is sized for the wide settings page; in the slim calibration panel its
        // label gets squished. Inset the control's left edge (the RectTransform "Left" field) so the title has room.
        private const float CalibrationDropdownLeftInset = 200f;
        private static void NarrowDropdownForPanel(PanelDropdown dropdown)
        {
            if (dropdown == null || dropdown.DropdownComponent == null)
            {
                return;
            }
            if (dropdown.DropdownComponent.transform is RectTransform rt)
            {
                rt.offsetMin = new Vector2(CalibrationDropdownLeftInset, rt.offsetMin.y);
            }
        }

        private void OnCalibrateButtonClicked()
        {
            if (BasisDeviceManagement.IsUserInDesktop() && _triggerDelegates.Count > 0 && !_calibrated)
            {
                OnTriggersConfirmed();
                return;
            }

            Calibrate();
        }

        public void Calibrate()
        {
            var localplayer = BasisLocalPlayer.Instance;
            if (localplayer == null)
            {
                return;
            }
            BasisUINeedsVisibleTrackers.Add(localplayer);
            // kept because you had it (even if unused)
            var localBoneDriver = localplayer.LocalBoneDriver;

            _calibrated = false;
            _leftPressed = false;
            _rightPressed = false;

            Button.Descriptor.SetTitle(GetAwaitConfirmTitle());
            localplayer.LocalAvatarDriver.PutAvatarIntoTPose();
            BasisCalibrationLockInVisualizer.Begin();
            SubscribeToTriggers();
        }

        // The wait-for-confirmation label must say HOW to confirm: VR completes by pulling both
        // triggers, desktop by clicking the button again.
        private static string GetAwaitConfirmTitle()
        {
            return BasisLocalization.Get(BasisDeviceManagement.IsUserInDesktop()
                ? "calibration.clickToConfirm"
                : "calibration.pullTriggers");
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

        private void CancelActiveCalibration()
        {
            UnsubscribeAll();
            BasisCalibrationLockInVisualizer.End();
            _leftPressed = false;
            _rightPressed = false;

            // The cutout mirror is owned by this panel: closing the panel takes it down.
            if (BasisCalibrationMirrorService.Available)
            {
                BasisCalibrationMirrorService.Provider.Hide();
            }

            if (BasisLocalPlayer.Instance == null)
            {
                return;
            }

            if (!_calibrated && BasisLocalAvatarDriver.CurrentlyTposing)
            {
                BasisLocalPlayer.Instance.LocalAvatarDriver.ResetAvatarAnimator();
                BasisLocalPlayer.Instance.LocalRigDriver.RigLayerActive = true;
            }

            BasisUINeedsVisibleTrackers.Remove(BasisLocalPlayer.Instance);

            if (Button != null && !Button.IsReleased)
            {
                Button.Descriptor.SetTitle(BasisLocalization.Get("calibration.calibrate"));
            }
        }

        private void OnTriggerChanged(BasisInput device)
        {
            // The calibration panel (and its Button) can be released while trigger
            // subscriptions are still active — e.g. a scene load fires input events
            // after the menu has been torn down. Stop listening and bail so we never
            // dereference a destroyed Button.
            if (Button == null || Button.IsReleased)
            {
                CancelActiveCalibration();
                return;
            }

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

            CalibrateOnce();
        }

        private void CalibrateOnce()
        {
            if (_calibrated)
                return;

            _calibrated = true;

            UnsubscribeAll();
            BasisCalibrationLockInVisualizer.End();
            BasisAvatarIKStageCalibration.FullBodyCalibration();
            BasisUINeedsVisibleTrackers.Remove(BasisLocalPlayer.Instance);
            Button.Descriptor.SetTitle(BasisLocalization.Get("calibration.calibrate"));

            BasisCalibrationQualityReport.Capture();
            if (_reportGroup != null)
            {
                _reportGroup.SetTitle(BasisCalibrationQualityReport.HasReport ? $"Calibration Report  —  {BasisCalibrationQualityReport.Grade}" : "Calibration Report");
                _reportGroup.SetDescription(BasisCalibrationQualityReport.HasReport ? BasisCalibrationQualityReport.Summary : "Calibration report unavailable.");
            }
        }

        public override void OnButtonCreated(PanelButton button)
        {
            base.OnButtonCreated(button);
            BasisDeviceManagement.OnBootModeChanged += BootModeChanged;
            BasisSettingsDefaults.EnableFBT.OnChanged += FBTToggleChanged;
            BasisSettingsDefaults.DevAlwaysShowCalibration.OnChanged += AlwaysShowCalibrationChanged;
            SetDeviceListSubscription(true);
            BoundButton.OnInstanceReleased += () =>
            {
                BasisDeviceManagement.OnBootModeChanged -= BootModeChanged;
                BasisSettingsDefaults.EnableFBT.OnChanged -= FBTToggleChanged;
                BasisSettingsDefaults.DevAlwaysShowCalibration.OnChanged -= AlwaysShowCalibrationChanged;
                SetDeviceListSubscription(false);
            };
            EvaluateButtonVisibility();
        }

        private void BootModeChanged(string _) => EvaluateButtonVisibility();
        private void FBTToggleChanged(bool _) => EvaluateButtonVisibility();
        private void AlwaysShowCalibrationChanged(bool _) => EvaluateButtonVisibility();

        private void SetDeviceListSubscription(bool subscribe)
        {
            BasisDeviceManagement manager = BasisDeviceManagement.Instance;
            if (manager == null)
            {
                return;
            }
            if (subscribe)
            {
                manager.AllInputDevices.OnListChanged += EvaluateButtonVisibility;
            }
            else
            {
                manager.AllInputDevices.OnListChanged -= EvaluateButtonVisibility;
            }
        }

        private void EvaluateButtonVisibility()
        {
            if (BoundButton == null || BoundButton.IsReleased)
            {
                return;
            }

            bool show = BasisSettingsDefaults.DevAlwaysShowCalibration.RawValue
                || !BasisDeviceManagement.IsUserInDesktop()
                || (BasisSettingsDefaults.EnableFBT.RawValue && HasNonCameraBodyTrackers());
            BoundButton.gameObject.SetActive(show);
        }

        /// <summary>
        /// True when at least one real or simulated full-body tracker is present that isn't
        /// camera/optical (MediaPipe) tracking. Webcam trackers flag themselves via
        /// <see cref="BasisInput.IsCameraTracked"/> and are excluded here.
        /// </summary>
        private static bool HasNonCameraBodyTrackers()
        {
            BasisDeviceManagement manager = BasisDeviceManagement.Instance;
            if (manager == null)
            {
                return false;
            }

            BasisObservableList<BasisInput> devices = manager.AllInputDevices;
            for (int i = 0; i < devices.Count; i++)
            {
                BasisInput device = devices[i];
                if (device == null || device.IsCameraTracked)
                {
                    continue;
                }
                if (device.TryGetRole(out BasisBoneTrackedRole role)
                    && BasisBoneTrackedRoleCommonCheck.CheckItsFBTracker(role))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
