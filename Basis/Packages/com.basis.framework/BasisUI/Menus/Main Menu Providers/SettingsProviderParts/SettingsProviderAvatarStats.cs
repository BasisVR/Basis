using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public static class SettingsProviderAvatarStats
    {
        public static PanelTabPage AvatarStatsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle(BasisLocalization.Get("settings.tab.myavatar"));
            descriptor.SetDescription(BasisLocalization.Get("settings.myAvatar.description"));

            RectTransform container = descriptor.ContentParent;

            PanelButton scanButton = PanelButton.CreateNew(container);
            scanButton.Descriptor.SetTitle(BasisLocalization.Get("settings.myAvatar.scan"));
            scanButton.Descriptor.SetDescription(BasisLocalization.Get("settings.myAvatar.scan.description"));
            scanButton.OnClicked += () =>
            {
                Object.Destroy(scanButton.gameObject);
                PopulateStats(container);
                LayoutRebuilder.ForceRebuildLayoutImmediate(container);
                LayoutRebuilder.ForceRebuildLayoutImmediate(tabGroup.Descriptor.ContentParent);
                descriptor.ForceRebuild();
            };

            PanelElementDescriptor trackersGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            trackersGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.trackerRoles"));
            trackersGroup.SetDescription(BasisLocalization.Get("settings.myAvatar.trackerRoles.description"));

            PanelToggle trackerRolesToggle = PanelToggle.CreateNewEntry(trackersGroup.ContentParent);
            trackerRolesToggle.Descriptor.SetTitle(BasisLocalization.Get("settings.myAvatar.showTrackers"));
            trackerRolesToggle.Descriptor.SetDescription(BasisLocalization.Get("settings.myAvatar.showTrackers.description"));
            trackerRolesToggle.AssignBinding(BasisSettingsDefaults.AvatarShowTrackerRoles);

            PanelElementDescriptor trackerListGroup = null;
            void CreateTrackerList()
            {
                trackerListGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                trackerListGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.assignedTrackers"));
                PopulateTrackerRoles(trackerListGroup);
            }
            if (BasisSettingsDefaults.AvatarShowTrackerRoles.RawValue) CreateTrackerList();
            trackerRolesToggle.OnValueChanged += on =>
            {
                if (trackerListGroup != null)
                {
                    Object.Destroy(trackerListGroup.gameObject);
                    trackerListGroup = null;
                }
                if (on) CreateTrackerList();
                LayoutRebuilder.ForceRebuildLayoutImmediate(container);
                descriptor.ForceRebuild();
            };

            descriptor.ForceRebuild();
            return tab;
        }

        public static void PopulateTrackerRoles(PanelElementDescriptor group)
        {
            BasisDeviceManagement manager = BasisDeviceManagement.Instance;
            if (manager == null)
            {
                group.SetDescription("Device manager is not available.");
                return;
            }

            int assignedCount = 0;
            foreach (BasisBoneTrackedRole role in System.Enum.GetValues(typeof(BasisBoneTrackedRole)))
            {
                string value;
                if (manager.FindDevice(out BasisInput device, role) && device != null)
                {
                    string deviceLabel = !string.IsNullOrEmpty(device.CommonDeviceIdentifier)
                        ? device.CommonDeviceIdentifier
                        : (!string.IsNullOrEmpty(device.ClassName) ? device.ClassName : "Unknown Device");
                    value = !string.IsNullOrEmpty(device.UniqueDeviceIdentifier)
                        ? $"{deviceLabel}  ({device.UniqueDeviceIdentifier})"
                        : deviceLabel;
                    assignedCount++;
                }
                else
                {
                    value = "Unassigned";
                }

                PanelElementDescriptor row = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
                row.SetTitle(role.ToString());
                row.SetDescription(value);
            }

            group.SetDescription($"{assignedCount} of {System.Enum.GetValues(typeof(BasisBoneTrackedRole)).Length} roles currently bound.");
        }

        /// <summary>
        /// Builds texture/VRAM stats UI into the given container.
        /// Called by SettingsProviderFaceTracking for the collapsible texture section.
        /// </summary>
        public static void PopulateStatsInto(RectTransform container)
        {
            PopulateStats(container);
            LayoutRebuilder.ForceRebuildLayoutImmediate(container);
        }

        static void PopulateStats(RectTransform container)
        {
            BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
            if (localPlayer == null || localPlayer.BasisAvatar == null)
            {
                PanelElementDescriptor errorGroup =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                errorGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.noAvatar"));
                errorGroup.SetDescription(BasisLocalization.Get("settings.myAvatar.noAvatar.description"));
                return;
            }

            // Resolve download size from bundle metadata
            long downloadBytes = 0;
            if (localPlayer.AvatarMetaData?.BasisBundleConnector != null)
            {
                var connector = localPlayer.AvatarMetaData.BasisBundleConnector;
                if (connector.GetPlatform(out BasisBundleGenerated generated))
                {
                    downloadBytes = generated.EndByte;
                }
            }

            BasisAvatarTextureStats stats = BasisAvatarTextureStats.Collect(
                localPlayer.BasisAvatar.Renders, downloadBytes);

            // --- Overview group ---
            PanelElementDescriptor overviewGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            overviewGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.overview"));

            if (downloadBytes > 0)
            {
                AddInfoField(overviewGroup, "Download Size", BasisAvatarTextureStats.FormatBytes(downloadBytes));
            }

            string avatarName = "Unknown";
            if (localPlayer.AvatarMetaData?.BasisBundleConnector?.BasisBundleDescription != null)
            {
                string name = localPlayer.AvatarMetaData.BasisBundleConnector.BasisBundleDescription.AssetBundleName;
                if (!string.IsNullOrEmpty(name))
                    avatarName = name;
            }
            AddInfoField(overviewGroup, "Avatar", avatarName);

            bool isFallback = localPlayer.IsConsideredFallBackAvatar;
            AddInfoField(overviewGroup, "Type", isFallback ? "Fallback" : "Custom");

            // Bundle metadata stats
            if (localPlayer.AvatarMetaData?.BasisBundleConnector != null)
            {
                var meta = localPlayer.AvatarMetaData.BasisBundleConnector.MetaData;
                if (meta.TrianglesCount > 0)
                    AddInfoField(overviewGroup, "Triangles", meta.TrianglesCount.ToString("N0"));
                if (meta.MaterialCount > 0)
                    AddInfoField(overviewGroup, "Materials", meta.MaterialCount.ToString("N0"));
                if (meta.BonesCount > 0)
                    AddInfoField(overviewGroup, "Bones", meta.BonesCount.ToString("N0"));
            }

            // --- VRAM group ---
            PanelElementDescriptor vramGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            vramGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.vramUsage"));
            vramGroup.SetDescription(BasisLocalization.Get("settings.myAvatar.vramUsage.description"));

            AddInfoField(vramGroup, "Total Texture VRAM", BasisAvatarTextureStats.FormatBytes(stats.TotalVRAMBytes));
            AddInfoField(vramGroup, "Non-Streaming VRAM", BasisAvatarTextureStats.FormatBytes(stats.NonStreamingVRAMBytes));

            if (stats.EstimatedSavingsBytes > 0)
            {
                AddInfoField(vramGroup, "Potential VRAM Savings", BasisAvatarTextureStats.FormatBytes(stats.EstimatedSavingsBytes));
            }

            // --- Mipmap streaming group ---
            PanelElementDescriptor streamGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            streamGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.mipmapStreaming"));
            streamGroup.SetDescription(BasisLocalization.Get("settings.myAvatar.mipmapStreaming.description"));

            AddInfoField(streamGroup, "Total Textures", stats.TotalTextureCount.ToString());
            AddInfoField(streamGroup, "Streaming", $"{stats.StreamingTextureCount} ({stats.StreamingPercentage:F0}%)");
            AddInfoField(streamGroup, "Not Streaming", stats.NonStreamingTextureCount.ToString());
            AddInfoField(streamGroup, "Rating", stats.GetStreamingRating());

            // --- Performance impact group ---
            PanelElementDescriptor perfGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            perfGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.perfImpact"));
            perfGroup.SetDescription(BasisLocalization.Get("settings.myAvatar.perfImpact.description"));

            AddInfoField(perfGroup, "Impact", stats.GetPerformanceImpact());

            // Combined "cost to others" summary
            string costSummary = BuildCostSummary(stats, downloadBytes);
            AddInfoField(perfGroup, "Cost To Others", costSummary);

            // --- Per-texture breakdown ---
            if (stats.Textures != null && stats.Textures.Count > 0)
            {
                PanelElementDescriptor texGroup =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                texGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.textureDetails"));
                texGroup.SetDescription($"{stats.Textures.Count} unique textures found.");

                for (int i = 0; i < stats.Textures.Count; i++)
                {
                    var tex = stats.Textures[i];
                    string streamTag = tex.IsStreamingMipmaps ? "[STREAMING]" : "[NOT STREAMING]";
                    string name = string.IsNullOrEmpty(tex.Name) ? $"Texture {i}" : tex.Name;
                    string detail = $"{tex.Width}x{tex.Height} {tex.Format} | {tex.MipCount} mips | {BasisAvatarTextureStats.FormatBytes(tex.EstimatedVRAMBytes)} | {streamTag}";

                    PanelPasswordField field = PanelPasswordField.CreateNew(texGroup.ContentParent);
                    field.Descriptor.SetTitle(name);
                    field.SetPassword(detail);
                    field.SetValue(true);
                    field.DisableIcons();
                }
            }
        }

        static string BuildCostSummary(BasisAvatarTextureStats stats, long downloadBytes)
        {
            var parts = new System.Collections.Generic.List<string>();

            if (downloadBytes > 0)
                parts.Add($"{BasisAvatarTextureStats.FormatBytes(downloadBytes)} download per player who sees you");

            parts.Add($"{BasisAvatarTextureStats.FormatBytes(stats.TotalVRAMBytes)} GPU memory per instance");

            if (stats.EstimatedSavingsBytes > 0)
                parts.Add($"~{BasisAvatarTextureStats.FormatBytes(stats.EstimatedSavingsBytes)} wasted VRAM from missing streaming mipmaps");

            return string.Join(". ", parts) + ".";
        }

        static void AddInfoField(PanelElementDescriptor parent, string label, string value)
        {
            PanelPasswordField field = PanelPasswordField.CreateNew(parent.ContentParent);
            field.Descriptor.SetTitle(label);
            field.SetPassword(value);
            field.SetValue(true);
            field.DisableIcons();
        }
    }
}
