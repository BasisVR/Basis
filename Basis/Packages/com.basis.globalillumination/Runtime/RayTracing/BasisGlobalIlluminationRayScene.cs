using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;

/// <summary>
/// Static and Dynamic are gone. Both re-baked a SkinnedMeshRenderer into a mesh of its own, which the
/// backend can only swap in by removing and re-adding the instance - a bottom level rebuild per pose,
/// rationed by a per frame budget. The body that bounced light was therefore each avatar's pose from
/// several frames ago, staggered differently per person, and no budget setting could fix that because the
/// rebuild is the cost. Proxy costs one transform update per limb, so everyone updates every frame.
///
/// The numbering is left alone: the mode is serialized into BasisGlobalIlluminationSettings, and an asset
/// or settings file holding a 3 must keep meaning Proxy.
/// </summary>
public enum BasisGlobalIlluminationRaySkinnedMode
{
    Off = 0,
    /// <summary>
    /// Avatars are traced as capsules on their bones rather than as their own deforming mesh, so every
    /// avatar updates every frame instead of waiting its turn in a bake budget. Shares its poses with the
    /// ambient occlusion tracer - see BasisAvatarProxy in Common.
    /// </summary>
    Proxy = 3
}

[Serializable]
public struct BasisGlobalIlluminationRaySceneSettings
{
    public LayerMask layerMask;
    public bool shadowCastersOnly;
    public float rescanInterval;
    public BasisGlobalIlluminationRaySkinnedMode skinnedMode;
    public bool textureAlbedo;
    public bool emissiveSurfaces;
    /// <summary>
    /// Skip the emission of a baked-emissive surface that already carries a lightmap, because that
    /// light is in the lightmap and injecting it again lights the room twice from one lamp.
    /// </summary>
    public bool respectBakedEmission;
    /// <summary>Emission multiplier for world geometry, and for anything on an avatar layer.</summary>
    public float emissionScale;
    public float avatarEmissionScale;

    public static BasisGlobalIlluminationRaySceneSettings Default => new BasisGlobalIlluminationRaySceneSettings
    {
        layerMask = ~0,
        shadowCastersOnly = false,
        rescanInterval = 2f,
        skinnedMode = BasisGlobalIlluminationRaySkinnedMode.Proxy,
        textureAlbedo = true,
        emissiveSurfaces = true,
        respectBakedEmission = true,
        emissionScale = 2f,
        avatarEmissionScale = 1f
    };
}

/// <summary>Per sub-mesh surface data a ray hit resolves. Must match BasisGIRtInstance in the trace kernel.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BasisGlobalIlluminationRayInstance
{
    public const int Stride = 96;
    public const uint FlagHasNormals = 1u;

    /// <summary>
    /// This instance is one of an avatar's proxy capsules rather than geometry the camera drew.
    ///
    /// The trace needs to know, because a proxy is the one thing in the structure that does not match what
    /// is on screen. A ray leaving the visible surface of a body starts INSIDE that body's own capsule -
    /// the spine bone sits near the back, so a 0.115 x height torso capsule swallows the chest - and comes
    /// straight back reporting the surface is fully enclosed. The kernel uses this flag to recognise the
    /// case and step out of the capsule instead of shading it.
    /// </summary>
    public const uint FlagProxy = 2u;

    public Vector4 albedo;
    public Vector4 emission;
    public uint indexOffset, vertexOffset, flags, indexCount;
    public Vector4 normal0, normal1, normal2;

    public void SetNormalMatrix(in Matrix4x4 localToWorld)
    {
        Matrix4x4 normalMatrix = localToWorld.inverse.transpose;
        normal0 = normalMatrix.GetRow(0);
        normal1 = normalMatrix.GetRow(1);
        normal2 = normalMatrix.GetRow(2);
    }

    /// <summary>
    /// The same three rows for a matrix whose columns are orthogonal — every proxy capsule, built as an
    /// orthonormal basis with per-axis scales — where the inverse-transpose is analytically each column
    /// over its own squared length. Only the 3x3 is kept either way, so this matches
    /// <see cref="SetNormalMatrix"/> exactly for that shape at a fraction of a general 4x4 inverse.
    /// </summary>
    public void SetNormalMatrixOrthogonal(in Matrix4x4 localToWorld)
    {
        Vector4 c0 = localToWorld.GetColumn(0);
        Vector4 c1 = localToWorld.GetColumn(1);
        Vector4 c2 = localToWorld.GetColumn(2);
        float s0 = c0.x * c0.x + c0.y * c0.y + c0.z * c0.z;
        float s1 = c1.x * c1.x + c1.y * c1.y + c1.z * c1.z;
        float s2 = c2.x * c2.x + c2.y * c2.y + c2.z * c2.z;
        if (s0 < 1e-12f || s1 < 1e-12f || s2 < 1e-12f)
        {
            SetNormalMatrix(localToWorld);
            return;
        }
        float i0 = 1f / s0, i1 = 1f / s1, i2 = 1f / s2;
        normal0 = new Vector4(c0.x * i0, c1.x * i1, c2.x * i2, 0f);
        normal1 = new Vector4(c0.y * i0, c1.y * i1, c2.y * i2, 0f);
        normal2 = new Vector4(c0.z * i0, c1.z * i1, c2.z * i2, 0f);
    }
}

/// <summary>
/// Keeps the acceleration structure, the shared geometry arenas and the per-instance surface data in step
/// with the scene. Skinned renderers are baked into a mesh of their own on a per-frame budget so avatars
/// bounce and occlude light in the pose they are actually standing in.
/// </summary>
public sealed class BasisGlobalIlluminationRayScene : IDisposable
{
    public const int MaxInstances = 8192;

    private sealed class MeshGeometry
    {
        public BasisGlobalIlluminationRayArena.Block normals;
        public BasisGlobalIlluminationRayArena.Block[] indices;
        public int vertexCount, references;
        public bool hasNormals;
    }

    private sealed class Entry
    {
        public Renderer renderer;
        public Transform transform;
        public Mesh sourceMesh;
        public MeshGeometry geometry;
        public bool sharedGeometry;
        public Matrix4x4 matrix;
        public int[] handles;
        public int[] instanceIds;
        public bool isStatic, seen;
        /// <summary>
        /// Which half of the room this is, remembered rather than re-derived because the renderer it came
        /// from may already be gone by the time the instance has to be re-registered.
        /// </summary>
        public byte category;
    }

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");
    private static readonly int EmissionEnabledId = Shader.PropertyToID("_EmissionEnabled");
    private const string EmissionKeyword = "_EMISSION";

    private readonly BasisGlobalIlluminationRayContext context;
    private readonly BasisGlobalIlluminationRayTextureAverage textures = new BasisGlobalIlluminationRayTextureAverage();
    private readonly BasisGlobalIlluminationRayArena normalArena = new BasisGlobalIlluminationRayArena("_BasisGIRtNormals");
    private readonly BasisGlobalIlluminationRayArena indexArena = new BasisGlobalIlluminationRayArena("_BasisGIRtIndices");
    private readonly Dictionary<EntityId, Entry> entries = new Dictionary<EntityId, Entry>();
    private readonly Dictionary<EntityId, MeshGeometry> meshCache = new Dictionary<EntityId, MeshGeometry>();

    /// <summary>
    /// One avatar's capsules. The limbs never change shape, so this holds nothing but instance handles and
    /// the transforms to read each frame - there is no mesh here to go stale.
    /// </summary>
    private sealed class ProxyEntry
    {
        public Animator animator;
        /// <summary>Shared with every other tracer looking at this avatar, sampled once per frame.</summary>
        public BasisAvatarProxyPose pose;
        public int[] handles;
        public int[] instanceIds;
        public MeshGeometry geometry;
        public bool seen;
        /// <summary>
        /// The renderer WriteProxyMaterials last read colour from, cached so the per frame block check below
        /// does not re-walk the avatar's hierarchy for every avatar every frame.
        /// </summary>
        public Renderer representativeRenderer;
    }

    private readonly Dictionary<EntityId, ProxyEntry> proxies = new Dictionary<EntityId, ProxyEntry>();
    private readonly List<EntityId> proxyRemoval = new List<EntityId>();
    public int ProxyCount => proxies.Count;
    private readonly List<EntityId> pendingRemoval = new List<EntityId>();
    private readonly List<int> freeInstanceIds = new List<int>();
    private readonly List<Vector3> normalScratch = new List<Vector3>();
    private readonly List<int> indexScratch = new List<int>();
    private readonly List<Material> materialScratch = new List<Material>();
    // One shared block: the surface read runs over every instance in the scene, and a fresh
    // MaterialPropertyBlock per surface would be garbage on a path that now runs every frame.
    private static readonly MaterialPropertyBlock blockScratch = new MaterialPropertyBlock();
    /// <summary>
    /// The whole-renderer block, read once per renderer. It needs a block of its own rather than sharing
    /// blockScratch because the per sub-mesh probe below overwrites that one and the renderer-wide answer
    /// has to survive across every sub-mesh of the entry.
    /// </summary>
    private static readonly MaterialPropertyBlock rendererBlockScratch = new MaterialPropertyBlock();

    /// <summary>
    /// One renderer's property block state, answered at most once however many sub-meshes ask.
    ///
    /// Both questions - does this renderer carry a block at all, and what is its whole-renderer block -
    /// are per RENDERER facts that the surface read was re-asking per sub-mesh: a four sub-mesh renderer
    /// made five HasPropertyBlock calls and up to eight GetPropertyBlock calls every frame for a block
    /// that could not have changed between one sub-mesh and the next.
    ///
    /// Lazy rather than eager because a single sub-mesh caller must not come out worse. The avatar proxy
    /// path (ApplyProxyMaterials) reads slot 0 alone for every avatar in the room every frame, and an
    /// AudioLink accessory drives that slot with its OWN block - so reading the whole-renderer block up
    /// front would spend a GetPropertyBlock per avatar per frame on an answer the slot block makes
    /// irrelevant. This way a walk pays 1 + N + 1 at worst and the single-slot reader pays exactly the
    /// two calls it always did.
    /// </summary>
    private struct RendererBlocks
    {
        private Renderer renderer;
        private bool probed, hasBlock, wideResolved;
        private MaterialPropertyBlock wide;

        public static RendererBlocks For(Renderer renderer) { return new RendererBlocks { renderer = renderer }; }

        /// <summary>
        /// The block that applies to this sub-mesh, or null when none does. A block set for one slot and a
        /// block set for the whole renderer are both reachable here, and the per slot one wins where both
        /// exist - the order the renderer itself applies them.
        /// </summary>
        public MaterialPropertyBlock Resolve(int materialIndex)
        {
            if (renderer == null) { return null; }
            if (!probed) { probed = true; hasBlock = renderer.HasPropertyBlock(); }
            if (!hasBlock) { return null; }

            if (materialIndex >= 0)
            {
                blockScratch.Clear();
                renderer.GetPropertyBlock(blockScratch, materialIndex);
                if (!blockScratch.isEmpty) { return blockScratch; }
            }

            if (!wideResolved)
            {
                wideResolved = true;
                rendererBlockScratch.Clear();
                renderer.GetPropertyBlock(rendererBlockScratch);
                wide = rendererBlockScratch.isEmpty ? null : rendererBlockScratch;
            }
            return wide;
        }
    }
    /// <summary>
    /// _EMISSION resolved per shader rather than per material read. FindKeyword is a name lookup into the
    /// shader's keyword space, and the surface read below ran one for every sub-mesh of every instance,
    /// every time the scene re-read its materials. A shader's keyword space is fixed for the shader's
    /// lifetime, so the answer only has to be found once; the cache is dropped on every rescan so a
    /// shader that has been unloaded cannot be held alive by it.
    /// </summary>
    private static readonly Dictionary<Shader, LocalKeyword> emissionKeywords = new Dictionary<Shader, LocalKeyword>();

    private BasisGlobalIlluminationRayInstance[] instances = new BasisGlobalIlluminationRayInstance[256];
    private GraphicsBuffer instanceBuffer;
    private IRayTracingAccelStruct accelStruct;
    private int instanceHighWater;
    private int instanceDirtyStart = int.MaxValue, instanceDirtyEnd = -1;
    private bool instanceBufferResized = true;
    private float nextScanTime;

    /// <summary>
    /// How many candidates the geometry pass walks per frame.
    ///
    /// The walk itself, not the FindObjectsByType in front of it, is what made the rescan a hitch: a
    /// component probe, a mesh resolve and a material read for every renderer in the world, all in the
    /// one frame the timer happened to fire on. A large world is a few hundred thousand interop calls
    /// that frame and nothing at all for the next hundred. This is the same walk over the same snapshot,
    /// spread across the interval instead - at 60fps and the default two seconds it covers about fifteen
    /// thousand renderers before the next pass is even due, and a world bigger than that simply runs its
    /// passes back to back, which is still a flat cost rather than a spike.
    /// </summary>
    private const int ScanBudget = 256;
    /// <summary>
    /// The snapshot the pass in flight is walking. Held rather than re-taken per frame so that objects
    /// created mid-pass are picked up by the NEXT pass rather than shifting the ground under the cursor -
    /// which is what the whole-scene walk gave for free by finishing inside one frame. It is the shared
    /// array from BasisSceneScan; that class replaces its array rather than refilling it, so a scan
    /// another consumer triggers mid-pass cannot disturb this one.
    /// </summary>
    private Renderer[] scanBatch;
    private int scanCursor;
    private bool scanning;
    private int scanTextureVersion;
    /// <summary>
    /// The avatar pass runs on its own timer, half an interval away from the geometry pass. They are the
    /// two biggest walks here and there is no reason for them to be the same frame's work.
    /// </summary>
    private float nextProxyScanTime;
    private bool proxyScanPhased;
    private int textureVersion = -1;
    private bool structureDirty = true;
    private bool everBuilt;

    public IRayTracingAccelStruct AccelerationStructure => accelStruct;
    public GraphicsBuffer InstanceBuffer => instanceBuffer;
    public GraphicsBuffer NormalBuffer => normalArena.Buffer;
    public GraphicsBuffer IndexBuffer => indexArena.Buffer;
    public int EntryCount => entries.Count;
    public int InstanceCount => instanceHighWater;
    public bool NeedsBuild => structureDirty || !everBuilt;
    public bool HasGeometry => instanceHighWater > 0 && instanceBuffer != null;

    public BasisGlobalIlluminationRayScene(BasisGlobalIlluminationRayContext context)
    {
        this.context = context;
        accelStruct = context.CreateAccelerationStructure();
        Application.onBeforeRender += ScheduleTransformGather;
    }

    // The per-frame world-entry matrix sweep, as a transform job instead of one
    // main-thread localToWorldMatrix read per dynamic entry inside render-graph
    // recording. Scheduling has to happen at onBeforeRender — see the ZBinning
    // note on BasisAvatarProxyJobs for why a job must never be SCHEDULED from
    // inside the render pipeline. UpdateTransforms only joins and compares.
    private struct GatherWorldMatricesJob : IJobParallelForTransform
    {
        public NativeArray<Matrix4x4> Matrices;

        public void Execute(int index, TransformAccess transform)
        {
            Matrices[index] = transform.localToWorldMatrix;
        }
    }

    private readonly List<Entry> dynamicEntries = new List<Entry>();
    private TransformAccessArray dynamicAccess;
    private NativeArray<Matrix4x4> dynamicMatrices;
    private JobHandle dynamicHandle;
    private bool dynamicScheduled;
    private bool dynamicListDirty = true;

    [BeforeRenderOrder(int.MaxValue)]
    private void ScheduleTransformGather()
    {
        // A gather the pass never reaped (the frame skipped Refresh — GI off, no
        // camera) is retired here so it cannot pin the transforms indefinitely.
        CompleteTransformGather();
        if (dynamicListDirty || !dynamicAccess.isCreated || dynamicEntries.Count == 0) { return; }
        dynamicHandle = new GatherWorldMatricesJob { Matrices = dynamicMatrices }.Schedule(dynamicAccess);
        dynamicScheduled = true;
        JobHandle.ScheduleBatchedJobs();
    }

    private void CompleteTransformGather()
    {
        if (!dynamicScheduled) { return; }
        dynamicHandle.Complete();
        dynamicScheduled = false;
    }

    // Rebuilt only when the dynamic set changed (an add, a remove, a dispose), never
    // per frame. A frame whose set changed falls back to the managed dictionary walk,
    // so stale gathered data is never applied to a removed entry's freed instances.
    private void RebuildDynamicList()
    {
        CompleteTransformGather();
        dynamicEntries.Clear();
        foreach (KeyValuePair<EntityId, Entry> pair in entries)
        {
            Entry entry = pair.Value;
            if (entry.isStatic || entry.transform == null) { continue; }
            dynamicEntries.Add(entry);
        }
        if (dynamicAccess.isCreated) { dynamicAccess.Dispose(); }
        if (dynamicMatrices.IsCreated) { dynamicMatrices.Dispose(); }
        dynamicListDirty = false;
        int count = dynamicEntries.Count;
        if (count == 0) { return; }
        Transform[] transforms = new Transform[count];
        for (int index = 0; index < count; index++) { transforms[index] = dynamicEntries[index].transform; }
        dynamicAccess = new TransformAccessArray(transforms);
        dynamicMatrices = new NativeArray<Matrix4x4>(count, Allocator.Persistent);
    }

    public void MarkDirty()
    {
        nextScanTime = 0f;
        nextProxyScanTime = 0f;
        // A pass in flight is abandoned rather than finished: it is walking a snapshot from before
        // whatever changed. Nothing is left half-applied by that - the next pass re-marks every entry
        // unseen and walks the whole set again, and the sweep only ever runs at the end of a pass that
        // completed. Invalidate so that next pass takes a genuinely fresh walk instead of the cached one
        // this call is saying is out of date.
        scanning = false;
        scanBatch = null;
        scanCursor = 0;
        BasisSceneScan.Invalidate();
        structureDirty = true;
    }

    public static bool ShouldInclude(Renderer renderer, in BasisGlobalIlluminationRaySceneSettings settings)
    {
        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) { return false; }
        if ((settings.layerMask.value & (1 << renderer.gameObject.layer)) == 0) { return false; }
        if (settings.shadowCastersOnly && renderer.shadowCastingMode == ShadowCastingMode.Off) { return false; }
        // TryGetComponent rather than GetComponent plus a fake-null compare, which is the miss path this
        // takes for nearly every renderer in the world and the one Unity documents TryGetComponent as
        // avoiding an editor allocation on. The RTAO twin of this method already reads it this way.
        return !renderer.TryGetComponent(out BasisGlobalIlluminationRayExclude _);
    }

    /// <summary>
    /// Never a SkinnedMeshRenderer. An avatar reaches the structure as proxy capsules or not at all, so no
    /// deforming mesh is registered here any more and there is nothing left to re-bake. The mode is still
    /// taken so callers do not have to know that.
    /// </summary>
    public static bool IsSupportedRendererType(Renderer renderer, BasisGlobalIlluminationRaySkinnedMode skinnedMode)
    {
        return renderer is MeshRenderer;
    }

    public static Mesh ResolveMesh(Renderer renderer)
    {
        if (renderer == null) { return null; }
        if (renderer is SkinnedMeshRenderer skinned) { return skinned.sharedMesh; }
        return renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
    }

    public static bool IsUsableMesh(Mesh mesh)
    {
        return mesh != null && mesh.subMeshCount > 0 && mesh.vertexCount > 0 && mesh.HasVertexAttribute(VertexAttribute.Position);
    }

    public void Refresh(in BasisGlobalIlluminationRaySceneSettings settings, in BasisGlobalIlluminationRayViewers viewers, float time, int frameCount)
    {
        if (accelStruct == null) { return; }

        float interval = Mathf.Max(0.1f, settings.rescanInterval);

        if (!scanning && time >= nextScanTime)
        {
            nextScanTime = time + interval;
            BeginScan(settings, interval);
        }
        if (scanning) { StepScan(settings, ScanBudget); }

        // Suppressed while a pass is in flight, because the pass is already re-reading the materials of
        // every entry it walks and running the whole-scene re-read alongside it would put back exactly the
        // per frame full walk this is here to remove. The version is taken when the pass FINISHES, so an
        // average that lands during one is caught by the frame after it ends rather than lost.
        if (!scanning && textureVersion != textures.Version)
        {
            RefreshMaterials(settings);
        }
        else
        {
            RefreshBlockMaterials(settings);
            if (settings.skinnedMode == BasisGlobalIlluminationRaySkinnedMode.Proxy) { RefreshProxyBlockMaterials(settings); }
        }

        if (settings.skinnedMode == BasisGlobalIlluminationRaySkinnedMode.Proxy)
        {
            if (time >= nextProxyScanTime)
            {
                // The first reschedule is a half interval longer than the rest, which parks the animator
                // walk permanently between two geometry walks instead of on the same frame as one. Both
                // still run immediately at startup, so an avatar occludes from the moment it arrives.
                nextProxyScanTime = time + (proxyScanPhased ? interval : interval * 1.5f);
                proxyScanPhased = true;
                RescanProxies(settings, interval);
            }
        }
        else if (proxies.Count > 0) { ClearProxies(); }

        UpdateTransforms();

        if (settings.skinnedMode == BasisGlobalIlluminationRaySkinnedMode.Proxy)
        {
            UpdateProxies(frameCount);
        }

        Upload();
    }

    /// <summary>
    /// The whole geometry pass at once, avatars included. Refresh does not use this - it drives the sliced
    /// form below - but a caller that has just changed the world and wants the structure to agree with it
    /// before the next frame does.
    /// </summary>
    public void Rescan(in BasisGlobalIlluminationRaySceneSettings settings)
    {
        float interval = Mathf.Max(0.1f, settings.rescanInterval);
        BeginScan(settings, interval);
        StepScan(settings, int.MaxValue);
        if (settings.skinnedMode == BasisGlobalIlluminationRaySkinnedMode.Proxy) { RescanProxies(settings, interval); }
        else if (proxies.Count > 0) { ClearProxies(); }
    }

    private void BeginScan(in BasisGlobalIlluminationRaySceneSettings settings, float interval)
    {
        // Dropped once per pass so an unloaded shader is not kept alive by the memo. Everything the memo
        // saves is spent inside a single pass anyway.
        emissionKeywords.Clear();
        foreach (KeyValuePair<EntityId, Entry> pair in entries) { pair.Value.seen = false; }

        // Shared with ray traced ambient occlusion, which wants the same set on the same cadence: whichever
        // of the two asks first in the window pays for the walk and the other reads its array.
        scanBatch = BasisSceneScan.Take<Renderer>(interval);
        scanCursor = 0;
        scanning = true;
        // The version as it stood when the walk STARTED, claimed as covered only when the walk ends. An
        // average that lands mid-pass has already been missed by every entry the cursor went past, so
        // claiming the version current at that point would bury it until the next pass; carrying the old
        // one instead leaves the mismatch standing and the frame after the pass does one catch-up read.
        scanTextureVersion = textures.Version;
    }

    private void StepScan(in BasisGlobalIlluminationRaySceneSettings settings, int budget)
    {
        if (!scanning) { return; }
        if (scanBatch == null) { FinishScan(); return; }

        int end = budget >= scanBatch.Length - scanCursor ? scanBatch.Length : scanCursor + budget;
        for (; scanCursor < end; scanCursor++)
        {
            Renderer renderer = scanBatch[scanCursor];
            if (!IsSupportedRendererType(renderer, settings.skinnedMode)) { continue; }
            if (!ShouldInclude(renderer, settings)) { continue; }

            Mesh mesh = ResolveMesh(renderer);
            if (!IsUsableMesh(mesh)) { continue; }

            EntityId id = renderer.GetEntityId();
            if (entries.TryGetValue(id, out Entry existing))
            {
                if (existing.sourceMesh == mesh)
                {
                    existing.seen = true;
                    WriteMaterials(existing, settings);
                    continue;
                }
                RemoveEntry(id, existing);
            }

            if (instanceHighWater >= MaxInstances) { continue; }
            AddEntry(renderer, mesh, settings);
        }

        if (scanCursor >= scanBatch.Length) { FinishScan(); }
    }

    /// <summary>
    /// Drops whatever the pass did not find. Only ever at the END of a pass: an entry the cursor has not
    /// reached yet is not missing, it is unvisited, and sweeping mid-pass would delete the whole structure
    /// and rebuild it a slice at a time.
    /// </summary>
    private void FinishScan()
    {
        pendingRemoval.Clear();
        foreach (KeyValuePair<EntityId, Entry> pair in entries)
        {
            if (!pair.Value.seen || pair.Value.renderer == null) { pendingRemoval.Add(pair.Key); }
        }
        for (int index = 0; index < pendingRemoval.Count; index++)
        {
            if (entries.TryGetValue(pendingRemoval[index], out Entry dead)) { RemoveEntry(pendingRemoval[index], dead); }
        }
        pendingRemoval.Clear();

        textureVersion = scanTextureVersion;
        scanBatch = null;
        scanCursor = 0;
        scanning = false;
    }

    private void RefreshMaterials(in BasisGlobalIlluminationRaySceneSettings settings)
    {
        textureVersion = textures.Version;
        foreach (KeyValuePair<EntityId, Entry> pair in entries) { WriteMaterials(pair.Value, settings); }
    }

    /// <summary>
    /// Re-reads the surfaces driven by a MaterialPropertyBlock, every frame rather than on the rescan timer.
    /// A block is how emission gets animated - that is what it is for - so a bounce that only notices the
    /// change when the next rescan comes round is whole seconds behind a light the player is watching pulse,
    /// which reads as the emission never reaching the scene at all. Only renderers actually carrying a block
    /// pay for this; a surface changed through the material itself is rare enough to ride the rescan.
    /// </summary>
    private void RefreshBlockMaterials(in BasisGlobalIlluminationRaySceneSettings settings)
    {
        foreach (KeyValuePair<EntityId, Entry> pair in entries)
        {
            Entry entry = pair.Value;
            if (entry.renderer == null || !entry.renderer.HasPropertyBlock()) { continue; }
            WriteMaterials(entry, settings);
        }
    }

    /// <summary>
    /// Proxy equivalent of RefreshBlockMaterials. An avatar's represented colour is normally only re-read on
    /// the rescan timer (default 2s), which is far too slow for a MaterialPropertyBlock driven pulse -
    /// AudioLink above all - so any proxy whose representative renderer actually carries a block gets re-read
    /// every frame instead, same as a non-avatar renderer already does.
    /// </summary>
    private void RefreshProxyBlockMaterials(in BasisGlobalIlluminationRaySceneSettings settings)
    {
        foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
        {
            ProxyEntry entry = pair.Value;
            if (entry.representativeRenderer == null || !entry.representativeRenderer.HasPropertyBlock()) { continue; }
            ApplyProxyMaterials(entry, settings);
        }
    }

    private void AddEntry(Renderer renderer, Mesh mesh, in BasisGlobalIlluminationRaySceneSettings settings)
    {
        MeshGeometry geometry = AcquireGeometry(mesh);
        if (geometry == null) { return; }

        Matrix4x4 matrix = renderer.transform.localToWorldMatrix;
        Entry entry = new Entry
        {
            renderer = renderer,
            transform = renderer.transform,
            sourceMesh = mesh,
            geometry = geometry,
            sharedGeometry = true,
            matrix = matrix,
            isStatic = renderer.gameObject.isStatic,
            seen = true,
            category = BasisTracedCategory.For(renderer.gameObject.layer, BasisGlobalIlluminationSettings.AvatarLayers())
        };

        if (!AddInstances(entry, mesh, matrix))
        {
            ReleaseGeometry(entry);
            return;
        }

        entries[renderer.GetEntityId()] = entry;
        WriteMaterials(entry, settings);
        structureDirty = true;
        if (!entry.isStatic) { dynamicListDirty = true; }
    }

    private bool AddInstances(Entry entry, Mesh mesh, in Matrix4x4 matrix)
    {
        int subMeshCount = mesh.subMeshCount;
        entry.handles = new int[subMeshCount];
        entry.instanceIds = new int[subMeshCount];
        for (int index = 0; index < subMeshCount; index++)
        {
            entry.handles[index] = -1;
            entry.instanceIds[index] = -1;
        }

        for (int index = 0; index < subMeshCount; index++)
        {
            int instanceId = AllocateInstanceId();
            if (instanceId < 0) { RemoveInstances(entry); return false; }

            try
            {
                MeshInstanceDesc desc = new MeshInstanceDesc(mesh, index)
                {
                    localToWorldMatrix = matrix,
                    // Which half of the room this instance belongs to, so a borrower tracing only Avatars
                    // or only World does not hit the half it did not ask for. See BasisTracedCategory.
                    mask = entry.category,
                    instanceID = (uint)instanceId,
                    enableTriangleCulling = false,
                    opaqueGeometry = true
                };
                entry.handles[index] = accelStruct.AddInstance(desc);
            }
            catch (Exception)
            {
                freeInstanceIds.Add(instanceId);
                RemoveInstances(entry);
                return false;
            }

            entry.instanceIds[index] = instanceId;
            BasisGlobalIlluminationRayArena.Block indices = entry.geometry.indices != null && index < entry.geometry.indices.Length
                ? entry.geometry.indices[index]
                : BasisGlobalIlluminationRayArena.Block.None;

            instances[instanceId].indexOffset = (uint)indices.Offset;
            instances[instanceId].indexCount = (uint)indices.Count;
            instances[instanceId].vertexOffset = (uint)entry.geometry.normals.Offset;
            instances[instanceId].flags = entry.geometry.hasNormals && indices.IsValid ? BasisGlobalIlluminationRayInstance.FlagHasNormals : 0u;
            instances[instanceId].SetNormalMatrix(matrix);
            MarkInstanceDirty(instanceId);
        }
        return true;
    }

    private void RemoveInstances(Entry entry)
    {
        if (entry.handles == null) { return; }
        for (int index = 0; index < entry.handles.Length; index++)
        {
            if (entry.handles[index] >= 0) { accelStruct.RemoveInstance(entry.handles[index]); }
            entry.handles[index] = -1;
            if (entry.instanceIds[index] < 0) { continue; }

            instances[entry.instanceIds[index]] = default;
            MarkInstanceDirty(entry.instanceIds[index]);
            freeInstanceIds.Add(entry.instanceIds[index]);
            entry.instanceIds[index] = -1;
        }
    }

    private int AllocateInstanceId()
    {
        if (freeInstanceIds.Count > 0)
        {
            int reused = freeInstanceIds[freeInstanceIds.Count - 1];
            freeInstanceIds.RemoveAt(freeInstanceIds.Count - 1);
            return reused;
        }
        if (instanceHighWater >= MaxInstances) { return -1; }
        if (instanceHighWater >= instances.Length)
        {
            Array.Resize(ref instances, Mathf.Min(MaxInstances, instances.Length * 2));
            instanceBufferResized = true;
        }
        return instanceHighWater++;
    }

    private void MarkInstanceDirty(int instanceId)
    {
        instanceDirtyStart = Mathf.Min(instanceDirtyStart, instanceId);
        instanceDirtyEnd = Mathf.Max(instanceDirtyEnd, instanceId + 1);
    }

    private MeshGeometry AcquireGeometry(Mesh mesh)
    {
        EntityId key = mesh.GetEntityId();
        if (meshCache.TryGetValue(key, out MeshGeometry cached))
        {
            cached.references++;
            return cached;
        }

        MeshGeometry built = BuildGeometry(mesh);
        built.references = 1;
        meshCache.Add(key, built);
        return built;
    }

    /// <summary>
    /// Copies the mesh's vertex normals and triangle indices into the shared arenas. A mesh that shipped with
    /// Read/Write disabled cannot be read back, so it still occludes and still bounces its material colour -
    /// the trace just falls back to a view facing normal on it.
    /// </summary>
    private MeshGeometry BuildGeometry(Mesh mesh)
    {
        MeshGeometry geometry = new MeshGeometry { vertexCount = mesh.vertexCount };
        if (!mesh.isReadable) { return geometry; }

        try
        {
            mesh.GetNormals(normalScratch);
            if (normalScratch.Count == mesh.vertexCount)
            {
                geometry.normals = normalArena.Allocate(mesh.vertexCount);
                WriteNormals(geometry.normals);
                geometry.hasNormals = true;
            }

            geometry.indices = new BasisGlobalIlluminationRayArena.Block[mesh.subMeshCount];
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) { continue; }
                mesh.GetIndices(indexScratch, subMesh);
                if (indexScratch.Count == 0) { continue; }

                BasisGlobalIlluminationRayArena.Block block = indexArena.Allocate(indexScratch.Count);
                uint[] target = indexArena.Data;
                for (int index = 0; index < indexScratch.Count; index++)
                {
                    target[block.Offset + index] = (uint)indexScratch[index];
                }
                indexArena.MarkDirty(block);
                geometry.indices[subMesh] = block;
            }
        }
        catch (Exception)
        {
            ReleaseGeometryBlocks(geometry);
            return new MeshGeometry { vertexCount = mesh.vertexCount };
        }

        return geometry;
    }

    private void WriteNormals(in BasisGlobalIlluminationRayArena.Block block)
    {
        uint[] target = normalArena.Data;
        int count = Mathf.Min(block.Count, normalScratch.Count);
        for (int index = 0; index < count; index++)
        {
            target[block.Offset + index] = PackNormal(normalScratch[index]);
        }
        normalArena.MarkDirty(block);
    }

    /// <summary>Octahedral normal packed into two signed 16 bit halves, unpacked by the trace kernel.</summary>
    public static uint PackNormal(Vector3 normal)
    {
        float sum = Mathf.Abs(normal.x) + Mathf.Abs(normal.y) + Mathf.Abs(normal.z);
        if (sum < 1e-6f) { return 0u; }

        float x = normal.x / sum;
        float y = normal.y / sum;
        if (normal.z < 0f)
        {
            float wrappedX = 1f - Mathf.Abs(y);
            float wrappedY = 1f - Mathf.Abs(x);
            x = x >= 0f ? wrappedX : -wrappedX;
            y = y >= 0f ? wrappedY : -wrappedY;
        }

        int quantisedX = Mathf.Clamp(Mathf.RoundToInt(x * 32767f), -32767, 32767);
        int quantisedY = Mathf.Clamp(Mathf.RoundToInt(y * 32767f), -32767, 32767);
        return (uint)(quantisedX & 0xffff) | ((uint)(quantisedY & 0xffff) << 16);
    }

    public static Vector3 UnpackNormal(uint packed)
    {
        int quantisedX = (int)(packed & 0xffff);
        int quantisedY = (int)(packed >> 16);
        if (quantisedX > 32767) { quantisedX -= 65536; }
        if (quantisedY > 32767) { quantisedY -= 65536; }

        float x = quantisedX / 32767f;
        float y = quantisedY / 32767f;
        float z = 1f - Mathf.Abs(x) - Mathf.Abs(y);
        float fold = Mathf.Clamp01(-z);
        x += x >= 0f ? -fold : fold;
        y += y >= 0f ? -fold : fold;
        return new Vector3(x, y, z).normalized;
    }

    private void WriteMaterials(Entry entry, in BasisGlobalIlluminationRaySceneSettings settings)
    {
        if (entry.renderer == null || entry.instanceIds == null) { return; }

        materialScratch.Clear();
        entry.renderer.GetSharedMaterials(materialScratch);
        bool avatarEntry = (BasisGlobalIlluminationSettings.AvatarLayers() & (1 << entry.renderer.gameObject.layer)) != 0;
        // Shared across every sub-mesh of this entry, so the renderer's block state is answered once for
        // the whole walk rather than once per sub-mesh. See RendererBlocks.
        RendererBlocks blocks = RendererBlocks.For(entry.renderer);
        for (int index = 0; index < entry.instanceIds.Length; index++)
        {
            int instanceId = entry.instanceIds[index];
            if (instanceId < 0) { continue; }

            Material material = index < materialScratch.Count ? materialScratch[index] : null;
            int blockIndex = index < materialScratch.Count ? index : -1;
            ReadSurface(material, entry.renderer, blockIndex, ref blocks, settings, textures, out Color albedo, out Color emission);
            Vector4 packedAlbedo = new Vector4(albedo.r, albedo.g, albedo.b, 1f);
            float emissionScale = avatarEntry ? settings.avatarEmissionScale : settings.emissionScale;
            Vector4 packedEmission = new Vector4(emission.r * emissionScale, emission.g * emissionScale, emission.b * emissionScale, 0f);
            if (instances[instanceId].albedo == packedAlbedo && instances[instanceId].emission == packedEmission) { continue; }

            instances[instanceId].albedo = packedAlbedo;
            instances[instanceId].emission = packedEmission;
            MarkInstanceDirty(instanceId);
        }
        materialScratch.Clear();
    }

    /// <summary>
    /// Whether this surface's light is already sitting in a lightmap.
    ///
    /// Both halves are required. `BakedEmissive` alone only says the author intended it to be baked - the
    /// world may never have been baked at all, and then refusing the emission would lose the light
    /// entirely. A lightmap index alone only says the renderer is baked - its emission may be realtime.
    /// Together they say this exact renderer was baked with this emission folded into it.
    /// </summary>
    public static bool IsBakedEmissive(Material material, Renderer renderer)
    {
        if (material == null || renderer == null) { return false; }
        if ((material.globalIlluminationFlags & MaterialGlobalIlluminationFlags.BakedEmissive) == 0) { return false; }
        return renderer.lightmapIndex >= 0 && renderer.lightmapIndex < LightmapSettings.lightmaps.Length;
    }

    /// <summary>
    /// The colour a hit on this material bounces and the light it emits on its own. Textures are folded in as
    /// an average because a hit only carries a per-instance colour, and almost every lit material leaves its
    /// base colour white and puts the actual colour in the map.
    /// </summary>
    public static void ReadSurface(Material material, in BasisGlobalIlluminationRaySceneSettings settings, BasisGlobalIlluminationRayTextureAverage textures, out Color albedo, out Color emission)
    {
        ReadSurface(material, null, -1, settings, textures, out albedo, out emission);
    }

    /// <summary>
    /// The same read with the renderer's MaterialPropertyBlock overrides on top. A block is the usual way a
    /// colour - emission above all - is driven at runtime, and it never touches the material, so a gather
    /// reading the material alone sees a surface pinned at whatever it was authored with while the frame
    /// plainly shows it changing. Only colours are taken from the block: a block-overridden map would have
    /// to be averaged and versioned like the material's own, and emission is driven by colour.
    /// </summary>
    public static void ReadSurface(Material material, Renderer renderer, int materialIndex,
        in BasisGlobalIlluminationRaySceneSettings settings, BasisGlobalIlluminationRayTextureAverage textures,
        out Color albedo, out Color emission)
    {
        RendererBlocks blocks = RendererBlocks.For(renderer);
        ReadSurface(material, renderer, materialIndex, ref blocks, settings, textures, out albedo, out emission);
    }

    /// <summary>
    /// The same read against a renderer's block state that a caller walking several sub-meshes can carry
    /// between them. A renderer's blocks do not change from one of its sub-meshes to the next.
    /// </summary>
    private static void ReadSurface(Material material, Renderer renderer, int materialIndex,
        ref RendererBlocks blocks,
        in BasisGlobalIlluminationRaySceneSettings settings, BasisGlobalIlluminationRayTextureAverage textures,
        out Color albedo, out Color emission)
    {
        albedo = Color.white;
        emission = Color.black;
        if (material == null) { return; }

        MaterialPropertyBlock block = blocks.Resolve(materialIndex);

        if (material.HasColor(BaseColorId)) { albedo = material.GetColor(BaseColorId); }
        else if (material.HasColor(ColorId)) { albedo = material.GetColor(ColorId); }

        if (block != null)
        {
            if (TryGetBlockColor(block, BaseColorId, out Color overriddenAlbedo)) { albedo = overriddenAlbedo; }
            else if (TryGetBlockColor(block, ColorId, out overriddenAlbedo)) { albedo = overriddenAlbedo; }
        }

        if (settings.textureAlbedo)
        {
            Texture baseMap = material.HasTexture(BaseMapId) ? material.GetTexture(BaseMapId) : null;
            if (baseMap == null && material.HasTexture(MainTexId)) { baseMap = material.GetTexture(MainTexId); }
            if (baseMap != null && textures != null) { albedo *= textures.Get(baseMap); }
        }

        albedo = new Color(Mathf.Clamp01(albedo.r), Mathf.Clamp01(albedo.g), Mathf.Clamp01(albedo.b), 1f);

        if (!settings.emissiveSurfaces) { return; }

        // A surface whose emission was BAKED, on a renderer that actually carries a lightmap, has already
        // delivered every photon it is going to deliver - into that lightmap, at bake time. Injecting it
        // again as a realtime source lights the room twice from one lamp, which is the usual way an
        // emissive quad used as an area light reads far too bright once this effect is switched on.
        //
        // Gated on BOTH halves, and that is what makes it safe next to the note below about not trusting
        // globalIlluminationFlags on its own. The worry there is a stale flag on a material whose emission
        // is driven at runtime; a renderer with a lightmap index is baked static geometry, and driving its
        // emission at runtime is already broken for the lightmap that geometry is lit by. Dynamic
        // renderers, realtime emission and unbaked worlds all keep the live material read below.
        if (settings.respectBakedEmission && IsBakedEmissive(material, renderer)) { return; }

        Color blockEmission = Color.black;
        bool hasBlockEmission = block != null && TryGetBlockColor(block, EmissionColorId, out blockEmission);
        if (!hasBlockEmission && !material.HasColor(EmissionColorId)) { return; }

        // Deliberately NOT gated on globalIlluminationFlags. That flag is written by the shader GUI when the
        // material is authored and nothing refreshes it afterwards, so a surface whose _EmissionColor is
        // raised at runtime keeps reporting EmissiveIsBlack forever: it visibly glows in the frame and the
        // bounce never sees a photon of it. Everything below is live material state, which is what the
        // surface is actually rendering with.
        //
        // The keyword is only honoured where the shader declares one - URP's Lit multiplies its emission by
        // it, so a material with the box unchecked genuinely emits nothing and reading its leftover colour
        // would light the room from a surface that is black on screen. Shaders that emit without declaring
        // _EMISSION (Poiyomi among them) have no such switch to read, and there the colour is the only
        // honest answer.
        LocalKeyword emissionKeyword = ResolveEmissionKeyword(material.shader);
        if (emissionKeyword.isValid && !material.IsKeywordEnabled(emissionKeyword)) { return; }

        // Absent on the material means no switch to fail, not a switch that is off - the original read only
        // applied this gate where the property existed. A block may drive it like any other property.
        float emissionEnabled = material.HasFloat(EmissionEnabledId) ? material.GetFloat(EmissionEnabledId) : 1f;
        if (block != null && block.HasFloat(EmissionEnabledId)) { emissionEnabled = block.GetFloat(EmissionEnabledId); }
        if (emissionEnabled < 0.5f) { return; }

        emission = hasBlockEmission ? blockEmission : material.GetColor(EmissionColorId);
        if (settings.textureAlbedo && material.HasTexture(EmissionMapId))
        {
            Texture emissionMap = material.GetTexture(EmissionMapId);
            if (emissionMap != null && textures != null) { emission *= textures.Get(emissionMap); }
        }
        emission = new Color(Mathf.Max(0f, emission.r), Mathf.Max(0f, emission.g), Mathf.Max(0f, emission.b), 0f);
    }

    private static LocalKeyword ResolveEmissionKeyword(Shader shader)
    {
        if (shader == null) { return default; }
        if (emissionKeywords.TryGetValue(shader, out LocalKeyword cached)) { return cached; }
        LocalKeyword keyword = shader.keywordSpace.FindKeyword(EmissionKeyword);
        emissionKeywords[shader] = keyword;
        return keyword;
    }

    /// <summary>
    /// A colour out of a property block, however it was put there. SetColor and SetVector write the same
    /// slot, and a tint driven with SetVector is still the emission the surface renders.
    /// </summary>
    private static bool TryGetBlockColor(MaterialPropertyBlock block, int nameId, out Color value)
    {
        if (block.HasColor(nameId)) { value = block.GetColor(nameId); return true; }
        if (block.HasVector(nameId))
        {
            Vector4 vector = block.GetVector(nameId);
            value = new Color(vector.x, vector.y, vector.z, vector.w);
            return true;
        }

        value = Color.black;
        return false;
    }

    private void RemoveEntry(EntityId id, Entry entry)
    {
        RemoveInstances(entry);
        ReleaseGeometry(entry);
        entries.Remove(id);
        structureDirty = true;
        if (!entry.isStatic) { dynamicListDirty = true; }
    }

    private void ReleaseGeometry(Entry entry)
    {
        if (entry.geometry == null) { return; }
        if (!entry.sharedGeometry)
        {
            ReleaseGeometryBlocks(entry.geometry);
            entry.geometry = null;
            return;
        }

        entry.geometry.references--;
        if (entry.geometry.references <= 0 && entry.sourceMesh != null)
        {
            meshCache.Remove(entry.sourceMesh.GetEntityId());
            ReleaseGeometryBlocks(entry.geometry);
        }
        entry.geometry = null;
    }

    private void ReleaseGeometryBlocks(MeshGeometry geometry)
    {
        normalArena.Release(geometry.normals);
        geometry.normals = BasisGlobalIlluminationRayArena.Block.None;
        geometry.hasNormals = false;
        if (geometry.indices == null) { return; }
        for (int index = 0; index < geometry.indices.Length; index++) { indexArena.Release(geometry.indices[index]); }
        geometry.indices = null;
    }

    private void UpdateTransforms()
    {
        if (dynamicListDirty)
        {
            RebuildDynamicList();
        }
        else if (dynamicScheduled)
        {
            dynamicHandle.Complete();
            dynamicScheduled = false;
            int count = dynamicEntries.Count;
            for (int index = 0; index < count; index++)
            {
                Entry entry = dynamicEntries[index];
                if (entry.transform == null) { continue; }
                ApplyEntryMatrix(entry, dynamicMatrices[index]);
            }
            return;
        }

        // The set changed this frame, or no gather was in flight yet — read on the
        // main thread exactly as before.
        foreach (KeyValuePair<EntityId, Entry> pair in entries)
        {
            Entry entry = pair.Value;
            if (entry.isStatic || entry.transform == null) { continue; }
            ApplyEntryMatrix(entry, entry.transform.localToWorldMatrix);
        }
    }

    private void ApplyEntryMatrix(Entry entry, in Matrix4x4 matrix)
    {
        if (matrix == entry.matrix) { return; }

        entry.matrix = matrix;
        for (int index = 0; index < entry.handles.Length; index++)
        {
            if (entry.handles[index] < 0) { continue; }
            accelStruct.UpdateInstanceTransform(entry.handles[index], matrix);
            instances[entry.instanceIds[index]].SetNormalMatrix(matrix);
            MarkInstanceDirty(entry.instanceIds[index]);
        }
        structureDirty = true;
    }

    /// <summary>
    /// Finds the humanoids whose capsules belong in the structure, and drops the ones that have gone.
    ///
    /// Discovery is by Animator rather than by renderer because the bone map is what the capsules hang on,
    /// and it runs on the same rescan cadence as everything else. A non-humanoid avatar resolves to nothing
    /// and is simply absent - a body-shaped guess at a rig this cannot read would be worse than no bounce.
    /// </summary>
    private void RescanProxies(in BasisGlobalIlluminationRaySceneSettings settings, float interval)
    {
        foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies) { pair.Value.seen = false; }

        // Shared with ray traced ambient occlusion, which discovers the same humanoids the same way.
        Animator[] animators = BasisSceneScan.Take<Animator>(interval);
        for (int index = 0; index < animators.Length; index++)
        {
            Animator animator = animators[index];
            if (animator == null || !animator.isHuman) { continue; }
            if ((settings.layerMask.value & (1 << animator.gameObject.layer)) == 0) { continue; }

            EntityId id = animator.GetEntityId();
            if (proxies.TryGetValue(id, out ProxyEntry existing))
            {
                existing.seen = true;
                WriteProxyMaterials(existing, settings);
                continue;
            }

            BasisAvatarProxyPose pose = BasisAvatarProxy.PoseFor(animator);
            if (pose == null || pose.Count == 0) { continue; }
            if (instanceHighWater + pose.Count >= MaxInstances) { continue; }
            AddProxy(animator, pose, settings);
        }

        proxyRemoval.Clear();
        foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
        {
            if (!pair.Value.seen || pair.Value.animator == null) { proxyRemoval.Add(pair.Key); }
        }
        for (int index = 0; index < proxyRemoval.Count; index++)
        {
            if (proxies.TryGetValue(proxyRemoval[index], out ProxyEntry dead)) { RemoveProxy(proxyRemoval[index], dead); }
        }
        proxyRemoval.Clear();
    }

    private void AddProxy(Animator animator, BasisAvatarProxyPose pose, in BasisGlobalIlluminationRaySceneSettings settings)
    {
        Mesh capsule = BasisAvatarProxy.SharedCapsule();
        if (!IsUsableMesh(capsule)) { return; }

        pose.Update(Time.renderedFrameCount);
        ProxyEntry entry = new ProxyEntry { animator = animator, pose = pose, seen = true };
        // Every limb of every avatar is an instance of the same mesh, so this resolves to one cached
        // geometry and one BLAS for the whole room, however many people are in it.
        entry.geometry = AcquireGeometry(capsule);
        entry.handles = new int[pose.Count];
        entry.instanceIds = new int[pose.Count];

        for (int index = 0; index < pose.Count; index++)
        {
            entry.handles[index] = -1;
            entry.instanceIds[index] = -1;
        }

        for (int index = 0; index < pose.Count; index++)
        {
            int instanceId = AllocateInstanceId();
            if (instanceId < 0) { RemoveProxyInstances(entry); ReleaseGeometryBlocks(entry.geometry); return; }

            Matrix4x4 matrix = pose.MatrixAt(index);
            try
            {
                MeshInstanceDesc desc = new MeshInstanceDesc(capsule, 0)
                {
                    localToWorldMatrix = matrix,
                    // A proxy capsule is always a person, never the room - and a capsule rather than
                    // geometry anybody drew, which is its own bit. See BasisTracedCategory.
                    mask = BasisTracedCategory.AvatarProxy,
                    instanceID = (uint)instanceId,
                    enableTriangleCulling = false,
                    opaqueGeometry = true
                };
                entry.handles[index] = accelStruct.AddInstance(desc);
            }
            catch (Exception)
            {
                freeInstanceIds.Add(instanceId);
                RemoveProxyInstances(entry);
                ReleaseGeometryBlocks(entry.geometry);
                return;
            }

            entry.instanceIds[index] = instanceId;
            BasisGlobalIlluminationRayArena.Block indices = entry.geometry.indices != null && entry.geometry.indices.Length > 0
                ? entry.geometry.indices[0]
                : BasisGlobalIlluminationRayArena.Block.None;

            instances[instanceId].indexOffset = (uint)indices.Offset;
            instances[instanceId].indexCount = (uint)indices.Count;
            instances[instanceId].vertexOffset = (uint)entry.geometry.normals.Offset;
            instances[instanceId].flags = BasisGlobalIlluminationRayInstance.FlagProxy
                | (entry.geometry.hasNormals && indices.IsValid ? BasisGlobalIlluminationRayInstance.FlagHasNormals : 0u);
            instances[instanceId].SetNormalMatrixOrthogonal(matrix);
            MarkInstanceDirty(instanceId);
        }

        proxies[animator.GetEntityId()] = entry;
        WriteProxyMaterials(entry, settings);
        structureDirty = true;
    }

    /// <summary>
    /// What an avatar's capsules bounce. There is no per-limb material to read - a capsule is not a piece
    /// of the avatar's mesh - so the whole body takes one colour, read off the first renderer that has a
    /// usable material. A body's bounce is a soft wash of its overall colour at this resolution; the cost
    /// of getting that colour per limb would be a material read per limb per rescan for a difference the
    /// denoiser removes.
    /// </summary>
    private void WriteProxyMaterials(ProxyEntry entry, in BasisGlobalIlluminationRaySceneSettings settings)
    {
        if (entry.animator == null || entry.instanceIds == null) { return; }

        entry.representativeRenderer = null;
        Renderer[] renderers = entry.animator.GetComponentsInChildren<Renderer>(false);
        for (int index = 0; index < renderers.Length; index++)
        {
            Renderer renderer = renderers[index];
            if (renderer == null || renderer.sharedMaterial == null) { continue; }
            entry.representativeRenderer = renderer;
            break;
        }

        ApplyProxyMaterials(entry, settings);
    }

    /// <summary>
    /// Re-reads colour off the proxy's already-found representative renderer and pushes it to its capsules,
    /// without walking the avatar's hierarchy again. Split out of WriteProxyMaterials so the per frame block
    /// refresh below can call this alone - GetComponentsInChildren allocates a fresh array per avatar, which
    /// is fine once per rescan but would be needless garbage every frame for a room full of AudioLink avatars.
    /// </summary>
    private void ApplyProxyMaterials(ProxyEntry entry, in BasisGlobalIlluminationRaySceneSettings settings)
    {
        Color albedo = Color.grey;
        Color emission = Color.black;
        Renderer renderer = entry.representativeRenderer;
        Material material = renderer != null ? renderer.sharedMaterial : null;
        if (material != null)
        {
            // Slot 0, matching sharedMaterial: picks up a block set for slot 0 specifically, and falls back
            // to a whole-renderer block (see ResolveBlock) - either is how an accessory's AudioLink material
            // actually drives its emission, and neither touches the shared material asset itself.
            ReadSurface(material, renderer, 0, settings, textures, out albedo, out emission);
        }

        float scale = settings.avatarEmissionScale;
        Vector4 packedAlbedo = new Vector4(albedo.r, albedo.g, albedo.b, 1f);
        Vector4 packedEmission = new Vector4(emission.r * scale, emission.g * scale, emission.b * scale, 0f);

        for (int index = 0; index < entry.instanceIds.Length; index++)
        {
            int instanceId = entry.instanceIds[index];
            if (instanceId < 0) { continue; }
            if (instances[instanceId].albedo == packedAlbedo && instances[instanceId].emission == packedEmission) { continue; }
            instances[instanceId].albedo = packedAlbedo;
            instances[instanceId].emission = packedEmission;
            MarkInstanceDirty(instanceId);
        }
    }

    /// <summary>
    /// Every limb of every avatar, every frame. This is the whole point: no bake, no readback, no geometry
    /// change and so no BLAS rebuild - just the transform each capsule sits at. There is no budget and no
    /// cursor because there is nothing here expensive enough to need one.
    /// </summary>
    private void UpdateProxies(int frame)
    {
        foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
        {
            ProxyEntry entry = pair.Value;
            if (entry.animator == null || entry.handles == null || entry.pose == null) { continue; }

            // Idempotent: the frame hook has normally already sampled this, and the second caller in a
            // frame gets the same matrices rather than re-reading the bones at a different instant.
            entry.pose.Update(frame);

            for (int index = 0; index < entry.handles.Length && index < entry.pose.Count; index++)
            {
                if (entry.handles[index] < 0) { continue; }

                Matrix4x4 matrix = entry.pose.MatrixAt(index);
                accelStruct.UpdateInstanceTransform(entry.handles[index], matrix);

                int instanceId = entry.instanceIds[index];
                if (instanceId >= 0)
                {
                    instances[instanceId].SetNormalMatrixOrthogonal(matrix);
                    MarkInstanceDirty(instanceId);
                }
            }
            structureDirty = true;
        }
    }

    private void RemoveProxyInstances(ProxyEntry entry)
    {
        if (entry.handles == null) { return; }
        for (int index = 0; index < entry.handles.Length; index++)
        {
            if (entry.handles[index] >= 0)
            {
                accelStruct.RemoveInstance(entry.handles[index]);
                entry.handles[index] = -1;
            }
            if (entry.instanceIds[index] >= 0)
            {
                freeInstanceIds.Add(entry.instanceIds[index]);
                entry.instanceIds[index] = -1;
            }
        }
    }

    private void ClearProxies()
    {
        foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
        {
            RemoveProxyInstances(pair.Value);
            ReleaseGeometryBlocks(pair.Value.geometry);
        }
        proxies.Clear();
        proxyRemoval.Clear();
        structureDirty = true;
    }

    private void RemoveProxy(EntityId id, ProxyEntry entry)
    {
        RemoveProxyInstances(entry);
        ReleaseGeometryBlocks(entry.geometry);
        proxies.Remove(id);
        structureDirty = true;
    }

    private void Upload()
    {
        normalArena.Upload();
        indexArena.Upload();

        if (instanceHighWater == 0) { return; }
        if (instanceBuffer == null || instanceBuffer.count < instances.Length)
        {
            instanceBuffer?.Dispose();
            instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instances.Length, BasisGlobalIlluminationRayInstance.Stride)
            {
                name = "_BasisGIRtInstances"
            };
            instanceBufferResized = true;
        }

        if (instanceBufferResized)
        {
            instanceBuffer.SetData(instances, 0, 0, instanceHighWater);
            instanceBufferResized = false;
        }
        else if (instanceDirtyEnd > instanceDirtyStart)
        {
            int count = Mathf.Min(instanceDirtyEnd, instanceHighWater) - instanceDirtyStart;
            if (count > 0) { instanceBuffer.SetData(instances, instanceDirtyStart, instanceDirtyStart, count); }
        }

        instanceDirtyStart = int.MaxValue;
        instanceDirtyEnd = -1;
    }

    public void Build(CommandBuffer cmd)
    {
        if (accelStruct == null || cmd == null) { return; }
        GraphicsBuffer scratch = context.GetBuildScratch(accelStruct);
        accelStruct.Build(cmd, scratch);
        structureDirty = false;
        everBuilt = true;
    }

    public void Dispose()
    {
        Application.onBeforeRender -= ScheduleTransformGather;
        CompleteTransformGather();
        if (dynamicAccess.isCreated) { dynamicAccess.Dispose(); }
        if (dynamicMatrices.IsCreated) { dynamicMatrices.Dispose(); }
        dynamicEntries.Clear();
        dynamicListDirty = true;

        // No mesh is owned here any more: entries share the renderer's own mesh through the cache and the
        // proxies share one capsule, so there is nothing of this scene's to destroy on the way out.
        entries.Clear();
        meshCache.Clear();
        ClearProxies();
        pendingRemoval.Clear();
        freeInstanceIds.Clear();

        accelStruct?.Dispose();
        accelStruct = null;
        instanceBuffer?.Dispose();
        instanceBuffer = null;
        normalArena.Dispose();
        indexArena.Dispose();
        textures.Dispose();
        instanceHighWater = 0;
    }
}
