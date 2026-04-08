using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.BasisUI
{
    public static class SettingsProviderAvatarStats
    {
        public static PanelTabPage AvatarStatsTab(PanelTabGroup tabGroup)
        {
            PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
            PanelElementDescriptor descriptor = tab.Descriptor;
            descriptor.SetIcon(AddressableAssets.Sprites.Settings);
            descriptor.SetTitle("My Avatar");
            descriptor.SetDescription("Texture and memory statistics for your current avatar.");

            RectTransform container = descriptor.ContentParent;

            PanelButton scanButton = PanelButton.CreateNew(container);
            scanButton.Descriptor.SetTitle("Scan Avatar");
            scanButton.Descriptor.SetDescription("Analyze your current avatar's textures, VRAM usage, and streaming mipmap status.");
            scanButton.OnClicked += () =>
            {
                Object.Destroy(scanButton.gameObject);
                PopulateStats(container);
                descriptor.ForceRebuild();
            };

            descriptor.ForceRebuild();
            return tab;
        }

        /// <summary>
        /// Builds texture/VRAM stats UI into the given container.
        /// Called by SettingsProviderFaceTracking for the collapsible texture section.
        /// </summary>
        public static void PopulateStatsInto(RectTransform container)
        {
            PopulateStats(container);
        }

        static void PopulateStats(RectTransform container)
        {
            BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
            if (localPlayer == null || localPlayer.BasisAvatar == null)
            {
                PanelElementDescriptor errorGroup =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                errorGroup.SetTitle("No Avatar");
                errorGroup.SetDescription("No avatar is currently loaded.");
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
            overviewGroup.SetTitle("Overview");

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
            vramGroup.SetTitle("Estimated VRAM Usage");
            vramGroup.SetDescription("GPU memory consumed by this avatar's textures.");

            AddInfoField(vramGroup, "Total Texture VRAM", BasisAvatarTextureStats.FormatBytes(stats.TotalVRAMBytes));
            AddInfoField(vramGroup, "Non-Streaming VRAM", BasisAvatarTextureStats.FormatBytes(stats.NonStreamingVRAMBytes));

            if (stats.EstimatedSavingsBytes > 0)
            {
                AddInfoField(vramGroup, "Potential VRAM Savings", BasisAvatarTextureStats.FormatBytes(stats.EstimatedSavingsBytes));
            }

            // --- Mipmap streaming group ---
            PanelElementDescriptor streamGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            streamGroup.SetTitle("Mipmap Streaming");
            streamGroup.SetDescription("Textures with streaming mipmaps load only the detail levels needed, reducing VRAM pressure on everyone in the instance.");

            AddInfoField(streamGroup, "Total Textures", stats.TotalTextureCount.ToString());
            AddInfoField(streamGroup, "Streaming", $"{stats.StreamingTextureCount} ({stats.StreamingPercentage:F0}%)");
            AddInfoField(streamGroup, "Not Streaming", stats.NonStreamingTextureCount.ToString());
            AddInfoField(streamGroup, "Rating", stats.GetStreamingRating());

            // --- Performance impact group ---
            PanelElementDescriptor perfGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            perfGroup.SetTitle("Performance Impact");
            perfGroup.SetDescription("How this avatar affects other players in the instance.");

            AddInfoField(perfGroup, "Impact", stats.GetPerformanceImpact());

            // Combined "cost to others" summary
            string costSummary = BuildCostSummary(stats, downloadBytes);
            AddInfoField(perfGroup, "Cost To Others", costSummary);

            // --- Per-texture breakdown ---
            if (stats.Textures != null && stats.Textures.Count > 0)
            {
                PanelElementDescriptor texGroup =
                    PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                texGroup.SetTitle("Texture Details");
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
