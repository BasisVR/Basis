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
            refreshButton.Descriptor.SetTitle("Refresh");
            refreshButton.Descriptor.SetDescription("Poll the current face tracking state from all components.");

            PanelElementDescriptor fieldFTActive = CreateInfoField(parent, "Face Tracking Active", "...");
            PanelElementDescriptor fieldOSC = CreateInfoField(parent, "OSC Acquisition", "...");
            PanelElementDescriptor fieldBlendshapeActive = CreateInfoField(parent, "Blendshape Tracking", "...");
            PanelElementDescriptor fieldActuatedAddresses = CreateInfoField(parent, "Actuated Addresses", "...");

            void Refresh() => RefreshFaceState(fieldFTActive, fieldOSC, fieldBlendshapeActive, fieldActuatedAddresses);
            refreshButton.OnClicked += Refresh;
            Refresh();
        }

        static void BuildEyeTrackingSection(RectTransform parent)
        {
            PanelButton refreshButton = PanelButton.CreateNew(parent);
            refreshButton.Descriptor.SetTitle("Refresh");
            refreshButton.Descriptor.SetDescription("Poll the current eye tracking state from all components.");

            PanelElementDescriptor fieldEyeOverride = CreateInfoField(parent, "Eye Override", "...");
            PanelElementDescriptor fieldEyeDriverEnabled = CreateInfoField(parent, "Eye Driver Enabled", "...");
            PanelElementDescriptor fieldEyeParamsActive = CreateInfoField(parent, "Eye Params Active", "...");
            PanelElementDescriptor fieldEyeLeftX = CreateInfoField(parent, "Eye Left X", "...");
            PanelElementDescriptor fieldEyeRightX = CreateInfoField(parent, "Eye Right X", "...");
            PanelElementDescriptor fieldEyeY = CreateInfoField(parent, "Eye Y", "...");

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
            ftActive.SetDescription(relay != null ? relay.IsTrackingActive.ToString() : "No relay found");

            OSCAcquisition oscAcq = avatar != null
                ? avatar.GetComponentInChildren<OSCAcquisition>(true)
                : null;
            if (oscAcq != null)
                oscAcquisition.SetDescription(oscAcq.isActiveAndEnabled ? "Active" : "DISABLED");
            else
                oscAcquisition.SetDescription("No component");

            BlendshapeActuation blendshape = avatar != null
                ? avatar.GetComponentInChildren<BlendshapeActuation>(true)
                : null;
            if (blendshape != null)
            {
                blendshapeActive.SetDescription(blendshape.IsTrackingActive.ToString());
                int addressCount = blendshape.debugAddresses != null ? blendshape.debugAddresses.Length : 0;
                actuatedAddresses.SetDescription(addressCount.ToString());
            }
            else
            {
                blendshapeActive.SetDescription("No component");
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

            eyeOverride.SetDescription(BasisLocalEyeDriver.Override.ToString());
            eyeDriverEnabled.SetDescription(BasisLocalEyeDriver.IsEnabled.ToString());

            EyeTrackingBoneActuation eyeActuation = avatar != null
                ? avatar.GetComponentInChildren<EyeTrackingBoneActuation>(true)
                : null;
            if (eyeActuation != null)
            {
                eyeParamsActive.SetDescription(eyeActuation.IsEyeTrackingParametersActive.ToString());
                eyeLeftX.SetDescription(eyeActuation._fEyeLeftX.ToString("F3"));
                eyeRightX.SetDescription(eyeActuation._fEyeRightX.ToString("F3"));
                eyeY.SetDescription(eyeActuation._fEyeY.ToString("F3"));
            }
            else
            {
                eyeParamsActive.SetDescription("No component");
                eyeLeftX.SetDescription("--");
                eyeRightX.SetDescription("--");
                eyeY.SetDescription("--");
            }
        }

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
            menuGroup.SetDescription("Trigger effects on this avatar.");

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
            resetButton.Descriptor.SetTitle("Reset to Default");
            resetButton.Descriptor.SetDescription("Restore all customization on this avatar to its defaults.");
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
                sectionToggle, container, "Eye Tracking");
            eyeGroup.SetDescription("Limit and scale eye tracking rotation on this avatar.");

            var overrideToggle = PanelToggle.CreateNewEntry(eyeGroup.ContentParent);
            overrideToggle.Descriptor.SetTitle("Override Rotation Limits");
            overrideToggle.Descriptor.SetDescription("When off, the values baked into the avatar are used.");
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
                BuildOverrideSlider(eyeGroup, sliders, "Horizontal Gain", "Multiplier applied to horizontal eye tracking angles.",
                    0f, Mathf.Max(2f, eyeActuation.DefaultMultiplyX), 2, ValueDisplayMode.Percentage,
                    () => eyeActuation.RuntimeMultiplyX, value => eyeActuation.RuntimeMultiplyX = value,
                    eyeActuation.DefaultMultiplyX),
                BuildOverrideSlider(eyeGroup, sliders, "Vertical Gain", "Multiplier applied to vertical eye tracking angles.",
                    0f, Mathf.Max(2f, eyeActuation.DefaultMultiplyY), 2, ValueDisplayMode.Percentage,
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
            resetButton.Descriptor.SetTitle("Reset Eye Tracking");
            resetButton.Descriptor.SetDescription("Turn off the override and restore the values baked into this avatar.");
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
