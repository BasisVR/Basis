using System;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Samples the main camera depth texture for animated image front-face points and
    /// asynchronously returns one visibility bit per card. The scheduler consumes the
    /// latest completed result and falls back to physics while no GPU result is available.
    /// </summary>
    [DisallowMultipleRendererFeature("Basis Image Depth Visibility")]
    public sealed class BasisImageDepthVisibilityFeature : ScriptableRendererFeature
    {
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Rendering;

        [SerializeField]
        private ComputeShader computeShader;
        private DepthVisibilityPass _pass;
        private bool _missingShaderWarningLogged;
        private bool _passInitializationFailed;

        public override void Create()
        {
            _passInitializationFailed = false;
            EnsurePass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!BasisImagePickupSettings.UseDepthBufferAnimationVisibility)
                return;

            Camera camera = renderingData.cameraData.camera;
            if (
                !ReferenceEquals(camera, BasisLocalCameraDriver.CameraInstance)
                || renderingData.cameraData.cameraType != CameraType.Game
                || renderingData.cameraData.xr.enabled
                || !BasisAnimatedImageDepthVisibility.CanUseDepthCamera(camera)
            )
            {
                return;
            }

            if (!EnsurePass())
            {
                if (!_missingShaderWarningLogged)
                {
                    _missingShaderWarningLogged = true;
                    BasisDebug.LogWarning(
                        "Image pickup depth visibility compute shader was not found; "
                            + "depth mode will use the physics fallback.",
                        LogTag
                    );
                }
                return;
            }

            if (
                !BasisAnimatedImageDepthVisibility.IsActive
                || BasisAnimatedImageDepthVisibility.PreparedCount <= 0
            )
            {
                return;
            }

            renderer.EnqueuePass(_pass);
        }

        private bool EnsurePass()
        {
            if (_passInitializationFailed)
                return false;
            if (_pass != null || computeShader == null)
                return _pass != null;

            try
            {
                _pass = new DepthVisibilityPass(computeShader);
            }
            catch (Exception exception)
            {
                if (!_missingShaderWarningLogged)
                {
                    _missingShaderWarningLogged = true;
                    BasisDebug.LogWarning(
                        $"Image pickup depth visibility could not initialize "
                            + $"({exception.Message}); using the physics fallback.",
                        LogTag
                    );
                }
                _pass = null;
                _passInitializationFailed = true;
            }
            return _pass != null;
        }

        protected override void Dispose(bool disposing)
        {
            _pass = null;
        }

        private sealed class DepthVisibilityPass : ScriptableRenderPass
        {
            private static readonly int DepthTextureId = Shader.PropertyToID("_BasisImageDepthTexture");
            private static readonly int SampleBufferId = Shader.PropertyToID("_BasisImageDepthSamples");
            private static readonly int VisibilityBufferId = Shader.PropertyToID("_BasisImageDepthVisibility");
            private static readonly int WorldToClipId = Shader.PropertyToID("_BasisImageWorldToClip");
            private static readonly int WorldToViewId = Shader.PropertyToID("_BasisImageWorldToView");
            private static readonly int TextureSizeId = Shader.PropertyToID("_BasisImageDepthTextureSize");
            private static readonly int CardCountId = Shader.PropertyToID("_BasisImageDepthCardCount");
            private static readonly int DepthBiasId = Shader.PropertyToID("_BasisImageDepthBiasMeters");

            private readonly ComputeShader _computeShader;
            private readonly int _kernel;
            private readonly ProfilingSampler _profilingSampler = new("Basis Image Depth Visibility");

            private sealed class ComputePassData
            {
                public ComputeShader Shader;
                public int Kernel;
                public TextureHandle DepthTexture;
                public BufferHandle Samples;
                public BufferHandle Visibility;
                public Matrix4x4 WorldToClip;
                public Matrix4x4 WorldToView;
                public Vector4 TextureSize;
                public int CardCount;
                public float DepthBiasMeters;
            }

            private sealed class ReadbackPassData
            {
                public BufferHandle Visibility;
                public Action<AsyncGPUReadbackRequest> Callback;
            }

            public DepthVisibilityPass(ComputeShader computeShader)
            {
                _computeShader = computeShader;
                _kernel = computeShader.FindKernel("CSMain");
                // DesktopRenderer copies camera depth after transparents. Sampling immediately
                // before post-processing reuses that copy instead of forcing an earlier depth path.
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!BasisAnimatedImageDepthVisibility.IsActive)
                    return;

                UniversalResourceData resourceData =
                    frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                TextureHandle depthTexture = resourceData.cameraDepthTexture;
                if (!depthTexture.IsValid())
                    return;

                Camera camera = cameraData.camera;
                if (
                    !BasisAnimatedImageDepthVisibility.TryBeginDispatch(
                        camera,
                        out GraphicsBuffer sampleBuffer,
                        out GraphicsBuffer visibilityBuffer,
                        out int cardCount,
                        out Action<AsyncGPUReadbackRequest> readbackCallback
                    )
                )
                {
                    return;
                }

                TextureDesc depthDescriptor = renderGraph.GetTextureDesc(in depthTexture);
                int width = Mathf.Max(1, depthDescriptor.width);
                int height = Mathf.Max(1, depthDescriptor.height);

                BufferHandle sampleHandle = renderGraph.ImportBuffer(sampleBuffer);
                BufferHandle visibilityHandle = renderGraph.ImportBuffer(visibilityBuffer);
                Matrix4x4 worldToView = cameraData.GetViewMatrix(0);
                Matrix4x4 worldToClip =
                    GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(0), true)
                    * worldToView;

                using (
                    var builder = renderGraph.AddComputePass<ComputePassData>(
                        "Basis Image Depth Visibility",
                        out ComputePassData passData,
                        _profilingSampler
                    )
                )
                {
                    passData.Shader = _computeShader;
                    passData.Kernel = _kernel;
                    passData.DepthTexture = depthTexture;
                    passData.Samples = sampleHandle;
                    passData.Visibility = visibilityHandle;
                    passData.WorldToClip = worldToClip;
                    passData.WorldToView = worldToView;
                    passData.TextureSize = new Vector4(width, height, 1f / width, 1f / height);
                    passData.CardCount = cardCount;
                    passData.DepthBiasMeters =
                        BasisImagePickupSettings.AnimationDepthOcclusionBiasMeters;

                    builder.UseTexture(passData.DepthTexture, AccessFlags.Read);
                    builder.UseBuffer(passData.Samples, AccessFlags.Read);
                    builder.UseBuffer(passData.Visibility, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(
                        static (ComputePassData data, ComputeGraphContext context) =>
                        {
                            context.cmd.SetComputeTextureParam(
                                data.Shader,
                                data.Kernel,
                                DepthTextureId,
                                data.DepthTexture
                            );
                            context.cmd.SetComputeBufferParam(data.Shader, data.Kernel, SampleBufferId, data.Samples);
                            context.cmd.SetComputeBufferParam(
                                data.Shader,
                                data.Kernel,
                                VisibilityBufferId,
                                data.Visibility
                            );
                            context.cmd.SetComputeMatrixParam(data.Shader, WorldToClipId, data.WorldToClip);
                            context.cmd.SetComputeMatrixParam(data.Shader, WorldToViewId, data.WorldToView);
                            context.cmd.SetComputeVectorParam(data.Shader, TextureSizeId, data.TextureSize);
                            context.cmd.SetComputeIntParam(data.Shader, CardCountId, data.CardCount);
                            context.cmd.SetComputeFloatParam(data.Shader, DepthBiasId, data.DepthBiasMeters);
                            context.cmd.DispatchCompute(
                                data.Shader,
                                data.Kernel,
                                Mathf.CeilToInt(data.CardCount / 64f),
                                1,
                                1
                            );
                        }
                    );
                }

                using (
                    var builder = renderGraph.AddUnsafePass<ReadbackPassData>(
                        "Basis Image Depth Visibility Readback",
                        out ReadbackPassData passData
                    )
                )
                {
                    passData.Visibility = visibilityHandle;
                    passData.Callback = readbackCallback;
                    builder.UseBuffer(passData.Visibility, AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(
                        static (ReadbackPassData data, UnsafeGraphContext context) =>
                        {
                            context.cmd.RequestAsyncReadback(
                                data.Visibility,
                                data.Callback
                            );
                        }
                    );
                }
            }
        }
    }
}
