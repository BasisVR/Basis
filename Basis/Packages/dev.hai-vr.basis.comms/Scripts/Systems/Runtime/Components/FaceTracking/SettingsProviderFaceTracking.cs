using System.Linq;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using HVR.Vixxy;
using UnityEngine;

namespace HVR.Basis.Comms
{
    /// <summary>
    /// Registers the face- and eye-tracking diagnostic builders into the
    /// framework's Developer tab. The framework owns the toggles
    /// (DevDebugFaceTracking / DevDebugEyeTracking) and the collapsible group
    /// containers; this package fills them in with the live state of the HVR
    /// pipeline components.
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

            var menuGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            menuGroup.SetTitle("Vixxy");
            menuGroup.SetDescription("Trigger effects on this avatar.");

            foreach (var menuItem in menuItems)
            {
                var hasControl = menuItem.TryResolveActualControl(out var control);
                if (!hasControl) continue;

                if (menuItem.presentation == HVRVixxyControlPresentation.Slider)
                {
                    BuildSlider(menuGroup, control, menuItem);
                }
                else
                {
                    if (!control.HasThreeOrMoreChoices)
                    {
                        BuildToggle(menuGroup, menuItem, control);
                    }
                    else
                    {
                        BuildDropdown(control, menuGroup, menuItem);
                    }
                }
            }
        }

        private static void BuildToggle(PanelElementDescriptor menuGroup, HVRVixxyMenuItem menuItem, HVRVixxyControl control)
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
        }

        private static void BuildSlider(PanelElementDescriptor menuGroup, HVRVixxyControl control, HVRVixxyMenuItem menuItem)
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
        }

        private static void BuildDropdown(HVRVixxyControl control, PanelElementDescriptor menuGroup, HVRVixxyMenuItem menuItem)
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
                BasisDebug.Log($"Selected {choice}, value is {valueForThatChoice}");
                menuItem.ApplyValue(valueForThatChoice);
                dropdown.Descriptor.SetTitle(menuItem.ResolveTitle());
            };
            var currentValue = (int)menuItem.GetValue();
            var matchingChoice = control.choices.FirstOrDefault(choice => Mathf.Approximately(choice.value, currentValue));
            var currentChoice = matchingChoice != null ? control.choices.ToList().IndexOf(matchingChoice) : -1;
            BasisDebug.Log($"Current choice is {currentChoice}, choicestring count is {choiceStrings.Count}");
            if (currentChoice >= 0 && currentChoice < choiceStrings.Count)
            {
                dropdown.SetValueWithoutNotify(choiceStrings[currentChoice]);
            }
        }
    }
}
