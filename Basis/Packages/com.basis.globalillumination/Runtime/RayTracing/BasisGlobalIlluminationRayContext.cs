using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;

/// <summary>
/// Owns the unified ray tracing context and the trace shader the ray traced mode dispatches. The hardware
/// backend is used whenever the GPU has one; the compute backend is a software BVH and is only reached when
/// the renderer feature opts into it.
/// </summary>
public sealed class BasisGlobalIlluminationRayContext : IDisposable
{
    public RayTracingContext Context { get; private set; }
    public IRayTracingShader TraceShader { get; private set; }
    public RayTracingBackend Backend { get; private set; }

    private GraphicsBuffer traceScratch, buildScratch;

    public static bool HardwareSupported => RayTracingContext.IsBackendSupported(RayTracingBackend.Hardware);
    public static bool ComputeSupported => RayTracingContext.IsBackendSupported(RayTracingBackend.Compute);
    public static bool Supported => HardwareSupported || ComputeSupported;

    private BasisGlobalIlluminationRayContext(RayTracingContext context, IRayTracingShader shader)
    {
        Context = context;
        TraceShader = shader;
        Backend = context.BackendType;
    }

    public static BasisGlobalIlluminationRayContext Create(RayTracingShader hardwareShader, ComputeShader computeShader, bool allowComputeFallback, out string error)
    {
        error = null;
        bool hardware = HardwareSupported;
        if (!hardware && !allowComputeFallback)
        {
            error = "This GPU has no hardware ray tracing and the compute fallback is disabled on the renderer feature.";
            return null;
        }
        if (!hardware && !ComputeSupported)
        {
            error = "This GPU supports neither hardware ray tracing nor the compute ray tracing backend.";
            return null;
        }

        RayTracingBackend backend = hardware ? RayTracingBackend.Hardware : RayTracingBackend.Compute;
        UnityEngine.Object shaderAsset = hardware ? hardwareShader : (UnityEngine.Object)computeShader;
        if (shaderAsset == null)
        {
            error = hardware
                ? "The ray traced global illumination trace shader is missing. Reimport com.basis.globalillumination."
                : "The compute ray tracing kernel is missing. Reimport com.basis.globalillumination.";
            return null;
        }

        RayTracingResources rayTracingResources = new RayTracingResources();
        if (!rayTracingResources.LoadFromRenderPipelineResources())
        {
#if UNITY_EDITOR
            rayTracingResources.Load();
#else
            if (backend == RayTracingBackend.Compute)
            {
                error = "The compute ray tracing backend needs RayTracingRenderPipelineResources, which this player build stripped. Use a GPU with hardware ray tracing.";
                return null;
            }
#endif
        }

        RayTracingContext context;
        try
        {
            context = new RayTracingContext(backend, rayTracingResources);
        }
        catch (Exception exception)
        {
            error = $"Failed to create the ray tracing context: {exception.Message}";
            return null;
        }

        IRayTracingShader shader;
        try
        {
            shader = context.CreateRayTracingShader(shaderAsset);
        }
        catch (Exception exception)
        {
            context.Dispose();
            error = $"Failed to load the ray traced global illumination trace shader: {exception.Message}";
            return null;
        }

        return new BasisGlobalIlluminationRayContext(context, shader);
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
