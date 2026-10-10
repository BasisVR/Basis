using System;
using System.Collections.Generic;
using System.Linq;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using HVR.Vixxy;
using UnityEngine;

namespace HVR.Basis.Comms
{
    /// <summary>
    /// Registers the face- and eye-tracking diagnostic builders into the
    /// framework's Developer tab. The framework owns the collapsible sections
    /// and calls these builders each time one is expanded; this package fills
    /// them in with the live state of the HVR pipeline components.
    /// </summary>
    public static class SettingsProviderFaceTracking
    {
        [RuntimeInitializeOnLoadMethod]
        static void Register()
        {
            SettingsProvider.FaceTrackingDebugBuilder = BuildFaceTrackingSection;
            SettingsProvider.EyeTrackingDebugBuilder = BuildEyeTrackingSection;
            SettingsProvider.AvatarCustomizationBuilder = BuildAvatarCustomizationSection;
        }

        static void BuildFaceTrackingSection(RectTransform parent)
        {
            PanelButton refreshButton = PanelButton.CreateNew(parent);
            refreshButton.Descriptor.SetTitle(BasisLocalization.Get("settings.faceTracking.refresh"));
            refreshButton.Descriptor.SetDescription(BasisLocalization.Get("settings.faceTracking.refresh.description"));

            PanelElementDescriptor fieldFTActive = CreateInfoField(parent, BasisLocalization.Get("settings.faceTracking.faceTrackingActive"), "...");
            PanelElementDescriptor fieldOSC = CreateInfoField(parent, BasisLocalization.Get("settings.faceTracking.oscAcquisition"), "...");
            PanelElementDescriptor fieldBlendshapeActive = CreateInfoField(parent, BasisLocalization.Get("settings.faceTracking.blendshapeTracking"), "...");
            PanelElementDescriptor fieldActuatedAddresses = CreateInfoField(parent, BasisLocalization.Get("settings.faceTracking.actuatedAddresses"), "...");

            void Refresh() => RefreshFaceState(fieldFTActive, fieldOSC, fieldBlendshapeActive, fieldActuatedAddresses);
            refreshButton.OnClicked += Refresh;
            Refresh();
        }

        static void BuildEyeTrackingSection(RectTransform parent)
        {
            PanelButton refreshButton = PanelButton.CreateNew(parent);
            refreshButton.Descriptor.SetTitle(BasisLocalization.Get("settings.eyeTracking.refresh"));
            refreshButton.Descriptor.SetDescription(BasisLocalization.Get("settings.eyeTracking.refresh.description"));

            PanelElementDescriptor fieldEyeOverride = CreateInfoField(parent, BasisLocalization.Get("settings.faceTracking.eyeOverride"), "...");
            PanelElementDescriptor fieldEyeDriverEnabled = CreateInfoField(parent, BasisLocalization.Get("settings.faceTracking.eyeDriverEnabled"), "...");
            PanelElementDescriptor fieldEyeParamsActive = CreateInfoField(parent, BasisLocalization.Get("settings.faceTracking.eyeParamsActive"), "...");
            PanelElementDescriptor fieldEyeLeftX = CreateInfoField(parent, BasisLocalization.Get("settings.faceTracking.eyeLeftX"), "...");
            PanelElementDescriptor fieldEyeRightX = CreateInfoField(parent, BasisLocalization.Get("settings.faceTracking.eyeRightX"), "...");
            PanelElementDescriptor fieldEyeY = CreateInfoField(parent, BasisLocalization.Get("settings.faceTracking.eyeY"), "...");

            void Refresh() => RefreshEyeState(
                fieldEyeOverride, fieldEyeDriverEnabled, fieldEyeParamsActive,
                fieldEyeLeftX, fieldEyeRightX, fieldEyeY);
            refreshButton.OnClicked += Refresh;
            Refresh();
        }

        static void BuildAvatarCustomizationSection(RectTransform parent)
        {
            InitializeVixxyPanel(parent);
            InitializeEyeTrackingPanel(parent);
        }

        static void RefreshFaceState(
            PanelElementDescriptor ftActive,
            PanelElementDescriptor oscAcquisition,
            PanelElementDescriptor blendshapeActive,
            PanelElementDescriptor actuatedAddresses)
        {
            BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
            var avatar = localPlayer != null ? localPlayer.BasisAvatar : null;

            FaceTrackingActivityRelay relay = avatar != null
                ? avatar.GetComponentInChildren<FaceTrackingActivityRelay>(true)
                : null;
            ftActive.SetDescription(relay != null ? YesNo(relay.IsTrackingActive) : BasisLocalization.Get("settings.faceTracking.noRelay"));

            OSCAcquisition oscAcq = avatar != null
                ? avatar.GetComponentInChildren<OSCAcquisition>(true)
                : null;
            if (oscAcq != null)
                oscAcquisition.SetDescription(BasisLocalization.Get(oscAcq.isActiveAndEnabled ? "settings.faceTracking.oscActive" : "settings.faceTracking.oscDisabled"));
            else
                oscAcquisition.SetDescription(BasisLocalization.Get("settings.faceTracking.noComponent"));

            BlendshapeActuation blendshape = avatar != null
                ? avatar.GetComponentInChildren<BlendshapeActuation>(true)
                : null;
            if (blendshape != null)
            {
                blendshapeActive.SetDescription(YesNo(blendshape.IsTrackingActive));
                int addressCount = blendshape.debugAddresses != null ? blendshape.debugAddresses.Length : 0;
                actuatedAddresses.SetDescription(addressCount.ToString());
            }
            else
            {
                blendshapeActive.SetDescription(BasisLocalization.Get("settings.faceTracking.noComponent"));
                actuatedAddresses.SetDescription("--");
            }
        }

        static void RefreshEyeState(
            PanelElementDescriptor eyeOverride,
            PanelElementDescriptor eyeDriverEnabled,
            PanelElementDescriptor eyeParamsActive,
            PanelElementDescriptor eyeLeftX,
            PanelElementDescriptor eyeRightX,
            PanelElementDescriptor eyeY)
        {
            BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
            var avatar = localPlayer != null ? localPlayer.BasisAvatar : null;

            eyeOverride.SetDescription(YesNo(BasisLocalEyeDriver.Override));
            eyeDriverEnabled.SetDescription(YesNo(BasisLocalEyeDriver.IsEnabled));

            EyeTrackingBoneActuation eyeActuation = avatar != null
                ? avatar.GetComponentInChildren<EyeTrackingBoneActuation>(true)
                : null;
            if (eyeActuation != null)
            {
                eyeParamsActive.SetDescription(YesNo(eyeActuation.IsEyeTrackingParametersActive));
                eyeLeftX.SetDescription(eyeActuation._fEyeLeftX.ToString("F3"));
                eyeRightX.SetDescription(eyeActuation._fEyeRightX.ToString("F3"));
                eyeY.SetDescription(eyeActuation._fEyeY.ToString("F3"));
            }
            else
            {
                eyeParamsActive.SetDescription(BasisLocalization.Get("settings.faceTracking.noComponent"));
                eyeLeftX.SetDescription("--");
                eyeRightX.SetDescription("--");
                eyeY.SetDescription("--");
            }
        }

        static string YesNo(bool value) => BasisLocalization.Get(value ? "ui.yes" : "ui.no");

        static PanelElementDescriptor CreateInfoField(RectTransform parent, string title, string initialValue)
        {
            PanelElementDescriptor field = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            field.SetTitle(title);
            field.SetDescription(initialValue);
            return field;
        }

        private static void InitializeVixxyPanel(RectTransform container)
        {
            var localPlayer = BasisLocalPlayer.Instance;
            var avatar = localPlayer?.BasisAvatar;
            if (avatar == null) return;

            var menuItems = avatar.GetComponentsInChildren<HVRVixxyMenuItem>(true);
            if (menuItems.Length <= 0) return;

            var sectionToggle = PanelSectionToggle.CreateNewEntry(container);
            var menuGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                sectionToggle, container, "Vixxy");
            menuGroup.SetDescription(BasisLocalization.Get("settings.faceTracking.vixxy.description"));

            var resetters = new List<Action>();
            foreach (var menuItem in menuItems)
            {
                var control = menuItem.control;
                if (control == null) continue;

                if (menuItem.presentation == HVRVixxyControlPresentation.Slider)
                {
                    resetters.Add(BuildSlider(menuGroup, control, menuItem));
                }
                else
                {
                    if (!control.HasThreeOrMoreChoices)
                    {
                        resetters.Add(BuildToggle(menuGroup, menuItem, control));
                    }
                    else
                    {
                        resetters.Add(BuildDropdown(control, menuGroup, menuItem));
                    }
                }
            }

            var resetButton = PanelButton.CreateNew(menuGroup.ContentParent);
            resetButton.Descriptor.SetTitle(BasisLocalization.Get("settings.faceTracking.resetToDefault"));
            resetButton.Descriptor.SetDescription(BasisLocalization.Get("settings.faceTracking.resetToDefault.description"));
            resetButton.OnClicked += () =>
            {
                foreach (var reset in resetters) reset();
            };

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(sectionToggle, menuGroup, true);
        }

        private static void InitializeEyeTrackingPanel(RectTransform container)
        {
            var localPlayer = BasisLocalPlayer.Instance;
            var avatar = localPlayer?.BasisAvatar;
            if (avatar == null) return;

            var eyeActuation = avatar.GetComponentInChildren<EyeTrackingBoneActuation>(true);
            if (eyeActuation == null) return;

            var sectionToggle = PanelSectionToggle.CreateNewEntry(container);
            var eyeGroup = PanelSectionToggleHelpers.CreateCollapsibleContentGroup(
                sectionToggle, container, BasisLocalization.Get("settings.faceTracking.eyeTracking"));
            eyeGroup.SetDescription(BasisLocalization.Get("settings.faceTracking.eyeTracking.description"));

            var overrideToggle = PanelToggle.CreateNewEntry(eyeGroup.ContentParent);
            overrideToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.faceTracking.overrideRotationLimits"));
            overrideToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.faceTracking.overrideRotationLimits.description"));
            overrideToggle.SetValueWithoutNotify(eyeActuation.RuntimeOverrideEnabled);

            var sliders = new List<PanelSlider>();
            var resetters = new List<Action>
            {
                BuildOverrideSlider(eyeGroup, sliders, "Horizontal Limit", "Maximum eye rotation left or right of center.",
                    0f, EyeTrackingBoneActuation.MaxAngleLimitDeg, 0, ValueDisplayMode.Degrees,
                    () => eyeActuation.RuntimeMaxAngleXDeg, value => eyeActuation.RuntimeMaxAngleXDeg = value,
                    EyeTrackingBoneActuation.MaxAngleLimitDeg),
                BuildOverrideSlider(eyeGroup, sliders, "Vertical Limit", "Maximum eye rotation up or down from center.",
                    0f, EyeTrackingBoneActuation.MaxAngleLimitDeg, 0, ValueDisplayMode.Degrees,
                    () => eyeActuation.RuntimeMaxAngleYDeg, value => eyeActuation.RuntimeMaxAngleYDeg = value,
                    EyeTrackingBoneActuation.MaxAngleLimitDeg),
                BuildOverrideSlider(eyeGroup, sliders, "Horizontal Gain", "Multiplier applied to horizontal eye tracking angles. Replaces the avatar's authored multiplier.",
                    0f, Mathf.Max(2f, eyeActuation.DefaultMultiplyX), 2, ValueDisplayMode.Raw,
                    () => eyeActuation.RuntimeMultiplyX, value => eyeActuation.RuntimeMultiplyX = value,
                    eyeActuation.DefaultMultiplyX),
                BuildOverrideSlider(eyeGroup, sliders, "Vertical Gain", "Multiplier applied to vertical eye tracking angles. Replaces the avatar's authored multiplier.",
                    0f, Mathf.Max(2f, eyeActuation.DefaultMultiplyY), 2, ValueDisplayMode.Raw,
                    () => eyeActuation.RuntimeMultiplyY, value => eyeActuation.RuntimeMultiplyY = value,
                    eyeActuation.DefaultMultiplyY),
            };

            void ApplySliderVisibility(bool visible)
            {
                foreach (var slider in sliders) slider.gameObject.SetActive(visible);
                eyeGroup.ForceRebuild();
            }
            ApplySliderVisibility(eyeActuation.RuntimeOverrideEnabled);
            overrideToggle.OnValueChanged += value =>
            {
                eyeActuation.RuntimeOverrideEnabled = value;
                ApplySliderVisibility(value);
            };

            var resetButton = PanelButton.CreateNew(eyeGroup.ContentParent);
            resetButton.Descriptor.SetTitle(BasisLocalization.Get("settings.faceTracking.resetEyeTracking"));
            resetButton.Descriptor.SetDescription(BasisLocalization.Get("settings.faceTracking.resetEyeTracking.description"));
            resetButton.OnClicked += () =>
            {
                eyeActuation.RuntimeOverrideEnabled = false;
                overrideToggle.SetValueWithoutNotify(false);
                foreach (var reset in resetters) reset();
                ApplySliderVisibility(false);
            };

            PanelSectionToggleHelpers.FinalizeCollapsibleGroup(sectionToggle, eyeGroup, true,
                visible =>
                {
                    if (visible)
                    {
                        ApplySliderVisibility(eyeActuation.RuntimeOverrideEnabled);
                    }
                });
        }

        private static Action BuildOverrideSlider(PanelElementDescriptor group, List<PanelSlider> sliders, string title, string description,
            float min, float max, int decimalPlaces, ValueDisplayMode displayMode,
            Func<float> getValue, Action<float> setValue, float defaultValue)
        {
            var slider = PanelSlider.CreateNew(group.ContentParent);
            sliders.Add(slider);
            slider.SetSliderSettings(new PanelSlider.SliderSettings
            {
                SliderMin = min,
                SliderMax = max,
                DecimalPlaces = decimalPlaces,
                DisplayMode = displayMode,
            });
            slider.Descriptor.SetTitle(title);
            slider.Descriptor.SetDescription(description);
            void WhenValueChanged(float value) => setValue(value);
            slider.SliderComponent.onValueChanged.AddListener(WhenValueChanged);
            slider.OnValueChanged += WhenValueChanged;
            slider.SetValueWithoutNotify(getValue());

            return () =>
            {
                setValue(defaultValue);
                slider.SetValueWithoutNotify(defaultValue);
            };
        }

        private static Action BuildToggle(PanelElementDescriptor menuGroup, HVRVixxyMenuItem menuItem, HVRVixxyControl control)
        {
            var toggle = PanelToggle.CreateNewEntry(menuGroup.ContentParent);
            toggle.Descriptor.SetTitle(menuItem.ResolveTitle());
            toggle.Descriptor.SetDescription(menuItem.ResolveDescription());
            toggle.OnValueChanged += value =>
            {
                menuItem.ApplyValue(value ? control.Max() : control.Min());
                toggle.Descriptor.SetTitle(menuItem.ResolveTitle());
                toggle.Descriptor.SetDescription(menuItem.ResolveDescription());
            };
            toggle.SetValueWithoutNotify(!Mathf.Approximately(menuItem.GetValue(), control.Min()));

            return () =>
            {
                menuItem.ApplyValue(control.defaultValue);
                toggle.SetValueWithoutNotify(!Mathf.Approximately(control.defaultValue, control.Min()));
                toggle.Descriptor.SetTitle(menuItem.ResolveTitle());
                toggle.Descriptor.SetDescription(menuItem.ResolveDescription());
            };
        }

        private static Action BuildSlider(PanelElementDescriptor menuGroup, HVRVixxyControl control, HVRVixxyMenuItem menuItem)
        {
            var slider = PanelSlider.CreateNew(menuGroup.ContentParent);
            slider.SetSliderSettings(new PanelSlider.SliderSettings
            {
                SliderMin = control.Min(),
                SliderMax = control.Max(),
                DecimalPlaces = 2,
                DisplayMode = ValueDisplayMode.Percentage,
            });
            slider.Descriptor.SetTitle(menuItem.ResolveTitle());
            slider.Descriptor.SetDescription(menuItem.ResolveDescription());
            void WhenValueChanged(float value)
            {
                menuItem.ApplyValue(value);
                slider.Descriptor.SetTitle(menuItem.ResolveTitle());
                slider.Descriptor.SetDescription(menuItem.ResolveDescription());
            }
            slider.SliderComponent.onValueChanged.AddListener(WhenValueChanged);
            slider.OnValueChanged += WhenValueChanged;
            slider.SetValueWithoutNotify(menuItem.GetValue());

            return () =>
            {
                menuItem.ApplyValue(control.defaultValue);
                slider.SetValueWithoutNotify(control.defaultValue);
                slider.Descriptor.SetTitle(menuItem.ResolveTitle());
                slider.Descriptor.SetDescription(menuItem.ResolveDescription());
            };
        }

        private static Action BuildDropdown(HVRVixxyControl control, PanelElementDescriptor menuGroup, HVRVixxyMenuItem menuItem)
        {
            var choiceStrings = control.choices.Select((choice, i) =>
            {
                if (string.IsNullOrWhiteSpace(choice.title)) return $"Option #{(i + 1)}";
                return $"{choice.title} (#{i + 1})";
            }).ToList();

            var dropdown = PanelDropdown.CreateNewEntry(menuGroup.ContentParent);
            dropdown.Descriptor.SetTitle(menuItem.ResolveTitle());
            dropdown.AssignEntries(choiceStrings);
            dropdown.OnValueChanged += choice =>
            {
                var valueForThatChoice = control.choices[choiceStrings.IndexOf(choice)].value;
                menuItem.ApplyValue(valueForThatChoice);
                dropdown.Descriptor.SetTitle(menuItem.ResolveTitle());
            };

            void SelectByValue(float value)
            {
                var asIndex = (int)value;
                var matchingChoice = control.choices.FirstOrDefault(choice => Mathf.Approximately(choice.value, asIndex));
                var choiceIndex = matchingChoice != null ? control.choices.ToList().IndexOf(matchingChoice) : -1;
                if (choiceIndex >= 0 && choiceIndex < choiceStrings.Count)
                {
                    dropdown.SetValueWithoutNotify(choiceStrings[choiceIndex]);
                }
            }
            SelectByValue(menuItem.GetValue());

            return () =>
            {
                menuItem.ApplyValue(control.defaultValue);
                SelectByValue(control.defaultValue);
                dropdown.Descriptor.SetTitle(menuItem.ResolveTitle());
            };
        }
    }
}
