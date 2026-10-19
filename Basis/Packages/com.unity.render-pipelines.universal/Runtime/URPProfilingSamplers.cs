using Unity.Profiling.LowLevel;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Static profiler marker declarations for the Universal Render Pipeline.
    /// Each field is a pre-allocated <see cref="ProfilingSampler"/>.
    /// </summary>
    public static class URPProfilingSamplers
    {
        // CPU

        /// <summary>
        /// Measures total URP rendering time across all cameras in a frame, including culling,
        /// render graph recording, compilation, and execution on the render thread.
        /// </summary>
        public static readonly ProfilingSampler UniversalRenderTotal = ProfilingSampler.Create(nameof(UniversalRenderTotal), MarkerFlags.Default);

        /// <summary>
        /// Evaluates and interpolates Volume component overrides for the current camera,
        /// blending parameters like exposure, color grading, and fog based on camera position.
        /// </summary>
        public static readonly ProfilingSampler UpdateVolumeFramework = ProfilingSampler.Create(nameof(UpdateVolumeFramework), MarkerFlags.Default);

        /// <summary>
        /// Renders the full camera stack for a base camera, including all overlay cameras,
        /// their individual render graph passes, and final compositing to the target display.
        /// </summary>
        public static readonly ProfilingSampler RenderCameraStack = ProfilingSampler.Create(nameof(RenderCameraStack), MarkerFlags.Default);

        // GPU

        /// <summary>
        /// Renders shadow maps for all additional (point, spot, and area) lights that cast shadows,
        /// including atlas packing and per-light shadow draw calls.
        /// </summary>
        public static readonly ProfilingSampler AdditionalLightsShadow = ProfilingSampler.Create(nameof(AdditionalLightsShadow), MarkerFlags.Default);

        /// <summary>
        /// Generates the 3D color grading lookup table from Volume-driven color adjustments
        /// (white balance, tone mapping, channel mixer, lift/gamma/gain, curves).
        /// </summary>
        public static readonly ProfilingSampler ColorGradingLUT = ProfilingSampler.Create(nameof(ColorGradingLUT), MarkerFlags.Default);

        /// <summary>
        /// Copies the camera color render target to a temporary texture so that subsequent
        /// passes (e.g. distortion, refraction) can sample the scene color.
        /// </summary>
        public static readonly ProfilingSampler CopyColor = ProfilingSampler.Create(nameof(CopyColor), MarkerFlags.Default);

        /// <summary>
        /// Copies the camera depth buffer to a shader-readable texture for passes that need
        /// depth sampling (soft particles, screen-space effects, depth-based compositing).
        /// </summary>
        public static readonly ProfilingSampler CopyDepth = ProfilingSampler.Create(nameof(CopyDepth), MarkerFlags.Default);

        /// <summary>
        /// Renders scene geometry into a depth-normal G-buffer used by screen-space effects
        /// such as SSAO and SSR that require both depth and surface normal information.
        /// </summary>
        public static readonly ProfilingSampler DrawDepthNormalPrepass = ProfilingSampler.Create(nameof(DrawDepthNormalPrepass), MarkerFlags.Default);

        /// <summary>
        /// Renders scene geometry into a depth-only buffer before the main color pass,
        /// enabling early-Z rejection and depth-dependent effects without normal data.
        /// </summary>
        public static readonly ProfilingSampler DepthPrepass = ProfilingSampler.Create(nameof(DepthPrepass), MarkerFlags.Default);

        /// <summary>
        /// Updates the reflection probe cubemap atlas by re-rendering or copying probe cubemaps
        /// that have changed, maintaining the shared atlas used for specular reflections.
        /// </summary>
        public static readonly ProfilingSampler UpdateReflectionProbeAtlas = ProfilingSampler.Create(nameof(UpdateReflectionProbeAtlas), MarkerFlags.Default);

        // DrawObjectsPass

        /// <summary>
        /// Draws all opaque renderers sorted front-to-back, executing the ForwardLit or
        /// UniversalForward shader passes with lighting, shadows, and material evaluation.
        /// </summary>
        public static readonly ProfilingSampler DrawOpaqueObjects = ProfilingSampler.Create(nameof(DrawOpaqueObjects), MarkerFlags.Default);

        /// <summary>
        /// Draws all transparent renderers sorted back-to-front, executing forward shader passes
        /// with alpha blending, lighting, and refraction against the opaque scene color.
        /// </summary>
        public static readonly ProfilingSampler DrawTransparentObjects = ProfilingSampler.Create(nameof(DrawTransparentObjects), MarkerFlags.Default);

        /// <summary>
        /// Renders screen-space UI elements (Canvas set to Screen Space - Overlay) into
        /// the final render target after all scene rendering and post-processing is complete.
        /// </summary>
        public static readonly ProfilingSampler DrawScreenSpaceUI = ProfilingSampler.Create(nameof(DrawScreenSpaceUI), MarkerFlags.Default);

        // Full Record Render Graph

        /// <summary>
        /// Records all render graph passes for the current camera by walking the renderer's
        /// pass list and adding resource declarations, dependencies, and execution callbacks.
        /// </summary>
        public static readonly ProfilingSampler RecordRenderGraph = ProfilingSampler.Create(nameof(RecordRenderGraph), MarkerFlags.Default);

        /// <summary>
        /// Uploads light cookie textures (projected patterns for point, spot, and directional lights)
        /// into the cookie atlas and updates the GPU buffer with cookie UV transforms.
        /// </summary>
        public static readonly ProfilingSampler LightCookies = ProfilingSampler.Create(nameof(LightCookies), MarkerFlags.Default);

        /// <summary>
        /// Renders the cascaded shadow map for the main directional light, including cascade
        /// splitting, per-cascade culling, and shadow draw calls into the shadow atlas.
        /// </summary>
        public static readonly ProfilingSampler MainLightShadow = ProfilingSampler.Create(nameof(MainLightShadow), MarkerFlags.Default);

        /// <summary>
        /// Computes Screen-Space Ambient Occlusion by sampling depth-normals to estimate how much
        /// ambient light reaches each pixel, then applies bilateral blur for noise reduction.
        /// </summary>
        public static readonly ProfilingSampler SSAO = ProfilingSampler.Create(nameof(SSAO), MarkerFlags.Default);

        /// <summary>
        /// Computes Screen-Space Reflections by ray-marching against the depth buffer to find
        /// reflected geometry, then compositing reflected color with fallback probe reflections.
        /// </summary>
        public static readonly ProfilingSampler SSR = ProfilingSampler.Create(nameof(SSR), MarkerFlags.Default);

        // PostProcessPass

        /// <summary>
        /// Renders per-object and camera motion vectors into a screen-space velocity buffer
        /// used by temporal anti-aliasing, motion blur, and other temporal effects.
        /// </summary>
        public static readonly ProfilingSampler DrawMotionVectors = ProfilingSampler.Create(nameof(DrawMotionVectors), MarkerFlags.Default);

        /// <summary>
        /// Performs the final blit from the internal color target to the back buffer, applying
        /// any required color space conversion, resolution scaling, or HDR output encoding.
        /// </summary>
        public static readonly ProfilingSampler BlitFinalToBackBuffer = ProfilingSampler.Create(nameof(BlitFinalToBackBuffer), MarkerFlags.Default);

        /// <summary>
        /// Draws the skybox using the camera's skybox material or the global RenderSettings skybox,
        /// rendering behind all scene geometry at the far depth plane.
        /// </summary>
        public static readonly ProfilingSampler DrawSkybox = ProfilingSampler.Create(nameof(DrawSkybox), MarkerFlags.Default);

        // PostProcessPass â€” top-level pass markers

        /// <summary>
        /// Replaces NaN and infinity pixel values with black to prevent visual artifacts
        /// from propagating through subsequent post-processing passes.
        /// </summary>
        public static readonly ProfilingSampler StopNaNs = ProfilingSampler.Create(nameof(StopNaNs), MarkerFlags.Default);

        /// <summary>
        /// Detects edges using luma or color differences for the first pass of SMAA,
        /// writing edge data to a stencil-masked render target.
        /// </summary>
        public static readonly ProfilingSampler SMAAEdgeDetection = ProfilingSampler.Create(nameof(SMAAEdgeDetection), MarkerFlags.Default);

        /// <summary>
        /// Computes per-edge blend weights by searching for crossing patterns along
        /// detected edges in the second pass of SMAA.
        /// </summary>
        public static readonly ProfilingSampler SMAABlendWeight = ProfilingSampler.Create(nameof(SMAABlendWeight), MarkerFlags.Default);

        /// <summary>
        /// Performs the final neighborhood blending pass of Subpixel Morphological Anti-Aliasing,
        /// compositing anti-aliased edges from the blend weight texture into the color target.
        /// </summary>
        public static readonly ProfilingSampler SMAANeighborhoodBlend = ProfilingSampler.Create(nameof(SMAANeighborhoodBlend), MarkerFlags.Default);

        /// <summary>
        /// Applies Gaussian-blur depth of field by separably blurring far and near planes
        /// based on circle-of-confusion, then compositing the result onto the color target.
        /// </summary>
        public static readonly ProfilingSampler GaussianDepthOfField = ProfilingSampler.Create(nameof(GaussianDepthOfField), MarkerFlags.Default);

        /// <summary>
        /// Applies bokeh depth of field by scattering circle-of-confusion discs per pixel,
        /// simulating a camera lens with a wide aperture for out-of-focus areas.
        /// </summary>
        public static readonly ProfilingSampler BokehDepthOfField = ProfilingSampler.Create(nameof(BokehDepthOfField), MarkerFlags.Default);

        /// <summary>
        /// Blurs pixels along their per-pixel motion vectors to simulate camera or object
        /// motion blur across the frame exposure interval.
        /// </summary>
        public static readonly ProfilingSampler MotionBlur = ProfilingSampler.Create(nameof(MotionBlur), MarkerFlags.Default);

        /// <summary>
        /// Applies Panini projection to reduce peripheral distortion at wide field-of-view
        /// angles while preserving straight vertical lines in the rendered image.
        /// </summary>
        public static readonly ProfilingSampler PaniniProjection = ProfilingSampler.Create(nameof(PaniniProjection), MarkerFlags.Default);

        /// <summary>
        /// Executes the combined uber post-processing shader that applies tonemapping, color grading,
        /// vignette, film grain, dithering, and other per-pixel adjustments in a single full-screen pass.
        /// </summary>
        public static readonly ProfilingSampler UberPostProcess = ProfilingSampler.Create(nameof(UberPostProcess), MarkerFlags.Default);

        /// <summary>
        /// Generates bloom mipmap chain by progressively downsampling bright areas of the image,
        /// then upsampling and accumulating glow back into the color target.
        /// </summary>
        public static readonly ProfilingSampler Bloom = ProfilingSampler.Create(nameof(Bloom), MarkerFlags.Default);

        /// <summary>
        /// Computes occlusion values for data-driven lens flares by sampling depth around
        /// each light screen position to determine flare visibility.
        /// </summary>
        public static readonly ProfilingSampler LensFlareDataDrivenComputeOcclusion = ProfilingSampler.Create(nameof(LensFlareDataDrivenComputeOcclusion), MarkerFlags.Default);

        /// <summary>
        /// Renders data-driven lens flare elements based on LensFlareDataSRP assets attached
        /// to lights, compositing flare ghosts, halos, and streaks onto the screen.
        /// </summary>
        public static readonly ProfilingSampler LensFlareDataDriven = ProfilingSampler.Create(nameof(LensFlareDataDriven), MarkerFlags.Default);

        /// <summary>
        /// Generates screen-space lens flares from bright areas in the image by downsampling,
        /// chromatic-shifting, and compositing streaks and ghosts.
        /// </summary>
        public static readonly ProfilingSampler LensFlareScreenSpace = ProfilingSampler.Create(nameof(LensFlareScreenSpace), MarkerFlags.Default);

        // PostProcessPass RenderGraph â€” hidden from Rendering Debugger Detailed Stats

        /// <summary>
        /// Prepares SMAA material properties and keywords for the render graph SMAA passes.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler SMAAMaterialSetup = ProfilingSampler.Create(nameof(SMAAMaterialSetup), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Initializes depth of field state and allocates temporary textures for the RG DoF passes.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler SetupDoF = ProfilingSampler.Create(nameof(SetupDoF), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Computes per-pixel circle-of-confusion values from depth for the depth of field effect.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler DOFComputeCOC = ProfilingSampler.Create(nameof(DOFComputeCOC), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Downscales and prefilters the color buffer to prepare for the depth of field blur passes.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler DOFDownscalePrefilter = ProfilingSampler.Create(nameof(DOFDownscalePrefilter), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Applies horizontal separable Gaussian blur for the Gaussian depth of field effect.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler DOFBlurH = ProfilingSampler.Create(nameof(DOFBlurH), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Applies vertical separable Gaussian blur for the Gaussian depth of field effect.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler DOFBlurV = ProfilingSampler.Create(nameof(DOFBlurV), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Scatters bokeh-shaped blur kernels per pixel for the bokeh depth of field effect.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler DOFBlurBokeh = ProfilingSampler.Create(nameof(DOFBlurBokeh), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Applies a post-filter pass to clean up artifacts from the depth of field blur.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler DOFPostFilter = ProfilingSampler.Create(nameof(DOFPostFilter), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Composites the blurred depth of field result back onto the sharp in-focus color target.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler DOFComposite = ProfilingSampler.Create(nameof(DOFComposite), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Executes the temporal anti-aliasing resolve pass, jitter-correcting and blending
        /// the current frame with the reprojected history buffer to reduce aliasing.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler TAA = ProfilingSampler.Create(nameof(TAA), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Copies the resolved TAA output into the history buffer for use in the next frame's reprojection.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler TAACopyHistory = ProfilingSampler.Create(nameof(TAACopyHistory), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Initializes bloom state, allocates mip-chain textures, and sets thresholds for the RG bloom passes.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler BloomSetup = ProfilingSampler.Create(nameof(BloomSetup), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Applies brightness threshold and downsamples to the first bloom mip level.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler BloomPrefilter = ProfilingSampler.Create(nameof(BloomPrefilter), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Progressively downsamples the bloom mip chain to spread glow over wider screen areas.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler BloomDownsample = ProfilingSampler.Create(nameof(BloomDownsample), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Upsamples and accumulates the bloom mip chain back to full resolution with additive blending.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler BloomUpsample = ProfilingSampler.Create(nameof(BloomUpsample), MarkerFlags.VerbosityAdvanced);

        /// <summary>
        /// Prepares bloom contribution textures and parameters for the uber post-processing pass.
        /// </summary>
        [HideInDebugUI] public static readonly ProfilingSampler UberPostSetupBloomPass = ProfilingSampler.Create(nameof(UberPostSetupBloomPass), MarkerFlags.VerbosityAdvanced);
    }
}
