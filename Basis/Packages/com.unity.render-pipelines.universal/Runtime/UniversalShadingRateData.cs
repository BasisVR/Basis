using System;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.Universal
{
    // Basis fork addition. Carries a per-frame variable-rate-shading image produced
    // by BasisVariableRateShadingFeature into every camera-resolution raster pass that
    // draws with depth. Binding the same image on all of them keeps RenderGraph native
    // render pass merging intact and the prepass/opaque rates consistent for depth priming.
    // APIs with image-based VRS (D3D12, Vulkan) bind it as the pass's shading rate attachment.
    // D3D11 has no such API, so there a native plugin (nativeRenderEvent) applies the image
    // and each pass switches it on and off around its own draws.
    public class UniversalShadingRateData : ContextItem
    {
        public const int NativeEventEnable = 1;
        public const int NativeEventDisable = 2;

        public static IntPtr nativeRenderEvent;

        public TextureHandle shadingRateImage;
        public IntPtr nativeShadingRateImage;
        public bool isValid;

        public override void Reset()
        {
            shadingRateImage = TextureHandle.nullHandle;
            nativeShadingRateImage = IntPtr.Zero;
            isValid = false;
        }

        // Desktop only; skipped on XR hardware foveation so we never override native foveated rendering.
        // Returns the native image the pass hands to BeginNative/EndNative, zero when there is nothing to do there.
        public static IntPtr TryBind(IRasterRenderGraphBuilder builder, ContextContainer frameData)
        {
#if !UNITY_ANDROID
            if (frameData == null || !frameData.Contains<UniversalShadingRateData>()) return IntPtr.Zero;
            var data = frameData.Get<UniversalShadingRateData>();
            if (!data.isValid || !data.shadingRateImage.IsValid()) return IntPtr.Zero;
            var cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.xr.enabled && cameraData.xr.supportsFoveatedRendering) return IntPtr.Zero;
            if (data.nativeShadingRateImage != IntPtr.Zero)
            {
                if (nativeRenderEvent == IntPtr.Zero) return IntPtr.Zero;
                builder.UseTexture(data.shadingRateImage, AccessFlags.Read);
                return data.nativeShadingRateImage;
            }
            builder.SetShadingRateImageAttachment(data.shadingRateImage);
            builder.SetShadingRateCombiner(ShadingRateCombinerStage.Fragment, ShadingRateCombiner.Override);
#endif
            return IntPtr.Zero;
        }

        // D3D11 (NVAPI): always pair BeginNative with EndNative in the same render function; a pass that
        // returns between them leaves VRS on for whatever the render thread runs next.
        public static void BeginNative(RasterCommandBuffer cmd, IntPtr nativeImage)
        {
            if (nativeImage != IntPtr.Zero) cmd.IssuePluginEventAndData(nativeRenderEvent, NativeEventEnable, nativeImage);
        }

        public static void EndNative(RasterCommandBuffer cmd, IntPtr nativeImage)
        {
            if (nativeImage != IntPtr.Zero) cmd.IssuePluginEvent(nativeRenderEvent, NativeEventDisable);
        }
    }
}
