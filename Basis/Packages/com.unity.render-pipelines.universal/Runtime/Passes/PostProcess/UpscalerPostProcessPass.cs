using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using System.Runtime.CompilerServices; // AggressiveInlining

namespace UnityEngine.Rendering.Universal
{
    internal sealed class UpscalerPostProcessPass : PostProcessPass
    {
        private static readonly ProfilingSampler k_ReactiveMaskProfilingSampler = new ProfilingSampler("Upscaler Reactive Mask");

        public const string k_UpscaledColorTargetName = "CameraColorUpscaled";
        Texture2D[] m_BlueNoise16LTex;
        Material m_UpscalerReactiveMaskMaterial;
        bool m_IsValid;

#if ENABLE_UPSCALER_FRAMEWORK
        private static readonly ProfilingSampler k_ViewCopyProfilingSampler = new ProfilingSampler("Upscaler View Copy");
        bool m_WarnedHardwareDrsTemporalUnsupported;
        bool m_WarnedMissingMotionData;
        bool m_WarnedMissingOpaqueTexture;
        bool m_WarnedOpaqueDownsamplingNotCompatible;
#endif

        public UpscalerPostProcessPass(Shader reactiveMaskShader, Texture2D[] blueNoise16LTex)
        {
            this.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing - 1;
            this.profilingSampler = null;   // Use default name
            m_BlueNoise16LTex = blueNoise16LTex;

            m_UpscalerReactiveMaskMaterial = PostProcessUtils.LoadShader(reactiveMaskShader, passName, logLevel: LogType.Log);

            m_IsValid = m_BlueNoise16LTex != null && m_BlueNoise16LTex.Length > 0
                && m_UpscalerReactiveMaskMaterial != null;
        }

        public override void Dispose()
        {
            CoreUtils.Destroy(m_UpscalerReactiveMaskMaterial);

            m_IsValid = false;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
#if ENABLE_UPSCALER_FRAMEWORK
            if (!m_IsValid)
                return;

            UniversalPostProcessingData postProcessingData = frameData.Get<UniversalPostProcessingData>();
            if (postProcessingData.activeUpscaler == null)
                return;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.imageScalingMode != ImageScalingMode.Upscaling)
                return;

            // Skip temporal upscaling when the camera uses hardware DRS (ScalableBufferManager). A temporal upscaler
            // reconstructs to full resolution mid-frame, but ScalableBufferManager is a single global scale with no per-stage
            // render->display transition, so the post-upscale chain (UberPost, final blit) keeps writing into the
            // ScalableBufferManager-scaled sub-rect and only that sub-rect of the screen updates.
            // Gate on camera.allowDynamicResolution (the stable opt-in), not the live ScalableBufferManager factor,
            // which would flip per-frame as the app crosses factor 1.0.
            if (cameraData.camera.allowDynamicResolution && postProcessingData.activeUpscaler.isTemporal)
            {
                if (Debug.isDebugBuild && !m_WarnedHardwareDrsTemporalUnsupported)
                {
                    m_WarnedHardwareDrsTemporalUnsupported = true;
                    Debug.LogWarning(
                        "Hardware Dynamic Resolution (Allow Dynamic Resolution / ScalableBufferManager) is not supported " +
                        $"with the temporal upscaler '{postProcessingData.activeUpscaler.name}' in URP yet (in any " +
                        "Resolution Mode); the upscaler is skipped and the camera falls back to hardware DRS without " +
                        "temporal upscaling. To use the temporal upscaler, disable Allow Dynamic Resolution on the camera" +
                        " and control the resolution via Render Scale or the upscaler's quality mode if available.");
                }
                return;
            }
            // Left the skip path: reset so re-entering it warns again.
            if (Debug.isDebugBuild)
                m_WarnedHardwareDrsTemporalUnsupported = false;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            var sourceTexture = resourceData.cameraColor;
            var srcDesc = sourceTexture.GetDescriptor(renderGraph);

            // Create a context item containing upscaling inputs
            UpscalingIO io = frameData.Create<UpscalingIO>();
            io.cameraColor = sourceTexture;
            io.cameraDepth = resourceData.cameraDepth;
            io.motionVectorColor = resourceData.motionVectorColor;
            io.motionVectorDomain = UpscalingIO.MotionVectorDomain.NDC;
            io.motionVectorDirection = UpscalingIO.MotionVectorDirection.PreviousFrameToCurrentFrame;
            io.jitteredMotionVectors = false; // URP has no jittering in MVs
            // Exposure left at UpscalingIO defaults (null texture, preExposureValue 1.0): URP handles pre-exposure in-pipeline, so the upscaler stays exposure-neutral.
            io.hdrDisplayInformation = cameraData.isHDROutputActive ? cameraData.hdrDisplayInformation : new HDROutputUtils.HDRDisplayInformation(-1, -1, -1, 160.0f);
            io.postUpscaleResolution = new Vector2Int(cameraData.pixelWidth, cameraData.pixelHeight);

            // Report DRS as active whenever the camera is aliased by hardware DRS / ScalableBufferManager (SBM)
            // (descriptor.useDynamicScale), not only when the
            // current factor is < 1.0: the upscaler bakes its dynamic-resolution state at context-creation time (e.g.
            // FSR2's EnableDynamicResolution flag) and isn't recreated when the factor changes, so a context created at
            // factor 1.0 would otherwise run with DRS off while later receiving a varying sub-rect. Use the SBM scale
            // captured on cameraData at setup, not the live global, which another pass could mutate before this point.
            Vector2 hwDrsScale = cameraData.hardwareDynamicResolutionScale;
            bool cameraUsesHardwareDrs = cameraData.cameraTargetDescriptor.useDynamicScale;
            io.dynamicResolution = cameraUsesHardwareDrs ? DynamicResolutionType.Hardware : (DynamicResolutionType?)null;
            // preUpscaleResolution is the actually-rendered region (the descriptor scaled by the SBM factor), not the
            // full descriptor allocation, so the upscaler reconstructs from what was rendered.
            io.preUpscaleResolution = cameraUsesHardwareDrs
                ? new Vector2Int(
                    Mathf.CeilToInt(hwDrsScale.x * cameraData.cameraTargetDescriptor.width),
                    Mathf.CeilToInt(hwDrsScale.y * cameraData.cameraTargetDescriptor.height))
                : new Vector2Int(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);

            // The max render size = the full camera-target allocation (the descriptor). Under SBM the rendered region
            // (preUpscaleResolution) is a sub-rect of it; without DRS they're equal. Upscalers allocate history at this.
            io.maxPreUpscaleResolution = new Vector2Int(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);

            cameraData.camera.TryGetComponent<UniversalAdditionalCameraData>(out var additionalCameraData);
            MotionVectorsPersistentData motionData = additionalCameraData != null ? additionalCameraData.motionVectorsPersistentData : null;
            if (motionData == null) // Per-camera motion data is required for upscaling (camera matrices, positions, previous-frame sizes).
            {
                if (Debug.isDebugBuild && !m_WarnedMissingMotionData)
                {
                    m_WarnedMissingMotionData = true;
                    Debug.LogWarning("UpscalerPostProcessPass: camera has no UniversalAdditionalCameraData/" +
                        "MotionVectorsPersistentData, which the temporal upscaler requires; skipping upscaling for this camera.");
                }
                return;
            }

            if (Debug.isDebugBuild) // Saw valid motion data: reset so a later missing-data camera warns again.
                m_WarnedMissingMotionData = false;

            // Track the previous frame's render resolution for temporal upscalers via the per-camera persistent state.
            io.previousPreUpscaleResolution = motionData.previousPreUpscaleResolution;
            motionData.previousPreUpscaleResolution = io.preUpscaleResolution;

            io.motionVectorTextureSize = io.preUpscaleResolution;
            io.enableTexArray = cameraData.xr.enabled && cameraData.xr.singlePassEnabled;

            io.cameraInstanceID = EntityId.ToULong(cameraData.camera.GetEntityId());
            io.nearClipPlane = cameraData.camera.nearClipPlane;
            io.farClipPlane = cameraData.camera.farClipPlane;
            io.fieldOfViewDegrees = cameraData.camera.fieldOfView;
            io.invertedDepth = SystemInfo.usesReversedZBuffer;
            io.flippedY = SystemInfo.graphicsUVStartsAtTop;
            io.flippedX = false;
            io.hdrInput = Experimental.Rendering.GraphicsFormatUtility.IsHDRFormat(srcDesc.format);
            io.numActiveViews = cameraData.xr.enabled ? cameraData.xr.viewCount : 1;

            io.projectionMatrices = motionData.projectionStereo;
            io.previousProjectionMatrices = motionData.previousProjectionStereo;
            io.previousPreviousProjectionMatrices = motionData.previousPreviousProjectionStereo;
            io.viewMatrices = motionData.viewStereo;
            io.previousViewMatrices = motionData.previousViewStereo;
            io.previousPreviousViewMatrices = motionData.previousPreviousViewStereo;

            // Per-view world-space camera positions into persistent per-camera buffers (resized only on view-count
            // change) to avoid a per-frame allocation. Mono reuses the stored camera position; each XR eye is at a
            // distinct position, derived from its view matrix via CoreMatrixUtils.GetWorldPositionFromOrthonormalViewMatrix
            // instead of a full inverse.
            var camPosViews = motionData.GetWorldSpaceCameraPosViews(io.numActiveViews);
            var prevCamPosViews = motionData.GetPreviousWorldSpaceCameraPosViews(io.numActiveViews);
            var prevPrevCamPosViews = motionData.GetPreviousPreviousWorldSpaceCameraPosViews(io.numActiveViews);
            if (io.numActiveViews == 1)
            {
                camPosViews[0] = motionData.worldSpaceCameraPos;
                prevCamPosViews[0] = motionData.previousWorldSpaceCameraPos;
                prevPrevCamPosViews[0] = motionData.previousPreviousWorldSpaceCameraPos;
            }
            else
            {
                for (int i = 0; i < io.numActiveViews; i++)
                {
                    camPosViews[i] = CoreMatrixUtils.GetWorldPositionFromOrthonormalViewMatrix(motionData.viewStereo[i]);
                    prevCamPosViews[i] = CoreMatrixUtils.GetWorldPositionFromOrthonormalViewMatrix(motionData.previousViewStereo[i]);
                    prevPrevCamPosViews[i] = CoreMatrixUtils.GetWorldPositionFromOrthonormalViewMatrix(motionData.previousPreviousViewStereo[i]);
                }
            }
            io.worldSpaceCameraPositions = camPosViews;
            io.previousWorldSpaceCameraPositions = prevCamPosViews;
            io.previousPreviousWorldSpaceCameraPositions = prevPrevCamPosViews;
            io.resetHistory = cameraData.resetHistory;
            io.frameIndex = TemporalAA.CalculateTaaFrameIndex(ref cameraData.taaSettings);
            io.deltaTime = motionData.deltaTime;
            io.previousDeltaTime = motionData.lastDeltaTime;
            io.blueNoiseTextureSet = m_BlueNoise16LTex;

            // The motion scaling feature is only active outside of test environments. If we allowed it to run
            // during automated graphics tests, the results of each test run would be dependent on system
            // performance.
#if LWRP_DEBUG_STATIC_POSTFX
            io.enableMotionScaling = false;
#else
            io.enableMotionScaling = true;
#endif

            // Acquire the per-camera context for the active upscaler
            // In XR multi-pass rendering, encode eye information into the camera ID to ensure separate contexts per eye
            var upscaler = postProcessingData.activeUpscaler;
            ulong viewId = io.cameraInstanceID;
            if (cameraData.xr.enabled && !cameraData.xr.singlePassEnabled)
                viewId = (ulong)HashCode.Combine(io.cameraInstanceID, cameraData.xr.multipassId);

            // Fetch the framework-owned options for this upscaler (per-camera overrides are future work).
            UpscalerOptions upscalerOptions = UniversalRenderPipeline.upscaling.GetGlobalOptions(upscaler);

            bool recordPerView = io.enableTexArray && upscaler.isTemporal && upscaler is not STPIUpscaler;
            io.context = recordPerView ? null : UniversalRenderPipeline.upscaling.AcquireContext(
                viewId,
                upscaler,
                upscalerOptions,
                io.postUpscaleResolution
            );

            // Use jitter already computed during camera setup
            io.subpixelJitter = cameraData.subpixelJitter;

            if (RequiresReactiveMaskPass(upscaler))
            {
                if (resourceData.cameraOpaqueTexture.IsValid())
                {
                    io.reactiveMask = UpscalerReactiveMaskPass(renderGraph, frameData, m_UpscalerReactiveMaskMaterial, upscaler.reactiveMaskSource, upscalerOptions, io.enableTexArray);
                    m_WarnedMissingOpaqueTexture = false;
                }
                else if (Debug.isDebugBuild && !m_WarnedMissingOpaqueTexture)
                {
                    Debug.LogWarning("UpscalerPostProcessPass: Opaque Texture is disabled in the current RP asset. Reactive mask generation will be skipped.");
                    m_WarnedMissingOpaqueTexture = true;
                }
            }

            // Per-frame settings (sharpness, etc.); upscalers read these from io.options, not from the context.
            io.options = upscalerOptions;

            // Insert the active upscaler's render graph passes
            if (recordPerView)
                RecordPerView(renderGraph, frameData, io, upscaler, upscalerOptions);
            else
                upscaler.RecordRenderGraph(renderGraph, frameData);

            // Update the camera resolution to reflect the upscaled size
            var dstDesc = io.cameraColor.GetDescriptor(renderGraph);
            UpdateCameraResolution(renderGraph, frameData, new Vector2Int(dstDesc.width, dstDesc.height));

            // Use the output texture of upscaling
            resourceData.cameraColor = io.cameraColor;

            // The negated jitter follows HDRP's sign convention (see HDCamera.GetJitteredProjectionMatrix).
            if (!upscaler.supportsAlphaUpscaling)
            {
                AlphaUpscaleUtils.Execute(renderGraph, cameraData, resourceData, sourceTexture, upscaler.isTemporal,
                    io.preUpscaleResolution, io.postUpscaleResolution, -io.subpixelJitter,
                    io.reactiveMask, upscaler.reactiveMaskSource == ReactiveMaskSource.Stencil);
            }
#endif
        }

        private class UpdateCameraResolutionPassData
        {
            internal Vector2Int newCameraTargetSize;

            // Exactly one of those is set, depending on the renderer this pass runs with.
            internal UniversalGlobalShaderData shaderData;
            internal Universal2DGlobalShaderData shaderData2D;
        }

        // Updates render target descriptors and shader constants to reflect a new render size
        // This should be called immediately after the resolution changes mid-frame (typically after an upscaling operation).
        static internal void UpdateCameraResolution(RenderGraph renderGraph, ContextContainer frameData, Vector2Int newCameraTargetSize)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            // Update the camera data descriptor to reflect post-upscaled sizes
            cameraData.cameraTargetDescriptor.width = newCameraTargetSize.x;
            cameraData.cameraTargetDescriptor.height = newCameraTargetSize.y;

            // Update the shader constants to reflect the new camera resolution
            using (var builder = renderGraph.AddUnsafePass<UpdateCameraResolutionPassData>("Update Camera Resolution", out var passData))
            {
                passData.newCameraTargetSize = newCameraTargetSize;

                if (frameData.Contains<UniversalGlobalShaderData>())
                    passData.shaderData = frameData.Get<UniversalGlobalShaderData>();
                else
                    passData.shaderData2D = frameData.Get<Universal2DGlobalShaderData>();

                // This pass only modifies shader constants
                builder.AllowGlobalStateModification(true);

                // Wrap constant modification into a pass to force graph execution timeline.
                builder.SetRenderFunc(static (UpdateCameraResolutionPassData data, UnsafeGraphContext ctx) =>
                {
                    // _ScreenSize lives in the centralized global shader variables, potentially stored in a global constant buffer.
                    IGlobalShaderVariablesCommonUploader vars;
                    if (data.shaderData != null)
                        vars = data.shaderData.Get();
                    else
                        vars = data.shaderData2D.Get();

                    vars._ScreenSize = new Vector4(
                        data.newCameraTargetSize.x,
                        data.newCameraTargetSize.y,
                        1.0f / data.newCameraTargetSize.x,
                        1.0f / data.newCameraTargetSize.y
                    );
                    vars.PushGlobal(ctx.cmd);
                });
            }
        }

#if ENABLE_UPSCALER_FRAMEWORK
        sealed class ViewInputs
        {
            internal readonly Matrix4x4[] projection = new Matrix4x4[1], previousProjection = new Matrix4x4[1], previousPreviousProjection = new Matrix4x4[1];
            internal readonly Matrix4x4[] view = new Matrix4x4[1], previousView = new Matrix4x4[1], previousPreviousView = new Matrix4x4[1];
            internal readonly Vector3[] position = new Vector3[1], previousPosition = new Vector3[1], previousPreviousPosition = new Vector3[1];
        }

        class ViewCopyPassData
        {
            internal TextureHandle source;
            internal TextureHandle destination;
            internal int sourceSlice;
            internal int destinationSlice;
        }

        ViewInputs[] m_ViewInputs = Array.Empty<ViewInputs>();

        void RecordPerView(RenderGraph renderGraph, ContextContainer frameData, UpscalingIO io, IUpscaler upscaler, UpscalerOptions upscalerOptions)
        {
            int viewCount = io.numActiveViews;
            if (m_ViewInputs.Length < viewCount)
            {
                int allocated = m_ViewInputs.Length;
                Array.Resize(ref m_ViewInputs, viewCount);
                for (int viewIndex = allocated; viewIndex < viewCount; viewIndex++)
                    m_ViewInputs[viewIndex] = new ViewInputs();
            }

            TextureHandle color = io.cameraColor;
            TextureHandle depth = io.cameraDepth;
            TextureHandle motionVectors = io.motionVectorColor;
            TextureHandle reactiveMask = io.reactiveMask;
            Matrix4x4[] projection = io.projectionMatrices, previousProjection = io.previousProjectionMatrices, previousPreviousProjection = io.previousPreviousProjectionMatrices;
            Matrix4x4[] view = io.viewMatrices, previousView = io.previousViewMatrices, previousPreviousView = io.previousPreviousViewMatrices;
            Vector3[] position = io.worldSpaceCameraPositions, previousPosition = io.previousWorldSpaceCameraPositions, previousPreviousPosition = io.previousPreviousWorldSpaceCameraPositions;
            TextureDesc colorDesc = color.GetDescriptor(renderGraph);
            TextureHandle output = TextureHandle.nullHandle;

            io.enableTexArray = false;
            io.numActiveViews = 1;
            for (int viewIndex = 0; viewIndex < viewCount; viewIndex++)
            {
                ViewInputs inputs = m_ViewInputs[viewIndex];
                io.projectionMatrices = SliceView(projection, inputs.projection, viewIndex);
                io.previousProjectionMatrices = SliceView(previousProjection, inputs.previousProjection, viewIndex);
                io.previousPreviousProjectionMatrices = SliceView(previousPreviousProjection, inputs.previousPreviousProjection, viewIndex);
                io.viewMatrices = SliceView(view, inputs.view, viewIndex);
                io.previousViewMatrices = SliceView(previousView, inputs.previousView, viewIndex);
                io.previousPreviousViewMatrices = SliceView(previousPreviousView, inputs.previousPreviousView, viewIndex);
                io.worldSpaceCameraPositions = SliceView(position, inputs.position, viewIndex);
                io.previousWorldSpaceCameraPositions = SliceView(previousPosition, inputs.previousPosition, viewIndex);
                io.previousPreviousWorldSpaceCameraPositions = SliceView(previousPreviousPosition, inputs.previousPreviousPosition, viewIndex);
                io.cameraColor = CopyViewToTexture(renderGraph, color, viewIndex, "_UpscalerViewColor");
                io.cameraDepth = CopyViewToTexture(renderGraph, depth, viewIndex, "_UpscalerViewDepth");
                io.motionVectorColor = motionVectors.IsValid() ? CopyViewToTexture(renderGraph, motionVectors, viewIndex, "_UpscalerViewMotionVectors") : motionVectors;
                io.reactiveMask = reactiveMask.IsValid() ? CopyViewToTexture(renderGraph, reactiveMask, viewIndex, "_UpscalerViewReactiveMask") : reactiveMask;
                io.context = UniversalRenderPipeline.upscaling.AcquireContext((ulong)HashCode.Combine(io.cameraInstanceID, viewIndex), upscaler, upscalerOptions, io.postUpscaleResolution);

                upscaler.RecordRenderGraph(renderGraph, frameData);

                if (!output.IsValid())
                {
                    TextureDesc outputDesc = io.cameraColor.GetDescriptor(renderGraph);
                    outputDesc.dimension = colorDesc.dimension;
                    outputDesc.slices = colorDesc.slices;
                    outputDesc.vrUsage = colorDesc.vrUsage;
                    outputDesc.enableRandomWrite = false;
                    outputDesc.clearBuffer = false;
                    outputDesc.name = k_UpscaledColorTargetName;
                    output = renderGraph.CreateTexture(outputDesc);
                }
                CopySlice(renderGraph, io.cameraColor, 0, output, viewIndex);
            }

            io.cameraColor = output;
            io.cameraDepth = depth;
            io.motionVectorColor = motionVectors;
            io.reactiveMask = reactiveMask;
            io.enableTexArray = true;
            io.numActiveViews = viewCount;
            io.projectionMatrices = projection;
            io.previousProjectionMatrices = previousProjection;
            io.previousPreviousProjectionMatrices = previousPreviousProjection;
            io.viewMatrices = view;
            io.previousViewMatrices = previousView;
            io.previousPreviousViewMatrices = previousPreviousView;
            io.worldSpaceCameraPositions = position;
            io.previousWorldSpaceCameraPositions = previousPosition;
            io.previousPreviousWorldSpaceCameraPositions = previousPreviousPosition;
        }

        static T[] SliceView<T>(T[] source, T[] slice, int viewIndex)
        {
            if (source == null || viewIndex >= source.Length)
                return source;

            slice[0] = source[viewIndex];
            return slice;
        }

        static TextureHandle CopyViewToTexture(RenderGraph renderGraph, TextureHandle source, int viewIndex, string name)
        {
            TextureDesc desc = source.GetDescriptor(renderGraph);
            desc.dimension = TextureDimension.Tex2D;
            desc.slices = 1;
            desc.vrUsage = VRTextureUsage.None;
            desc.clearBuffer = false;
            desc.discardBuffer = false;
            desc.name = name;
            TextureHandle destination = renderGraph.CreateTexture(desc);
            CopySlice(renderGraph, source, viewIndex, destination, 0);
            return destination;
        }

        static void CopySlice(RenderGraph renderGraph, TextureHandle source, int sourceSlice, TextureHandle destination, int destinationSlice)
        {
            using (var builder = renderGraph.AddUnsafePass<ViewCopyPassData>("Upscaler View Copy", out var passData, k_ViewCopyProfilingSampler))
            {
                passData.source = source;
                passData.destination = destination;
                passData.sourceSlice = sourceSlice;
                passData.destinationSlice = destinationSlice;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(destination, destinationSlice == 0 ? AccessFlags.Write : AccessFlags.ReadWrite);
                builder.SetRenderFunc(static (ViewCopyPassData data, UnsafeGraphContext ctx) =>
                    CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd).CopyTexture(data.source, data.sourceSlice, data.destination, data.destinationSlice));
            }
        }

        static internal bool RequiresReactiveMaskPass(IUpscaler upscaler)
        {
            UpscalerOptions upscalerOptions = UniversalRenderPipeline.upscaling.GetGlobalOptions(upscaler);
            return upscalerOptions != null && upscalerOptions.enableReactiveMaskPass && upscaler.reactiveMaskSource != ReactiveMaskSource.None;
        }

        internal TextureHandle UpscalerReactiveMaskPass(RenderGraph renderGraph, ContextContainer frameData, Material material, ReactiveMaskSource reactiveMaskSource, UpscalerOptions upscalerOptions, bool enableTexArray)
        {
            if (reactiveMaskSource == ReactiveMaskSource.None)
                return TextureHandle.nullHandle;

            var io = frameData.Get<UpscalingIO>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            var preAlphaDesc = resourceData.cameraOpaqueTexture.GetDescriptor(renderGraph);
            var postAlphaDesc = resourceData.cameraColorBeforePP.GetDescriptor(renderGraph);
            if (preAlphaDesc.width != postAlphaDesc.width || preAlphaDesc.height != postAlphaDesc.height)
            {
                if (Debug.isDebugBuild && !m_WarnedOpaqueDownsamplingNotCompatible)
                {
                    Debug.LogWarning($"Opaque Downsampling in the current RP Asset should be None for reactive mask generation, but is set to {UniversalRenderPipeline.asset.opaqueDownsampling}.");
                    m_WarnedOpaqueDownsamplingNotCompatible = true;
                }
                return TextureHandle.nullHandle;
            }

            if (Debug.isDebugBuild)
                m_WarnedOpaqueDownsamplingNotCompatible = false;

            TextureHandle output = TextureHandle.nullHandle;

            using (var builder = renderGraph.AddRasterRenderPass<UpscalerReactiveMask.PassData>("Upscaler Reactive Mask", out var passData, k_ReactiveMaskProfilingSampler))
            {
                int passId = 0;
                if (reactiveMaskSource == ReactiveMaskSource.Color)
                {
                    output = renderGraph.CreateTexture(
                        new TextureDesc(io.preUpscaleResolution.x, io.preUpscaleResolution.y, xrReady: enableTexArray)
                        {
                            format = GraphicsFormat.R8_UNorm,
                            clearBuffer = true,
                            clearColor = Color.black,
                            name = "Upscaler Reactive Mask"
                        });
                    builder.SetRenderAttachment(output, 0);

                    passId = 0;
                }
                else if (reactiveMaskSource == ReactiveMaskSource.Stencil)
                {
                    output = renderGraph.CreateTexture(
                        new TextureDesc(io.preUpscaleResolution.x, io.preUpscaleResolution.y, xrReady: enableTexArray)
                        {
                            format = GraphicsFormatUtility.GetDepthStencilFormat(0, 8),
                            clearBuffer = true,
                            clearColor = Color.black,
                            name = "Upscaler Reactive Mask"
                        });
                    builder.SetRenderAttachmentDepth(output, AccessFlags.Write);

                    passId = 1;
                }

                CoreUtils.SetKeyword(material, UpscalerReactiveMask.ShaderKeywords.k_InputTextureArrayKeyword, enableTexArray);

                uint reactiveFlags = 0;
                if (io.hdrInput)
                    reactiveFlags |= UpscalerReactiveMask.ReactiveFlags._ApplyTonemap;
                if (upscalerOptions.reactiveMaskUseComponentMax)
                    reactiveFlags |= UpscalerReactiveMask.ReactiveFlags._UseComponentMax;
                if (upscalerOptions.applyReactiveMaskThreshold)
                    reactiveFlags |= UpscalerReactiveMask.ReactiveFlags._ApplyThreshold;

                passData.reactiveMaskMaterial = material;
                passData.reactiveMaskPassId = passId;
                passData.destWidth = io.preUpscaleResolution.x;
                passData.destHeight = io.preUpscaleResolution.y;
                passData.reactiveScale = upscalerOptions.reactiveMaskScale;
                passData.reactiveThreshold = upscalerOptions.reactiveMaskThreshold;
                passData.reactiveBinaryValue = upscalerOptions.reactiveMaskBinaryValue;
                passData.reactiveFlags = reactiveFlags;
                passData.cameraColorPreAlpha = resourceData.cameraOpaqueTexture;
                passData.cameraColorPostAlpha = resourceData.cameraColorBeforePP;

                builder.UseTexture(passData.cameraColorPreAlpha, AccessFlags.Read);
                builder.UseTexture(passData.cameraColorPostAlpha, AccessFlags.Read);

                builder.SetRenderFunc((UpscalerReactiveMask.PassData data, RasterGraphContext ctx) =>
                {
                    var material = data.reactiveMaskMaterial;
                    material.SetFloat(UpscalerReactiveMask.ShaderConstants._ReactiveScale, data.reactiveScale);
                    material.SetFloat(UpscalerReactiveMask.ShaderConstants._ReactiveThreshold, data.reactiveThreshold);
                    material.SetFloat(UpscalerReactiveMask.ShaderConstants._ReactiveBinaryValue, data.reactiveBinaryValue);
                    material.SetInteger(UpscalerReactiveMask.ShaderConstants._ReactiveFlags, (int)data.reactiveFlags);
                    material.SetTexture(UpscalerReactiveMask.ShaderConstants._ColorPreAlpha, data.cameraColorPreAlpha);
                    material.SetTexture(UpscalerReactiveMask.ShaderConstants._ColorPostAlpha, data.cameraColorPostAlpha);

                    ctx.cmd.SetViewport(new Rect(0, 0, data.destWidth, data.destHeight));
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), material, data.reactiveMaskPassId);
                });
            }

            return output;
        }
#endif
    }
}
