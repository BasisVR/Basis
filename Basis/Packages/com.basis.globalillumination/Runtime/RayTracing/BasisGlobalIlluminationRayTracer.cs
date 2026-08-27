using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The ray traced mode's shared state: one acceleration structure, one geometry arena and one light list for
/// the whole application. Every camera that renders the effect in a frame refreshes it at most once, so a
/// mirror, a photo camera and the player's eye all trace the same structure instead of building one each.
/// </summary>
public sealed class BasisGlobalIlluminationRayTracer : IDisposable
{
    public readonly struct SkyBinding
    {
        public readonly Texture Cube;
        public readonly Vector4 Decode;
        public readonly float Mip;
        public readonly float Intensity;

        public SkyBinding(Texture cube, Vector4 decode, float mip, float intensity)
        {
            Cube = cube;
            Decode = decode;
            Mip = mip;
            Intensity = intensity;
        }

        public bool IsValid => Cube != null && Intensity > 0f;
    }

    private static BasisGlobalIlluminationRayTracer instance;
    private static string failure;
    private static int failedSignature;

    public static BasisGlobalIlluminationRayTracer Instance => instance;
    public static string Failure => failure;

    public BasisGlobalIlluminationRayContext Context { get; private set; }
    public BasisGlobalIlluminationRayScene Scene { get; private set; }
    public BasisGlobalIlluminationRayLights Lights { get; private set; }

    private int lastRefreshFrame = -1;

    private BasisGlobalIlluminationRayTracer(BasisGlobalIlluminationRayContext context)
    {
        Context = context;
        Scene = new BasisGlobalIlluminationRayScene(context);
        Lights = new BasisGlobalIlluminationRayLights();
    }

    public bool Ready => Context != null && Scene != null && Scene.HasGeometry;

    public static bool Supported => BasisGlobalIlluminationRayContext.Supported;

    public static BasisGlobalIlluminationRayTracer GetOrCreate(RayTracingShader hardwareShader, ComputeShader computeShader, bool allowComputeFallback)
    {
        if (instance != null) { return instance; }

        int signature = Signature(hardwareShader, computeShader, allowComputeFallback);
        if (failure != null && signature == failedSignature) { return null; }

        BasisGlobalIlluminationRayContext context = BasisGlobalIlluminationRayContext.Create(hardwareShader, computeShader, allowComputeFallback, out string error);
        if (context == null)
        {
            failure = error;
            failedSignature = signature;
            return null;
        }

        failure = null;
        instance = new BasisGlobalIlluminationRayTracer(context);
        return instance;
    }

    private static int Signature(RayTracingShader hardwareShader, ComputeShader computeShader, bool allowComputeFallback)
    {
        int hash = allowComputeFallback ? 17 : 31;
        hash = unchecked(hash * 397) ^ (hardwareShader != null ? hardwareShader.GetEntityId().GetHashCode() : 0);
        hash = unchecked(hash * 397) ^ (computeShader != null ? computeShader.GetEntityId().GetHashCode() : 0);
        return hash;
    }

    /// <summary>Refreshes the scene once per frame no matter how many cameras ask for it.</summary>
    public bool Refresh(in BasisGlobalIlluminationRaySceneSettings sceneSettings, in BasisGlobalIlluminationRayLightSettings lightSettings,
        Vector3 viewerPosition, int frame, float time)
    {
        if (frame == lastRefreshFrame) { return Ready; }
        lastRefreshFrame = frame;

        Scene.Refresh(sceneSettings, viewerPosition, time, frame);
        Lights.Refresh(lightSettings, viewerPosition, time);
        return Ready;
    }

    public void MarkDirty()
    {
        Scene?.MarkDirty();
        Lights?.MarkDirty();
    }

    /// <summary>
    /// The environment a miss returns. Both fallbacks read the same convolved sky cubemap the screen space
    /// mode samples, so switching modes does not change what an unoccluded ray is worth - Sky just reads a
    /// blurrier mip than Reflection Probe does.
    /// </summary>
    public static SkyBinding ResolveSky(BasisGlobalIlluminationFallback fallback, float intensity)
    {
        // The cubemap is resolved even when the fallback is off so the kernel always has something bound,
        // and only the intensity decides whether a miss reads it.
        float resolved = fallback == BasisGlobalIlluminationFallback.None ? 0f : Mathf.Max(0f, intensity);

        Texture custom = RenderSettings.customReflectionTexture;
        bool useCustom = custom != null && custom.dimension == TextureDimension.Cube;
        Texture cube = useCustom ? custom : ReflectionProbe.defaultTexture;
        if (cube == null) { return new SkyBinding(null, Vector4.zero, 0f, 0f); }

        Vector4 decode = useCustom ? new Vector4(1f, 1f, 0f, 0f) : ReflectionProbe.defaultTextureHDRDecodeValues;
        return new SkyBinding(cube, decode, MipFor(fallback, cube), resolved);
    }

    private static float MipFor(BasisGlobalIlluminationFallback fallback, Texture cube)
    {
        int mipCount = Mathf.Max(1, cube.mipmapCount);
        return fallback == BasisGlobalIlluminationFallback.Sky ? mipCount - 1 : Mathf.Max(0, mipCount - 3);
    }

    public static void Release()
    {
        instance?.Dispose();
        instance = null;
        failure = null;
        failedSignature = 0;
    }

    public void Dispose()
    {
        Scene?.Dispose();
        Scene = null;
        Lights?.Dispose();
        Lights = null;
        Context?.Dispose();
        Context = null;
        if (instance == this) { instance = null; }
    }
}
