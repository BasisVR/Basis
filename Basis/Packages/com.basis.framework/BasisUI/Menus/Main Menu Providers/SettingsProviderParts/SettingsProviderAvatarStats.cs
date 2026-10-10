using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    /// <summary>
    /// Helpers for populating avatar diagnostics. The diagnostic content
    /// (texture stats, tracker list, face/eye tracking) lives on the Developer
    /// tab's Avatar Debug section; this class still hosts the "My Avatar" tab
    /// itself (currently empty — kept as a placeholder so the tab name and
    /// position are reserved) and the helpers Developer calls into.
    /// </summary>
    public static class SettingsProviderAvatarStats
    {
        public static PanelTabPage AvatarStatsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle(BasisLocalization.Get("settings.tab.myavatar"));
            descriptor.ForceRebuild();

            RectTransform content = tab.Descriptor.ContentParent;
            SettingsProvider.AvatarCustomizationBuilder?.Invoke(content);

            if (content.childCount == 0)
            {
                PanelElementDescriptor emptyGroup =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, content);
                emptyGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.noCustomization"));
                emptyGroup.SetDescription(BasisLocalization.Get("settings.myAvatar.noCustomization.description"));
                descriptor.ForceRebuild();
            }

            return tab;
        }

        public static void PopulateTrackerRoles(PanelElementDescriptor group)
        {
            group.SetBackgroundVisible(false);

            BasisDeviceManagement manager = BasisDeviceManagement.Instance;
            if (manager == null)
            {
                group.SetDescription(BasisLocalization.Get("settings.avatarStats.deviceManagerUnavailable"));
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
                        : (!string.IsNullOrEmpty(device.ClassName) ? device.ClassName : BasisLocalization.Get("settings.avatarStats.unknownDevice"));
                    value = !string.IsNullOrEmpty(device.UniqueDeviceIdentifier)
                        ? $"{deviceLabel}  ({device.UniqueDeviceIdentifier})"
                        : deviceLabel;
                    assignedCount++;
                }
                else
                {
                    value = BasisLocalization.Get("settings.avatarStats.unassigned");
                }

                PanelElementDescriptor row = PanelElementDescriptor.CreateNew(
                    PanelElementDescriptor.ElementStyles.Group, group.ContentParent);
                row.SetTitle(SettingsProviderControllerConfig.BodyRoleLabel(role));
                row.SetDescription(value);
            }

            group.SetDescription(BasisLocalization.Get("settings.avatarStats.rolesBound", assignedCount, System.Enum.GetValues(typeof(BasisBoneTrackedRole)).Length));
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
                AddInfoField(overviewGroup, BasisLocalization.Get("settings.myAvatar.stat.downloadSize"), BasisAvatarTextureStats.FormatBytes(downloadBytes));
            }

            string avatarName = BasisLocalization.Get("settings.myAvatar.stat.unknown");
            if (localPlayer.AvatarMetaData?.BasisBundleConnector?.BasisBundleDescription != null)
            {
                string name = localPlayer.AvatarMetaData.BasisBundleConnector.BasisBundleDescription.AssetBundleName;
                if (!string.IsNullOrEmpty(name))
                    avatarName = name;
            }
            AddInfoField(overviewGroup, BasisLocalization.Get("settings.myAvatar.stat.avatar"), avatarName);

            bool isFallback = localPlayer.IsConsideredFallBackAvatar;
            AddInfoField(overviewGroup, BasisLocalization.Get("settings.myAvatar.stat.type"), BasisLocalization.Get(isFallback ? "settings.myAvatar.stat.type.fallback" : "settings.myAvatar.stat.type.custom"));

            // Bundle metadata stats
            if (localPlayer.AvatarMetaData?.BasisBundleConnector != null)
            {
                var meta = localPlayer.AvatarMetaData.BasisBundleConnector.MetaData;
                if (meta.TrianglesCount > 0)
                    AddInfoField(overviewGroup, BasisLocalization.Get("settings.myAvatar.stat.triangles"), meta.TrianglesCount.ToString("N0"));
                if (meta.MaterialCount > 0)
                    AddInfoField(overviewGroup, BasisLocalization.Get("settings.myAvatar.stat.materials"), meta.MaterialCount.ToString("N0"));
                if (meta.BonesCount > 0)
                    AddInfoField(overviewGroup, BasisLocalization.Get("settings.myAvatar.stat.bones"), meta.BonesCount.ToString("N0"));
            }

            // --- VRAM group ---
            PanelElementDescriptor vramGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            vramGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.vramUsage"));
            vramGroup.SetDescription(BasisLocalization.Get("settings.myAvatar.vramUsage.description"));

            AddInfoField(vramGroup, BasisLocalization.Get("settings.myAvatar.stat.totalTextureVram"), BasisAvatarTextureStats.FormatBytes(stats.TotalVRAMBytes));
            AddInfoField(vramGroup, BasisLocalization.Get("settings.myAvatar.stat.nonStreamingVram"), BasisAvatarTextureStats.FormatBytes(stats.NonStreamingVRAMBytes));

            if (stats.EstimatedSavingsBytes > 0)
            {
                AddInfoField(vramGroup, BasisLocalization.Get("settings.myAvatar.stat.potentialVramSavings"), BasisAvatarTextureStats.FormatBytes(stats.EstimatedSavingsBytes));
            }

            // --- Mipmap streaming group ---
            PanelElementDescriptor streamGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            streamGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.mipmapStreaming"));
            streamGroup.SetDescription(BasisLocalization.Get("settings.myAvatar.mipmapStreaming.description"));

            AddInfoField(streamGroup, BasisLocalization.Get("settings.myAvatar.stat.totalTextures"), stats.TotalTextureCount.ToString());
            AddInfoField(streamGroup, BasisLocalization.Get("settings.myAvatar.stat.streaming"), $"{stats.StreamingTextureCount} ({stats.StreamingPercentage:F0}%)");
            AddInfoField(streamGroup, BasisLocalization.Get("settings.myAvatar.stat.notStreaming"), stats.NonStreamingTextureCount.ToString());
            AddInfoField(streamGroup, BasisLocalization.Get("settings.myAvatar.stat.rating"), stats.GetStreamingRating());

            BasisPanelTint.Apply(BasisPanelTint.Capture(streamGroup), StreamingSeverity(stats), false);

            // --- Performance impact group ---
            PanelElementDescriptor perfGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            perfGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.perfImpact"));
            perfGroup.SetDescription(BasisLocalization.Get("settings.myAvatar.perfImpact.description"));

            AddInfoField(perfGroup, BasisLocalization.Get("settings.myAvatar.stat.impact"), stats.GetPerformanceImpact());

            // Combined "cost to others" summary
            string costSummary = BuildCostSummary(stats, downloadBytes);
            AddInfoField(perfGroup, BasisLocalization.Get("settings.myAvatar.stat.costToOthers"), costSummary);

            BasisPanelTint.Apply(BasisPanelTint.Capture(perfGroup), ImpactSeverity(stats), false);

            // --- Per-texture breakdown ---
            if (stats.Textures != null && stats.Textures.Count > 0)
            {
                PanelElementDescriptor texGroup =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                texGroup.SetTitle(BasisLocalization.Get("settings.myAvatar.textureDetails"));
                texGroup.SetDescription(BasisLocalization.Get("settings.myAvatar.uniqueTextures", stats.Textures.Count));

                for (int i = 0; i < stats.Textures.Count; i++)
                {
                    var tex = stats.Textures[i];
                    string streamTag = BasisLocalization.Get(tex.IsStreamingMipmaps ? "settings.myAvatar.tag.streaming" : "settings.myAvatar.tag.notStreaming");
                    string name = string.IsNullOrEmpty(tex.Name) ? BasisLocalization.Get("settings.myAvatar.textureFallbackName", i) : tex.Name;
                    string detail = $"{tex.Width}x{tex.Height} {tex.Format} | {BasisLocalization.Get("settings.myAvatar.mips", tex.MipCount)} | {BasisAvatarTextureStats.FormatBytes(tex.EstimatedVRAMBytes)} | {streamTag}";

                    PanelPasswordField field = PanelPasswordField.CreateNew(texGroup.ContentParent);
                    field.Descriptor.SetTitle(name);
                    field.SetPassword(detail);
                    field.SetValue(true);
                    field.DisableIcons();
                }
            }
        }

        /// <summary>
        /// Colour for the mipmap-streaming card. Thresholds mirror
        /// <see cref="BasisAvatarTextureStats.GetStreamingRating"/> so the tint and the wording agree.
        /// </summary>
        static BasisPanelSeverity StreamingSeverity(BasisAvatarTextureStats stats)
        {
            if (stats.TotalTextureCount == 0) return BasisPanelSeverity.None;
            if (stats.StreamingPercentage >= 75f) return BasisPanelSeverity.Calm;
            if (stats.StreamingPercentage >= 25f) return BasisPanelSeverity.Caution;
            return BasisPanelSeverity.Hot;
        }

        /// <summary>
        /// Colour for the performance-impact card. Thresholds mirror
        /// <see cref="BasisAvatarTextureStats.GetPerformanceImpact"/>: Severe and High both read hot,
        /// Moderate warns, No impact and Low stay calm.
        /// </summary>
        static BasisPanelSeverity ImpactSeverity(BasisAvatarTextureStats stats)
        {
            if (stats.NonStreamingTextureCount == 0) return BasisPanelSeverity.Calm;
            if (stats.EstimatedSavingsBytes > 64L * 1024 * 1024) return BasisPanelSeverity.Hot;
            if (stats.EstimatedSavingsBytes > 16L * 1024 * 1024) return BasisPanelSeverity.Caution;
            return BasisPanelSeverity.Calm;
        }

        static string BuildCostSummary(BasisAvatarTextureStats stats, long downloadBytes)
        {
            var parts = new System.Collections.Generic.List<string>();

            if (downloadBytes > 0)
                parts.Add(BasisLocalization.Get("settings.myAvatar.cost.download", BasisAvatarTextureStats.FormatBytes(downloadBytes)));

            parts.Add(BasisLocalization.Get("settings.myAvatar.cost.gpuMemory", BasisAvatarTextureStats.FormatBytes(stats.TotalVRAMBytes)));

            if (stats.EstimatedSavingsBytes > 0)
                parts.Add(BasisLocalization.Get("settings.myAvatar.cost.wastedVram", BasisAvatarTextureStats.FormatBytes(stats.EstimatedSavingsBytes)));

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
