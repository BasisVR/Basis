using System;
using Unity.Collections;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public sealed partial class BasisGlobalIlluminationPass
{
    public const int WorldVoxels = 64;
    public const int WorldProbes = WorldVoxels / 2;
    public const int WorldCascades = 2;
    public const int WorldSnapProbes = 8;
    public const int WorldMaxLevels = 4;
    public const int WorldMaxLights = 32;
    public const int WorldShadowSteps = 20;
    public const int WorldSpecularBins = 8;
    public const float WorldTemporalBlend = 0.15f;
    public const float WorldSeenDecay = 0.996f;
    public const int PassVoxelCapture = 14;
    private const int WorldThreads = 4;
    private const int WorldBlock = 4;
    private const int WorldCoarse = 16;
    public static bool WorldPyramid = true;
    private const int WorldVoxelizeEvery = 2;

    public static Func<Camera, bool> WorldAnchor;

    private struct WorldLight
    {
        public Vector4 position, colour, spot, attenuation;
    }

    private sealed class WorldPlan
    {
        public Camera camera;
        public int serial;
        public bool consumed;
        public bool culled;
        public CullingResults cull;
        public int levels;
        public int turn;
        public bool due;
        public float voxelSize;
        public readonly Vector4[] boxes = new Vector4[WorldMaxLevels];
        public readonly bool[] voxelize = new bool[WorldMaxLevels];
        public readonly bool[] moved = new bool[WorldMaxLevels];
    }

    private sealed class WorldData
    {
        public ComputeShader compute;
        public readonly RendererListHandle[] renderers = new RendererListHandle[WorldMaxLevels * 3];
        public readonly RendererListHandle[] emissionRenderers = new RendererListHandle[WorldMaxLevels * 3];
        public bool hasEmission;
        public bool hasRenderers;
        public int levels;
        public readonly Vector4[] boxes = new Vector4[WorldMaxLevels];
        public readonly bool[] voxelize = new bool[WorldMaxLevels];
        public readonly bool[] moved = new bool[WorldMaxLevels];
        public readonly bool[] cascades = new bool[WorldMaxLevels];
        public readonly Vector4[] previousBoxes = new Vector4[WorldMaxLevels];
        public int lightCount;
        public int interval;
        public float bounceFeedback;
        public float firstInterval;
        public int frame;
        public bool fresh;
        public bool specular;
        public XRPass xr;
    }

    [NoAutoStaticsCleanup] private static readonly WorldPlan worldPlan = new WorldPlan();
    [NoAutoStaticsCleanup] private static readonly WorldLight[] worldLights = new WorldLight[WorldMaxLights];
    [NoAutoStaticsCleanup] private static readonly Vector4[] worldBoxUpload = new Vector4[WorldMaxLevels];
    [NoAutoStaticsCleanup] private static readonly ProfilingSampler samplerWorld = new ProfilingSampler("Basis GI World Cascades");
    public static float GpuMsWorld => samplerWorld.gpuElapsedTime;
    [NoAutoStaticsCleanup] private static readonly ProfilingSampler samplerWorldCapture = new ProfilingSampler("Basis GI World Capture");

    private static RTHandle worldFaces, worldBlocks, worldCoarse, worldEmission;
    private static RTHandle worldAlbedo, worldNormal, worldSeen, worldSeenWeight, worldSeenAlbedo, worldRadiance, worldStamp;
    private static Camera worldCaptureCamera;
    [NoAutoStaticsCleanup] private static readonly RTHandle[] worldCascadeAtlas = new RTHandle[WorldCascades];
    [NoAutoStaticsCleanup] private static readonly RTHandle[][] worldHarmonics = { new RTHandle[4], new RTHandle[4] };
    [NoAutoStaticsCleanup] private static readonly RTHandle[] worldSpecular = new RTHandle[2];
    private static RTHandle worldCascadeDetail;
    private static int worldSpecularAllocated = -1;
    [NoAutoStaticsCleanup] private static readonly int[] worldSpecularTurn = new int[WorldMaxLevels];
    private static int worldRead;
    private static int worldAllocatedLevels;
    private static bool worldFresh;
    [NoAutoStaticsCleanup] private static GraphicsBuffer worldLightBuffer;
    [NoAutoStaticsCleanup] private static readonly Vector4[] worldLastBoxes = new Vector4[WorldMaxLevels];
    [NoAutoStaticsCleanup] private static readonly Matrix4x4[] worldRotations = new Matrix4x4[WorldMaxLevels];
    private static GameObject worldCullerObject;
    private static Camera worldCuller;
    private static int worldSerial;
    private static int worldLightCount;
    private static bool worldHooked;
    private static bool worldRayTraced;

    private static readonly int idWorldLevel = Shader.PropertyToID("_BasisGIWorldLevel");
    private static readonly int idWorldPrevious = Shader.PropertyToID("_BasisGIWorldPrevious");
    private static readonly int idWorldParams = Shader.PropertyToID("_BasisGIWorldParams");
    private static readonly int idWorldSHParams = Shader.PropertyToID("_BasisGIWorldSHParams");
    private static readonly int[] idWorldSH =
    {
        Shader.PropertyToID("_BasisGIWorldSH0"), Shader.PropertyToID("_BasisGIWorldSH1"),
        Shader.PropertyToID("_BasisGIWorldSH2"), Shader.PropertyToID("_BasisGIWorldSH3")
    };
    private static readonly int[] idWorldSHPrev =
    {
        Shader.PropertyToID("_BasisGIWorldSHPrev0"), Shader.PropertyToID("_BasisGIWorldSHPrev1"),
        Shader.PropertyToID("_BasisGIWorldSHPrev2"), Shader.PropertyToID("_BasisGIWorldSHPrev3")
    };
    private static readonly int[] idWorldSHOut =
    {
        Shader.PropertyToID("_BasisGIWorldSHOut0"), Shader.PropertyToID("_BasisGIWorldSHOut1"),
        Shader.PropertyToID("_BasisGIWorldSHOut2"), Shader.PropertyToID("_BasisGIWorldSHOut3")
    };
    private static readonly int idVoxAlbedo = Shader.PropertyToID("_BasisGIVoxAlbedo");
    private static readonly int idVoxNormal = Shader.PropertyToID("_BasisGIVoxNormal");
    private static readonly int idVoxFaces = Shader.PropertyToID("_BasisGIVoxFaces");
    private static readonly int idVoxBlocks = Shader.PropertyToID("_BasisGIVoxBlocks");
    private static readonly int idVoxCoarse = Shader.PropertyToID("_BasisGIVoxCoarse");
    private static readonly int idVoxBlocksRW = Shader.PropertyToID("_BasisGIVoxBlocksRW");
    private static readonly int idVoxCoarseRW = Shader.PropertyToID("_BasisGIVoxCoarseRW");
    private static readonly int idWorldPyramid = Shader.PropertyToID("_BasisGIWorldPyramid");
    private static readonly int idVoxFacesRW = Shader.PropertyToID("_BasisGIVoxFacesRW");
    private static readonly int idVoxSeenRW = Shader.PropertyToID("_BasisGIVoxSeenRW");
    private static readonly int idVoxSeenWeightRW = Shader.PropertyToID("_BasisGIVoxSeenWeightRW");
    private static readonly int idVoxSeenAlbedoRW = Shader.PropertyToID("_BasisGIVoxSeenAlbedoRW");
    private static readonly int idVoxSeenAlbedo = Shader.PropertyToID("_BasisGIVoxSeenAlbedo");
    private static readonly int idWorldSeen = Shader.PropertyToID("_BasisGIWorldSeen");
    private static readonly int idVoxRadiance = Shader.PropertyToID("_BasisGIVoxRadiance");
    private static readonly int idVoxAlbedoRW = Shader.PropertyToID("_BasisGIVoxAlbedoRW");
    private static readonly int idVoxNormalRW = Shader.PropertyToID("_BasisGIVoxNormalRW");
    private static readonly int idVoxRadianceRW = Shader.PropertyToID("_BasisGIVoxRadianceRW");
    private static readonly int idVoxEmission = Shader.PropertyToID("_BasisGIVoxEmission");
    private static readonly int idVoxEmissionRW = Shader.PropertyToID("_BasisGIVoxEmissionRW");
    private static readonly int idWorldCascadeUpper = Shader.PropertyToID("_BasisGIWorldCascadeUpper");
    private static readonly int idWorldCascadeOut = Shader.PropertyToID("_BasisGIWorldCascadeOut");
    private static readonly int idWorldCascadeZero = Shader.PropertyToID("_BasisGIWorldCascadeZero");
    private static readonly int idWorldStamp = Shader.PropertyToID("_BasisGIWorldStamp");
    private static readonly int idWorldCascadeDetail = Shader.PropertyToID("_BasisGIWorldCascadeDetail");
    private static readonly int idWorldCascadeDetailRW = Shader.PropertyToID("_BasisGIWorldCascadeDetailRW");
    private static readonly int idWorldSpecular = Shader.PropertyToID("_BasisGIWorldSpecular");
    private static readonly int idWorldSpecularPrev = Shader.PropertyToID("_BasisGIWorldSpecularPrev");
    private static readonly int idWorldSpecularOut = Shader.PropertyToID("_BasisGIWorldSpecularOut");
    private static readonly int idWorldSpecularParams = Shader.PropertyToID("_BasisGIWorldSpecularParams");
    private static readonly int idWorldLights = Shader.PropertyToID("_BasisGIWorldLights");
    private static readonly int idWorldDispatch = Shader.PropertyToID("_BasisGIWorldDispatch");
    private static readonly int idWorldInterval = Shader.PropertyToID("_BasisGIWorldInterval");
    private static readonly int idWorldInject = Shader.PropertyToID("_BasisGIWorldInject");
    private static readonly int idWorldResolve = Shader.PropertyToID("_BasisGIWorldResolve");
    private static readonly int idWorldRotation = Shader.PropertyToID("_BasisGIWorldRotation");
    private static readonly int idVoxViewProj = Shader.PropertyToID("_BasisGIVoxViewProj");
    private static readonly int idVoxLevel = Shader.PropertyToID("_BasisGIVoxLevel");
    private static readonly int idVoxParams = Shader.PropertyToID("_BasisGIVoxParams");
    private static readonly int idVoxTarget = Shader.PropertyToID("_BasisGIVoxTarget");

    private ComputeShader worldCompute;
    private Shader voxelShader;
    private Material voxelMaterial;

    public void SetWorld(ComputeShader compute, Shader voxelize)
    {
        worldCompute = compute;
        if (voxelShader != voxelize)
        {
            CoreUtils.Destroy(voxelMaterial);
            voxelMaterial = voxelize != null ? CoreUtils.CreateEngineMaterial(voxelize) : null;
        }
        voxelShader = voxelize;
        if (!worldHooked)
        {
            worldHooked = true;
            RenderPipelineManager.beginCameraRendering -= PlanWorld;
            RenderPipelineManager.beginCameraRendering += PlanWorld;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= WorldSceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += WorldSceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= WorldSceneUnloaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += WorldSceneUnloaded;
        }
    }

    public static Vector4 WorldLevelBox(Vector3 viewer, int level, float voxelSize)
    {
        float voxel = voxelSize * (1 << level);
        float snap = voxel * 2f * WorldSnapProbes;
        float half = voxel * WorldVoxels * 0.5f;
        Vector3 min = viewer - Vector3.one * half;
        min = new Vector3(Mathf.Round(min.x / snap) * snap, Mathf.Round(min.y / snap) * snap, Mathf.Round(min.z / snap) * snap);
        return new Vector4(min.x, min.y, min.z, voxel);
    }

    public static Vector2 WorldInterval(int cascade, float voxel)
    {
        float first = CascadePenumbra * voxel * 2f;
        float scale = Mathf.Pow(4f, cascade);
        float start = first * (scale - 1f) / 3f;
        return new Vector2(start, start + first * scale);
    }

    private static bool IsWorldAnchor(Camera camera)
    {
        if (camera == null || camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection) { return false; }
        if (BasisGlobalIlluminationFeature.IsMirrorReflection(camera)) { return false; }
        if (WorldAnchor != null) { return WorldAnchor(camera); }
        return camera.cameraType == CameraType.Game;
    }

    public static bool WorldBinarySchedule = true;

    private static int WorldBinaryTurn(int step, int levels)
    {
        int turn = 0;
        for (int value = step + 1; turn < levels - 1 && (value & 1) == 0; value >>= 1) { turn++; }
        return turn;
    }

    private static int WorldLevelPeriod(int level, int levels, int interval)
    {
        if (!WorldBinarySchedule || levels <= 1) { return Mathf.Max(1, levels * interval); }
        return Mathf.Max(1, interval << Mathf.Min(level + 1, levels - 1));
    }

    private static void PlanWorld(ScriptableRenderContext context, Camera camera)
    {
        worldPlan.camera = null;
        worldPlan.culled = false;
        BasisGlobalIlluminationSettings settings = BasisGlobalIlluminationSettings.Current;
        if (!settings.DiffuseActive() || !settings.worldSpace || !IsWorldAnchor(camera)) { return; }
        if (settings.IsRayTraced() && worldRayTraced) { return; }

        int levels = Mathf.Clamp(settings.worldLevels, 1, WorldMaxLevels);
        float voxelSize = settings.worldVoxelSize;
        worldSerial++;
        worldPlan.camera = camera;
        worldPlan.serial = worldSerial;
        worldPlan.consumed = false;
        worldPlan.levels = levels;
        worldPlan.voxelSize = voxelSize;

        bool reallocated = worldAllocatedLevels != levels || worldAlbedo == null;
        bool any = false;
        int interval = settings.ResolvedWorldInterval();
        bool due = worldSerial % interval == 0;
        worldPlan.due = due;
        int turn = WorldBinarySchedule ? WorldBinaryTurn(worldSerial / interval, levels) : (worldSerial / interval) % levels;
        worldPlan.turn = turn;
        int voxelizeInterval = interval * WorldVoxelizeEvery;
        bool voxelizeDue = worldSerial % voxelizeInterval == 0;
        int voxelizeTurn = (worldSerial / voxelizeInterval) % levels;
        Vector3 viewer = camera.transform.position;
        for (int level = 0; level < WorldMaxLevels; level++)
        {
            worldPlan.voxelize[level] = false;
            worldPlan.moved[level] = false;
            if (level >= levels) { continue; }
            Vector4 box = WorldLevelBox(viewer, level, voxelSize);
            worldPlan.boxes[level] = box;
            bool moved = reallocated || box != worldLastBoxes[level];
            worldPlan.moved[level] = moved;
            worldPlan.voxelize[level] = moved || (voxelizeDue && level == voxelizeTurn);
            any |= worldPlan.voxelize[level];
        }

        worldPlan.culled = false;
        if (!any) { return; }

        int largest = 0;
        for (int level = 0; level < levels; level++) { if (worldPlan.voxelize[level]) { largest = level; } }
        Vector4 outer = worldPlan.boxes[largest];
        float half = outer.w * WorldVoxels * 0.5f;
        Vector3 centre = new Vector3(outer.x, outer.y, outer.z) + Vector3.one * half;

        EnsureWorldCuller();
        worldCuller.orthographic = true;
        worldCuller.orthographicSize = half;
        worldCuller.aspect = 1f;
        worldCuller.nearClipPlane = 0.01f;
        worldCuller.farClipPlane = half * 2f + 0.02f;
        worldCuller.cullingMask = settings.ResolvedTraceLayers();
        worldCuller.transform.SetPositionAndRotation(centre - Vector3.forward * (half + 0.01f), Quaternion.identity);
        if (!worldCuller.TryGetCullingParameters(false, out ScriptableCullingParameters parameters)) { return; }
        parameters.cullingOptions = CullingOptions.None;
        parameters.shadowDistance = 0f;
        worldPlan.cull = context.Cull(ref parameters);
        worldPlan.culled = true;
    }

    private static void EnsureWorldCuller()
    {
        if (worldCuller != null) { return; }
        worldCullerObject = new GameObject("BasisGIWorldCuller") { hideFlags = HideFlags.HideAndDontSave };
        worldCuller = worldCullerObject.AddComponent<Camera>();
        worldCuller.enabled = false;
    }

    private static RTHandle AllocateVolume(int width, int height, int depth, GraphicsFormat format, string name)
    {
        return RTHandles.Alloc(width, height, depth, dimension: TextureDimension.Tex3D, colorFormat: format,
            filterMode: FilterMode.Bilinear, wrapMode: TextureWrapMode.Clamp, enableRandomWrite: true, name: name);
    }

    private static void EnsureWorldAllocated(int levels)
    {
        if (worldAlbedo != null && worldAllocatedLevels == levels) { return; }
        ReleaseWorld();
        int depth = WorldVoxels * levels;
        worldAlbedo = AllocateVolume(WorldVoxels, WorldVoxels, depth, GraphicsFormat.R8G8B8A8_UNorm, "_BasisGIVoxAlbedo");
        worldNormal = AllocateVolume(WorldVoxels, WorldVoxels, depth, GraphicsFormat.R8G8B8A8_UNorm, "_BasisGIVoxNormal");
        worldFaces = AllocateVolume(WorldVoxels, WorldVoxels, depth, GraphicsFormat.R32_UInt, "_BasisGIVoxFaces");
        worldSeen = AllocateVolume(WorldVoxels, WorldVoxels, depth, GraphicsFormat.R32_UInt, "_BasisGIVoxSeen");
        worldSeenWeight = AllocateVolume(WorldVoxels, WorldVoxels, depth, GraphicsFormat.R32_SFloat, "_BasisGIVoxSeenWeight");
        worldSeenAlbedo = AllocateVolume(WorldVoxels, WorldVoxels, depth, GraphicsFormat.R32_UInt, "_BasisGIVoxSeenAlbedo");
        worldRadiance = AllocateVolume(WorldVoxels, WorldVoxels, depth, GraphicsFormat.R16G16B16A16_SFloat, "_BasisGIVoxRadiance");
        worldEmission = AllocateVolume(WorldVoxels, WorldVoxels, depth, GraphicsFormat.R16G16B16A16_SFloat, "_BasisGIVoxEmission");
        int blockSide = WorldVoxels / WorldBlock, coarseSide = WorldVoxels / WorldCoarse;
        worldBlocks = AllocateVolume(blockSide, blockSide, blockSide * levels, GraphicsFormat.R32_SFloat, "_BasisGIVoxBlocks");
        worldCoarse = AllocateVolume(coarseSide, coarseSide, coarseSide * levels, GraphicsFormat.R32_SFloat, "_BasisGIVoxCoarse");
        int width = WorldProbes * CascadeDirections;
        for (int cascade = 0; cascade < WorldCascades; cascade++)
        {
            worldCascadeAtlas[cascade] = AllocateVolume(width, width, (WorldProbes >> cascade) * levels, GraphicsFormat.R16G16B16A16_SFloat, "_BasisGIWorldCascade" + cascade);
        }
        for (int set = 0; set < 2; set++)
        {
            for (int band = 0; band < 4; band++)
            {
                worldHarmonics[set][band] = AllocateVolume(WorldProbes, WorldProbes, WorldProbes * levels, GraphicsFormat.R16G16B16A16_SFloat, "_BasisGIWorldSH" + set + band);
            }
        }
        worldStamp = AllocateVolume(WorldProbes, WorldProbes, WorldProbes * levels, GraphicsFormat.R32_SFloat, "_BasisGIWorldStamp");
        worldLightBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, WorldMaxLights, 64);
        worldAllocatedLevels = levels;
        worldFresh = true;
        worldRead = 0;
    }

    private static void EnsureWorldSpecular(int levels, bool enabled)
    {
        int wanted = enabled ? levels : 0;
        if (worldSpecular[0] != null && worldSpecularAllocated == wanted) { return; }
        ReleaseWorldSpecular();
        GraphicsFormat format = SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, GraphicsFormatUsage.LoadStore)
            ? GraphicsFormat.B10G11R11_UFloatPack32 : GraphicsFormat.R16G16B16A16_SFloat;
        int side = enabled ? WorldProbes * WorldSpecularBins : 1;
        int depth = enabled ? WorldProbes * levels : 1;
        for (int set = 0; set < 2; set++) { worldSpecular[set] = AllocateVolume(side, side, depth, format, "_BasisGIWorldSpecular" + set); }
        int detailSide = enabled ? WorldProbes * CascadeDirections : 1;
        int detailDepth = enabled ? (WorldProbes >> 1) * levels : 1;
        worldCascadeDetail = AllocateVolume(detailSide, detailSide, detailDepth, GraphicsFormat.R16G16B16A16_SFloat, "_BasisGIWorldCascadeDetail");
        worldSpecularAllocated = wanted;
    }

    private static void ReleaseWorldSpecular()
    {
        for (int set = 0; set < 2; set++) { RTHandles.Release(worldSpecular[set]); worldSpecular[set] = null; }
        RTHandles.Release(worldCascadeDetail); worldCascadeDetail = null;
        worldSpecularAllocated = -1;
    }

    public static void ReleaseWorld()
    {
        RTHandles.Release(worldAlbedo); worldAlbedo = null;
        RTHandles.Release(worldNormal); worldNormal = null;
        RTHandles.Release(worldFaces); worldFaces = null;
        RTHandles.Release(worldBlocks); worldBlocks = null;
        RTHandles.Release(worldCoarse); worldCoarse = null;
        RTHandles.Release(worldSeen); worldSeen = null;
        RTHandles.Release(worldSeenWeight); worldSeenWeight = null;
        RTHandles.Release(worldSeenAlbedo); worldSeenAlbedo = null;
        RTHandles.Release(worldRadiance); worldRadiance = null;
        RTHandles.Release(worldEmission); worldEmission = null;
        RTHandles.Release(worldStamp); worldStamp = null;
        for (int cascade = 0; cascade < WorldCascades; cascade++) { RTHandles.Release(worldCascadeAtlas[cascade]); worldCascadeAtlas[cascade] = null; }
        for (int set = 0; set < 2; set++) { for (int band = 0; band < 4; band++) { RTHandles.Release(worldHarmonics[set][band]); worldHarmonics[set][band] = null; } }
        ReleaseWorldSpecular();
        worldLightBuffer?.Release();
        worldLightBuffer = null;
        worldAllocatedLevels = 0;
        Shader.SetGlobalVector(idWorldParams, Vector4.zero);
        Shader.SetGlobalVector(idWorldSpecularParams, Vector4.zero);
    }

    private static void WorldSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode) { ReleaseWorldIfAllocated(); }
    private static void WorldSceneUnloaded(UnityEngine.SceneManagement.Scene scene) { ReleaseWorldIfAllocated(); }

    private static void ReleaseWorldIfAllocated()
    {
        if (worldAlbedo != null) { ReleaseWorld(); }
    }

    private static void DisableWorld()
    {
        Shader.SetGlobalVector(idWorldParams, Vector4.zero);
        Shader.SetGlobalVector(idWorldSpecularParams, Vector4.zero);
    }

    private static int GatherWorldLights(in CullingResults cull)
    {
        NativeArray<VisibleLight> visible = cull.visibleLights;
        int count = 0;
        for (int index = 0; index < visible.Length && count < WorldMaxLights; index++)
        {
            VisibleLight light = visible[index];
            Color colour = light.finalColor;
            if (colour.maxColorComponent <= 1e-4f) { continue; }
            Matrix4x4 matrix = light.localToWorldMatrix;
            Vector3 forward = matrix.GetColumn(2);
            Vector3 position = matrix.GetColumn(3);
            float range = Mathf.Max(light.range, 0.01f);
            WorldLight packed = new WorldLight { colour = new Vector4(colour.r, colour.g, colour.b, range) };
            switch (light.lightType)
            {
                case LightType.Directional:
                    packed.position = new Vector4(forward.x, forward.y, forward.z, 0f);
                    break;
                case LightType.Point:
                    packed.position = new Vector4(position.x, position.y, position.z, 1f);
                    packed.attenuation = new Vector4(0f, 1f, 1f / (range * range), 0f);
                    break;
                case LightType.Spot:
                    {
                        float outer = Mathf.Cos(Mathf.Deg2Rad * light.spotAngle * 0.5f);
                        float inner = Mathf.Cos(Mathf.Deg2Rad * (light.light != null ? light.light.innerSpotAngle : light.spotAngle * 0.75f) * 0.5f);
                        float scale = 1f / Mathf.Max(inner - outer, 0.001f);
                        packed.position = new Vector4(position.x, position.y, position.z, 2f);
                        packed.spot = new Vector4(forward.x, forward.y, forward.z, 0f);
                        packed.attenuation = new Vector4(scale, -outer * scale, 1f / (range * range), 0f);
                        break;
                    }
                default:
                    continue;
            }
            worldLights[count++] = packed;
        }
        return count;
    }

    public static Matrix4x4 VoxelAxisMatrix(Vector4 box, int axis)
    {
        float half = box.w * WorldVoxels * 0.5f;
        Vector3 centre = new Vector3(box.x, box.y, box.z) + Vector3.one * half;
        float scale = 1f / half;
        Matrix4x4 matrix = Matrix4x4.zero;
        int u = axis == 0 ? 2 : 0, v = axis == 1 ? 2 : 1;
        matrix[0, u] = scale; matrix[0, 3] = -centre[u] * scale;
        matrix[1, v] = scale; matrix[1, 3] = -centre[v] * scale;
        matrix[2, 3] = 0.5f;
        matrix[3, 3] = 1f;
        return matrix;
    }

    private void RecordWorld(RenderGraph renderGraph, UniversalCameraData cameraData, BasisGlobalIlluminationSettings settings, int frame)
    {
        if (!settings.worldSpace || worldCompute == null || voxelMaterial == null || !SystemInfo.supportsComputeShaders || !SystemInfo.supports3DRenderTextures)
        {
            ReleaseWorldIfAllocated();
            DisableWorld();
            return;
        }
        if (worldPlan.camera != cameraData.camera || worldPlan.consumed) { return; }
        worldPlan.consumed = true;

        int levels = worldPlan.levels;
        EnsureWorldAllocated(levels);
        EnsureWorldSpecular(levels, settings.worldReflections);

        using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("Basis GI World Cascades", out WorldData data, samplerWorld))
        {
            data.compute = worldCompute;
            data.levels = levels;
            data.fresh = worldFresh;
            data.specular = settings.worldReflections;
            data.frame = frame;
            data.xr = cameraData.xr;
            data.bounceFeedback = settings.temporalFilter ? settings.bounceFeedback : 0f;
            data.interval = settings.ResolvedWorldInterval();
            data.hasRenderers = false;
            data.hasEmission = false;
            for (int level = 0; level < WorldMaxLevels; level++)
            {
                data.boxes[level] = level < levels ? worldPlan.boxes[level] : Vector4.zero;
                data.voxelize[level] = level < levels && worldPlan.culled && worldPlan.voxelize[level];
                data.moved[level] = data.voxelize[level] && worldPlan.moved[level];
                data.previousBoxes[level] = worldFresh ? new Vector4(1e7f, 1e7f, 1e7f, data.boxes[level].w) : worldLastBoxes[level];
                data.cascades[level] = level < levels && (worldFresh || worldPlan.voxelize[level] || (worldPlan.due && level == worldPlan.turn));
                if (data.cascades[level])
                {
                    CascadeJitter(worldPlan.serial + level * 7, settings.temporalFilter ? settings.jitter : 0f, 1, out _, out Quaternion rotation);
                    worldRotations[level] = Matrix4x4.Rotate(rotation);
                }
            }

            if (worldPlan.culled)
            {
                worldLightCount = GatherWorldLights(worldPlan.cull);
                data.lightCount = worldLightCount;
                worldLightBuffer.SetData(worldLights, 0, 0, worldLightCount);

                EnsureLightmapMaskTags();
                DrawingSettings drawing = new DrawingSettings(lightmapMaskTags[0], new SortingSettings(cameraData.camera) { criteria = SortingCriteria.None })
                {
                    overrideMaterial = voxelMaterial,
                    overrideMaterialPassIndex = 0,
                    perObjectData = PerObjectData.None,
                    enableInstancing = true
                };
                for (int index = 1; index < lightmapMaskTags.Length; index++) { drawing.SetShaderPassName(index, lightmapMaskTags[index]); }
                FilteringSettings filtering = new FilteringSettings(RenderQueueRange.opaque, settings.ResolvedTraceLayers());
                RendererListParams listParams = new RendererListParams(worldPlan.cull, drawing, filtering);
                for (int level = 0; level < levels; level++)
                {
                    if (!data.voxelize[level]) { continue; }
                    for (int axis = 0; axis < 3; axis++)
                    {
                        RendererListHandle list = renderGraph.CreateRendererList(listParams);
                        data.renderers[level * 3 + axis] = list;
                        builder.UseRendererList(list);
                    }
                }
                data.hasRenderers = true;
                uint emissiveMask = BasisGlobalIlluminationSurfaceTags.EmissiveMask;
                data.hasEmission = emissiveMask != 0u && voxelMaterial.passCount > 1;
                if (data.hasEmission)
                {
                    DrawingSettings emissionDrawing = new DrawingSettings(lightmapMaskTags[0], new SortingSettings(cameraData.camera) { criteria = SortingCriteria.None })
                    {
                        overrideShader = voxelMaterial.shader,
                        overrideShaderPassIndex = 1,
                        perObjectData = PerObjectData.None,
                        enableInstancing = true
                    };
                    for (int index = 1; index < lightmapMaskTags.Length; index++) { emissionDrawing.SetShaderPassName(index, lightmapMaskTags[index]); }
                    FilteringSettings emissionFiltering = new FilteringSettings(RenderQueueRange.opaque, settings.ResolvedTraceLayers()) { renderingLayerMask = emissiveMask, batchLayerMask = ~BatchLayer.InstanceCullingMask };
                    RendererListParams emissionParams = new RendererListParams(worldPlan.cull, emissionDrawing, emissionFiltering);
                    for (int level = 0; level < levels; level++)
                    {
                        if (!data.voxelize[level]) { continue; }
                        for (int axis = 0; axis < 3; axis++)
                        {
                            RendererListHandle list = renderGraph.CreateRendererList(emissionParams);
                            data.emissionRenderers[level * 3 + axis] = list;
                            builder.UseRendererList(list);
                        }
                    }
                }
                for (int level = 0; level < levels; level++) { if (data.voxelize[level]) { worldLastBoxes[level] = worldPlan.boxes[level]; } }
            }
            else
            {
                data.lightCount = worldLightCount;
            }

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((WorldData pass, UnsafeGraphContext context) => ExecuteWorld(pass, context));
        }
        worldFresh = false;
        worldCaptureCamera = cameraData.camera;
    }

    private sealed class CaptureData
    {
        public Material material;
        public TextureHandle sceneColor;
        public TextureHandle surface;
    }

    private void RecordWorldCapture(RenderGraph renderGraph, UniversalResourceData resourceData, Camera camera, TextureHandle sceneColor, TextureHandle surface, int tracedWidth, int tracedHeight)
    {
        if (worldCaptureCamera != camera || worldSeen == null) { return; }
        worldCaptureCamera = null;

        TextureHandle seen = renderGraph.ImportTexture(worldSeen);
        TextureHandle seenWeight = renderGraph.ImportTexture(worldSeenWeight);
        TextureHandle seenAlbedo = renderGraph.ImportTexture(worldSeenAlbedo);
        TextureDesc dummyDescriptor = new TextureDesc(tracedWidth, tracedHeight)
        {
            format = GraphicsFormat.R8_UNorm,
            dimension = TextureXR.dimension,
            slices = TextureXR.slices,
            name = "_BasisGIVoxCaptureTarget"
        };
        TextureHandle dummy = renderGraph.CreateTexture(dummyDescriptor);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Basis GI World Capture", out CaptureData data, samplerWorldCapture))
        {
            data.material = material;
            data.sceneColor = sceneColor;
            data.surface = surface;
            builder.SetRenderAttachment(dummy, 0, AccessFlags.WriteAll);
            builder.SetRandomAccessAttachment(seen, 1, AccessFlags.ReadWrite);
            builder.SetRandomAccessAttachment(seenWeight, 2, AccessFlags.ReadWrite);
            builder.SetRandomAccessAttachment(seenAlbedo, 3, AccessFlags.ReadWrite);
            if (surface.IsValid()) { builder.UseTexture(surface); }
            builder.UseTexture(sceneColor);
            builder.UseTexture(resourceData.cameraDepthTexture);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((CaptureData pass, RasterGraphContext context) =>
            {
                context.cmd.SetGlobalTexture(idSceneColor, pass.sceneColor);
                context.cmd.SetGlobalFloat(idSurfaceValid, pass.surface.IsValid() ? 1f : 0f);
                if (pass.surface.IsValid()) { context.cmd.SetGlobalTexture(idSurface, pass.surface); }
                Blitter.BlitTexture(context.cmd, pass.sceneColor, new Vector4(1f, 1f, 0f, 0f), pass.material, PassVoxelCapture);
            });
        }
    }

    private static void ExecuteWorld(WorldData data, UnsafeGraphContext context)
    {
        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        ComputeShader compute = data.compute;
        int levels = data.levels;
        for (int level = 0; level < WorldMaxLevels; level++) { worldBoxUpload[level] = data.boxes[level]; }
        cmd.SetGlobalVectorArray(idWorldLevel, worldBoxUpload);
        cmd.SetGlobalVector(idWorldParams, new Vector4(levels, WorldVoxels, WorldProbes, data.fresh ? 0f : 1f));
        cmd.SetGlobalVector(idWorldSHParams, new Vector4(0f, WorldProbes * levels, 0f, 0f));
        cmd.SetGlobalFloatArray(idRcSolidAngle, cascadeSolidAngles);
        cmd.SetGlobalTexture(idVoxAlbedo, worldAlbedo);
        RTHandle[] read = worldHarmonics[worldRead], write = worldHarmonics[1 - worldRead];
        for (int band = 0; band < 4; band++) { cmd.SetGlobalTexture(idWorldSH[band], read[band]); }
        cmd.SetGlobalTexture(idWorldSpecular, worldSpecular[worldRead]);
        cmd.SetGlobalVector(idWorldSpecularParams, new Vector4(data.specular ? 1f : 0f, WorldSpecularBins, 1f, 0f));

        int voxelGroups = WorldVoxels / WorldThreads;
        int clear = compute.FindKernel("ClearVoxels");
        int inject = compute.FindKernel("Inject");
        int cascadeKernel = compute.FindKernel("Cascade");
        int resolve = compute.FindKernel("Resolve");
        int buildBlocks = compute.FindKernel("BuildBlocks");
        int buildCoarse = compute.FindKernel("BuildCoarse");
        int detailKernel = compute.FindKernel("Detail");
        cmd.SetComputeFloatParam(compute, idWorldPyramid, WorldPyramid ? 1f : 0f);

        bool anyVoxelize = false;
        for (int level = 0; level < levels; level++) { anyVoxelize |= data.voxelize[level]; }
        if (anyVoxelize && data.hasRenderers)
        {
            bool stereo = data.xr != null && data.xr.enabled && data.xr.singlePassEnabled;
            if (stereo) { data.xr.StopSinglePass(cmd); }
            cmd.GetTemporaryRT(idVoxTarget, WorldVoxels, WorldVoxels, 0, FilterMode.Point, GraphicsFormat.R8_UNorm);
            for (int level = 0; level < levels; level++)
            {
                if (!data.voxelize[level]) { continue; }
                cmd.SetComputeVectorParam(compute, idWorldDispatch, new Vector4(level, data.moved[level] ? 1f : 0f, WorldCascades, data.frame));
                cmd.SetComputeVectorParam(compute, idWorldPrevious, data.previousBoxes[level]);
                cmd.SetComputeTextureParam(compute, clear, idVoxAlbedoRW, worldAlbedo);
                cmd.SetComputeTextureParam(compute, clear, idVoxNormalRW, worldNormal);
                cmd.SetComputeTextureParam(compute, clear, idVoxFacesRW, worldFaces);
                cmd.SetComputeTextureParam(compute, clear, idVoxEmissionRW, worldEmission);
                cmd.SetComputeTextureParam(compute, clear, idVoxSeenRW, worldSeen);
                cmd.SetComputeTextureParam(compute, clear, idVoxSeenWeightRW, worldSeenWeight);
                cmd.SetComputeTextureParam(compute, clear, idVoxSeenAlbedoRW, worldSeenAlbedo);
                cmd.DispatchCompute(compute, clear, voxelGroups, voxelGroups, voxelGroups);

                cmd.SetRenderTarget(idVoxTarget);
                cmd.SetViewport(new Rect(0f, 0f, WorldVoxels, WorldVoxels));
                cmd.ClearRandomWriteTargets();
                cmd.SetRandomWriteTarget(1, worldAlbedo);
                cmd.SetRandomWriteTarget(2, worldNormal);
                cmd.SetRandomWriteTarget(3, worldFaces);
                cmd.SetGlobalVector(idVoxLevel, data.boxes[level]);
                cmd.SetGlobalVector(idVoxParams, new Vector4(WorldVoxels, level * WorldVoxels, 0f, 0f));
                for (int axis = 0; axis < 3; axis++)
                {
                    cmd.SetGlobalMatrix(idVoxViewProj, VoxelAxisMatrix(data.boxes[level], axis));
                    cmd.DrawRendererList(data.renderers[level * 3 + axis]);
                }
                if (data.hasEmission)
                {
                    cmd.SetRandomWriteTarget(4, worldEmission);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        cmd.SetGlobalMatrix(idVoxViewProj, VoxelAxisMatrix(data.boxes[level], axis));
                        cmd.DrawRendererList(data.emissionRenderers[level * 3 + axis]);
                    }
                }
                cmd.ClearRandomWriteTargets();

                cmd.SetComputeTextureParam(compute, buildBlocks, idVoxAlbedo, worldAlbedo);
                cmd.SetComputeTextureParam(compute, buildBlocks, idVoxBlocksRW, worldBlocks);
                cmd.DispatchCompute(compute, buildBlocks, WorldVoxels / WorldBlock / WorldThreads, WorldVoxels / WorldBlock / WorldThreads, WorldVoxels / WorldBlock / WorldThreads);
                cmd.SetComputeTextureParam(compute, buildCoarse, idVoxBlocks, worldBlocks);
                cmd.SetComputeTextureParam(compute, buildCoarse, idVoxCoarseRW, worldCoarse);
                cmd.DispatchCompute(compute, buildCoarse, 1, 1, 1);
            }
            cmd.ReleaseTemporaryRT(idVoxTarget);
            if (stereo) { data.xr.StartSinglePass(cmd); }
        }

        cmd.SetComputeBufferParam(compute, inject, idWorldLights, worldLightBuffer);
        cmd.SetComputeVectorParam(compute, idWorldInject, new Vector4(data.bounceFeedback, data.lightCount, WorldShadowSteps, data.fresh ? 0f : 1f));
        cmd.SetComputeTextureParam(compute, inject, idVoxAlbedo, worldAlbedo);
        cmd.SetComputeTextureParam(compute, inject, idVoxNormal, worldNormal);
        cmd.SetComputeTextureParam(compute, inject, idVoxSeenRW, worldSeen);
        cmd.SetComputeTextureParam(compute, inject, idVoxSeenWeightRW, worldSeenWeight);
        cmd.SetComputeTextureParam(compute, inject, idVoxSeenAlbedo, worldSeenAlbedo);
        cmd.SetComputeTextureParam(compute, inject, idVoxBlocks, worldBlocks);
        cmd.SetComputeTextureParam(compute, inject, idVoxCoarse, worldCoarse);
        cmd.SetComputeTextureParam(compute, inject, idVoxRadianceRW, worldRadiance);
        cmd.SetComputeTextureParam(compute, inject, idVoxEmission, worldEmission);
        for (int band = 0; band < 4; band++) { cmd.SetComputeTextureParam(compute, inject, idWorldSH[band], read[band]); }
        bool anyCascade = false;
        for (int level = 0; level < levels; level++)
        {
            if (!data.cascades[level]) { continue; }
            anyCascade = true;
            cmd.SetComputeVectorParam(compute, idWorldSeen, new Vector4(Mathf.Pow(WorldSeenDecay, WorldLevelPeriod(level, levels, data.interval)), 0f, 0f, 0f));
            cmd.SetComputeVectorParam(compute, idWorldDispatch, new Vector4(level, 0f, WorldCascades, data.frame));
            cmd.DispatchCompute(compute, inject, voxelGroups, voxelGroups, voxelGroups);
        }

        int width = WorldProbes * CascadeDirections;
        for (int level = 0; level < levels; level++)
        {
            if (!data.cascades[level]) { continue; }
            cmd.SetComputeMatrixParam(compute, idWorldRotation, worldRotations[level]);
            for (int cascade = WorldCascades - 1; cascade >= 0; cascade--)
            {
                Vector2 interval = WorldInterval(cascade, data.boxes[level].w);
                float coarsest = data.boxes[levels - 1].w;
                float marchStep = Mathf.Min(data.boxes[level].w * (1 << cascade), coarsest);
                int probes = WorldProbes >> cascade;
                cmd.SetComputeVectorParam(compute, idWorldDispatch, new Vector4(level, cascade, WorldCascades, data.frame));
                cmd.SetComputeVectorParam(compute, idWorldInterval, new Vector4(interval.x, interval.y, marchStep, CascadeDirections << cascade));
                cmd.SetComputeTextureParam(compute, cascadeKernel, idVoxRadiance, worldRadiance);
                cmd.SetComputeTextureParam(compute, cascadeKernel, idVoxFaces, worldFaces);
                cmd.SetComputeTextureParam(compute, cascadeKernel, idVoxAlbedo, worldAlbedo);
                cmd.SetComputeTextureParam(compute, cascadeKernel, idVoxBlocks, worldBlocks);
                cmd.SetComputeTextureParam(compute, cascadeKernel, idVoxCoarse, worldCoarse);
                for (int band = 0; band < 4; band++) { cmd.SetComputeTextureParam(compute, cascadeKernel, idWorldSH[band], read[band]); }
                cmd.SetComputeTextureParam(compute, cascadeKernel, idWorldCascadeOut, worldCascadeAtlas[cascade]);
                cmd.SetComputeTextureParam(compute, cascadeKernel, idWorldCascadeUpper, cascade + 1 < WorldCascades ? worldCascadeAtlas[cascade + 1] : worldRadiance);
                cmd.DispatchCompute(compute, cascadeKernel, (width + 7) / 8, (width + 7) / 8, probes);
            }
            if (data.specular)
            {
                int upperWidth = (WorldProbes >> 1) * CascadeDirections * 2;
                cmd.SetComputeVectorParam(compute, idWorldDispatch, new Vector4(level, 1f, WorldCascades, data.frame));
                cmd.SetComputeTextureParam(compute, detailKernel, idWorldCascadeUpper, worldCascadeAtlas[1]);
                cmd.SetComputeTextureParam(compute, detailKernel, idWorldCascadeDetailRW, worldCascadeDetail);
                cmd.DispatchCompute(compute, detailKernel, (upperWidth + 7) / 8, (upperWidth + 7) / 8, WorldProbes >> 1);
            }
        }

        if (!anyCascade)
        {
            cmd.SetGlobalVector(idWorldParams, new Vector4(levels, WorldVoxels, WorldProbes, 1f));
            return;
        }

        cmd.SetComputeTextureParam(compute, resolve, idWorldCascadeZero, worldCascadeAtlas[0]);
        cmd.SetComputeTextureParam(compute, resolve, idVoxAlbedo, worldAlbedo);
        cmd.SetComputeTextureParam(compute, resolve, idWorldStamp, worldStamp);
        cmd.SetComputeTextureParam(compute, resolve, idWorldCascadeDetail, worldCascadeDetail);
        cmd.SetComputeTextureParam(compute, resolve, idVoxBlocks, worldBlocks);
        cmd.SetComputeTextureParam(compute, resolve, idWorldSpecularPrev, worldSpecular[worldRead]);
        cmd.SetComputeTextureParam(compute, resolve, idWorldSpecularOut, worldSpecular[1 - worldRead]);
        for (int band = 0; band < 4; band++)
        {
            cmd.SetComputeTextureParam(compute, resolve, idWorldSHPrev[band], read[band]);
            cmd.SetComputeTextureParam(compute, resolve, idWorldSHOut[band], write[band]);
        }
        int probeGroups = WorldProbes / WorldThreads;
        for (int level = 0; level < levels; level++)
        {
            float resolveBlend = 1f - Mathf.Pow(1f - WorldTemporalBlend, WorldLevelPeriod(level, levels, data.interval));
            cmd.SetComputeVectorParam(compute, idWorldResolve, new Vector4(resolveBlend, data.fresh ? 1f : 0f, data.cascades[level] ? 0f : 1f, data.specular ? 1f + (worldSpecularTurn[level] & 1) : 0f));
            if (data.cascades[level]) { worldSpecularTurn[level]++; }
            cmd.SetComputeMatrixParam(compute, idWorldRotation, worldRotations[level]);
            cmd.SetComputeVectorParam(compute, idWorldDispatch, new Vector4(level, 0f, WorldCascades, data.frame));
            cmd.DispatchCompute(compute, resolve, probeGroups, probeGroups, probeGroups);
        }

        cmd.SetGlobalTexture(idWorldSpecular, worldSpecular[1 - worldRead]);
        worldRead = 1 - worldRead;
        for (int band = 0; band < 4; band++) { cmd.SetGlobalTexture(idWorldSH[band], write[band]); }
        cmd.SetGlobalVector(idWorldParams, new Vector4(levels, WorldVoxels, WorldProbes, 1f));
    }
}
