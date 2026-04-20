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

            // ── Tracking Master Switches ──
            PanelElementDescriptor trackingTogglesGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            trackingTogglesGroup.SetTitle("Tracking");
            trackingTogglesGroup.SetDescription("Enable or disable face tracking and eye tracking on your avatar. Turning a feature off also collapses its diagnostics panel.");

            PanelToggle toggleFaceTracking = PanelToggle.CreateNewEntry(trackingTogglesGroup.ContentParent);
            toggleFaceTracking.Descriptor.SetTitle("Face Tracking");
            toggleFaceTracking.Descriptor.SetDescription("Drive your avatar's facial blendshapes from face tracking data.");
            toggleFaceTracking.AssignBinding(BasisSettingsDefaults.EnableFaceTracking);

            PanelToggle toggleEyeTracking = PanelToggle.CreateNewEntry(trackingTogglesGroup.ContentParent);
            toggleEyeTracking.Descriptor.SetTitle("Eye Tracking");
            toggleEyeTracking.Descriptor.SetDescription("Drive your avatar's eye bones from eye tracking data. The natural eye look keeps running when disabled.");
            toggleEyeTracking.AssignBinding(BasisSettingsDefaults.EnableEyeTracking);

            // ── Collapsible Face Tracking Status ──
            PanelElementDescriptor faceTrackingSection = null;
            void CreateFaceTrackingSection()
            {
                faceTrackingSection = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Group, container);
                faceTrackingSection.SetTitle("Face Tracking");
                faceTrackingSection.SetDescription("Live state of the face tracking pipeline. Press Refresh to poll current values.");

                PanelButton refreshButton = PanelButton.CreateNew(faceTrackingSection.ContentParent);
                refreshButton.Descriptor.SetTitle("Refresh");
                refreshButton.Descriptor.SetDescription("Poll the current face tracking state from all components.");

                var fieldFTActive = CreateInfoField(faceTrackingSection.ContentParent, "Face Tracking Active", "...");
                var fieldOSC = CreateInfoField(faceTrackingSection.ContentParent, "OSC Acquisition", "...");
                var fieldBlendshapeActive = CreateInfoField(faceTrackingSection.ContentParent, "Blendshape Tracking", "...");
                var fieldActuatedAddresses = CreateInfoField(faceTrackingSection.ContentParent, "Actuated Addresses", "...");

                void Refresh() => RefreshFaceState(fieldFTActive, fieldOSC, fieldBlendshapeActive, fieldActuatedAddresses);
                refreshButton.OnClicked += Refresh;
                Refresh();
            }

            if (BasisSettingsDefaults.EnableFaceTracking.RawValue)
            {
                CreateFaceTrackingSection();
            }

            toggleFaceTracking.OnValueChanged += on =>
            {
                if (faceTrackingSection != null)
                {
                    Object.Destroy(faceTrackingSection.gameObject);
                    faceTrackingSection = null;
                }
                if (on) CreateFaceTrackingSection();
            };

            // ── Collapsible Eye Tracking Status ──
            PanelElementDescriptor eyeTrackingSection = null;
            void CreateEyeTrackingSection()
            {
                eyeTrackingSection = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Group, container);
                eyeTrackingSection.SetTitle("Eye Tracking");
                eyeTrackingSection.SetDescription("State of the eye tracking bone actuation and the natural eye driver.");

                PanelButton refreshButton = PanelButton.CreateNew(eyeTrackingSection.ContentParent);
                refreshButton.Descriptor.SetTitle("Refresh");
                refreshButton.Descriptor.SetDescription("Poll the current eye tracking state from all components.");

                var fieldEyeOverride = CreateInfoField(eyeTrackingSection.ContentParent, "Eye Override", "...");
                var fieldEyeDriverEnabled = CreateInfoField(eyeTrackingSection.ContentParent, "Eye Driver Enabled", "...");
                var fieldEyeParamsActive = CreateInfoField(eyeTrackingSection.ContentParent, "Eye Params Active", "...");
                var fieldEyeLeftX = CreateInfoField(eyeTrackingSection.ContentParent, "Eye Left X", "...");
                var fieldEyeRightX = CreateInfoField(eyeTrackingSection.ContentParent, "Eye Right X", "...");
                var fieldEyeY = CreateInfoField(eyeTrackingSection.ContentParent, "Eye Y", "...");

                void Refresh() => RefreshEyeState(
                    fieldEyeOverride, fieldEyeDriverEnabled, fieldEyeParamsActive,
                    fieldEyeLeftX, fieldEyeRightX, fieldEyeY);
                refreshButton.OnClicked += Refresh;
                Refresh();
            }

            if (BasisSettingsDefaults.EnableEyeTracking.RawValue)
            {
                CreateEyeTrackingSection();
            }

            toggleEyeTracking.OnValueChanged += on =>
            {
                if (eyeTrackingSection != null)
                {
                    Object.Destroy(eyeTrackingSection.gameObject);
                    eyeTrackingSection = null;
                }
                if (on) CreateEyeTrackingSection();
            };

            // ── Section Toggles ──
            PanelElementDescriptor sectionTogglesGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            sectionTogglesGroup.SetTitle("Sections");
            sectionTogglesGroup.SetDescription("Toggle additional avatar information panels.");

            PanelToggle toggleTextures = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleTextures.Descriptor.SetTitle("Texture Statistics");
            toggleTextures.Descriptor.SetDescription("Show texture, VRAM, and streaming mipmap statistics for the current avatar.");
            toggleTextures.AssignBinding(BasisSettingsDefaults.AvatarShowTextureStats);

            PanelToggle toggleTrackerRoles = PanelToggle.CreateNewEntry(sectionTogglesGroup.ContentParent);
            toggleTrackerRoles.Descriptor.SetTitle("Show Assigned Trackers");
            toggleTrackerRoles.Descriptor.SetDescription("List every input device that has been assigned a tracked bone role.");
            toggleTrackerRoles.AssignBinding(BasisSettingsDefaults.AvatarShowTrackerRoles);

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

            // ── Collapsible Tracker Roles Section (disabled by default) ──
            PanelElementDescriptor trackerRolesSection = null;
            void CreateTrackerRolesSection()
            {
                trackerRolesSection = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Group, container);
                trackerRolesSection.SetTitle("Assigned Trackers");
                SettingsProviderAvatarStats.PopulateTrackerRoles(trackerRolesSection);
            }

            if (BasisSettingsDefaults.AvatarShowTrackerRoles.RawValue)
            {
                CreateTrackerRolesSection();
            }

            toggleTrackerRoles.OnValueChanged += on =>
            {
                if (trackerRolesSection != null)
                {
                    Object.Destroy(trackerRolesSection.gameObject);
                    trackerRolesSection = null;
                }
                if (on) CreateTrackerRolesSection();
            };

            descriptor.ForceRebuild();
            return tab;
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
