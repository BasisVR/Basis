using UnityEngine;

namespace Basis.Rendering.RTAO
{
    public static class BasisRTAOSettingsMap
    {
        public const string ModeRayTraced = "Ray Traced";
        public const string ModeScreenSpace = "Screen Space";

        public static BasisRTAOTracingMode ReadMode(string value)
        {
            switch (Normalise(value))
            {
                case "raytraced":
                case "ray traced":
                case "raytracedonly":
                    return BasisRTAOTracingMode.RayTracedOnly;
                case "screenspace":
                case "screen space":
                    return BasisRTAOTracingMode.ScreenSpace;
                case "computebvh":
                case "compute bvh":
                    return BasisRTAOTracingMode.ComputeBvh;
                // "Auto" still reads, because a settings file written before it was removed keeps saying it
                // for as long as that file survives. It lands on screen space rather than on ray tracing:
                // Auto's whole problem was handing people the expensive backend without being asked.
                default:
                    return BasisRTAOTracingMode.ScreenSpace;
            }
        }

        public static string WriteMode(BasisRTAOTracingMode mode)
        {
            switch (mode)
            {
                case BasisRTAOTracingMode.RayTracedOnly: return ModeRayTraced;
                default: return ModeScreenSpace;
            }
        }

        public static BasisRTAOQuality ReadQuality(string value)
        {
            switch (Normalise(value))
            {
                case "low": return BasisRTAOQuality.Low;
                case "high": return BasisRTAOQuality.High;
                case "ultra": return BasisRTAOQuality.Ultra;
                default: return BasisRTAOQuality.Medium;
            }
        }

        public static string WriteQuality(BasisRTAOQuality quality)
        {
            switch (quality)
            {
                case BasisRTAOQuality.Low: return "Low";
                case BasisRTAOQuality.High: return "High";
                case BasisRTAOQuality.Ultra: return "Ultra";
                default: return "Medium";
            }
        }

        public const string DenoiseOff = "Off";
        public const string DenoiseStandard = "Standard";
        public const string DenoiseHigh = "High";
        public const string DenoiseMaximum = "Maximum";

        public static int ReadDenoisePasses(string value)
        {
            switch (Normalise(value))
            {
                case "off": return 0;
                case "standard": return 1;
                case "high": return 2;
                case "maximum": return 3;
                default: return 2;
            }
        }

        public static string WriteDenoisePasses(int passes)
        {
            switch (Mathf.Clamp(passes, 0, 3))
            {
                case 0: return DenoiseOff;
                case 1: return DenoiseStandard;
                case 3: return DenoiseMaximum;
                default: return DenoiseHigh;
            }
        }

        public const string ApplyLighting = "Lighting";
        public const string ApplyFinalImage = "Final Image";

        public static readonly string[] DebugStageNames =
        {
            "Final", "Raw", "Temporal", "Denoised", "Position", "Normal"
        };

        public static BasisRTAODebugStage ReadDebugStage(string value)
        {
            switch (Normalise(value))
            {
                case "raw": return BasisRTAODebugStage.Raw;
                case "temporal": return BasisRTAODebugStage.Temporal;
                case "denoised": return BasisRTAODebugStage.Denoised;
                case "position": return BasisRTAODebugStage.Position;
                case "normal": return BasisRTAODebugStage.Normal;
                default: return BasisRTAODebugStage.Final;
            }
        }

        public static string WriteDebugStage(BasisRTAODebugStage stage)
        {
            int index = (int)stage;
            return index >= 0 && index < DebugStageNames.Length ? DebugStageNames[index] : DebugStageNames[0];
        }

        public static BasisRTAOApplyMode ReadApplyMode(string value)
        {
            switch (Normalise(value))
            {
                case "final image":
                case "finalimage":
                case "afteropaque":
                case "after opaque": return BasisRTAOApplyMode.AfterOpaque;
                default: return BasisRTAOApplyMode.Lighting;
            }
        }

        public static string WriteApplyMode(BasisRTAOApplyMode mode)
        {
            return mode == BasisRTAOApplyMode.AfterOpaque ? ApplyFinalImage : ApplyLighting;
        }

        /// <summary>
        /// Which layers the trace walks, as a preset rather than a raw mask - the settings panel has no
        /// multi-select control, and the three sets below are the ones that differ in what a player can
        /// actually see. Anything finer belongs on the renderer feature, where a mask field already exists.
        /// </summary>
        public static UnityEngine.LayerMask ReadLayers(string value)
        {
            switch (Normalise(value))
            {
                case "world": return BasisRTAOSceneSettings.WorldLayers;
                case "world and avatars": return BasisRTAOSceneSettings.EverythingButInterfaceLayers;
                // Written by earlier builds. "Avatars And World" was the same intent under a different
                // name, and "Everything" was already interface-filtered, so both are the widest set.
                case "avatars and world":
                case "everything": return BasisRTAOSceneSettings.EverythingButInterfaceLayers;
                default: return BasisRTAOSceneSettings.AvatarLayers;
            }
        }

        public static string WriteLayers(UnityEngine.LayerMask mask)
        {
            if (mask.value == BasisRTAOSceneSettings.EverythingButInterfaceLayers.value) { return "World And Avatars"; }
            if (mask.value == BasisRTAOSceneSettings.WorldLayers.value) { return "World"; }
            return "Avatars";
        }

        /// <summary>
        /// Static and Dynamic still read, because a settings file written before they were removed is just
        /// text on disk and will keep saying one of them for as long as that file survives. They land on
        /// Proxy rather than Off: both meant "put avatars in the structure", Proxy is how that is done now,
        /// and answering Off would quietly take avatar occlusion away from everyone who had asked for it.
        /// </summary>
        public static BasisRTAOSkinnedMode ReadSkinnedMode(string value)
        {
            switch (Normalise(value))
            {
                case "static":
                case "dynamic":
                case "proxy": return BasisRTAOSkinnedMode.Proxy;
                default: return BasisRTAOSkinnedMode.Off;
            }
        }

        public static string WriteSkinnedMode(BasisRTAOSkinnedMode mode)
        {
            switch (mode)
            {
                case BasisRTAOSkinnedMode.Proxy: return "Proxy";
                default: return "Off";
            }
        }

        private static string Normalise(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }
    }
}
