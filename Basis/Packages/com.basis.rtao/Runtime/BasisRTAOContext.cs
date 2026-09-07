using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;

namespace Basis.Rendering.RTAO
{
    public sealed class BasisRTAOContext : IDisposable
    {
        public RayTracingContext Context { get; private set; }
        public IRayTracingShader TraceShader { get; private set; }
        public RayTracingBackend Backend { get; private set; }

        private GraphicsBuffer traceScratch, buildScratch;

        public static bool HardwareSupported => SystemInfo.supportsComputeShaders && RayTracingContext.IsBackendSupported(RayTracingBackend.Hardware);
        public static bool ComputeSupported => RayTracingContext.IsBackendSupported(RayTracingBackend.Compute);

        private BasisRTAOContext(RayTracingContext context, IRayTracingShader shader)
        {
            Context = context;
            TraceShader = shader;
            Backend = context.BackendType;
        }

        public static BasisRTAOContext Create(BasisRTAOResources resources, BasisRTAOBackend backend, out string error)
        {
            error = null;
            if (resources == null)
            {
                error = "BasisRTAOResources asset is not assigned.";
                return null;
            }

            if (!BasisRTAOTracing.IsRayTraced(backend))
            {
                error = $"{BasisRTAOTracing.Describe(backend)} does not use a ray tracing context.";
                return null;
            }

            if (backend == BasisRTAOBackend.Hardware && !HardwareSupported)
            {
                error = "This GPU does not support hardware ray tracing. On Windows that needs Direct3D12; Direct3D11 has no ray tracing path at all.";
                return null;
            }
            if (backend == BasisRTAOBackend.ComputeBvh && !ComputeSupported)
            {
                error = "This GPU does not support compute shaders.";
                return null;
            }

            if (!resources.IsComplete(backend))
            {
                error = $"BasisRTAOResources is missing: {resources.DescribeMissing(backend)}.";
                return null;
            }

            RayTracingBackend rayTracingBackend = backend == BasisRTAOBackend.Hardware ? RayTracingBackend.Hardware : RayTracingBackend.Compute;
            RayTracingResources rayTracingResources = new RayTracingResources();
            if (!rayTracingResources.LoadFromRenderPipelineResources())
            {
#if UNITY_EDITOR
                rayTracingResources.Load();
#else
                if (rayTracingBackend == RayTracingBackend.Compute)
                {
                    error = "The software BVH backend needs RayTracingRenderPipelineResources, which this player build stripped. Use a ray tracing GPU, or the screen space fallback.";
                    return null;
                }
#endif
            }

            RayTracingContext context;
            try
            {
                context = new RayTracingContext(rayTracingBackend, rayTracingResources);
            }
            catch (Exception exception)
            {
                error = $"Failed to create the ray tracing context: {exception.Message}";
                return null;
            }

            UnityEngine.Object shaderAsset = rayTracingBackend == RayTracingBackend.Hardware
                ? resources.HardwareTraceShader
                : (UnityEngine.Object)resources.ComputeTraceShader;

            IRayTracingShader shader;
            try
            {
                shader = context.CreateRayTracingShader(shaderAsset);
            }
            catch (Exception exception)
            {
                context.Dispose();
                error = $"Failed to load the RTAO trace shader: {exception.Message}";
                return null;
            }

            return new BasisRTAOContext(context, shader);
        }

        public IRayTracingAccelStruct CreateAccelerationStructure()
        {
            return Context.CreateAccelerationStructure(new AccelerationStructureOptions
            {
                buildFlags = BuildFlags.PreferFastTrace
            });
        }

        public GraphicsBuffer GetTraceScratch(int width, int height, int depth)
        {
            RayTracingHelper.ResizeScratchBufferForTrace(TraceShader, (uint)width, (uint)height, (uint)depth, ref traceScratch);
            return traceScratch;
        }

        public GraphicsBuffer GetBuildScratch(IRayTracingAccelStruct accelStruct)
        {
            RayTracingHelper.ResizeScratchBufferForBuild(accelStruct, ref buildScratch);
            return buildScratch;
        }

        public void Dispose()
        {
            traceScratch?.Dispose();
            traceScratch = null;
            buildScratch?.Dispose();
            buildScratch = null;
            Context?.Dispose();
            Context = null;
            TraceShader = null;
        }
    }
}
