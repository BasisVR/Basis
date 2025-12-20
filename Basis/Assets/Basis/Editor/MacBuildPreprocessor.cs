using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Basis.Editor
{
    /// <summary>
    /// Build preprocessor that automatically switches to Metal-compatible URP settings
    /// when building for Apple platforms (macOS, iOS, tvOS, visionOS, watchOS).
    /// Metal has a 14 cbuffer slot limit that requires disabling some URP features.
    /// </summary>
    public class MacBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        private const string METAL_URP_ASSET_GUID = "981a7d8f014944bd79e454270cbd4bda";
        private const string WINDOWS_URP_ASSET_GUID = "7b7fd9122c28c4d15b667c7040e3b3fd";

        private RenderPipelineAsset originalAsset;

        public void OnPreprocessBuild(BuildReport report)
        {
            // Apply Metal-compatible settings for all Apple platforms
            bool isMetalPlatform = report.summary.platform == BuildTarget.StandaloneOSX ||
                                   report.summary.platform == BuildTarget.iOS ||
                                   report.summary.platform == BuildTarget.tvOS ||
                                   report.summary.platform == BuildTarget.VisionOS;

            if (isMetalPlatform)
            {
                string platformName = report.summary.platform.ToString();
                Debug.Log($"[MetalBuildPreprocessor] Detected {platformName} build target. Switching to Metal-compatible URP settings...");

                // Get the Metal-compatible URP asset
                string metalAssetPath = AssetDatabase.GUIDToAssetPath(METAL_URP_ASSET_GUID);
                RenderPipelineAsset metalAsset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(metalAssetPath);

                if (metalAsset != null)
                {
                    // Store original for restoration (if needed)
                    originalAsset = QualitySettings.renderPipeline;

                    // Set Metal-compatible URP asset
                    QualitySettings.renderPipeline = metalAsset;
                    GraphicsSettings.defaultRenderPipeline = metalAsset;

                    Debug.Log($"[MetalBuildPreprocessor] Successfully switched to Metal URP asset: {metalAssetPath}");
                    Debug.Log("[MetalBuildPreprocessor] Features disabled for Metal cbuffer compatibility:");
                    Debug.Log("  - Probe Volumes (using legacy lightprobes instead)");
                    Debug.Log("  - Reflection Probe Blending/Box Projection/Atlas");
                    Debug.Log("  - Light Cookies");
                    Debug.Log("  - LOD Cross Fade");
                }
                else
                {
                    Debug.LogError($"[MetalBuildPreprocessor] Could not find Metal URP asset at path: {metalAssetPath}");
                    Debug.LogError($"[MetalBuildPreprocessor] {platformName} build may fail with Metal cbuffer overflow!");
                }
            }
        }
    }
}
