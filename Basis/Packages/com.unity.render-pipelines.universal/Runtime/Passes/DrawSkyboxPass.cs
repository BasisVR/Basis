using System;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Draw the skybox into the given color buffer using the given depth buffer for depth testing.
    ///
    /// This pass renders the standard Unity skybox.
    /// </summary>
    public partial class DrawSkyboxPass : ScriptableRenderPass
    {
        /// <summary>
        /// Creates a new <c>DrawSkyboxPass</c> instance.
        /// </summary>
        /// <param name="evt">The <c>RenderPassEvent</c> to use.</param>
        /// <seealso cref="RenderPassEvent"/>
        public DrawSkyboxPass(RenderPassEvent evt)
        {
            profilingSampler = ProfilingSampler.Get(URPProfileId.DrawSkybox);
            renderPassEvent = evt;
        }

        private RendererListHandle CreateSkyBoxRendererList(RenderGraph renderGraph, UniversalCameraData cameraData)
        {
            var skyRendererListHandle = new RendererListHandle();

#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
            {
                // Setup Legacy XR buffer states
                if (cameraData.xr.singlePassEnabled)
                {
                    skyRendererListHandle = renderGraph.CreateSkyboxRendererList(cameraData.camera,
                        cameraData.GetProjectionMatrix(0), cameraData.GetViewMatrix(0),
                        cameraData.GetProjectionMatrix(1), cameraData.GetViewMatrix(1));
                }
                else
                {
                    skyRendererListHandle = renderGraph.CreateSkyboxRendererList(cameraData.camera, cameraData.GetProjectionMatrix(0), cameraData.GetViewMatrix(0));
                }
            }
            else
#endif
            {
                // Mirror reflection cameras use a custom reflected view and an oblique projection
                // for scene geometry. The native camera-only skybox path does not preserve that setup
                // correctly, and the oblique clip must not be applied to an infinitely distant skybox.
                // Basis mirrors store their clean pre-oblique projection in nonJitteredProjectionMatrix.
                if (cameraData.isMirrorReflectionCamera)
                {
                    skyRendererListHandle = renderGraph.CreateSkyboxRendererList(
                        cameraData.camera,
                        cameraData.camera.nonJitteredProjectionMatrix,
                        cameraData.GetViewMatrix(0));
                }
                else
                {
                    skyRendererListHandle = renderGraph.CreateSkyboxRendererList(cameraData.camera);
                }
            }

            return skyRendererListHandle;
        }

        private static void ExecutePass(RasterCommandBuffer cmd, XRPass xr, RendererList rendererList)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            if (xr.enabled && xr.singlePassEnabled)
                cmd.SetSinglePassStereo(SystemInfo.supportsMultiview ? SinglePassStereoMode.Multiview : SinglePassStereoMode.Instancing);
#endif
            cmd.DrawRendererList(rendererList);

#if ENABLE_VR && ENABLE_XR_MODULE
            if (xr.enabled && xr.singlePassEnabled)
                cmd.SetSinglePassStereo(SinglePassStereoMode.None);
#endif
        }

        private class PassData
        {
            internal XRPass xr;
            internal RendererListHandle skyRendererListHandle;
            internal Material material;
        }

        private void InitPassData(ref PassData passData, in XRPass xr, in RendererListHandle handle)
        {
            passData.xr = xr;
            passData.skyRendererListHandle = handle;
        }

        internal void Render(RenderGraph renderGraph, ContextContainer frameData, ScriptableRenderContext context, in TextureHandle colorTarget, in TextureHandle depthTarget, Material skyboxMaterial)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            var activeDebugHandler = GetActiveDebugHandler(cameraData);
            if (activeDebugHandler != null)
            {
                // TODO: The skybox needs to work the same as the other shaders, but until it does we'll not render it
                // when certain debug modes are active (e.g. wireframe/overdraw modes)
                if (activeDebugHandler.IsScreenClearNeeded)
                {
                    return;
                }
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
            {
                var skyRendererListHandle = CreateSkyBoxRendererList(renderGraph, cameraData);
                InitPassData(ref passData, cameraData.xr, skyRendererListHandle);
                passData.material = skyboxMaterial;
                builder.UseRendererList(skyRendererListHandle);
                builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.ReadWrite);

                builder.AllowPassCulling(false);
                if (cameraData.xr.enabled)
                {
                    bool passSupportsFoveation = cameraData.xrUniversal.canFoveateIntermediatePasses || resourceData.isActiveTargetBackBuffer;
                    builder.EnableFoveatedRasterization(cameraData.xr.supportsFoveatedRendering && passSupportsFoveation);
                    // Apply MultiviewRenderRegionsCompatible flag only to the peripheral view in Quad Views
                    if (cameraData.xr.multipassId == 0)
                    {
                        builder.SetExtendedFeatureFlags(ExtendedFeatureFlags.MultiviewRenderRegionsCompatible);
                    }
                }

#if !UNITY_ANDROID
                // Basis VRS injection: the peripheral skybox shades at the coarse rate too.
                // Skipped on XR hardware foveation so we never override native foveated rendering.
                if (frameData.Contains<UniversalShadingRateData>())
                {
                    var basisVrs = frameData.Get<UniversalShadingRateData>();
                    bool xrFoveated = cameraData.xr.enabled && cameraData.xr.supportsFoveatedRendering;
                    if (basisVrs.isValid && basisVrs.shadingRateImage.IsValid() && !xrFoveated)
                    {
                        builder.SetShadingRateImageAttachment(basisVrs.shadingRateImage);
                        builder.SetShadingRateCombiner(ShadingRateCombinerStage.Fragment, ShadingRateCombiner.Override);
                    }
                }
#endif

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    ExecutePass(context.cmd, data.xr, data.skyRendererListHandle);
                });
            }
        }
    }
}
