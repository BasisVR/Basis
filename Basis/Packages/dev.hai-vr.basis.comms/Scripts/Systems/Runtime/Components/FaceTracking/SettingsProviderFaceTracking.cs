using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace HVR.Basis.Comms
{
    public static class SettingsProviderFaceTracking
    {
        [RuntimeInitializeOnLoadMethod]
        static void Register()
        {
            SettingsProvider.MyAvatarTabOverride = MyAvatarTab;
        }

        public static PanelTabPage MyAvatarTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle("My Avatar");
            descriptor.SetDescription("Face tracking diagnostics and avatar statistics.");

            RectTransform container = descriptor.ContentParent;

            // ── Face Tracking Status ──
            PanelElementDescriptor statusGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            statusGroup.SetTitle("Face Tracking");
            statusGroup.SetDescription("Live state of the face tracking pipeline. Press Refresh to poll current values.");

            PanelButton refreshButton = PanelButton.CreateNew(statusGroup.ContentParent);
            refreshButton.Descriptor.SetTitle("Refresh");
            refreshButton.Descriptor.SetDescription("Poll the current face tracking state from all components.");

            var fieldFTActive = CreateInfoField(statusGroup.ContentParent, "Face Tracking Active", "...");
            var fieldOSC = CreateInfoField(statusGroup.ContentParent, "OSC Acquisition", "...");
            var fieldBlendshapeActive = CreateInfoField(statusGroup.ContentParent, "Blendshape Tracking", "...");
            var fieldActuatedAddresses = CreateInfoField(statusGroup.ContentParent, "Actuated Addresses", "...");

            // ── Eye Tracking ──
            PanelElementDescriptor eyeGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            eyeGroup.SetTitle("Eye Tracking");
            eyeGroup.SetDescription("State of the eye tracking bone actuation and the natural eye driver.");

            var fieldEyeOverride = CreateInfoField(eyeGroup.ContentParent, "Eye Override", "...");
            var fieldEyeDriverEnabled = CreateInfoField(eyeGroup.ContentParent, "Eye Driver Enabled", "...");
            var fieldEyeParamsActive = CreateInfoField(eyeGroup.ContentParent, "Eye Params Active", "...");
            var fieldEyeLeftX = CreateInfoField(eyeGroup.ContentParent, "Eye Left X", "...");
            var fieldEyeRightX = CreateInfoField(eyeGroup.ContentParent, "Eye Right X", "...");
            var fieldEyeY = CreateInfoField(eyeGroup.ContentParent, "Eye Y", "...");

            refreshButton.OnClicked += () => RefreshState(
                fieldFTActive, fieldOSC, fieldBlendshapeActive, fieldActuatedAddresses,
                fieldEyeOverride, fieldEyeDriverEnabled, fieldEyeParamsActive,
                fieldEyeLeftX, fieldEyeRightX, fieldEyeY);

            RefreshState(
                fieldFTActive, fieldOSC, fieldBlendshapeActive, fieldActuatedAddresses,
                fieldEyeOverride, fieldEyeDriverEnabled, fieldEyeParamsActive,
                fieldEyeLeftX, fieldEyeRightX, fieldEyeY);

            // ── Section Toggles ──
            PanelElementDescriptor sectionTogglesGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            sectionTogglesGroup.SetTitle("Sections");
            sectionTogglesGroup.SetDescription("Toggle additional avatar information panels.");

            PanelToggle toggleTextures = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleTextures.Descriptor.SetTitle("Texture Statistics");
            toggleTextures.Descriptor.SetDescription("Show texture, VRAM, and streaming mipmap statistics for the current avatar.");
            toggleTextures.AssignBinding(BasisSettingsDefaults.AvatarShowTextureStats);

            // ── Collapsible Texture Section (disabled by default) ──
            PanelElementDescriptor textureSection = null;
            void CreateTextureSection()
            {
                textureSection = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Group, container);
                textureSection.SetTitle("Texture Statistics");
                textureSection.SetDescription("Texture and memory statistics for your current avatar.");
                BuildTextureStats(textureSection.ContentParent);
            }

            if (BasisSettingsDefaults.AvatarShowTextureStats.RawValue)
            {
                CreateTextureSection();
            }

            toggleTextures.OnValueChanged += on =>
            {
                if (textureSection != null)
                {
                    Object.Destroy(textureSection.gameObject);
                    textureSection = null;
                }
                if (on) CreateTextureSection();
            };

            descriptor.ForceRebuild();
            return tab;
        }

        static void RefreshState(
            PanelElementDescriptor ftActive,
            PanelElementDescriptor oscAcquisition,
            PanelElementDescriptor blendshapeActive,
            PanelElementDescriptor actuatedAddresses,
            PanelElementDescriptor eyeOverride,
            PanelElementDescriptor eyeDriverEnabled,
            PanelElementDescriptor eyeParamsActive,
            PanelElementDescriptor eyeLeftX,
            PanelElementDescriptor eyeRightX,
            PanelElementDescriptor eyeY)
        {
            BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
            var avatar = localPlayer != null ? localPlayer.BasisAvatar : null;

            // Face tracking activity relay
            FaceTrackingActivityRelay relay = avatar != null
                ? avatar.GetComponentInChildren<FaceTrackingActivityRelay>(true)
                : null;
            ftActive.SetDescription(relay != null ? relay.IsTrackingActive.ToString() : "No relay found");

            // OSC acquisition
            OSCAcquisition oscAcq = avatar != null
                ? avatar.GetComponentInChildren<OSCAcquisition>(true)
                : null;
            if (oscAcq != null)
                oscAcquisition.SetDescription(oscAcq.isActiveAndEnabled ? "Active" : "DISABLED");
            else
                oscAcquisition.SetDescription("No component");

            // Blendshape actuation
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

            // Eye driver statics
            eyeOverride.SetDescription(BasisLocalEyeDriver.Override.ToString());
            eyeDriverEnabled.SetDescription(BasisLocalEyeDriver.IsEnabled.ToString());

            // Eye tracking bone actuation
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

        static void BuildTextureStats(RectTransform container)
        {
            PanelButton scanButton = PanelButton.CreateNew(container);
            scanButton.Descriptor.SetTitle("Scan Avatar");
            scanButton.Descriptor.SetDescription("Analyze your current avatar's textures, VRAM usage, and streaming mipmap status.");
            scanButton.OnClicked += () =>
            {
                Object.Destroy(scanButton.gameObject);
                SettingsProviderAvatarStats.PopulateStatsInto(container);
            };
        }

        static PanelElementDescriptor CreateInfoField(RectTransform parent, string title, string initialValue)
        {
            var field = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, parent);
            field.SetTitle(title);
            field.SetDescription(initialValue);
            return field;
        }
    }
}
