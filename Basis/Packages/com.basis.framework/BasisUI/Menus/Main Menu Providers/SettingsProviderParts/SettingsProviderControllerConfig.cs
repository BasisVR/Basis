using Basis;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static BasisActionDriver;

public static class SettingsProviderControllerConfig
{
    // Layout, top to bottom: the everyday settings first (General, then Turning in VR or
    // Keyboard Bindings on desktop), then the VR tuning sections, then bindings and placement.
    // Sections that only make sense on one input mode are not built on the other.
    public static PanelTabPage OpenControllerConfig(PanelTabGroup tabGroup)
    {
        PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
        PanelElementDescriptor descriptor = tab.Descriptor;
        RectTransform container = descriptor.ContentParent;

        bool isVR = BasisDeviceManagement.IsCurrentModeVR();
        bool isDesktop = BasisDeviceManagement.IsUserInDesktop();

        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.controls.general.title"), BasisLocalization.Get("settings.controls.general.description"), group =>
        {
            PanelDropdown dropdownDominantHand = PanelDropdown.CreateNewEntry(group);
            dropdownDominantHand.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.dominantHand"));
            dropdownDominantHand.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.dominantHand.tooltip"));
            dropdownDominantHand.AssignLocalizedEntries(
                new List<string> { BasisDominantHand.Right, BasisDominantHand.Left },
                new List<string> { "settings.controls.dominantHand.right", "settings.controls.dominantHand.left" });
            dropdownDominantHand.AssignBinding(BasisSettingsDefaults.DominantHand);

            if (isDesktop)
            {
                PanelSlider mousesensitivty = PanelSlider.CreateEntryAndBind(
                    group,
                    PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.mouseSensitivity"), 0, 2f, false, 2, ValueDisplayMode.Percentage),
                    BasisSettingsDefaults.mousesensitivty);
                mousesensitivty.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.mouseSensitivity.tooltip"));

                PanelToggle toggleInvertMouse = PanelToggle.CreateNewEntry(group);
                toggleInvertMouse.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.invertMouse"));
                toggleInvertMouse.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.invertMouse.tooltip"));
                toggleInvertMouse.AssignBinding(BasisSettingsDefaults.InvertMouse);
            }

            if (isVR)
            {
                PanelDropdown dropdownDesktopInputInVR = PanelDropdown.CreateNewEntry(group);
                dropdownDesktopInputInVR.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.desktopInputInVR"));
                dropdownDesktopInputInVR.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.desktopInputInVR.tooltip"));
                dropdownDesktopInputInVR.AssignLocalizedEntries(
                    new List<string>
                    {
                        BasisSettingsDefaults.DesktopInputInVR_Adaptive,
                        BasisSettingsDefaults.DesktopInputInVR_AlwaysOn,
                        BasisSettingsDefaults.DesktopInputInVR_Off,
                    },
                    new List<string>
                    {
                        "settings.controls.desktopInputInVR.adaptive",
                        "settings.controls.desktopInputInVR.alwaysOn",
                        "settings.controls.desktopInputInVR.off",
                    });
                dropdownDesktopInputInVR.AssignBinding(BasisSettingsDefaults.DesktopInputInVR);

                PanelToggle toggleQuestControllerFix = PanelToggle.CreateNewEntry(group);
                toggleQuestControllerFix.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.questControllerFix"));
                toggleQuestControllerFix.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.questControllerFix.tooltip"));
                toggleQuestControllerFix.AssignBinding(BasisSettingsDefaults.QuestControllerFix);
            }
        });

        if (isVR)
        {
            BuildTurningSection(container);
        }

        if (isDesktop)
        {
            SettingsProviderKeyboardBindings.BuildKeyboardBindingsUI(tab);
        }

        // VR tuning: none of these have a desktop equivalent (no triggers, sticks or fingertips).
        if (isVR)
        {
            BuildFingerTouchSection(container);
            BuildTriggerSection(container);
            BuildThumbstickSection(container);

            // Joystick slider binding — hand a slider to a thumbstick and tune it away from the menu.
            SettingsProviderKeyboardBindings.CreateCollapsibleSection(
                container, BasisLocalization.Get("settings.controls.joystickBind.title"),
                BasisLocalization.Get("settings.controls.joystickBind.description"), BuildJoystickBindSection);
        }

        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.controls.actionBindings.title"),
            BasisLocalization.Get("settings.controls.actionBindings.description"), group =>
        {
            BuildBindingsUI(group.ContentParent);
        });

        // Grid Snap (placement snapping for held objects)
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.developer.gridSnap.title"),
            BasisLocalization.Get("settings.developer.gridSnap.description"), group =>
        {
            PanelToggle toggleForceGridSnap = PanelToggle.CreateNewEntry(group);
            toggleForceGridSnap.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.gridSnap.force"));
            toggleForceGridSnap.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gridSnap.force.tooltip"));
            toggleForceGridSnap.AssignBinding(BasisSettingsDefaults.ForceGridSnap);

            PanelSlider sliderGridSnapSize = PanelSlider.CreateEntryAndBind(
                group,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.developer.gridSnap.size"),
                    BasisLocalization.Get("settings.developer.gridSnap.size.description"),
                    0.05f, 5f, false, 2, ValueDisplayMode.Meters),
                BasisSettingsDefaults.GridSnapSize);
            sliderGridSnapSize.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gridSnap.size.tooltip"));

            PanelToggle toggleForceRotationSnap = PanelToggle.CreateNewEntry(group);
            toggleForceRotationSnap.Descriptor.SetTitle(BasisLocalization.Get("settings.developer.gridSnap.forceRotation"));
            toggleForceRotationSnap.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gridSnap.forceRotation.tooltip"));
            toggleForceRotationSnap.AssignBinding(BasisSettingsDefaults.ForceRotationSnap);

            PanelSlider sliderRotationSnapDegrees = PanelSlider.CreateEntryAndBind(
                group,
                new PanelSlider.SliderSettings(
                    BasisLocalization.Get("settings.developer.gridSnap.rotation"),
                    BasisLocalization.Get("settings.developer.gridSnap.rotation.description"),
                    1f, 90f, false, 1, ValueDisplayMode.Degrees),
                BasisSettingsDefaults.RotationSnapDegrees);
            sliderRotationSnapDegrees.Descriptor.SetTooltip(BasisLocalization.Get("settings.developer.gridSnap.rotation.tooltip"));
        });

        if (BasisSettingsDefaults.ShowDeveloperTab.RawValue)
        {
            SettingsProviderSpawnAnchors.Build(container, descriptor);
        }

        SettingsProvider.RegisterPageReset("settings.tab.controls", () =>
        {
            ResetControlsDefaults();
            BasisPanelJoystickBind.Clear();
            BasisActionDriver.ResetBindingsToDefaultsAsyncIgnored();
            if (BasisLocalInputActions.Asset != null)
            {
                SettingsProviderKeyboardBindings.ResetAllBindings(BasisLocalInputActions.Asset);
            }
        });

        descriptor.ForceRebuild();
        return tab;
    }
    /// <summary>
    /// Snap turn on/off, then only the one slider that applies: the snap angle, or the smooth
    /// turn speed.
    /// </summary>
    private static void BuildTurningSection(RectTransform container)
    {
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.controls.turning.title"), BasisLocalization.Get("settings.controls.turning.description"), group =>
        {
            PanelToggle snapturntoggle = PanelToggle.CreateNewEntry(group);
            snapturntoggle.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.snapTurn"));
            snapturntoggle.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.snapTurn.tooltip"));
            snapturntoggle.AssignBinding(BasisSettingsDefaults.usesnapturn);

            PanelSlider sliderSnapTurnAngle = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.snapTurnAngle"), 0, 120, true, 0, ValueDisplayMode.Degrees),
                BasisSettingsDefaults.SnapTurnAngle);
            sliderSnapTurnAngle.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.snapTurnAngle.tooltip"));

            PanelSlider sliderSmoothTurnSpeed = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.smoothTurnSpeed"), 50, 400, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.SmoothTurnSpeed);
            sliderSmoothTurnSpeed.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.smoothTurnSpeed.tooltip"));

            void ApplyTurnMode(bool snapOn)
            {
                sliderSnapTurnAngle.Descriptor.SetActive(snapOn);
                sliderSmoothTurnSpeed.Descriptor.SetActive(!snapOn);
            }
            ApplyTurnMode(BasisSettingsDefaults.usesnapturn.RawValue);
            snapturntoggle.OnValueChanged += isOn =>
            {
                ApplyTurnMode(isOn);
                group.ForceRebuild();
            };
        });
    }
    /// <summary>
    /// VR finger touch — direct fingertip presses on menus (BasisDirectTouch). The tuning rows only
    /// show while the feature is on.
    /// </summary>
    private static void BuildFingerTouchSection(RectTransform container)
    {
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.general.fingerTouch.title"),
            BasisLocalization.Get("settings.general.disableVRFingerTouch.description"), group =>
        {
            List<PanelElementDescriptor> tuning = new List<PanelElementDescriptor>();

            PanelToggle toggleDisableVRFingerTouch = PanelToggle.CreateNewEntry(group);
            toggleDisableVRFingerTouch.AssignBinding(BasisSettingsDefaults.DisableVRFingerTouch);
            toggleDisableVRFingerTouch.Descriptor.SetTitle(BasisLocalization.Get("settings.general.disableVRFingerTouch"));
            toggleDisableVRFingerTouch.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.disableVRFingerTouch.tooltip"));

            PanelDropdown dropdownTouchHands = PanelDropdown.CreateNewEntry(group);
            dropdownTouchHands.Descriptor.SetTitle(BasisLocalization.Get("settings.general.fingerTouch.hands"));
            dropdownTouchHands.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.hands.tooltip"));
            dropdownTouchHands.AssignLocalizedEntries(
                new List<string>
                {
                    BasisSettingsDefaults.FingerTouchHands_Both,
                    BasisSettingsDefaults.FingerTouchHands_Left,
                    BasisSettingsDefaults.FingerTouchHands_Right,
                },
                new List<string>
                {
                    "settings.general.fingerTouch.hands.both",
                    "settings.general.fingerTouch.hands.left",
                    "settings.general.fingerTouch.hands.right",
                });
            dropdownTouchHands.AssignBinding(BasisSettingsDefaults.FingerTouchHands);
            tuning.Add(dropdownTouchHands.Descriptor);

            PanelDropdown dropdownTouchFinger = PanelDropdown.CreateNewEntry(group);
            dropdownTouchFinger.Descriptor.SetTitle(BasisLocalization.Get("settings.general.fingerTouch.finger"));
            dropdownTouchFinger.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.finger.tooltip"));
            dropdownTouchFinger.AssignLocalizedEntries(
                new List<string>
                {
                    BasisSettingsDefaults.FingerTouchFinger_Index,
                    BasisSettingsDefaults.FingerTouchFinger_Thumb,
                    BasisSettingsDefaults.FingerTouchFinger_Middle,
                    BasisSettingsDefaults.FingerTouchFinger_Ring,
                    BasisSettingsDefaults.FingerTouchFinger_Little,
                },
                new List<string>
                {
                    "settings.general.fingerTouch.finger.index",
                    "settings.general.fingerTouch.finger.thumb",
                    "settings.general.fingerTouch.finger.middle",
                    "settings.general.fingerTouch.finger.ring",
                    "settings.general.fingerTouch.finger.little",
                });
            dropdownTouchFinger.AssignBinding(BasisSettingsDefaults.FingerTouchFinger);
            tuning.Add(dropdownTouchFinger.Descriptor);

            PanelToggle toggleFingerTouchHaptics = PanelToggle.CreateNewEntry(group);
            toggleFingerTouchHaptics.AssignBinding(BasisSettingsDefaults.FingerTouchHaptics);
            toggleFingerTouchHaptics.Descriptor.SetTitle(BasisLocalization.Get("settings.general.fingerTouch.haptics"));
            toggleFingerTouchHaptics.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.haptics.tooltip"));
            tuning.Add(toggleFingerTouchHaptics.Descriptor);

            tuning.Add(AddMetersSlider(group, "settings.general.fingerTouch.hoverDistance", 0.01f, 0.15f, BasisSettingsDefaults.FingerTouchHoverDistance));
            tuning.Add(AddMetersSlider(group, "settings.general.fingerTouch.pressDepth", 0.002f, 0.05f, BasisSettingsDefaults.FingerTouchPressDepth));
            tuning.Add(AddMetersSlider(group, "settings.general.fingerTouch.releaseDistance", 0.005f, 0.08f, BasisSettingsDefaults.FingerTouchReleaseDistance));
            tuning.Add(AddMetersSlider(group, "settings.general.fingerTouch.radius", 0.001f, 0.05f, BasisSettingsDefaults.FingerTouchRadius));
            tuning.Add(AddMetersSlider(group, "settings.general.fingerTouch.tipOffset", 0f, 0.05f, BasisSettingsDefaults.FingerTouchTipOffset));
            tuning.Add(AddMetersSlider(group, "settings.general.fingerTouch.fingerLength", 0.02f, 0.3f, BasisSettingsDefaults.FingerTouchFingerLength));

            PanelSlider sliderScrollSensitivity = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.general.fingerTouch.scrollSensitivity"), 100f, 2000f, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.FingerTouchScrollSensitivity);
            sliderScrollSensitivity.Descriptor.SetTooltip(BasisLocalization.Get("settings.general.fingerTouch.scrollSensitivity.tooltip"));
            tuning.Add(sliderScrollSensitivity.Descriptor);

            PanelToggle toggleFingerTouchGizmo = SettingsProvider.AddDeveloperGizmoToggle(group, "settings.developer.fingerTouch", BasisSettingsDefaults.GizmoFingerTouch);
            if (toggleFingerTouchGizmo != null)
            {
                tuning.Add(toggleFingerTouchGizmo.Descriptor);
            }

            void ApplyTuningVisibility()
            {
                bool visible = !BasisSettingsDefaults.DisableVRFingerTouch.RawValue;
                for (int i = 0; i < tuning.Count; i++)
                {
                    tuning[i].SetActive(visible);
                }
            }
            ApplyTuningVisibility();
            toggleDisableVRFingerTouch.OnValueChanged += _ =>
            {
                ApplyTuningVisibility();
                group.ForceRebuild();
            };
        });
    }
    /// <summary>
    /// Every trigger threshold in one place: menu clicking first, then the in-world interactions
    /// ordered from the most to the least used.
    /// </summary>
    private static void BuildTriggerSection(RectTransform container)
    {
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(container, BasisLocalization.Get("settings.controls.triggerPressure.title"), BasisLocalization.Get("settings.controls.triggerPressure.description"), group =>
        {
            PanelElementDescriptor uiClick = SettingsProviderKeyboardBindings.CreateSubGroup(group,
                BasisLocalization.Get("settings.controls.uiClick.title"), BasisLocalization.Get("settings.controls.uiClick.description"));

            PanelSlider clickPressSlider = PanelSlider.CreateEntryAndBind(
                uiClick,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.uiClickPress"), 0.05f, 1f, false, 2, ValueDisplayMode.percentageFromZero),
                BasisSettingsDefaults.UIClickPressThreshold);
            clickPressSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.uiClickPress.tooltip"));

            PanelSlider clickReleaseSlider = PanelSlider.CreateEntryAndBind(
                uiClick,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.uiClickRelease"), 0.05f, 1f, false, 2, ValueDisplayMode.percentageFromZero),
                BasisSettingsDefaults.UIClickReleaseThreshold);
            clickReleaseSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.uiClickRelease.tooltip"));

            void ClampReleaseToPress()
            {
                float press = BasisSettingsDefaults.UIClickPressThreshold.RawValue;
                if (BasisSettingsDefaults.UIClickReleaseThreshold.RawValue <= press)
                {
                    return;
                }
                BasisSettingsDefaults.UIClickReleaseThreshold.SetValue(press);
                clickReleaseSlider.SetValueWithoutNotify(press);
            }

            clickPressSlider.OnValueChanged += _ => ClampReleaseToPress();
            clickReleaseSlider.OnValueChanged += _ => ClampReleaseToPress();

            AddTriggerPressureSlider(group, "grab", BasisSettingsDefaults.TriggerDownThreshold);
            AddTriggerPressureSlider(group, "release", BasisSettingsDefaults.triggerUpThreshold);
            AddTriggerPressureSlider(group, "primaryActivation", BasisSettingsDefaults.InteractableObjectIsTriggered);
            AddTriggerPressureSlider(group, "secondaryActivation", BasisSettingsDefaults.InteractableObjectIsSecondaryTriggered);
            AddTriggerPressureSlider(group, "heldInteraction", BasisSettingsDefaults.InteractableObject);
            AddTriggerPressureSlider(group, "autoHoldDrop", BasisSettingsDefaults.IsHoldDropTriggeredThreshold);
            AddTriggerPressureSlider(group, "jiggleGrab", BasisSettingsDefaults.TriggerDownJiggleThreshold);
            AddTriggerPressureSlider(group, "snapInteractable", BasisSettingsDefaults.SnapInteractableThreshold);
            AddTriggerPressureSlider(group, "playspaceMover", BasisSettingsDefaults.PlaySpaceTriggerThreshold);
            AddTriggerPressureSlider(group, "flyPitch", BasisSettingsDefaults.FlyPitchTriggerThreshold);
            AddTriggerPressureSlider(group, "lookAtPointer", BasisSettingsDefaults.LookAtPointerTriggerThreshold);
            AddTriggerPressureSlider(group, "depthOfField", BasisSettingsDefaults.DepthOfFieldThreshold);
            AddTriggerPressureSlider(group, "calibration", BasisSettingsDefaults.Calibrationtrigger);
        });
    }
    /// <summary>
    /// Menu scroll speed and every stick deadzone, grouped under their own headings instead of
    /// three one-slider sections.
    /// </summary>
    private static void BuildThumbstickSection(RectTransform container)
    {
        SettingsProviderKeyboardBindings.CreateCollapsibleSection(
            container, BasisLocalization.Get("settings.controls.thumbsticks.title"), BasisLocalization.Get("settings.controls.thumbsticks.description"), group =>
        {
            PanelSlider scrollSpeedSlider = PanelSlider.CreateEntryAndBind(
                group,
                PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.scrollSpeed"), 20, 300, true, 0, ValueDisplayMode.Raw),
                BasisSettingsDefaults.ScrollSpeed);
            scrollSpeedSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.scrollSpeed.tooltip"));

            PanelElementDescriptor general = SettingsProviderKeyboardBindings.CreateSubGroup(group,
                BasisLocalization.Get("settings.controls.generalDeadzone.title"), BasisLocalization.Get("settings.controls.generalDeadzone.description"));
            AddPercentSlider(general, "settings.controls.radialDeadZone", 0f, 1f, BasisSettingsDefaults.ControllerDeadZone);

            PanelElementDescriptor yaw = SettingsProviderKeyboardBindings.CreateSubGroup(group,
                BasisLocalization.Get("settings.controls.yawComfort.title"), BasisLocalization.Get("settings.controls.yawComfort.description"));
            AddPercentSlider(yaw, "settings.controls.xDeadZoneMin", 0f, 1f, BasisSettingsDefaults.Basexdeadzone);
            AddPercentSlider(yaw, "settings.controls.xGateFullY", 0f, 1f, BasisSettingsDefaults.Extraxdeadzoneatfully);
            AddPercentSlider(yaw, "settings.controls.gateCurve", 0f, 3f, BasisSettingsDefaults.Wingexponent);

            PanelElementDescriptor pitch = SettingsProviderKeyboardBindings.CreateSubGroup(group,
                BasisLocalization.Get("settings.controls.pitchComfort.title"), BasisLocalization.Get("settings.controls.pitchComfort.description"));
            AddPercentSlider(pitch, "settings.controls.lookYDeadZone", 0f, 1f, BasisSettingsDefaults.Ydeadzone);
        });
    }
    private static PanelElementDescriptor AddMetersSlider(PanelElementDescriptor group, string key, float min, float max, BasisSettingsBinding<float> binding)
    {
        PanelSlider slider = PanelSlider.CreateEntryAndBind(
            group,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get(key), min, max, false, 3, ValueDisplayMode.Meters),
            binding);
        slider.Descriptor.SetTooltip(BasisLocalization.Get(key + ".tooltip"));
        return slider.Descriptor;
    }
    private static void AddPercentSlider(PanelElementDescriptor group, string key, float min, float max, BasisSettingsBinding<float> binding)
    {
        PanelSlider slider = PanelSlider.CreateEntryAndBind(
            group,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get(key), min, max, false, 3, ValueDisplayMode.Percentage),
            binding);
        slider.Descriptor.SetTooltip(BasisLocalization.Get(key + ".tooltip"));
    }
    /// <summary>
    /// Shows the joystick slider bind (<see cref="BasisPanelJoystickBind"/>). Taking the bind
    /// happens out on the pages themselves — a slider's own options window offers it — so all this
    /// section holds is what is bound right now, a way to give it back, and how fast the stick
    /// moves it.
    /// </summary>
    private static void BuildJoystickBindSection(PanelElementDescriptor group)
    {
        RectTransform parent = group.ContentParent;

        PanelElementDescriptor status = PanelElementDescriptor.CreateNew(
            PanelElementDescriptor.ElementStyles.Group, parent);
        status.SetTitle(BasisLocalization.Get("settings.controls.joystickBind.status"));

        PanelButton unbindButton = PanelButton.CreateNew(parent);
        unbindButton.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.joystickBind.unbind"));
        unbindButton.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.joystickBind.unbind.tooltip"));
        unbindButton.OnClicked += BasisPanelJoystickBind.Clear;

        PanelSlider sweepSlider = PanelSlider.CreateEntryAndBind(
            parent,
            PanelSlider.SliderSettings.Advanced(BasisLocalization.Get("settings.controls.joystickBind.sweep"), 0.5f, 20f, false, 1, ValueDisplayMode.Raw),
            BasisSettingsDefaults.JoystickBindSweepSeconds);
        sweepSlider.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.joystickBind.sweep.tooltip"));

        void Refresh()
        {
            // The page is rebuilt each time the tab opens; a build that has since been torn down
            // drops itself here rather than needing a teardown hook of its own.
            if (unbindButton == null)
            {
                BasisPanelJoystickBind.StateChanged -= Refresh;
                return;
            }

            status.SetDescription(BasisPanelJoystickBind.StatusText);

            // Greyed rather than hidden: expanding the section re-activates everything under it,
            // which would put a hidden row back whether or not anything is bound.
            bool bound = BasisPanelJoystickBind.HasBinding;
            unbindButton.SetInteractable(bound, bound ? null : BasisLocalization.Get("settings.controls.joystickBind.status.none"));
            group.ForceRebuild();
        }

        BasisPanelJoystickBind.StateChanged += Refresh;
        Refresh();
    }
    private static void ResetControlsDefaults()
    {
        BasisSettingsDefaults.ForceGridSnap.ResetToDefault();
        BasisSettingsDefaults.GridSnapSize.ResetToDefault();
        BasisSettingsDefaults.ForceRotationSnap.ResetToDefault();
        BasisSettingsDefaults.RotationSnapDegrees.ResetToDefault();
        BasisSettingsDefaults.DominantHand.ResetToDefault();
        BasisSettingsDefaults.DesktopInputInVR.ResetToDefault();
        BasisSettingsDefaults.QuestControllerFix.ResetToDefault();
        BasisSettingsDefaults.InvertMouse.ResetToDefault();
        BasisSettingsDefaults.mousesensitivty.ResetToDefault();
        BasisSettingsDefaults.usesnapturn.ResetToDefault();
        BasisSettingsDefaults.SnapTurnAngle.ResetToDefault();
        BasisSettingsDefaults.SmoothTurnSpeed.ResetToDefault();
        BasisSettingsDefaults.ScrollSpeed.ResetToDefault();
        BasisSettingsDefaults.ControllerDeadZone.ResetToDefault();
        BasisSettingsDefaults.Basexdeadzone.ResetToDefault();
        BasisSettingsDefaults.Extraxdeadzoneatfully.ResetToDefault();
        BasisSettingsDefaults.Wingexponent.ResetToDefault();
        BasisSettingsDefaults.Ydeadzone.ResetToDefault();
        BasisSettingsDefaults.DisableVRFingerTouch.ResetToDefault();
        BasisSettingsDefaults.FingerTouchFinger.ResetToDefault();
        BasisSettingsDefaults.FingerTouchHands.ResetToDefault();
        BasisSettingsDefaults.FingerTouchTipOffset.ResetToDefault();
        BasisSettingsDefaults.FingerTouchFingerLength.ResetToDefault();
        BasisSettingsDefaults.FingerTouchRadius.ResetToDefault();
        BasisSettingsDefaults.FingerTouchHoverDistance.ResetToDefault();
        BasisSettingsDefaults.FingerTouchPressDepth.ResetToDefault();
        BasisSettingsDefaults.FingerTouchReleaseDistance.ResetToDefault();
        BasisSettingsDefaults.FingerTouchScrollSensitivity.ResetToDefault();
        BasisSettingsDefaults.FingerTouchHaptics.ResetToDefault();
        BasisSettingsDefaults.GizmoFingerTouch.ResetToDefault();
        BasisSettingsDefaults.SpawnAnchorHandles.ResetToDefault();
        BasisSettingsDefaults.SpawnAnchorSeatOnSurface.ResetToDefault();
        BasisSettingsDefaults.SpawnAnchorPositionSnap.ResetToDefault();
        BasisSettingsDefaults.SpawnAnchorPositionSnapSize.ResetToDefault();
        BasisSettingsDefaults.SpawnAnchorRotationSnap.ResetToDefault();
        BasisSettingsDefaults.SpawnAnchorRotationSnapDegrees.ResetToDefault();
        BasisSettingsDefaults.UIClickPressThreshold.ResetToDefault();
        BasisSettingsDefaults.UIClickReleaseThreshold.ResetToDefault();
        BasisSettingsDefaults.DepthOfFieldThreshold.ResetToDefault();
        BasisSettingsDefaults.LookAtPointerTriggerThreshold.ResetToDefault();
        BasisSettingsDefaults.FlyPitchTriggerThreshold.ResetToDefault();
        BasisSettingsDefaults.SnapInteractableThreshold.ResetToDefault();
        BasisSettingsDefaults.TriggerDownThreshold.ResetToDefault();
        BasisSettingsDefaults.TriggerDownJiggleThreshold.ResetToDefault();
        BasisSettingsDefaults.InteractableObject.ResetToDefault();
        BasisSettingsDefaults.InteractableObjectIsTriggered.ResetToDefault();
        BasisSettingsDefaults.InteractableObjectIsSecondaryTriggered.ResetToDefault();
        BasisSettingsDefaults.IsHoldDropTriggeredThreshold.ResetToDefault();
        BasisSettingsDefaults.PlaySpaceTriggerThreshold.ResetToDefault();
        BasisSettingsDefaults.Calibrationtrigger.ResetToDefault();
        BasisSettingsDefaults.triggerUpThreshold.ResetToDefault();
        BasisSettingsDefaults.JoystickBindSweepSeconds.ResetToDefault();
    }
    private static void AddTriggerPressureSlider(PanelElementDescriptor group,string key, BasisSettingsBinding<float> binding)
    {
        string localizationKey = "settings.controls.triggerPressure." + key;
        PanelSlider slider = PanelSlider.CreateEntryAndBind(
            group,
            PanelSlider.SliderSettings.Advanced(
                BasisLocalization.Get(localizationKey),
                0.05f,
                1f,
                false,
                2,
                ValueDisplayMode.percentageFromZero),
            binding);
        slider.Descriptor.SetTooltip(BasisLocalization.Get(localizationKey + ".tooltip"));
    }
    private static void BuildBindingsUI(RectTransform container)
    {
        var roles = (BasisBoneTrackedRole[])Enum.GetValues(typeof(BasisBoneTrackedRole));
        var roleNames = roles.Select(BodyRoleLabel).ToArray();

        var actions = ((ActionId[])Enum.GetValues(typeof(ActionId)))
            .Where(a => a != ActionId.Count)
            .ToArray();

        var actionNames = actions.Select(a => PrettyEnumName(a.ToString())).ToList();
        var actionKeys = actions.Select(a => "settings.controls.actionId." + LowerFirst(a.ToString())).ToList();

        // Bindings are saved per device mode. The current mode is edited live; any other mode is
        // read from and written to its own file, and takes effect the next time that mode starts.
        var modes = new List<string> { BasisConstants.Desktop, BasisConstants.OpenVRLoader, BasisConstants.OpenXRLoader };
        var modeLabels = new List<string>
        {
            BasisLocalization.Get("settings.platform.mode.desktop"),
            BasisLocalization.Get("settings.platform.mode.openvr"),
            BasisLocalization.Get("settings.platform.mode.openxr"),
        };
        string currentMode = BasisDeviceManagement.StaticCurrentMode;
        if (!modes.Contains(currentMode))
        {
            modes.Insert(0, currentMode);
            modeLabels.Insert(0, currentMode);
        }

        var selectorGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        selectorGroup.SetDescription(BasisLocalization.Get("settings.controls.actionBindings.description"));

        PanelDropdown modeDropdown = PanelDropdown.CreateNewEntry(selectorGroup.ContentParent);
        modeDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.platform.deviceMode"));
        modeDropdown.AssignEntries(modes, modeLabels);
        modeDropdown.SetValueWithoutNotify(currentMode);

        PanelDropdown actionDropdown = PanelDropdown.CreateNewEntry(selectorGroup.ContentParent);
        actionDropdown.Descriptor.SetTitle(BasisLocalization.Get("settings.controls.action"));
        actionDropdown.Descriptor.SetTooltip(BasisLocalization.Get("settings.controls.action.tooltip"));
        actionDropdown.AssignLocalizedEntries(actionNames, actionKeys);
        if (actionNames.Count > 0)
        {
            actionDropdown.SetValueWithoutNotify(actionNames[0]);
        }

        var rolesGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        rolesGroup.SetTitle(BasisLocalization.Get("settings.controls.roles"));

        var roleToggles = new PanelToggle[roles.Length];

        bool updatingUI = false;
        ActionId currentAction = actions.Length > 0 ? actions[0] : ActionId.Count;
        string editMode = null;
        string wantedMode = currentMode;
        int modeReads = 0;
        // Bindings of the selected mode while it is not the live one; null means edit the driver.
        Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> detached = null;

        bool IsLive(string mode) => mode != null && string.Equals(mode, BasisActionDriver.BindingsMode, StringComparison.Ordinal);

        bool IsGone() => rolesGroup == null || rolesGroup.IsReleased;

        async void ShowMode(string mode)
        {
            int read = ++modeReads;
            wantedMode = mode;
            Dictionary<ActionId, HashSet<BasisBoneTrackedRole>> loaded = null;
            if (!IsLive(mode))
            {
                loaded = await BasisActionDriver.ReadBindingsForMode(mode);
                if (read != modeReads || IsGone())
                {
                    return;
                }
                if (IsLive(mode))
                {
                    loaded = null;
                }
            }
            editMode = mode;
            detached = loaded;
            RefreshRoleToggles();
        }

        void RefreshRoleToggles()
        {
            updatingUI = true;
            for (int i = 0; i < roles.Length; i++)
            {
                bool bound = detached != null
                    ? detached.TryGetValue(currentAction, out var set) && set.Contains(roles[i])
                    : BasisActionDriver.GetBindings(currentAction).Contains(roles[i]);
                roleToggles[i].SetValueWithoutNotify(bound);
            }
            updatingUI = false;
        }

        for (int i = 0; i < roles.Length; i++)
        {
            var role = roles[i];

            PanelToggle t = PanelToggle.CreateNewEntry(rolesGroup.ContentParent);
            t.Descriptor.SetTitle(roleNames[i]);
            t.ExcludeFromShare = true;

            t.OnValueChanged += async isOn =>
            {
                if (updatingUI)
                {
                    return;
                }

                string mode = editMode;
                var map = detached;
                if (map == null)
                {
                    if (!IsLive(mode))
                    {
                        ShowMode(wantedMode);
                        return;
                    }

                    if (isOn)
                    {
                        BasisActionDriver.Bind(currentAction, role);
                    }
                    else
                    {
                        BasisActionDriver.Unbind(currentAction, role);
                    }

                    await BasisActionDriver.SaveFromDriver();
                    return;
                }

                if (!map.TryGetValue(currentAction, out var set))
                {
                    set = new HashSet<BasisBoneTrackedRole>();
                    map[currentAction] = set;
                }
                if (isOn)
                {
                    set.Add(role);
                }
                else
                {
                    set.Remove(role);
                }

                await BasisActionDriver.WriteBindingsForMode(mode, map);
            };

            roleToggles[i] = t;
        }

        actionDropdown.OnValueChanged += value =>
        {
            int index = actionNames.IndexOf(value);
            if (index < 0)
            {
                return;
            }
            currentAction = actions[index];
            RefreshRoleToggles();
        };

        modeDropdown.OnValueChanged += ShowMode;

        Action<string> rederive = null;
        void Unsubscribe()
        {
            BasisDeviceManagement.OnBootModeChanged -= rederive;
            BasisActionDriver.OnBindingsLoaded -= rederive;
        }
        rederive = _ =>
        {
            if (IsGone())
            {
                Unsubscribe();
                return;
            }
            ShowMode(wantedMode);
        };
        BasisDeviceManagement.OnBootModeChanged += rederive;
        BasisActionDriver.OnBindingsLoaded += rederive;
        rolesGroup.OnInstanceReleased += Unsubscribe;

        ShowMode(currentMode);
    }
    public static string BodyRoleLabel(BasisBoneTrackedRole role) => BasisLocalization.Get("ui.bodyRole." + LowerFirst(role.ToString()));
    private static string LowerFirst(string value) => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);
    private static string PrettyEnumName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) { return raw; }
        var chars = new List<char>(raw.Length + 8);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(raw[i - 1]) || (i + 1 < raw.Length && char.IsLower(raw[i + 1]))))
            {
                chars.Add(' ');
            }
            chars.Add(c);
        }
        return new string(chars.ToArray());
    }
}
