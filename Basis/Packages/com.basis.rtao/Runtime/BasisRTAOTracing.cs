using UnityEngine;

namespace Basis.Rendering.RTAO
{
    /// <summary>
    /// Which backend the occlusion is gathered with.
    ///
    /// Auto is gone. It resolved to hardware ray tracing wherever the device offered it, which meant the
    /// answer to "what is this setting doing" was different on every machine, and the expensive path was
    /// the one people got without asking for it. The choice is now explicit.
    ///
    /// The remaining values keep their old numbers rather than closing the gap, because the mode is
    /// serialized on BasisRTAOFeature. An asset written before this holds a 0 where Auto used to be; that
    /// is not a value any more, so it falls through Resolve and ReadMode to Screen Space, which is where
    /// the default now sits.
    /// </summary>
    public enum BasisRTAOTracingMode
    {
        RayTracedOnly = 1,
        ScreenSpace = 2,
        ComputeBvh = 3
    }

    /// <summary>
    /// Which buffer the debug view draws. The occlusion goes through several stages before it reaches the
    /// image, and an artifact looks the same in the final picture whichever stage introduced it. Stepping
    /// through these says where it first appears, which is the difference between finding a bug and guessing
    /// at one.
    /// </summary>
    public enum BasisRTAODebugStage
    {
        /// <summary>The composited, full resolution buffer the rest of the frame consumes.</summary>
        Final = 0,

        /// <summary>Straight out of the tracer, before any accumulation. Grainy by nature. An artifact
        /// visible here is in the tracing itself: the rays, the origin bias or the noise.</summary>
        Raw = 1,

        /// <summary>After reprojection against the previous frame. An artifact that appears here but not in
        /// Raw is the history being rejected or misaligned.</summary>
        Temporal = 2,

        /// <summary>After the blur cascade. An artifact that appears here but not in Temporal is the
        /// bilateral weighting, most likely its depth tolerance.</summary>
        Denoised = 3,

        /// <summary>The world position the tracer fired from, shown as a repeating gradient. This is what
        /// the denoiser and the upscale both compare against, so a break in the gradient explains a break in
        /// everything downstream.</summary>
        Position = 4,

        /// <summary>The normal the tracer built its hemisphere around, decoded to colour.</summary>
        Normal = 5
    }

    /// <summary>How the resolved occlusion reaches the image.</summary>
    public enum BasisRTAOApplyMode
    {
        /// <summary>Publish _ScreenSpaceOcclusionTexture and let URP's lighting consume it. Physically
        /// honest, but only shaders that read it are affected, a material's own occlusion map clamps it,
        /// and direct light is only dimmed by Occlusion On Direct Light.</summary>
        Lighting = 0,

        /// <summary>Multiply the finished opaque image by it, the way URP's own SSAO After Opaque does.
        /// Lands on every opaque surface whatever its shader is, ignores material occlusion maps, and dims
        /// direct light and specular that already carry their own shadowing.</summary>
        AfterOpaque = 1
    }

    public enum BasisRTAOBackend
    {
        None = 0,
        Hardware = 1,
        ComputeBvh = 2,
        ScreenSpace = 3
    }

    public static class BasisRTAOTracing
    {
        public static bool IsRayTraced(BasisRTAOBackend backend)
        {
            return backend == BasisRTAOBackend.Hardware || backend == BasisRTAOBackend.ComputeBvh;
        }

        public static BasisRTAOBackend Resolve(BasisRTAOTracingMode mode, bool hardwareSupported, bool computeSupported)
        {
            if (!computeSupported)
                return BasisRTAOBackend.None;

            switch (mode)
            {
                case BasisRTAOTracingMode.RayTracedOnly:
                    // Without hardware ray tracing this degrades to the screen space estimator rather
                    // than to nothing. A "Ray Traced" saved on a Direct3D12 machine used to resolve to
                    // None here on Direct3D11 - the API the estimator exists for - and the None gate in
                    // AddRenderPasses is silent, while the settings menu hides the mode row on such a
                    // device, so the effect was off with no diagnostic and no way back from the UI.
                    // ReportBackendOnce is what tells the player the degrade happened.
                    return hardwareSupported ? BasisRTAOBackend.Hardware : BasisRTAOBackend.ScreenSpace;
                case BasisRTAOTracingMode.ComputeBvh:
                    return BasisRTAOBackend.ComputeBvh;
                // Screen space, and the 0 an asset written before Auto was removed still holds.
                default:
                    return BasisRTAOBackend.ScreenSpace;
            }
        }

        public static BasisRTAOBackend Resolve(BasisRTAOTracingMode mode)
        {
            return Resolve(mode, BasisRTAOContext.HardwareSupported, BasisRTAOContext.ComputeSupported);
        }

        public static string Describe(BasisRTAOBackend backend)
        {
            switch (backend)
            {
                case BasisRTAOBackend.Hardware: return "hardware ray tracing";
                case BasisRTAOBackend.ComputeBvh: return "software BVH ray tracing";
                case BasisRTAOBackend.ScreenSpace: return "screen space fallback";
                default: return "disabled";
            }
        }

        public static float ProjectionScale(Matrix4x4 projection, int targetHeight, bool orthographic)
        {
            if (orthographic)
                return 0f;
            return 0.5f * targetHeight * Mathf.Abs(projection.m11);
        }
    }
}
