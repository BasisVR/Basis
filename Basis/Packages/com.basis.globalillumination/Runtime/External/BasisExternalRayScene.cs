using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

public unsafe sealed class BasisExternalRayScene : IDisposable
{
    public enum JobKind { Vertices, Positions, Indices, Texture }

    public struct Job
    {
        public JobKind kind;
        public GraphicsBuffer stream0, stream1, indices;
        public Texture source;
        public RenderTexture scratch;
        public uint first, count, texel, width, height, row, hdr;
        public Vector4Int layout0, layout1;
    }

    public struct Vector4Int { public int x, y, z, w; public Vector4Int(int x, int y, int z, int w) { this.x = x; this.y = y; this.z = z; this.w = w; } }

    private sealed class MeshRecord
    {
        public uint id;
        public Mesh mesh;
        public SkinnedMeshRenderer skinned;
        public int vertexCount, indexCount;
        public uint[] submeshFlags;
        public int[] submeshStarts, submeshCounts;
        public int uploadedVertices, uploadedIndices, submeshCursor;
        public bool defined, committed, posed, released;
        public int lastSeen, lastPosed, poseCopyFrame;
        public GraphicsBuffer poseCopy;
    }

    private sealed class TextureRecord
    {
        public uint id;
        public Texture texture;
        public int width, height, uploadedRows;
        public bool hdr, defined, committed;
        public int lastSeen;
    }

    private sealed class RendererRecord
    {
        public Renderer renderer;
        public Transform transform;
        public GameObject gameObject;
        public MeshFilter filter;
        public MeshRecord mesh;
        public Transform rootBone;
        public Material[] materialRefs = Array.Empty<Material>();
        public int id, phase, slot = -1, layer;
        public ShadowCastingMode shadowMode;
        public bool stale = true, hasBlock, avatar, seen, mirror, drawn, dead;
        public string skip = "new";
        public RendererRecord shadowSource, shadowClone;
    }

    private readonly Dictionary<Mesh, MeshRecord> staticMeshes = new Dictionary<Mesh, MeshRecord>();
    private readonly Dictionary<SkinnedMeshRenderer, MeshRecord> skinnedMeshes = new Dictionary<SkinnedMeshRenderer, MeshRecord>();
    private readonly Dictionary<Texture, TextureRecord> textures = new Dictionary<Texture, TextureRecord>();
    private readonly List<RendererRecord> renderers = new List<RendererRecord>();
    private readonly Dictionary<int, RendererRecord> rendererLookup = new Dictionary<int, RendererRecord>();
    private readonly Stack<uint> freeMeshIds = new Stack<uint>(), freeTextureIds = new Stack<uint>();
    private uint nextMeshId = 1, nextTextureId = 0;
    private readonly List<BasisExternalRayCommand> commands = new List<BasisExternalRayCommand>();
    public readonly List<Job> Jobs = new List<Job>();
    private readonly List<GraphicsBuffer> disposeNextFrame = new List<GraphicsBuffer>(), disposeNow = new List<GraphicsBuffer>();
    private readonly List<RenderTexture> scratchNextFrame = new List<RenderTexture>(), scratchNow = new List<RenderTexture>();
    private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
    private readonly List<Material> materialScratch = new List<Material>();
    private readonly List<(float power, uint instance, uint geometry, uint triangles)> emitterScratch = new List<(float, uint, uint, uint)>();
    private readonly List<Light> lights = new List<Light>();
    private readonly HashSet<int> lightIds = new HashSet<int>();
    private bool respectBaked = true;
    private readonly Dictionary<Light, Vector4> lightStates = new Dictionary<Light, Vector4>();
    private readonly Dictionary<Light, Vector4> lightReach = new Dictionary<Light, Vector4>();
    private readonly List<Light> lightsGone = new List<Light>();
    private readonly List<Vector4> dirty = new List<Vector4>();
    private bool resetCache = true;
    private Vector4 lastAmbient;
    private float nextScan;
    private int frameCount;
    private uint texelCursor;
    private bool uploadOpen = true;
    public bool UploadDeferred { get; private set; }
    public uint UploadTexels => texelCursor;
    public uint UploadRows => (texelCursor + (uint)BasisExternalRayTracer.UploadWidth - 1) / (uint)BasisExternalRayTracer.UploadWidth;
    public int MaxTextureSize = 512;
    public int MaxSkinnedVerticesPerFrame = 524288;
    private const float AvatarMoveThreshold = 0.03f * 0.03f;
    private readonly List<(MeshRecord mesh, uint instance, bool visible)> skinnedQueue = new List<(MeshRecord, uint, bool)>();
    private Comparison<(MeshRecord mesh, uint instance, bool visible)> byPriority;
    private readonly Dictionary<long, (BasisExternalRayMaterial, uint, float, int)> materialCache = new Dictionary<long, (BasisExternalRayMaterial, uint, float, int)>();
    private readonly List<long> materialsGone = new List<long>();
    private LightmapData[] frameLightmaps = System.Array.Empty<LightmapData>();
    private readonly Dictionary<Shader, bool> sharedTransformShaders = new Dictionary<Shader, bool>();
    public const int MaxMirrors = 8;
    public readonly Matrix4x4[] MirrorToLocal = new Matrix4x4[MaxMirrors];
    public readonly Vector4[] MirrorBounds = new Vector4[MaxMirrors * 2];
    public int MirrorCount;
    private static readonly int[] idMirrorTextures = { Shader.PropertyToID("_ReflectionTexLeft"), Shader.PropertyToID("_ReflectionTexRight"), Shader.PropertyToID("_ReflectionTex0"), Shader.PropertyToID("_ReflectionTex") };

    private static readonly int idBaseMap = Shader.PropertyToID("_BaseMap"), idMainTex = Shader.PropertyToID("_MainTex");
    private static readonly int idBaseColor = Shader.PropertyToID("_BaseColor"), idColor = Shader.PropertyToID("_Color");
    private static readonly int idEmissionColor = Shader.PropertyToID("_EmissionColor"), idEmissionMap = Shader.PropertyToID("_EmissionMap");
    private static readonly int idEmissionStrength = Shader.PropertyToID("_EmissionStrength"), idEnableEmission = Shader.PropertyToID("_EnableEmission"), idUseEmission = Shader.PropertyToID("_UseEmission");
    private static readonly int idEmissionBlend = Shader.PropertyToID("_EmissionBlend");
    private static readonly int idEnableEmission1 = Shader.PropertyToID("_EnableEmission1"), idEmissionColor1 = Shader.PropertyToID("_EmissionColor1"), idEmissionMap1 = Shader.PropertyToID("_EmissionMap1");
    private static readonly int idEmissionStrength1 = Shader.PropertyToID("_EmissionStrength1"), idEmissionBaseColorAsMap1 = Shader.PropertyToID("_EmissionBaseColorAsMap1");
    private static readonly int idGlobalMasksEnable = Shader.PropertyToID("_GlobalMaskTexturesEnable");
    private static readonly int[] idEmissionGlobalMask = { Shader.PropertyToID("_EmissionMask0GlobalMask"), Shader.PropertyToID("_EmissionMask1GlobalMask") };
    private static readonly int[] idEmissionGlobalMaskBlend = { Shader.PropertyToID("_EmissionMask0GlobalMaskBlendType"), Shader.PropertyToID("_EmissionMask1GlobalMaskBlendType") };
    private static readonly int[] idGlobalMaskTexture = { Shader.PropertyToID("_GlobalMaskTexture0"), Shader.PropertyToID("_GlobalMaskTexture1"), Shader.PropertyToID("_GlobalMaskTexture2"), Shader.PropertyToID("_GlobalMaskTexture3") };
    private static readonly int[] idGlobalMaskUV = { Shader.PropertyToID("_GlobalMaskTexture0UV"), Shader.PropertyToID("_GlobalMaskTexture1UV"), Shader.PropertyToID("_GlobalMaskTexture2UV"), Shader.PropertyToID("_GlobalMaskTexture3UV") };
    private static readonly int[] idGlobalMaskSplit = { Shader.PropertyToID("_GlobalMaskTexture0Split"), Shader.PropertyToID("_GlobalMaskTexture1Split"), Shader.PropertyToID("_GlobalMaskTexture2Split"), Shader.PropertyToID("_GlobalMaskTexture3Split") };
    private static readonly string[] channelNames = { "R", "G", "B", "A" };
    private static readonly int[] idEmissionMapUV = { Shader.PropertyToID("_EmissionMapUV"), Shader.PropertyToID("_EmissionMap1UV") };
    private static readonly int[] idEmissionMaskTexture = { Shader.PropertyToID("_EmissionMask"), Shader.PropertyToID("_EmissionMask1") };
    private static readonly int[] idEmissionMaskUV = { Shader.PropertyToID("_EmissionMaskUV"), Shader.PropertyToID("_EmissionMask1UV") };
    private static readonly int[] idEmissionMaskChannel = { Shader.PropertyToID("_EmissionMaskChannel"), Shader.PropertyToID("_EmissionMask1Channel") };
    private static readonly int[] idEmissionMaskInvert = { Shader.PropertyToID("_EmissionMaskInvert"), Shader.PropertyToID("_EmissionMaskInvert1") };

    private struct EmissionSlot
    {
        public Vector3 color;
        public int map, mask, channel;
        public bool invert, baseAsMap;
        public Vector4 mapST, maskST;
    }
    private static readonly int idEmissionBaseColorAsMap = Shader.PropertyToID("_EmissionBaseColorAsMap"), idEmissionMainStrength = Shader.PropertyToID("_EmissionMainStrength");
    private static readonly int idCull = Shader.PropertyToID("_Cull");
    private static readonly int idMetallic = Shader.PropertyToID("_Metallic"), idSmoothness = Shader.PropertyToID("_Smoothness"), idGlossiness = Shader.PropertyToID("_Glossiness");
    private static readonly Vector3[] ambientDirections = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
    private readonly Color[] ambientResults = new Color[6];

    public static uint FormatCode(VertexAttributeFormat format)
    {
        switch (format)
        {
            case VertexAttributeFormat.Float32: return 1;
            case VertexAttributeFormat.Float16: return 2;
            case VertexAttributeFormat.UNorm8: return 3;
            case VertexAttributeFormat.SNorm8: return 4;
            case VertexAttributeFormat.UNorm16: return 5;
            case VertexAttributeFormat.SNorm16: return 6;
            default: return 0;
        }
    }

    public BasisExternalRayScene() { UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        RenderPipelineManager.beginContextRendering += OnBeginContext; Application.onBeforeRender += ScheduleSpatial;
        pool = new NativeArray<PoolEntry>(1024, Allocator.Persistent); }

    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode) { resetCache = true; }

    private void MarkDirty(Vector3 centre, float radius, bool soft = false, bool clear = false)
    {
        if (dirty.Count >= BasisExternalRayTracer.MaxDirty) { if (!soft) { resetCache = true; } return; }
        dirty.Add(new Vector4(centre.x, centre.y, centre.z, clear ? radius + 100000f : soft ? -radius : radius));
    }

    public void Dispose()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        RenderPipelineManager.beginContextRendering -= OnBeginContext;
        Application.onBeforeRender -= ScheduleSpatial;
        DisposeSpatial();
        changes.Dispose();
        if (slots.IsCreated) { slots.Dispose(); }
        if (pool.IsCreated) { pool.Dispose(); }
        if (frameList.IsCreated) { frameList.Dispose(); }
        if (dirtyOut.IsCreated) { dirtyOut.Dispose(); }
        if (emitterOut.IsCreated) { emitterOut.Dispose(); }
        if (buildCounts.IsCreated) { buildCounts.Dispose(); }
        foreach (KeyValuePair<SkinnedMeshRenderer, MeshRecord> kv in skinnedMeshes) { kv.Value.poseCopy?.Dispose(); kv.Value.poseCopy = null; }
        foreach (GraphicsBuffer b in disposeNow) { b?.Dispose(); }
        foreach (GraphicsBuffer b in disposeNextFrame) { b?.Dispose(); }
        foreach (RenderTexture rt in scratchNow) { RenderTexture.ReleaseTemporary(rt); }
        foreach (RenderTexture rt in scratchNextFrame) { RenderTexture.ReleaseTemporary(rt); }
        disposeNow.Clear(); disposeNextFrame.Clear(); scratchNow.Clear(); scratchNextFrame.Clear();
    }

    private void Push(uint op, uint id, uint a = 0, uint b = 0, uint c = 0, uint d = 0)
    {
        if (commands.Count < BasisExternalRayTracer.MaxCommands) { commands.Add(new BasisExternalRayCommand { op = op, id = id, a = a, b = b, c = c, d = d }); }
    }

    private bool Reserve(uint texels, out uint at)
    {
        at = texelCursor;
        if (!uploadOpen) { UploadDeferred = true; return false; }
        if (texelCursor + texels > BasisExternalRayTracer.UploadTexels || commands.Count > BasisExternalRayTracer.MaxCommands - 8) { return false; }
        texelCursor += texels;
        return true;
    }

    private uint AllocateMeshId() => freeMeshIds.Count > 0 ? freeMeshIds.Pop() : nextMeshId++;

    private MeshRecord MeshFor(RendererRecord owner)
    {
        Renderer renderer = owner.renderer;
        if (renderer is SkinnedMeshRenderer skinned)
        {
            Mesh shared = skinned.sharedMesh;
            if (shared == null) { return null; }
            if (owner.mesh != null && !owner.mesh.released && ReferenceEquals(owner.mesh.mesh, shared) && ReferenceEquals(owner.mesh.skinned, skinned)) { return owner.mesh; }
            if (!skinnedMeshes.TryGetValue(skinned, out MeshRecord r) || r.mesh != shared)
            {
                if (r != null) { Release(r); }
                skinned.vertexBufferTarget |= GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource;
                r = Describe(shared);
                r.skinned = skinned;
                skinnedMeshes[skinned] = r;
            }
            return r;
        }
        if (owner.filter == null && !renderer.TryGetComponent(out owner.filter)) { return null; }
        Mesh mesh = owner.filter.sharedMesh;
        if (mesh == null) { return null; }
        if (owner.mesh != null && !owner.mesh.released && owner.mesh.skinned == null && ReferenceEquals(owner.mesh.mesh, mesh)) { return owner.mesh; }
        if (!staticMeshes.TryGetValue(mesh, out MeshRecord record))
        {
            record = Describe(mesh);
            staticMeshes[mesh] = record;
        }
        return record;
    }

    private MeshRecord Describe(Mesh mesh)
    {
        if ((mesh.vertexBufferTarget & GraphicsBuffer.Target.Raw) == 0) { mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw; }
        if ((mesh.indexBufferTarget & GraphicsBuffer.Target.Raw) == 0) { mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw; }
        int submeshes = mesh.subMeshCount;
        MeshRecord r = new MeshRecord { id = AllocateMeshId(), mesh = mesh, vertexCount = mesh.vertexCount, submeshFlags = new uint[submeshes], submeshStarts = new int[submeshes], submeshCounts = new int[submeshes] };
        int total = 0;
        for (int i = 0; i < submeshes; i++)
        {
            SubMeshDescriptor d = mesh.GetSubMesh(i);
            int count = d.topology == MeshTopology.Triangles ? d.indexCount : 0;
            r.submeshStarts[i] = total;
            r.submeshCounts[i] = count;
            r.submeshFlags[i] = SubmeshOpaqueDefault;
            total += count;
        }
        r.indexCount = total;
        return r;
    }

    private const uint SubmeshOpaqueDefault = BasisExternalRayTracer.SubmeshOpaque;

    private static uint MergeSubmeshFlags(uint current, uint wanted)
    {
        if ((wanted & BasisExternalRayTracer.SubmeshOpaque) != 0) { return current; }
        if ((current & BasisExternalRayTracer.SubmeshOpaque) != 0) { return wanted; }
        return current | wanted;
    }

    private MeshRecord Redefine(MeshRecord old)
    {
        MeshRecord fresh = Describe(old.mesh);
        fresh.skinned = old.skinned;
        fresh.lastSeen = old.lastSeen;
        Array.Copy(old.submeshFlags, fresh.submeshFlags, Mathf.Min(old.submeshFlags.Length, fresh.submeshFlags.Length));
        if (old.skinned != null) { skinnedMeshes[old.skinned] = fresh; }
        else { staticMeshes[old.mesh] = fresh; }
        Release(old);
        return fresh;
    }

    private void OnBeginContext(ScriptableRenderContext context, List<Camera> cameras) => SnapshotPoses();

    private void SnapshotPoses()
    {
        int now = Time.frameCount;
        if (snapshotFrame == now) { return; }
        snapshotFrame = now;
        foreach (KeyValuePair<SkinnedMeshRenderer, MeshRecord> kv in skinnedMeshes)
        {
            MeshRecord r = kv.Value;
            SkinnedMeshRenderer smr = r.skinned;
            if (smr == null || !r.committed || !smr.forceMatrixRecalculationPerRender) { continue; }
            GraphicsBuffer live = smr.GetVertexBuffer();
            if (live == null) { continue; }
            if ((live.target & GraphicsBuffer.Target.CopySource) == 0)
            {
                live.Dispose();
                smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource;
                continue;
            }
            if (r.poseCopy == null || r.poseCopy.count != live.count || r.poseCopy.stride != live.stride)
            {
                r.poseCopy?.Dispose();
                r.poseCopy = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopyDestination, live.count, live.stride);
            }
            Graphics.CopyBuffer(live, r.poseCopy);
            live.Dispose();
            r.poseCopyFrame = now;
        }
    }

    private int snapshotFrame = -1;

    private void Release(MeshRecord r)
    {
        r.poseCopy?.Dispose();
        r.poseCopy = null;
        r.released = true;
        if (r.defined) { Push(BasisExternalRayTracer.OpMeshRelease, r.id); }
        freeMeshIds.Push(r.id);
    }

    private bool UploadMesh(MeshRecord r)
    {
        if (r.committed) { return true; }
        Mesh mesh = r.mesh;
        if (mesh == null || r.vertexCount == 0 || r.indexCount == 0 || r.id >= BasisExternalRayTracer.MaxMeshes) { return false; }
        if (!r.defined)
        {
            if (commands.Count + r.submeshFlags.Length + 4 > BasisExternalRayTracer.MaxCommands) { return false; }
            Push(BasisExternalRayTracer.OpMeshDefine, r.id, (uint)r.vertexCount, (uint)r.indexCount, (uint)r.submeshFlags.Length);
            for (int i = 0; i < r.submeshFlags.Length; i++) { Push(BasisExternalRayTracer.OpMeshSubmesh, r.id, (uint)r.submeshStarts[i], (uint)r.submeshCounts[i], r.submeshFlags[i]); }
            r.defined = true;
        }
        if (!TryLayout(mesh, out Vector4Int layout0, out Vector4Int layout1, out int stream0, out int stream1)) { return false; }
        while (r.uploadedVertices < r.vertexCount)
        {
            uint want = (uint)Mathf.Min(r.vertexCount - r.uploadedVertices, 65536);
            uint available = (BasisExternalRayTracer.UploadTexels - texelCursor) / 2;
            uint count = Math.Min(want, available);
            if (count == 0 || !Reserve(count * 2, out uint at)) { return false; }
            GraphicsBuffer s0 = mesh.GetVertexBuffer(stream0);
            GraphicsBuffer s1 = stream1 == stream0 ? s0 : mesh.GetVertexBuffer(stream1);
            disposeNow.Add(s0);
            if (s1 != s0) { disposeNow.Add(s1); }
            Jobs.Add(new Job { kind = JobKind.Vertices, stream0 = s0, stream1 = s1, first = (uint)r.uploadedVertices, count = count, texel = at, layout0 = layout0, layout1 = layout1 });
            Push(BasisExternalRayTracer.OpMeshVertices, r.id, (uint)r.uploadedVertices, count, at);
            r.uploadedVertices += (int)count;
        }
        while (r.submeshCursor < r.submeshCounts.Length)
        {
            int submesh = r.submeshCursor;
            int count = r.submeshCounts[submesh];
            if (count == 0) { r.submeshCursor++; continue; }
            int done = r.uploadedIndices - r.submeshStarts[submesh];
            uint remaining = (uint)(count - done);
            uint texels = Math.Min((remaining + 3) / 4, BasisExternalRayTracer.UploadTexels - texelCursor);
            if (texels == 0 || !Reserve(texels, out uint at)) { return false; }
            uint n = Math.Min(remaining, texels * 4);
            SubMeshDescriptor d = mesh.GetSubMesh(submesh);
            GraphicsBuffer ib = mesh.GetIndexBuffer();
            disposeNow.Add(ib);
            Jobs.Add(new Job { kind = JobKind.Indices, indices = ib, first = (uint)(d.indexStart + done), count = n, texel = at, layout0 = new Vector4Int(mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4, d.baseVertex, 0, 0) });
            Push(BasisExternalRayTracer.OpMeshIndices, r.id, (uint)r.uploadedIndices, n, at);
            r.uploadedIndices += (int)n;
            if (r.uploadedIndices - r.submeshStarts[submesh] >= count) { r.submeshCursor++; }
        }
        Push(BasisExternalRayTracer.OpMeshCommit, r.id, r.skinned != null ? BasisExternalRayTracer.MeshDynamic : 0u);
        r.committed = true;
        return true;
    }

    private static bool TryLayout(Mesh mesh, out Vector4Int layout0, out Vector4Int layout1, out int stream0, out int stream1)
    {
        layout0 = default;
        layout1 = default;
        stream0 = mesh.GetVertexAttributeStream(VertexAttribute.Position);
        stream1 = stream0;
        if (stream0 < 0) { return false; }
        uint positionFormat = FormatCode(mesh.GetVertexAttributeFormat(VertexAttribute.Position));
        if (positionFormat != 1 && positionFormat != 2) { return false; }
        uint normalFormat = 0;
        int normalOffset = 0;
        if (mesh.HasVertexAttribute(VertexAttribute.Normal) && mesh.GetVertexAttributeStream(VertexAttribute.Normal) == stream0)
        {
            normalFormat = FormatCode(mesh.GetVertexAttributeFormat(VertexAttribute.Normal));
            normalOffset = mesh.GetVertexAttributeOffset(VertexAttribute.Normal);
        }
        layout0 = new Vector4Int(mesh.GetVertexBufferStride(stream0), mesh.GetVertexAttributeOffset(VertexAttribute.Position), normalOffset, (int)(positionFormat | (normalFormat << 8)));
        if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0))
        {
            stream1 = mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0);
            uint uv0 = FormatCode(mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord0)), uv1 = 0;
            int uv1Offset = 0;
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord1) && mesh.GetVertexAttributeStream(VertexAttribute.TexCoord1) == stream1)
            {
                uv1 = FormatCode(mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord1));
                uv1Offset = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord1);
            }
            layout1 = new Vector4Int(mesh.GetVertexBufferStride(stream1), mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0), uv1Offset, (int)(uv0 | (uv1 << 8)));
        }
        else
        {
            layout1 = new Vector4Int(layout0.x, 0, 0, 0);
        }
        return true;
    }

    private static Matrix4x4? SkinnedSpace(MeshRecord mesh) => mesh.skinned != null && mesh.posed && mesh.skinned.rootBone != null ? Matrix4x4.TRS(mesh.skinned.rootBone.position, mesh.skinned.rootBone.rotation, Vector3.one) : (Matrix4x4?)null;

    private unsafe void PoseSkinned(BasisExternalRayInstance* instances)
    {
        if (byPriority == null) { byPriority = (a, b) => Priority(b).CompareTo(Priority(a)); }
        SnapshotPoses();
        skinnedQueue.Sort(byPriority);
        int budget = MaxSkinnedVerticesPerFrame;
        bool first = true;
        foreach ((MeshRecord mesh, uint instance, bool visible) in skinnedQueue)
        {
            int stale = mesh.posed ? frameCount - mesh.lastPosed : int.MaxValue;
            if (!visible && stale < 8) { continue; }
            if (!first && mesh.vertexCount > budget) { continue; }
            if (!UploadSkinnedPose(mesh)) { continue; }
            first = false;
            budget -= mesh.vertexCount;
            mesh.lastPosed = frameCount;
            if (mesh.posed) { continue; }
            mesh.posed = true;
            if (SkinnedSpace(mesh) is Matrix4x4 space)
            {
                Rows(space, out Vector4 r0, out Vector4 r1, out Vector4 r2);
                instances[instance].row0 = r0;
                instances[instance].row1 = r1;
                instances[instance].row2 = r2;
            }
        }
    }

    private long Priority((MeshRecord mesh, uint instance, bool visible) entry)
    {
        long stale = entry.mesh.posed ? frameCount - entry.mesh.lastPosed : int.MaxValue;
        return entry.visible ? stale * 8 : stale;
    }

    private bool UploadSkinnedPose(MeshRecord r)
    {
        SkinnedMeshRenderer smr = r.skinned;
        if (smr == null || !r.committed) { return false; }
        bool copied = r.poseCopy != null && r.poseCopyFrame == Time.frameCount;
        GraphicsBuffer skinned = copied ? r.poseCopy : smr.GetVertexBuffer();
        if (skinned == null) { return false; }
        uint count = (uint)r.vertexCount;
        if (!Reserve(count, out uint at)) { if (!copied) { skinned.Dispose(); } return false; }
        if (!copied) { disposeNow.Add(skinned); }
        Mesh mesh = r.mesh;
        int normalOffset = mesh.HasVertexAttribute(VertexAttribute.Normal) ? 12 : 0;
        uint normalFormat = mesh.HasVertexAttribute(VertexAttribute.Normal) ? 1u : 0u;
        Jobs.Add(new Job { kind = JobKind.Positions, stream0 = skinned, stream1 = skinned, first = 0, count = count, texel = at, layout0 = new Vector4Int(skinned.stride, 0, normalOffset, (int)(1u | (normalFormat << 8))) });
        Push(BasisExternalRayTracer.OpMeshPositions, r.id, 0, count, at);
        Push(BasisExternalRayTracer.OpMeshRefit, r.id);
        return true;
    }

    private int TextureFor(Texture texture, bool hdr)
    {
        if (texture == null || texture.dimension != TextureDimension.Tex2D) { return -1; }
        if (!textures.TryGetValue(texture, out TextureRecord r))
        {
            uint id = freeTextureIds.Count > 0 ? freeTextureIds.Pop() : nextTextureId++;
            if (id >= BasisExternalRayTracer.MaxTextures) { return -1; }
            int limit = hdr ? Mathf.Max(MaxTextureSize, 1024) : MaxTextureSize;
            float scale = Mathf.Min(1f, (float)limit / Mathf.Max(texture.width, texture.height));
            r = new TextureRecord { id = id, texture = texture, hdr = hdr, width = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale)), height = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale)) };
            textures[texture] = r;
        }
        r.lastSeen = frameCount;
        if (r.committed) { return (int)r.id; }
        if (!r.defined)
        {
            Push(BasisExternalRayTracer.OpTextureDefine, r.id, (uint)r.width, (uint)r.height, r.hdr ? BasisExternalRayTracer.TextureHdr : 0u);
            r.defined = true;
        }
        RenderTexture scratch = null;
        while (r.uploadedRows < r.height)
        {
            uint rowsLeft = (uint)(r.height - r.uploadedRows);
            uint available = (BasisExternalRayTracer.UploadTexels - texelCursor) * 4 / (uint)r.width;
            uint rows = Math.Min(rowsLeft, available);
            if (rows == 0 || !Reserve((rows * (uint)r.width + 3) / 4, out uint at)) { textureMisses++; return -1; }
            if (scratch == null)
            {
                scratch = RenderTexture.GetTemporary(new RenderTextureDescriptor(r.width, r.height, r.hdr ? GraphicsFormat.R16G16B16A16_SFloat : GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.None) { msaaSamples = 1, useMipMap = false });
                scratchNow.Add(scratch);
            }
            Jobs.Add(new Job { kind = JobKind.Texture, source = texture, scratch = scratch, width = (uint)r.width, count = rows * (uint)r.width, texel = at, row = (uint)r.uploadedRows, hdr = r.hdr ? 1u : 0u });
            Push(BasisExternalRayTracer.OpTexturePixels, r.id, (uint)r.uploadedRows, rows, at);
            r.uploadedRows += (int)rows;
        }
        Push(BasisExternalRayTracer.OpTextureCommit, r.id);
        r.committed = true;
        return (int)r.id;
    }

    private static bool Has(Material m, int id) => m.HasProperty(id);

    private bool SharesBaseTransform(Shader shader)
    {
        if (shader == null) { return false; }
        if (!sharedTransformShaders.TryGetValue(shader, out bool shared)) { shared = shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal); sharedTransformShaders[shader] = shared; }
        return shared;
    }

    private Color ReadColor(Material m, bool hasBlock, int id, Color fallback)
    {
        if (hasBlock && block.HasColor(id)) { return block.GetColor(id); }
        if (hasBlock && block.HasVector(id)) { return block.GetVector(id); }
        return m.HasColor(id) || m.HasVector(id) ? m.GetColor(id) : fallback;
    }

    private float ReadFloat(Material m, bool hasBlock, int id, float fallback)
    {
        if (hasBlock && block.HasFloat(id)) { return block.GetFloat(id); }
        return m.HasFloat(id) ? m.GetFloat(id) : fallback;
    }

    private Texture ReadTexture(Material m, bool hasBlock, int id)
    {
        if (hasBlock && block.HasTexture(id)) { return block.GetTexture(id); }
        return m.HasTexture(id) ? m.GetTexture(id) : null;
    }

    private Vector4 ReadST(Material m, int id)
    {
        if (!m.HasTexture(id)) { return new Vector4(1, 1, 0, 0); }
        Vector2 s = m.GetTextureScale(id), o = m.GetTextureOffset(id);
        return new Vector4(s.x, s.y, o.x, o.y);
    }

    private BasisExternalRayMaterial ReadMaterialCached(Renderer renderer, bool hasBlock, bool lightmapped, int index, Material m, bool avatar, float emissionScale, out uint submeshFlags, out float emissionPower)
    {
        if (hasBlock || m == null) { return ReadMaterial(renderer, hasBlock, lightmapped, index, m, avatar, emissionScale, out submeshFlags, out emissionPower); }
        long group = emissionScale == worldScale ? 0L : emissionScale == localScale ? 1L : 2L;
        long key = ((long)m.GetHashCode() << 4) | (group << 2) | (avatar ? 2L : 0L) | (lightmapped ? 1L : 0L);
        if (materialCache.TryGetValue(key, out (BasisExternalRayMaterial material, uint flags, float power, int frame) hit) && frameCount - hit.frame <= RefreshMask) { submeshFlags = hit.flags; emissionPower = hit.power; return hit.material; }
        int misses = textureMisses;
        BasisExternalRayMaterial read = ReadMaterial(renderer, hasBlock, lightmapped, index, m, avatar, emissionScale, out submeshFlags, out emissionPower);
        if (textureMisses == misses) { materialCache[key] = (read, submeshFlags, emissionPower, frameCount); }
        return read;
    }

    private BasisExternalRayMaterial ReadMaterial(Renderer renderer, bool hasBlock, bool lightmapped, int index, Material m, bool avatar, float emissionScale, out uint submeshFlags, out float emissionPower)
    {
        submeshFlags = BasisExternalRayTracer.SubmeshOpaque;
        emissionPower = 0f;
        BasisExternalRayMaterial result = new BasisExternalRayMaterial { baseColor = Vector4.one, baseST = new Vector4(1, 1, 0, 0), emissionST = new Vector4(1, 1, 0, 0), baseTexture = -1, emissionTexture = -1, cutoff = 0.5f };
        if (m == null) { return result; }
        if (hasBlock) { renderer.GetPropertyBlock(block, index); }
        int baseId = m.HasTexture(idBaseMap) ? idBaseMap : idMainTex;
        int colorId = m.HasColor(idBaseColor) ? idBaseColor : idColor;
        result.baseColor = ReadColor(m, hasBlock, colorId, Color.white).linear;
        result.baseTexture = TextureFor(ReadTexture(m, hasBlock, baseId), false);
        result.baseST = ReadST(m, baseId);
        bool baked = respectBaked && !avatar && lightmapped && (m.globalIlluminationFlags & MaterialGlobalIlluminationFlags.BakedEmission) != 0;
        bool on0 = !baked && (m.IsKeywordEnabled("_EMISSION") || ReadFloat(m, hasBlock, idEnableEmission, 0f) > 0.5f || ReadFloat(m, hasBlock, idUseEmission, 0f) > 0.5f);
        bool on1 = !baked && Has(m, idEnableEmission1) && ReadFloat(m, hasBlock, idEnableEmission1, 0f) > 0.5f;
        EmissionSlot slot0 = default, slot1 = default;
        bool has0 = on0 && ReadEmissionSlot(m, hasBlock, 0, emissionScale, result.baseST, out slot0);
        bool has1 = on1 && ReadEmissionSlot(m, hasBlock, 1, emissionScale, result.baseST, out slot1);
        if (!has0 && has1) { slot0 = slot1; has0 = true; has1 = false; }
        if (has0)
        {
            result.emission = new Vector4(slot0.color.x, slot0.color.y, slot0.color.z, 0f);
            result.emissionTexture = slot0.map;
            result.emissionST = slot0.map >= 0 ? slot0.mapST : slot0.mask >= 0 ? slot0.maskST : result.baseST;
            if (slot0.mask >= 0) { result.emission.w = BitConverter.Int32BitsToSingle(0x40000000 | slot0.channel | (slot0.invert ? 4 : 0) | (slot0.mask << 8)); }
            if (slot0.baseAsMap) { result.emissionBase = 1f; }
            else if (Has(m, idEmissionMainStrength)) { result.emissionBase = Mathf.Clamp01(ReadFloat(m, hasBlock, idEmissionMainStrength, 0f)); }
            emissionPower = Mathf.Max(slot0.color.x, Mathf.Max(slot0.color.y, slot0.color.z));
        }
        uint secondary = 0;
        if (has1)
        {
            result.emission2 = PackRGB9E5(slot1.color);
            secondary = (uint)(slot1.map + 1) << 2 | (slot1.baseAsMap ? 0x8000u : 0u) | (uint)(slot1.mask + 1) << 16 | (uint)slot1.channel << 29 | (slot1.invert ? 0x80000000u : 0u);
            emissionPower += Mathf.Max(slot1.color.x, Mathf.Max(slot1.color.y, slot1.color.z));
        }
        BasisGlobalIlluminationAlphaKind kind = BasisGlobalIlluminationMaterialAlpha.Classify(m, hasBlock ? block : null, out result.cutoff);
        if (BasisGlobalIlluminationMaterialAlpha.IsBlend(kind))
        {
            result.alphaMode = BasisExternalRayTracer.AlphaBlend;
            submeshFlags = BasisExternalRayTracer.SubmeshBlend;
            if (BasisGlobalIlluminationMaterialAlpha.LightPassesThrough(m, hasBlock ? block : null)) { result.cutoff = 2f; }
        }
        else if (kind == BasisGlobalIlluminationAlphaKind.Cutout) { result.alphaMode = BasisExternalRayTracer.AlphaCutout; submeshFlags = BasisExternalRayTracer.SubmeshAlphaTest; }
        result.alphaMode |= secondary;
        result.metallic = ReadFloat(m, hasBlock, idMetallic, 0f);
        if (avatar) { result.flags |= BasisExternalRayTracer.MaterialAnimated; }
        if (m.doubleSidedGI || (Has(m, idCull) && ReadFloat(m, hasBlock, idCull, 2f) < 0.5f)) { result.flags |= BasisExternalRayTracer.MaterialDoubleSided; }
        return result;
    }

    private bool ReadEmissionSlot(Material m, bool hasBlock, int slot, float emissionScale, Vector4 baseST, out EmissionSlot result)
    {
        result = new EmissionSlot { map = -1, mask = -1, mapST = baseST, maskST = new Vector4(1, 1, 0, 0) };
        int colorProperty = slot == 0 ? idEmissionColor : idEmissionColor1, mapProperty = slot == 0 ? idEmissionMap : idEmissionMap1;
        int strengthProperty = slot == 0 ? idEmissionStrength : idEmissionStrength1, baseAsMapProperty = slot == 0 ? idEmissionBaseColorAsMap : idEmissionBaseColorAsMap1;
        if (!Has(m, colorProperty)) { return false; }
        Color e = ReadColor(m, hasBlock, colorProperty, Color.black);
        float strength = Has(m, strengthProperty) ? ReadFloat(m, hasBlock, strengthProperty, 1f) : 1f;
        Vector4 linear = (Vector4)e.linear * strength * emissionScale;
        result.color = new Vector3(Mathf.Max(linear.x, 0f), Mathf.Max(linear.y, 0f), Mathf.Max(linear.z, 0f));
        if (result.color.x + result.color.y + result.color.z <= 0f) { return false; }
        if (Mathf.RoundToInt(ReadFloat(m, hasBlock, idEmissionMapUV[slot], 0f)) == 0)
        {
            result.map = TextureFor(ReadTexture(m, hasBlock, mapProperty), false);
            if (Has(m, mapProperty) && !SharesBaseTransform(m.shader)) { result.mapST = ReadST(m, mapProperty); }
        }
        result.baseAsMap = Has(m, baseAsMapProperty) && ReadFloat(m, hasBlock, baseAsMapProperty, 0f) > 0.5f;
        Texture localMask = Has(m, idEmissionMaskTexture[slot]) && Mathf.RoundToInt(ReadFloat(m, hasBlock, idEmissionMaskUV[slot], 0f)) == 0 ? ReadTexture(m, hasBlock, idEmissionMaskTexture[slot]) : null;
        if (localMask != null && (result.mask = TextureFor(localMask, false)) >= 0)
        {
            result.channel = Mathf.Clamp(Mathf.RoundToInt(ReadFloat(m, hasBlock, idEmissionMaskChannel[slot], 0f)), 0, 3);
            result.invert = ReadFloat(m, hasBlock, idEmissionMaskInvert[slot], 0f) > 0.5f;
            result.maskST = ReadST(m, idEmissionMaskTexture[slot]);
            return true;
        }
        ReadGlobalMask(m, hasBlock, slot, ref result);
        return true;
    }

    private void ReadGlobalMask(Material m, bool hasBlock, int slot, ref EmissionSlot result)
    {
        if (!Has(m, idEmissionGlobalMask[slot]) || ReadFloat(m, hasBlock, idGlobalMasksEnable, 1f) < 0.5f) { return; }
        int index = Mathf.RoundToInt(ReadFloat(m, hasBlock, idEmissionGlobalMask[slot], 0f)) - 1;
        if (index < 0 || index > 15) { return; }
        int blend = Mathf.RoundToInt(ReadFloat(m, hasBlock, idEmissionGlobalMaskBlend[slot], 2f));
        if (blend != 0 && blend != 1 && blend != 2 && blend != 4) { return; }
        int texture = index >> 2, channel = index & 3;
        if (Mathf.RoundToInt(ReadFloat(m, hasBlock, idGlobalMaskUV[texture], 0f)) != 0) { return; }
        int id = TextureFor(ReadTexture(m, hasBlock, idGlobalMaskTexture[texture]), false);
        if (id < 0) { return; }
        Vector4 st = ReadST(m, idGlobalMaskTexture[texture]);
        if (ReadFloat(m, hasBlock, idGlobalMaskSplit[texture], 0f) > 0.5f)
        {
            string split = "_GlobalMaskTexture" + texture + "SplitTilingOffset_" + channelNames[channel];
            if (m.HasVector(split)) { st = m.GetVector(split); }
        }
        result.mask = id;
        result.channel = channel;
        result.invert = blend == 1;
        result.maskST = st;
    }

    private static uint PackRGB9E5(Vector3 c)
    {
        float m = Mathf.Max(Mathf.Max(Mathf.Min(c.x, 65408f), Mathf.Min(c.y, 65408f)), Mathf.Max(Mathf.Min(c.z, 65408f), 1e-9f));
        int e = Mathf.Max(Mathf.FloorToInt(Mathf.Log(m, 2f)), -16) + 1;
        float scale = Mathf.Pow(2f, e - 9);
        if ((uint)(m / scale + 0.5f) >= 512) { e++; scale *= 2f; }
        uint r = (uint)Mathf.Min(Mathf.Min(c.x, 65408f) / scale + 0.5f, 511f), g = (uint)Mathf.Min(Mathf.Min(c.y, 65408f) / scale + 0.5f, 511f), b = (uint)Mathf.Min(Mathf.Min(c.z, 65408f) / scale + 0.5f, 511f);
        return r | (g << 9) | (b << 18) | ((uint)Mathf.Clamp(e + 15, 0, 31) << 27);
    }

    private readonly BasisRendererChanges changes = new BasisRendererChanges();
    private readonly List<Renderer> changedRenderers = new List<Renderer>();
    private readonly List<Material> changedMaterials = new List<Material>();
    private readonly List<Light> changedLights = new List<Light>();
    private readonly HashSet<int> changedMaterialIds = new HashSet<int>();
    private readonly Dictionary<int, List<RendererRecord>> materialUsers = new Dictionary<int, List<RendererRecord>>();
    private int trackedMask = int.MinValue;
    private bool pairClones = true;

    private void Track(int mask)
    {
        int accept = mask & BasisGlobalIlluminationSettings.DefaultRayTracedLayers();
        bool remask = trackedMask != int.MinValue && trackedMask != accept;
        trackedMask = accept;
        changedRenderers.Clear(); changedMaterials.Clear(); changedLights.Clear();
        changes.Collect(changedRenderers, changedMaterials, changedLights);
        if (remask) { foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include)) { Consider(r); } }
        foreach (Renderer r in changedRenderers) { Consider(r); }
        foreach (Material m in changedMaterials)
        {
            if (ReferenceEquals(m, null)) { continue; }
            long key = (long)m.GetHashCode() << 4;
            for (long f = 0; f < 16; f++) { materialCache.Remove(key | f); }
            changedMaterialIds.Add(m.GetHashCode());
        }
        foreach (int id in changedMaterialIds) { if (materialUsers.TryGetValue(id, out List<RendererRecord> users)) { foreach (RendererRecord r in users) { r.stale = true; } } }
        changedMaterialIds.Clear();
        foreach (Light l in changedLights) { if (l != null && lightIds.Add(l.GetHashCode())) { lights.Add(l); } }
    }

    private void Consider(Renderer r)
    {
        if (r == null) { return; }
        bool accept = (r is MeshRenderer || r is SkinnedMeshRenderer) && (trackedMask & (1 << r.gameObject.layer)) != 0;
        int id = r.GetHashCode();
        if (rendererLookup.TryGetValue(id, out RendererRecord record))
        {
            if (!accept) { if (!record.dead) { record.dead = true; layoutChanged = true; } return; }
            if (record.dead) { record.dead = false; }
        }
        else
        {
            if (!accept) { return; }
            record = new RendererRecord { renderer = r, transform = r.transform, gameObject = r.gameObject, avatar = r is SkinnedMeshRenderer, phase = nextPhase++, id = id };
            rendererLookup[id] = record;
            renderers.Add(record);
            layoutChanged = true;
        }
        record.drawn = Drawn(record);
        record.layer = record.gameObject.layer;
        record.stale = true;
    }

    private void Housekeep()
    {
        materialsGone.Clear();
        foreach (KeyValuePair<long, (BasisExternalRayMaterial, uint, float, int)> kv in materialCache) { if (frameCount - kv.Value.Item4 > 600) { materialsGone.Add(kv.Key); } }
        foreach (long key in materialsGone) { materialCache.Remove(key); }
        for (int i = lights.Count - 1; i >= 0; i--) { if (lights[i] == null) { lightIds.Remove(lights[i].GetHashCode()); lights.RemoveAt(i); } }
        List<Mesh> dead = null;
        foreach (KeyValuePair<Mesh, MeshRecord> kv in staticMeshes) { if (kv.Key == null || frameCount - kv.Value.lastSeen > 600) { (dead ??= new List<Mesh>()).Add(kv.Key); } }
        if (dead != null) { foreach (Mesh m in dead) { Release(staticMeshes[m]); staticMeshes.Remove(m); } }
        List<Texture> deadTextures = null;
        foreach (KeyValuePair<Texture, TextureRecord> kv in textures) { if (kv.Key == null || frameCount - kv.Value.lastSeen > 600) { (deadTextures ??= new List<Texture>()).Add(kv.Key); } }
        if (deadTextures != null)
        {
            foreach (Texture t in deadTextures)
            {
                TextureRecord r = textures[t];
                if (r.defined) { Push(BasisExternalRayTracer.OpTextureRelease, r.id); }
                freeTextureIds.Push(r.id);
                textures.Remove(t);
            }
        }
    }

    private readonly Dictionary<(Mesh, Transform), RendererRecord> cloneKeys = new Dictionary<(Mesh, Transform), RendererRecord>();

    private void PairShadowClones()
    {
        cloneKeys.Clear();
        foreach (RendererRecord r in renderers)
        {
            r.shadowSource = r.shadowClone = null;
            if (r.renderer is SkinnedMeshRenderer s && s.shadowCastingMode == ShadowCastingMode.ShadowsOnly && s.sharedMesh != null) { cloneKeys[(s.sharedMesh, s.rootBone)] = r; }
        }
        if (cloneKeys.Count == 0) { return; }
        foreach (RendererRecord r in renderers)
        {
            if (!(r.renderer is SkinnedMeshRenderer s) || s.shadowCastingMode == ShadowCastingMode.ShadowsOnly || s.sharedMesh == null) { continue; }
            if (!cloneKeys.TryGetValue((s.sharedMesh, s.rootBone), out RendererRecord clone) || clone.shadowSource != null) { continue; }
            clone.shadowSource = r;
            r.shadowClone = clone;
        }
    }

    private void Unuse(RendererRecord record)
    {
        foreach (Material m in record.materialRefs) { if (!ReferenceEquals(m, null) && materialUsers.TryGetValue(m.GetHashCode(), out List<RendererRecord> users)) { users.Remove(record); if (users.Count == 0) { materialUsers.Remove(m.GetHashCode()); } } }
    }

    private bool IsMirror(RendererRecord record, List<Material> list)
    {
        Material[] known = record.materialRefs;
        bool same = known.Length == list.Count;
        for (int i = 0; same && i < known.Length; i++) { same = ReferenceEquals(known[i], list[i]); }
        if (same) { return record.mirror; }
        Unuse(record);
        if (known.Length != list.Count) { record.materialRefs = known = new Material[list.Count]; }
        for (int i = 0; i < known.Length; i++)
        {
            if (ReferenceEquals(list[i], null)) { continue; }
            int id = list[i].GetHashCode();
            if (!materialUsers.TryGetValue(id, out List<RendererRecord> users)) { materialUsers[id] = users = new List<RendererRecord>(); }
            if (!users.Contains(record)) { users.Add(record); }
        }
        bool mirror = false;
        for (int i = 0; i < known.Length; i++)
        {
            Material m = known[i] = list[i];
            if (mirror || m == null) { continue; }
            foreach (int id in idMirrorTextures) { if (m.HasProperty(id)) { mirror = true; break; } }
        }
        return mirror;
    }

    private void AddMirror(Renderer r)
    {
        if (MirrorCount >= MaxMirrors) { return; }
        Bounds local = r.localBounds;
        MirrorToLocal[MirrorCount] = r.transform.worldToLocalMatrix;
        MirrorBounds[MirrorCount * 2] = local.center;
        MirrorBounds[MirrorCount * 2 + 1] = local.extents;
        MirrorCount++;
    }

    private static bool Drawn(RendererRecord r) => r.renderer != null && r.renderer.enabled && r.gameObject.activeInHierarchy && !r.renderer.forceRenderingOff;

    private static void Rows(in Matrix4x4 m, out Vector4 r0, out Vector4 r1, out Vector4 r2)
    {
        r0 = new Vector4(m.m00, m.m01, m.m02, m.m03);
        r1 = new Vector4(m.m10, m.m11, m.m12, m.m13);
        r2 = new Vector4(m.m20, m.m21, m.m22, m.m23);
    }

    public void ClearUpload() { texelCursor = 0; commands.Clear(); }

    public void Reupload()
    {
        foreach (KeyValuePair<Mesh, MeshRecord> kv in staticMeshes) { Forget(kv.Value); }
        foreach (KeyValuePair<SkinnedMeshRenderer, MeshRecord> kv in skinnedMeshes) { Forget(kv.Value); }
        foreach (KeyValuePair<Texture, TextureRecord> kv in textures) { kv.Value.uploadedRows = 0; kv.Value.defined = kv.Value.committed = false; }
        materialCache.Clear();
    }

    private static void Forget(MeshRecord r)
    {
        r.uploadedVertices = r.uploadedIndices = r.submeshCursor = 0;
        r.defined = r.committed = r.posed = false;
    }

    private string DescribeAvatars()
    {
        int definedMeshes = 0, definedTextures = 0;
        foreach (KeyValuePair<Mesh, MeshRecord> kv in staticMeshes) { if (kv.Value.defined) { definedMeshes++; } }
        foreach (KeyValuePair<SkinnedMeshRenderer, MeshRecord> kv in skinnedMeshes) { if (kv.Value.defined) { definedMeshes++; } }
        foreach (KeyValuePair<Texture, TextureRecord> kv in textures) { if (kv.Value.defined) { definedTextures++; } }
        System.Text.StringBuilder text = new System.Text.StringBuilder(512);
        text.Append("[BasisGI] Path traced scene: tracer meshes ").Append(BasisExternalRayTracer.Stat(2)).Append(" of ").Append(definedMeshes).Append(", textures ").Append(BasisExternalRayTracer.Stat(3)).Append(" of ").Append(definedTextures).Append(", free ids ").Append(freeMeshIds.Count).Append('/').Append(freeTextureIds.Count).Append(", bridge '").Append(BasisExternalRayTracer.ReadError()).Append("', avatars:");
        int listed = 0, total = 0, healthy = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (RendererRecord r in renderers)
            {
                if (!r.avatar || r.renderer == null) { continue; }
                MeshRecord m = r.mesh;
                Slot slot = r.slot >= 0 && slots.IsCreated && r.slot < slots.Length ? slots[r.slot] : default;
                bool ok = r.skip.Length == 0 && m != null && m.posed && slot.lastMask != 0u && frameCount - m.lastPosed < 30;
                if (pass == 0) { total++; if (ok) { healthy++; } }
                if ((pass == 0) == ok || listed >= 24) { continue; }
                listed++;
                float glow = 0f;
                for (int e = 0; e < slot.submeshes; e++) { glow += pool[slot.materialOffset + e].lastPower; }
                text.Append(' ').Append(r.renderer.name).Append('[').Append(m == null ? "no mesh" : $"id {m.id} v{m.vertexCount} {(m.defined ? 'd' : '-')}{(m.committed ? 'c' : '-')}{(m.posed ? 'p' : '-')} posed {frameCount - m.lastPosed} ago").Append(" mask ").Append(slot.lastMask.ToString("X")).Append(r.skip.Length > 0 ? " skip " + r.skip : "").Append(r.seen ? " seen" : " unseen").Append(r.drawn ? "" : " off").Append(r.shadowSource != null ? " clone" : "").Append(r.renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly ? " shadowonly" : "").Append(r.renderer.isVisible ? "" : " invisible").Append(" glow ").Append(glow.ToString("0.##")).Append(']');
            }
            if (pass == 0) { text.Append(" | healthy ").Append(healthy).Append(" of ").Append(total).Append(':'); }
        }
        return text.ToString();
    }

    public void BeginFrame(bool uploadFree)
    {
        uploadOpen = uploadFree;
        UploadDeferred = false;
        foreach (GraphicsBuffer b in disposeNextFrame) { b?.Dispose(); }
        disposeNextFrame.Clear();
        disposeNextFrame.AddRange(disposeNow);
        disposeNow.Clear();
        foreach (RenderTexture rt in scratchNextFrame) { RenderTexture.ReleaseTemporary(rt); }
        scratchNextFrame.Clear();
        scratchNextFrame.AddRange(scratchNow);
        scratchNow.Clear();
        Jobs.Clear();
        commands.Clear();
        texelCursor = 0;
        frameCount++;
    }

    private struct Slot
    {
        public float3 localCenter, localExtents, lastCenter, lastExtents, center, extents;
        public float4 last0, last1, last2, row0, row1, row2, lightmapST;
        public int lightmap, materialOffset, submeshes, flags;
        public uint mesh, instance, materialBase, lastMask;
    }

    private struct PoolEntry
    {
        public BasisExternalRayMaterial material;
        public float power, lastPower;
        public uint triangles;
    }

    private struct SpatialOut { public float4 row0, row1, row2; public float3 center, extents; public int valid; }
    private struct EmitterCandidate { public float power; public uint instance, geometry, triangles; }

    private const int SlotSeen = 1, SlotAvatar = 2, SlotShadowOnly = 4, SlotNoShadow = 8, SlotUnposed = 16, SlotBounds = 32, SlotRows = 64, SlotRefreshed = 128, SlotKept = SlotAvatar | SlotShadowOnly;

    [BurstCompile]
    private struct SpatialJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<Slot> Slots;
        [WriteOnly] public NativeArray<SpatialOut> Output;

        public void Execute(int index, TransformAccess transform)
        {
            if (!transform.isValid) { Output[index] = default; return; }
            float4x4 m = transform.localToWorldMatrix;
            Slot s = Slots[index];
            float3 centre = math.mul(m, new float4(s.localCenter, 1f)).xyz;
            float3 extents = math.abs(m.c0.xyz) * s.localExtents.x + math.abs(m.c1.xyz) * s.localExtents.y + math.abs(m.c2.xyz) * s.localExtents.z;
            Output[index] = new SpatialOut { row0 = new float4(m.c0.x, m.c1.x, m.c2.x, m.c3.x), row1 = new float4(m.c0.y, m.c1.y, m.c2.y, m.c3.y), row2 = new float4(m.c0.z, m.c1.z, m.c2.z, m.c3.z), center = centre, extents = extents, valid = 1 };
        }
    }

    [BurstCompile]
    private struct BuildJob : IJob
    {
        [ReadOnly] public NativeArray<int> List;
        [ReadOnly] public NativeArray<SpatialOut> Spatial;
        public NativeArray<Slot> Slots;
        public NativeArray<PoolEntry> Pool;
        public NativeArray<float4> Dirty;
        public NativeArray<EmitterCandidate> Emitters;
        public NativeArray<int> Counts;
        [NativeDisableUnsafePtrRestriction] public BasisExternalRayInstance* Instances;
        [NativeDisableUnsafePtrRestriction] public BasisExternalRayMaterial* Materials;
        public float3 Viewer;
        public int Count;
        public bool EmittersOn;

        public void Execute()
        {
            int dirtyCount = 0, emitterCount = 0;
            for (int k = 0; k < Count; k++)
            {
                int index = List[k];
                Slot s = Slots[index];
                int flags = s.flags;
                bool avatar = (flags & SlotAvatar) != 0, seen = (flags & SlotSeen) != 0, shadowOnly = (flags & SlotShadowOnly) != 0, unposed = (flags & SlotUnposed) != 0, noShadow = (flags & SlotNoShadow) != 0, refreshed = (flags & SlotRefreshed) != 0;
                float4 r0, r1, r2;
                float3 c, e;
                if ((flags & SlotRows) != 0) { r0 = s.row0; r1 = s.row1; r2 = s.row2; }
                else { SpatialOut o = Spatial[index]; r0 = o.row0; r1 = o.row1; r2 = o.row2; }
                if ((flags & SlotBounds) != 0) { c = s.center; e = s.extents; }
                else { SpatialOut o = Spatial[index]; c = o.center; e = o.extents; }
                bool changed = false;
                if (!avatar)
                {
                    changed = math.any(math.abs(r0 - s.last0) > 1e-4f) || math.any(math.abs(r1 - s.last1) > 1e-4f) || math.any(math.abs(r2 - s.last2) > 1e-4f);
                    s.last0 = r0; s.last1 = r1; s.last2 = r2;
                }
                float emissive = 0f;
                for (int m = 0; m < s.submeshes; m++)
                {
                    PoolEntry p = Pool[s.materialOffset + m];
                    BasisExternalRayMaterial mat = p.material;
                    if (EmittersOn && p.power > 0.001f && !shadowOnly)
                    {
                        emissive += p.power;
                        if (p.triangles > 0 && !unposed)
                        {
                            float3 size = e * 2f;
                            float area = math.max(0.01f, size.x * size.y + size.y * size.z + size.x * size.z);
                            float distance = math.max(1f, math.lengthsq(c - Viewer));
                            Emitters[emitterCount++] = new EmitterCandidate { power = p.power * area / distance, instance = s.instance, geometry = (uint)m, triangles = p.triangles };
                            mat.flags |= BasisExternalRayTracer.MaterialEmitter;
                        }
                    }
                    Materials[s.materialBase + m] = mat;
                    if (refreshed)
                    {
                        float before = p.lastPower, now = p.power;
                        if (seen && math.abs(now - before) > 0.1f * math.max(now, before) + 0.01f)
                        {
                            bool large = math.max(now, before) > 2f * math.max(math.min(now, before), 0.01f);
                            Dirty[dirtyCount++] = Mark(c, math.length(e) + 2f + math.min(math.sqrt(math.max(now, before)) * 2f, 8f), !large);
                        }
                        p.lastPower = now;
                        Pool[s.materialOffset + m] = p;
                    }
                }
                bool moved = avatar && (math.lengthsq(c - s.lastCenter) > AvatarMoveThreshold || math.lengthsq(e - s.lastExtents) > AvatarMoveThreshold);
                if (!seen) { Dirty[dirtyCount++] = Mark(c, math.length(e) + 2f, false); }
                else if (moved || (!avatar && changed))
                {
                    bool emitting = emissive > 0.001f;
                    float reach = 1.5f + (emitting ? math.min(math.sqrt(emissive) * 2f, 8f) : 0f);
                    Dirty[dirtyCount++] = Mark(c, math.length(e) + reach, !emitting);
                    Dirty[dirtyCount++] = Mark(s.lastCenter, math.length(s.lastExtents) + reach, !emitting);
                }
                if (!seen || !avatar || moved) { s.lastCenter = c; s.lastExtents = e; }
                bool dynamic = avatar || s.lightmap < 0;
                uint mask = unposed ? 0u : shadowOnly ? 0x04u : (noShadow ? 0x01u : 0xFDu) | (dynamic ? BasisExternalRayTracer.MaskDynamic : 0u);
                Instances[s.instance] = new BasisExternalRayInstance
                {
                    row0 = r0, row1 = r1, row2 = r2, mesh = s.mesh, materialBase = s.materialBase, mask = mask,
                    flags = (avatar ? BasisExternalRayTracer.InstanceAvatar : 0u) | (noShadow ? BasisExternalRayTracer.InstanceNoShadow : 0u) | (dynamic ? BasisExternalRayTracer.InstanceDynamic : 0u),
                    lightmap = s.lightmap, lightmapST = s.lightmapST,
                };
                s.lastMask = mask;
                Slots[index] = s;
            }
            Counts[0] = dirtyCount;
            Counts[1] = emitterCount;
        }

        private static float4 Mark(float3 centre, float radius, bool soft) => new float4(centre, soft ? -radius : radius);
    }

    private const int RefreshMask = 63;
    private TransformAccessArray spatialAccess;
    private NativeArray<Slot> slots;
    private NativeArray<PoolEntry> pool;
    private NativeArray<SpatialOut> spatialOut;
    private NativeArray<int> frameList, buildCounts;
    private NativeArray<float4> dirtyOut;
    private NativeArray<EmitterCandidate> emitterOut;
    private int poolCount;
    private JobHandle spatialHandle;
    private bool spatialScheduled, layoutChanged = true;
    private int spatialFrame = -1, nextPhase, textureMisses, lightmapsFrame = -1;

    [BeforeRenderOrder(int.MaxValue)]
    private void ScheduleSpatial()
    {
        CompleteSpatial();
        if (layoutChanged || !spatialAccess.isCreated || spatialAccess.length == 0) { return; }
        spatialHandle = new SpatialJob { Slots = slots, Output = spatialOut }.ScheduleReadOnly(spatialAccess, 32);
        spatialScheduled = true;
        spatialFrame = Time.frameCount;
        JobHandle.ScheduleBatchedJobs();
    }

    private bool CompleteSpatial()
    {
        if (!spatialScheduled) { return false; }
        spatialHandle.Complete();
        spatialScheduled = false;
        return spatialFrame == Time.frameCount;
    }

    private void DisposeSpatial()
    {
        CompleteSpatial();
        if (spatialAccess.isCreated) { spatialAccess.Dispose(); }
        if (spatialOut.IsCreated) { spatialOut.Dispose(); }
    }

    private static void Ensure<T>(ref NativeArray<T> array, int length) where T : struct
    {
        if (array.IsCreated && array.Length >= length) { return; }
        if (array.IsCreated) { array.Dispose(); }
        array = new NativeArray<T>(Mathf.Max(Mathf.NextPowerOfTwo(length), 64), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
    }

    private int AllocatePool(int count)
    {
        if (poolCount + count > pool.Length)
        {
            NativeArray<PoolEntry> grown = new NativeArray<PoolEntry>(Mathf.Max(pool.Length * 2, poolCount + count), Allocator.Persistent);
            NativeArray<PoolEntry>.Copy(pool, grown, poolCount);
            pool.Dispose();
            pool = grown;
        }
        int at = poolCount;
        poolCount += count;
        return at;
    }

    private void RebuildSlots()
    {
        DisposeSpatial();
        Slot* old = slots.IsCreated ? (Slot*)slots.GetUnsafePtr() : null;
        for (int i = renderers.Count - 1; i >= 0; i--)
        {
            RendererRecord r = renderers[i];
            if (!r.dead && r.renderer != null) { continue; }
            if (r.seen && old != null && r.slot >= 0) { MarkDirty(old[r.slot].lastCenter, math.length(old[r.slot].lastExtents) + 2f); }
            rendererLookup.Remove(r.id);
            Unuse(r);
            if (r.mesh != null && r.mesh.skinned is SkinnedMeshRenderer smr) { Release(r.mesh); skinnedMeshes.Remove(smr); }
            renderers.RemoveAt(i);
        }
        int n = renderers.Count, used = 0;
        for (int i = 0; i < n; i++) { RendererRecord r = renderers[i]; if (r.slot >= 0 && old != null) { used += old[r.slot].submeshes; } }
        NativeArray<Slot> next = new NativeArray<Slot>(Mathf.Max(n, 1), Allocator.Persistent);
        NativeArray<PoolEntry> nextPool = new NativeArray<PoolEntry>(Mathf.Max(used * 2, 1024), Allocator.Persistent);
        PoolEntry* from = (PoolEntry*)pool.GetUnsafePtr(), to = (PoolEntry*)nextPool.GetUnsafePtr();
        Transform[] transforms = new Transform[n];
        int cursor = 0;
        for (int i = 0; i < n; i++)
        {
            RendererRecord r = renderers[i];
            Slot slot = r.slot >= 0 && old != null ? old[r.slot] : new Slot { lightmap = -1 };
            if (slot.submeshes > 0) { UnsafeUtility.MemCpy(to + cursor, from + slot.materialOffset, slot.submeshes * sizeof(PoolEntry)); }
            slot.materialOffset = cursor;
            cursor += slot.submeshes;
            next[i] = slot;
            r.slot = i;
            transforms[i] = r.transform;
        }
        if (slots.IsCreated) { slots.Dispose(); }
        pool.Dispose();
        slots = next;
        pool = nextPool;
        poolCount = cursor;
        spatialAccess = new TransformAccessArray(transforms);
        spatialOut = new NativeArray<SpatialOut>(Mathf.Max(n, 1), Allocator.Persistent);
        Ensure(ref frameList, n);
        layoutChanged = false;
        pairClones = true;
    }

    private LightmapData[] Lightmaps()
    {
        if (lightmapsFrame != frameCount) { frameLightmaps = LightmapSettings.lightmaps ?? System.Array.Empty<LightmapData>(); lightmapsFrame = frameCount; }
        return frameLightmaps;
    }

    private float worldScale = 1f, localScale = 1f, remoteScale = 1f;

    public void InvalidateEmission()
    {
        materialCache.Clear();
        foreach (RendererRecord r in renderers) { r.stale = true; }
    }

    private bool Refresh(RendererRecord record, Slot* slot)
    {
        Renderer renderer = record.renderer;
        renderer.GetSharedMaterials(materialScratch);
        record.mirror = IsMirror(record, materialScratch);
        if (record.mirror) { record.stale = false; return true; }
        MeshRecord mesh = record.mesh = MeshFor(record);
        if (mesh == null) { record.skip = "nomesh"; record.stale = true; return false; }
        record.hasBlock = renderer.HasPropertyBlock();
        LightmapData[] maps = Lightmaps();
        int lightmapIndex = renderer.lightmapIndex;
        bool lightmapped = lightmapIndex >= 0 && lightmapIndex < maps.Length;
        int submeshes = mesh.submeshFlags.Length;
        if (slot->submeshes != submeshes)
        {
            slot->materialOffset = AllocatePool(submeshes);
            slot->submeshes = submeshes;
            UnsafeUtility.MemClear((PoolEntry*)pool.GetUnsafePtr() + slot->materialOffset, submeshes * sizeof(PoolEntry));
        }
        PoolEntry* entries = (PoolEntry*)pool.GetUnsafePtr() + slot->materialOffset;
        int misses = textureMisses, last = Mathf.Max(materialScratch.Count - 1, 0);
        bool widen = false;
        for (int s = 0; s < submeshes; s++)
        {
            Material m = s < materialScratch.Count ? materialScratch[s] : (materialScratch.Count > 0 ? materialScratch[materialScratch.Count - 1] : null);
            entries[s].material = ReadMaterialCached(renderer, record.hasBlock, lightmapped, Mathf.Min(s, last), m, record.avatar, BasisGlobalIlluminationSettings.EmitterIntensityForLayer(record.layer, worldScale, localScale, remoteScale), out uint submeshFlags, out entries[s].power);
            entries[s].triangles = (uint)(mesh.submeshCounts[s] / 3);
            uint merged = MergeSubmeshFlags(mesh.submeshFlags[s], submeshFlags);
            if (merged == mesh.submeshFlags[s]) { continue; }
            widen |= mesh.defined;
            mesh.submeshFlags[s] = merged;
        }
        if (widen) { mesh = record.mesh = Redefine(mesh); }
        slot->lightmap = -1;
        slot->lightmapST = float4.zero;
        if (lightmapped && lightmapIndex < 0xFFFE)
        {
            slot->lightmap = TextureFor(maps[lightmapIndex].lightmapColor, true);
            slot->lightmapST = renderer.lightmapScaleOffset;
        }
        record.shadowMode = renderer.shadowCastingMode;
        slot->flags = (slot->flags & ~SlotKept) | (record.avatar ? SlotAvatar : 0) | (record.shadowMode == ShadowCastingMode.ShadowsOnly ? SlotShadowOnly : 0);
        if (renderer is SkinnedMeshRenderer smr) { record.rootBone = smr.rootBone; }
        else
        {
            Bounds local = renderer.localBounds;
            slot->localCenter = local.center;
            slot->localExtents = local.extents;
        }
        record.stale = textureMisses != misses;
        return true;
    }

    public void Fill(ulong frame, BasisGlobalIlluminationSettings settings, Vector3 viewer, ref BasisExternalRayFrame parameters)
    {
        BasisGlobalIlluminationRaySceneSettings sceneSettings = settings.ResolvedSceneSettings();
        bool spatial = CompleteSpatial();
        Track(sceneSettings.layerMask);
        if (layoutChanged) { RebuildSlots(); spatial = false; }
        if (pairClones) { PairShadowClones(); pairClones = false; }
        float now = Time.realtimeSinceStartup;
        float scanEvery = Mathf.Max(0.25f, sceneSettings.rescanInterval);
        if (now >= nextScan || nextScan - now > scanEvery) { Housekeep(); nextScan = now + scanEvery; }

        BasisExternalRayInstance* instances = BasisExternalRayTracer.Instances(frame);
        BasisExternalRayMaterial* materials = BasisExternalRayTracer.Materials(frame);
        BasisExternalRayEmitter* emitters = BasisExternalRayTracer.Emitters(frame);
        uint instanceCount = 0, materialCount = 0;
        int listCount = 0;
        bool anyDead = false;
        skinnedQueue.Clear();
        emitterScratch.Clear();
        MirrorCount = 0;
        worldScale = settings.emitterIntensity;
        localScale = settings.localAvatarEmitterIntensity;
        remoteScale = settings.remoteAvatarEmitterIntensity;
        respectBaked = settings.respectBakedEmission;
        Slot* slotBase = (Slot*)slots.GetUnsafePtr();
        SpatialOut* spatialBase = spatial ? (SpatialOut*)spatialOut.GetUnsafeReadOnlyPtr() : null;
        int* list = (int*)frameList.GetUnsafePtr();

        for (int i = 0; i < renderers.Count && instanceCount < BasisExternalRayTracer.MaxInstances; i++)
        {
            RendererRecord record = renderers[i];
            Slot* slot = slotBase + i;
            Renderer renderer = record.renderer;
            if (renderer == null) { if (record.seen) { MarkDirty(slot->lastCenter, math.length(slot->lastExtents) + 2f); record.seen = false; } anyDead = true; continue; }
            if (!record.drawn)
            {
                if (record.seen)
                {
                    float glow = 0f;
                    for (int s = 0; s < slot->submeshes; s++) { glow = Mathf.Max(glow, pool[slot->materialOffset + s].lastPower); }
                    if (!record.avatar || glow > 0.001f) { MarkDirty(slot->lastCenter, math.length(slot->lastExtents) + 2f + Mathf.Min(Mathf.Sqrt(glow) * 2f, 8f), glow <= 0.001f, glow > 0.001f); }
                    record.seen = false;
                }
                record.skip = "off";
                continue;
            }
            if (record.shadowSource != null && record.shadowSource.drawn) { record.skip = "clone"; continue; }
            MeshRecord mesh = record.mesh;
            bool exact = record.stale;
            bool refresh = record.stale || record.hasBlock || ((frameCount + record.phase) & RefreshMask) == 0 || (mesh != null && (mesh.released || !mesh.committed));
            if (refresh && !Refresh(record, slot)) { continue; }
            if (record.mirror) { AddMirror(renderer); record.skip = "mirror"; continue; }
            mesh = record.mesh;
            record.skip = "upload";
            mesh.lastSeen = frameCount;
            int submeshes = slot->submeshes;
            if (materialCount + submeshes > BasisExternalRayTracer.MaxMaterials) { break; }
            if (!mesh.committed && !UploadMesh(mesh)) { record.stale = true; continue; }

            bool unposed = mesh.skinned != null && !mesh.posed;
            bool noShadow = record.shadowMode == ShadowCastingMode.Off && !(record.shadowClone != null && record.shadowClone.drawn);
            int flags = (slot->flags & SlotKept) | (record.seen ? SlotSeen : 0) | (refresh ? SlotRefreshed : 0) | (unposed ? SlotUnposed : 0) | (noShadow ? SlotNoShadow : 0);
            bool fallback = spatialBase == null || spatialBase[i].valid == 0;
            if (fallback || record.avatar || exact)
            {
                Bounds bounds = renderer.bounds;
                slot->center = bounds.center;
                slot->extents = bounds.extents;
                flags |= SlotBounds;
            }
            if (record.avatar && mesh.skinned != null && mesh.posed && record.rootBone != null)
            {
                record.rootBone.GetPositionAndRotation(out Vector3 rootPosition, out Quaternion rootRotation);
                Rows(Matrix4x4.TRS(rootPosition, rootRotation, Vector3.one), out Vector4 r0, out Vector4 r1, out Vector4 r2);
                slot->row0 = r0; slot->row1 = r1; slot->row2 = r2;
                flags |= SlotRows;
            }
            else if (fallback)
            {
                Rows(record.transform.localToWorldMatrix, out Vector4 r0, out Vector4 r1, out Vector4 r2);
                slot->row0 = r0; slot->row1 = r1; slot->row2 = r2;
                flags |= SlotRows;
            }
            slot->flags = flags;
            slot->mesh = mesh.id;
            slot->instance = instanceCount;
            slot->materialBase = materialCount;
            if (mesh.skinned != null) { skinnedQueue.Add((mesh, instanceCount, renderer.isVisible)); }
            list[listCount++] = i;
            instanceCount++;
            materialCount += (uint)submeshes;
            record.seen = true;
            record.skip = string.Empty;
        }

        Ensure(ref dirtyOut, listCount * 2 + (int)materialCount + 8);
        Ensure(ref emitterOut, (int)materialCount + 1);
        Ensure(ref buildCounts, 2);
        JobHandle build = new BuildJob
        {
            List = frameList, Spatial = spatialOut, Slots = slots, Pool = pool, Dirty = dirtyOut, Emitters = emitterOut, Counts = buildCounts,
            Instances = instances, Materials = materials, Viewer = viewer, Count = listCount, EmittersOn = settings.emitters,
        }.Schedule();
        JobHandle.ScheduleBatchedJobs();

        int lightCount = FillLights(BasisExternalRayTracer.Lights(frame), viewer, settings);
        FillAmbient(ref parameters, settings);
        build.Complete();
        if (anyDead) { layoutChanged = true; }

        int dirtyCount = buildCounts[0], candidates = buildCounts[1];
        float4* marks = (float4*)dirtyOut.GetUnsafeReadOnlyPtr();
        for (int i = 0; i < dirtyCount; i++)
        {
            float4 v = marks[i];
            if (dirty.Count >= BasisExternalRayTracer.MaxDirty) { if (v.w >= 0f) { resetCache = true; } continue; }
            dirty.Add(v);
        }
        EmitterCandidate* found = (EmitterCandidate*)emitterOut.GetUnsafeReadOnlyPtr();
        for (int i = 0; i < candidates; i++) { emitterScratch.Add((found[i].power, found[i].instance, found[i].geometry, found[i].triangles)); }

        PoseSkinned(instances);

        emitterScratch.Sort((a, b) => b.power.CompareTo(a.power));
        int emitterCount = Mathf.Min(emitterScratch.Count, BasisExternalRayTracer.MaxEmitters);
        for (int i = emitterCount; i < emitterScratch.Count; i++) { materials[instances[emitterScratch[i].instance].materialBase + emitterScratch[i].geometry].flags &= ~BasisExternalRayTracer.MaterialEmitter; }
        float total = 0f;
        for (int i = 0; i < emitterCount; i++) { total += emitterScratch[i].power; }
        float running = 0f;
        for (int i = 0; i < emitterCount; i++)
        {
            running += emitterScratch[i].power;
            emitters[i] = new BasisExternalRayEmitter { instance = emitterScratch[i].instance, geometry = emitterScratch[i].geometry, triangleCount = emitterScratch[i].triangles, cdf = i == emitterCount - 1 ? 1f : running / Mathf.Max(total, 1e-6f) };
        }

        Vector4 ambient = parameters.ambient0 + parameters.ambient1 * 3.1f + parameters.ambient2 * 7.3f;
        float ambientNow = ambient.magnitude, ambientBefore = lastAmbient.magnitude;
        if (Mathf.Abs(ambientNow - ambientBefore) > 0.25f * Mathf.Max(ambientBefore, 0.05f)) { resetCache = true; }
        lastAmbient = ambient;
        if (resetCache) { parameters.flags |= BasisExternalRayTracer.FlagCacheReset; dirty.Clear(); }
        Vector4* outDirty = BasisExternalRayTracer.Dirty(frame);
        for (int i = 0; i < dirty.Count; i++) { outDirty[i] = dirty[i]; }
        parameters.dirtyCount = (uint)dirty.Count;
        dirty.Clear();
        resetCache = false;
        BasisExternalRayCommand* outCommands = BasisExternalRayTracer.Commands(frame);
        for (int i = 0; i < commands.Count; i++) { outCommands[i] = commands[i]; }
        parameters.instanceCount = instanceCount;
        parameters.materialCount = materialCount;
        parameters.lightCount = (uint)lightCount;
        parameters.commandCount = (uint)commands.Count;
        parameters.emitterCount = (uint)emitterCount;
        parameters.uploadTexels = texelCursor;
    }


    private int FillLights(BasisExternalRayLight* outLights, Vector3 viewer, BasisGlobalIlluminationSettings settings)
    {
        if (!settings.rayTracedLights) { return 0; }
        lightsGone.Clear();
        foreach (KeyValuePair<Light, Vector4> kv in lightReach)
        {
            Light l = kv.Key;
            if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f || (l.bakingOutput.isBaked && l.bakingOutput.lightmapBakeType == LightmapBakeType.Baked)) { lightsGone.Add(l); }
        }
        foreach (Light l in lightsGone)
        {
            Vector4 reach = lightReach[l];
            if (reach.w < 0f) { resetCache = true; }
            else { MarkDirty(reach, reach.w * 1.5f + 1f, false, true); }
            lightReach.Remove(l);
            lightStates.Remove(l);
        }
        int count = 0;
        foreach (Light l in lights)
        {
            if (count >= BasisExternalRayTracer.MaxLights) { break; }
            if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f || (l.bakingOutput.isBaked && l.bakingOutput.lightmapBakeType == LightmapBakeType.Baked)) { continue; }
            Color c = l.color.linear * l.intensity;
            if (GraphicsSettings.lightsUseColorTemperature && l.useColorTemperature) { c *= Mathf.CorrelatedColorTemperatureToRGB(l.colorTemperature); }
            Vector3 p = l.transform.position, f = l.transform.forward;
            Vector4 state = new Vector4(p.x + f.x * 0.37f + l.range * 0.013f, p.y + f.y * 0.37f + l.spotAngle * 0.011f, p.z + f.z * 0.37f, c.r + c.g * 1.7f + c.b * 2.9f);
            if (!lightStates.TryGetValue(l, out Vector4 previous) || (previous - state).sqrMagnitude > 1e-6f)
            {
                bool known = lightStates.ContainsKey(l);
                float before = known ? previous.w : 0f, now = state.w;
                bool large = !known || Mathf.Max(now, before) > 2f * Mathf.Max(Mathf.Min(now, before), 0.01f) || (new Vector3(previous.x, previous.y, previous.z) - new Vector3(state.x, state.y, state.z)).magnitude > 0.5f;
                    if (l.type == LightType.Directional) { if (large) { resetCache = true; } else { MarkDirty(p, 1e4f, true); } }
                else { MarkDirty(p, l.range, !large); }
                lightStates[l] = state;
            }
            lightReach[l] = new Vector4(p.x, p.y, p.z, l.type == LightType.Directional ? -1f : l.range);
            uint type = l.type == LightType.Directional ? 0u : l.type == LightType.Spot ? 2u : 1u;
            if (l.type != LightType.Directional && l.type != LightType.Point && l.type != LightType.Spot) { continue; }
            if (type != 0 && (p - viewer).sqrMagnitude > (l.range + settings.maxRayLength) * (l.range + settings.maxRayLength)) { continue; }
            Vector4 spot = Vector4.zero;
            if (type == 2)
            {
                float cosOuter = Mathf.Cos(l.spotAngle * 0.5f * Mathf.Deg2Rad);
                float cosInner = Mathf.Cos(l.innerSpotAngle * 0.5f * Mathf.Deg2Rad);
                float scale = 1f / Mathf.Max(cosInner - cosOuter, 1e-4f);
                spot = new Vector4(scale, -cosOuter * scale, 0f, 0f);
            }
            outLights[count++] = new BasisExternalRayLight
            {
                position = new Vector4(p.x, p.y, p.z, BitConverter.Int32BitsToSingle((int)type)),
                direction = new Vector4(f.x, f.y, f.z, l.range),
                color = new Vector4(c.r, c.g, c.b, settings.rayTracedShadows && l.shadows != LightShadows.None ? 1f : 0f),
                spot = spot,
            };
        }
        return count;
    }

    private void FillAmbient(ref BasisExternalRayFrame p, BasisGlobalIlluminationSettings settings)
    {
        RenderSettings.ambientProbe.Evaluate(ambientDirections, ambientResults);
        Color l0 = Color.black;
        for (int i = 0; i < 6; i++) { l0 += ambientResults[i]; }
        l0 /= 6f;
        Color x = (ambientResults[0] - ambientResults[1]) * 0.5f, y = (ambientResults[2] - ambientResults[3]) * 0.5f, z = (ambientResults[4] - ambientResults[5]) * 0.5f;
        p.ambient0 = new Vector4(x.r, y.r, z.r, l0.r);
        p.ambient1 = new Vector4(x.g, y.g, z.g, l0.g);
        p.ambient2 = new Vector4(x.b, y.b, z.b, l0.b);
        p.ambient3 = p.ambient4 = p.ambient5 = p.ambient6 = Vector4.zero;
        p.skyIntensity = settings.fallbackIntensity;
    }
}
