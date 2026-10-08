using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.Rendering
{
    [DisallowMultipleRendererFeature("Basis Unity Lighting Settings")]
    [Tooltip("Writes the player's settings for Unity's ambient occlusion, global illumination and reflections onto the volume stack. It has to sit above those features in the list: URP blends the volumes, then runs each feature in order, so this is the point after the world's volumes and before Unity's effects read them.")]
    public sealed class BasisUnityLightingFeature : ScriptableRendererFeature
    {
        public override void Create()
        {
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            BasisUnityLightingSettings.ApplyToStack(VolumeManager.instance.stack, renderingData.cameraData.xr.singlePassEnabled);
        }
    }
}
