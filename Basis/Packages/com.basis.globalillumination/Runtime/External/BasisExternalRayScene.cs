using System;
using System.Collections.Generic;
using UnityEngine;
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
        public bool defined, committed, posed;
        public int lastSeen, lastPosed;
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
        public MeshFilter filter;
        public MeshRecord mesh;
        public Material[] materials = Array.Empty<Material>();
        public int materialCount;
        public bool avatar, seen, mirror;
        public RendererRecord shadowSource, shadowClone;
        public Matrix4x4 lastMatrix;
        public Bounds lastBounds;
        public float[] lastEmission = Array.Empty<float>();
    }

    private readonly Dictionary<Mesh, MeshRecord> staticMeshes = new Dictionary<Mesh, MeshRecord>();
    private readonly Dictionary<SkinnedMeshRenderer, MeshRecord> skinnedMeshes = new Dictionary<SkinnedMeshRenderer, MeshRecord>();
    private readonly Dictionary<Texture, TextureRecord> textures = new Dictionary<Texture, TextureRecord>();
    private readonly List<RendererRecord> renderers = new List<RendererRecord>();
    private readonly Dictionary<Renderer, RendererRecord> rendererLookup = new Dictionary<Renderer, RendererRecord>();
    private readonly Stack<uint> freeMeshIds = new Stack<uint>(), freeTextureIds = new Stack<uint>();
    private uint nextMeshId = 1, nextTextureId = 0;
    private readonly List<BasisExternalRayCommand> commands = new List<BasisExternalRayCommand>();
    public readonly List<Job> Jobs = new List<Job>();
    private readonly List<GraphicsBuffer> disposeNextFrame = new List<GraphicsBuffer>(), disposeNow = new List<GraphicsBuffer>();
    private readonly List<RenderTexture> scratchNextFrame = new List<RenderTexture>(), scratchNow = new List<RenderTexture>();
    private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
    private readonly List<Material> materialScratch = new List<Material>();
    private readonly List<(float power, uint instance, uint geometry, uint triangles)> emitterScratch = new List<(float, uint, uint, uint)>();
    private Light[] lights = Array.Empty<Light>();
    private bool respectBaked = true;
    private readonly Dictionary<Light, Vector4> lightStates = new Dictionary<Light, Vector4>();
    private readonly Dictionary<Light, Vector4> lightReach = new Dictionary<Light, Vector4>();
    private readonly List<Light> lightsGone = new List<Light>();
    private readonly List<Vector4> dirty = new List<Vector4>();
    private bool resetCache = true;
    private int statHard, statSoft, statResets, statEmission, statLights, statAmbient, statAppear, statRemoved;
    private float statNext;
    private string resetReason = "start";
    private Vector4 lastAmbient;
    private BasisExternalRayMaterial[] pendingMaterials = new BasisExternalRayMaterial[16];
    private float[] pendingPower = new float[16];
    private float nextScan, nextLightScan;
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
    private readonly Dictionary<(Material, bool, bool), (BasisExternalRayMaterial, uint, float)> materialCache = new Dictionary<(Material, bool, bool), (BasisExternalRayMaterial, uint, float)>();
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
    private static readonly int idCutoff = Shader.PropertyToID("_Cutoff"), idAlphaClip = Shader.PropertyToID("_AlphaClip"), idSurface = Shader.PropertyToID("_Surface");
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

    public BasisExternalRayScene() { UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded; }

    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode) { resetCache = true; resetReason = "scene load " + scene.name; }

    private void MarkDirty(Vector3 centre, float radius, bool soft = false)
    {
        if (soft) { statSoft++; } else { statHard++; }
        if (dirty.Count >= BasisExternalRayTracer.MaxDirty) { if (!soft) { resetCache = true; } return; }
        dirty.Add(new Vector4(centre.x, centre.y, centre.z, soft ? -radius : radius));
    }

    public void Dispose()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
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
            if (!skinnedMeshes.TryGetValue(skinned, out MeshRecord r) || r.mesh != shared)
            {
                if (r != null) { Release(r); }
                skinned.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                r = Describe(shared);
                r.skinned = skinned;
                skinnedMeshes[skinned] = r;
            }
            return r;
        }
        if (owner.filter == null && !renderer.TryGetComponent(out owner.filter)) { return null; }
        Mesh mesh = owner.filter.sharedMesh;
        if (mesh == null) { return null; }
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

    private void Release(MeshRecord r)
    {
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

    private static Matrix4x4? SkinnedSpace(MeshRecord mesh) => mesh.skinned != null && mesh.posed && mesh.skinned.rootBone != null ? mesh.skinned.rootBone.localToWorldMatrix : (Matrix4x4?)null;

    private unsafe void PoseSkinned(BasisExternalRayInstance* instances)
    {
        if (byPriority == null) { byPriority = (a, b) => Priority(b).CompareTo(Priority(a)); }
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
        GraphicsBuffer skinned = smr.GetVertexBuffer();
        if (skinned == null) { return false; }
        uint count = (uint)r.vertexCount;
        if (!Reserve(count, out uint at)) { skinned.Dispose(); return false; }
        disposeNow.Add(skinned);
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
            if (rows == 0 || !Reserve((rows * (uint)r.width + 3) / 4, out uint at)) { return -1; }
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
        return Has(m, id) ? m.GetColor(id) : fallback;
    }

    private float ReadFloat(Material m, bool hasBlock, int id, float fallback)
    {
        if (hasBlock && block.HasFloat(id)) { return block.GetFloat(id); }
        return Has(m, id) ? m.GetFloat(id) : fallback;
    }

    private Texture ReadTexture(Material m, bool hasBlock, int id)
    {
        if (hasBlock && block.HasTexture(id)) { return block.GetTexture(id); }
        return Has(m, id) ? m.GetTexture(id) : null;
    }

    private Vector4 ReadST(Material m, int id)
    {
        if (!Has(m, id)) { return new Vector4(1, 1, 0, 0); }
        Vector2 s = m.GetTextureScale(id), o = m.GetTextureOffset(id);
        return new Vector4(s.x, s.y, o.x, o.y);
    }

    private BasisExternalRayMaterial ReadMaterialCached(Renderer renderer, bool hasBlock, bool lightmapped, int index, Material m, bool avatar, float emissionScale, out uint submeshFlags, out float emissionPower)
    {
        if (hasBlock || m == null) { return ReadMaterial(renderer, hasBlock, lightmapped, index, m, avatar, emissionScale, out submeshFlags, out emissionPower); }
        (Material, bool, bool) key = (m, avatar, lightmapped);
        if (materialCache.TryGetValue(key, out (BasisExternalRayMaterial material, uint flags, float power) hit)) { submeshFlags = hit.flags; emissionPower = hit.power; return hit.material; }
        BasisExternalRayMaterial read = ReadMaterial(renderer, hasBlock, lightmapped, index, m, avatar, emissionScale, out submeshFlags, out emissionPower);
        materialCache[key] = (read, submeshFlags, emissionPower);
        return read;
    }

    private BasisExternalRayMaterial ReadMaterial(Renderer renderer, bool hasBlock, bool lightmapped, int index, Material m, bool avatar, float emissionScale, out uint submeshFlags, out float emissionPower)
    {
        submeshFlags = BasisExternalRayTracer.SubmeshOpaque;
        emissionPower = 0f;
        BasisExternalRayMaterial result = new BasisExternalRayMaterial { baseColor = Vector4.one, baseST = new Vector4(1, 1, 0, 0), emissionST = new Vector4(1, 1, 0, 0), baseTexture = -1, emissionTexture = -1, cutoff = 0.5f };
        if (m == null) { return result; }
        if (hasBlock) { renderer.GetPropertyBlock(block, index); }
        int baseId = Has(m, idBaseMap) ? idBaseMap : idMainTex;
        int colorId = Has(m, idBaseColor) ? idBaseColor : idColor;
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
        int queue = m.renderQueue;
        bool clip = ReadFloat(m, hasBlock, idAlphaClip, 0f) > 0.5f || m.IsKeywordEnabled("_ALPHATEST_ON") || (queue >= 2450 && queue < 3000);
        bool blend = ReadFloat(m, hasBlock, idSurface, 0f) > 0.5f || queue >= 3000;
        if (blend) { result.alphaMode = BasisExternalRayTracer.AlphaBlend; submeshFlags = BasisExternalRayTracer.SubmeshBlend; }
        else if (clip) { result.alphaMode = BasisExternalRayTracer.AlphaCutout; submeshFlags = BasisExternalRayTracer.SubmeshAlphaTest; }
        result.alphaMode |= secondary;
        result.cutoff = ReadFloat(m, hasBlock, idCutoff, 0.5f);
        result.metallic = ReadFloat(m, hasBlock, idMetallic, 0f);
        if (avatar) { result.flags |= BasisExternalRayTracer.MaterialAnimated; }
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
            if (m.HasProperty(split)) { st = m.GetVector(split); }
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

    private void Scan(LayerMask mask)
    {
        Renderer[] found = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
        HashSet<Renderer> seen = new HashSet<Renderer>();
        int interfaceMask = BasisGlobalIlluminationSettings.DefaultRayTracedLayers();
        foreach (Renderer r in found)
        {
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) { continue; }
            int bit = 1 << r.gameObject.layer;
            if ((mask & bit) == 0 || (interfaceMask & bit) == 0) { continue; }
            seen.Add(r);
            if (rendererLookup.TryGetValue(r, out RendererRecord known)) { known.mirror = IsMirror(r); continue; }
            RendererRecord record = new RendererRecord { renderer = r, transform = r.transform, avatar = r is SkinnedMeshRenderer, mirror = IsMirror(r) };
            rendererLookup[r] = record;
            renderers.Add(record);
        }
        for (int i = renderers.Count - 1; i >= 0; i--)
        {
            Renderer r = renderers[i].renderer;
            if (r != null && seen.Contains(r)) { continue; }
            if (renderers[i].seen) { statRemoved++; MarkDirty(renderers[i].lastBounds.center, renderers[i].lastBounds.extents.magnitude + 2f); }
            rendererLookup.Remove(renderers[i].renderer);
            if (renderers[i].mesh != null && renderers[i].mesh.skinned != null)
            {
                Release(renderers[i].mesh);
                if (renderers[i].renderer is SkinnedMeshRenderer s) { skinnedMeshes.Remove(s); }
            }
            renderers.RemoveAt(i);
        }
        PairShadowClones();
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

    private bool IsMirror(Renderer r)
    {
        r.GetSharedMaterials(materialScratch);
        foreach (Material m in materialScratch)
        {
            if (m == null) { continue; }
            foreach (int id in idMirrorTextures) { if (m.HasProperty(id)) { return true; } }
        }
        return false;
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

    private static bool Drawn(Renderer r) => r != null && r.enabled && r.gameObject.activeInHierarchy && !r.forceRenderingOff;

    private static void Rows(in Matrix4x4 m, out Vector4 r0, out Vector4 r1, out Vector4 r2)
    {
        r0 = new Vector4(m.m00, m.m01, m.m02, m.m03);
        r1 = new Vector4(m.m10, m.m11, m.m12, m.m13);
        r2 = new Vector4(m.m20, m.m21, m.m22, m.m23);
    }

    public void ClearUpload() { texelCursor = 0; commands.Clear(); }

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

    public void Fill(ulong frame, BasisGlobalIlluminationSettings settings, Vector3 viewer, ref BasisExternalRayFrame parameters)
    {
        BasisGlobalIlluminationRaySceneSettings sceneSettings = settings.ResolvedSceneSettings();
        float now = Time.realtimeSinceStartup;
        if (now >= nextScan) { Scan(sceneSettings.layerMask); nextScan = now + Mathf.Max(0.25f, sceneSettings.rescanInterval); }
        if (now >= nextLightScan) { lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude); nextLightScan = now + 1f; }

        BasisExternalRayInstance* instances = BasisExternalRayTracer.Instances(frame);
        BasisExternalRayMaterial* materials = BasisExternalRayTracer.Materials(frame);
        BasisExternalRayEmitter* emitters = BasisExternalRayTracer.Emitters(frame);
        uint instanceCount = 0, materialCount = 0;
        skinnedQueue.Clear();
        emitterScratch.Clear();
        MirrorCount = 0;
        materialCache.Clear();
        frameLightmaps = LightmapSettings.lightmaps ?? System.Array.Empty<LightmapData>();
        float avatarScale = settings.avatarEmissionScale;
        float worldScale = 1f;
        respectBaked = settings.respectBakedEmission;

        for (int i = 0; i < renderers.Count && instanceCount < BasisExternalRayTracer.MaxInstances; i++)
        {
            RendererRecord record = renderers[i];
            Renderer renderer = record.renderer;
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff)
            {
                if (record.seen)
                {
                    float glow = 0f;
                    foreach (float e in record.lastEmission) { glow = Mathf.Max(glow, e); }
                    if (!record.avatar || glow > 0.001f) { statRemoved++; MarkDirty(record.lastBounds.center, record.lastBounds.extents.magnitude + 2f + Mathf.Min(Mathf.Sqrt(glow) * 2f, 8f), glow <= 0.001f); }
                    record.seen = false;
                }
                continue;
            }
            if (record.shadowSource != null && Drawn(record.shadowSource.renderer)) { continue; }
            if (record.mirror) { AddMirror(renderer); continue; }
            MeshRecord mesh = record.mesh = MeshFor(record);
            if (mesh == null) { continue; }
            mesh.lastSeen = frameCount;
            renderer.GetSharedMaterials(materialScratch);
            bool hasBlock = renderer.HasPropertyBlock();
            int lightmapIndex = renderer.lightmapIndex;
            bool lightmapped = lightmapIndex >= 0 && lightmapIndex < frameLightmaps.Length;
            int submeshes = mesh.submeshFlags.Length;
            if (materialCount + submeshes > BasisExternalRayTracer.MaxMaterials) { break; }
            if (pendingMaterials.Length < submeshes) { pendingMaterials = new BasisExternalRayMaterial[Mathf.NextPowerOfTwo(submeshes)]; pendingPower = new float[pendingMaterials.Length]; }
            for (int s = 0; s < submeshes; s++)
            {
                Material m = s < materialScratch.Count ? materialScratch[s] : (materialScratch.Count > 0 ? materialScratch[materialScratch.Count - 1] : null);
                pendingMaterials[s] = ReadMaterialCached(renderer, hasBlock, lightmapped, Mathf.Min(s, Mathf.Max(materialScratch.Count - 1, 0)), m, record.avatar, record.avatar ? avatarScale : worldScale, out uint submeshFlags, out pendingPower[s]);
                if (!mesh.defined) { mesh.submeshFlags[s] = submeshFlags; }
            }
            if (!mesh.committed && !UploadMesh(mesh)) { continue; }
            uint materialBase = materialCount;
            float emissivePower = 0f;
            bool unposed = mesh.skinned != null && !mesh.posed;
            for (int s = 0; s < submeshes; s++)
            {
                BasisExternalRayMaterial mat = pendingMaterials[s];
                if (settings.emitters && pendingPower[s] > 0.001f) { emissivePower += pendingPower[s]; }
                if (pendingPower[s] > 0.001f && settings.emitters && mesh.submeshCounts[s] > 0 && !unposed)
                {
                    Vector3 size = renderer.bounds.size;
                    float area = Mathf.Max(0.01f, size.x * size.y + size.y * size.z + size.x * size.z);
                    float distance = Mathf.Max(1f, (renderer.bounds.center - viewer).sqrMagnitude);
                    emitterScratch.Add((pendingPower[s] * area / distance, instanceCount, (uint)s, (uint)(mesh.submeshCounts[s] / 3)));
                    mat.flags |= BasisExternalRayTracer.MaterialEmitter;
                }
                materials[materialCount++] = mat;
            }
            Bounds bounds = renderer.bounds;
            if (record.lastEmission.Length != submeshes) { record.lastEmission = new float[submeshes]; }
            for (int s = 0; s < submeshes; s++)
            {
                float before = record.lastEmission[s], power = pendingPower[s];
                if (record.seen && Mathf.Abs(power - before) > 0.1f * Mathf.Max(power, before) + 0.01f)
                {
                    bool large = Mathf.Max(power, before) > 2f * Mathf.Max(Mathf.Min(power, before), 0.01f);
                    statEmission++;
                    MarkDirty(bounds.center, bounds.extents.magnitude + 2f + Mathf.Min(Mathf.Sqrt(Mathf.Max(power, before)) * 2f, 8f), !large);
                }
                record.lastEmission[s] = power;
            }
            if (mesh.skinned != null) { skinnedQueue.Add((mesh, instanceCount, renderer.isVisible)); }
            Matrix4x4 matrix = SkinnedSpace(mesh) ?? record.transform.localToWorldMatrix;
            Rows(matrix, out Vector4 r0, out Vector4 r1, out Vector4 r2);
            bool moved = record.avatar && ((bounds.center - record.lastBounds.center).sqrMagnitude > AvatarMoveThreshold || (bounds.extents - record.lastBounds.extents).sqrMagnitude > AvatarMoveThreshold);
            bool referenceBounds = !record.seen || !record.avatar || moved;
            if (!record.seen) { statAppear++; MarkDirty(bounds.center, bounds.extents.magnitude + 2f); }
            else if (moved || (!record.avatar && !Approximately(matrix, record.lastMatrix)))
            {
                bool emissive = emissivePower > 0.001f;
                float reach = 1.5f + (emissive ? Mathf.Min(Mathf.Sqrt(emissivePower) * 2f, 8f) : 0f);
                MarkDirty(bounds.center, bounds.extents.magnitude + reach, !emissive);
                MarkDirty(record.lastBounds.center, record.lastBounds.extents.magnitude + reach, !emissive);
            }
            record.seen = true;
            record.lastMatrix = matrix;
            if (referenceBounds) { record.lastBounds = bounds; }
            int lightmap = -1;
            Vector4 lightmapST = Vector4.zero;
            if (lightmapIndex >= 0 && lightmapIndex < 0xFFFE && lightmapIndex < frameLightmaps.Length)
            {
                lightmap = TextureFor(frameLightmaps[lightmapIndex].lightmapColor, true);
                lightmapST = renderer.lightmapScaleOffset;
            }
            bool dynamic = record.avatar || lightmap < 0;
            bool noShadow = renderer.shadowCastingMode == ShadowCastingMode.Off && !(record.shadowClone != null && Drawn(record.shadowClone.renderer));
            instances[instanceCount++] = new BasisExternalRayInstance
            {
                row0 = r0, row1 = r1, row2 = r2, mesh = mesh.id, materialBase = materialBase,
                mask = unposed ? 0u : (noShadow ? 0x01u : 0xFDu) | (dynamic ? BasisExternalRayTracer.MaskDynamic : 0u),
                flags = (record.avatar ? BasisExternalRayTracer.InstanceAvatar : 0u) | (noShadow ? BasisExternalRayTracer.InstanceNoShadow : 0u) | (dynamic ? BasisExternalRayTracer.InstanceDynamic : 0u),
                lightmap = lightmap, lightmapST = lightmapST,
            };
        }

        PoseSkinned(instances);

        emitterScratch.Sort((a, b) => b.power.CompareTo(a.power));
        int emitterCount = Mathf.Min(emitterScratch.Count, BasisExternalRayTracer.MaxEmitters);
        float total = 0f;
        for (int i = 0; i < emitterCount; i++) { total += emitterScratch[i].power; }
        float running = 0f;
        for (int i = 0; i < emitterCount; i++)
        {
            running += emitterScratch[i].power;
            emitters[i] = new BasisExternalRayEmitter { instance = emitterScratch[i].instance, geometry = emitterScratch[i].geometry, triangleCount = emitterScratch[i].triangles, cdf = i == emitterCount - 1 ? 1f : running / Mathf.Max(total, 1e-6f) };
        }

        int lightCount = FillLights(BasisExternalRayTracer.Lights(frame), viewer, settings);
        FillAmbient(ref parameters, settings);
        Vector4 ambient = parameters.ambient0 + parameters.ambient1 * 3.1f + parameters.ambient2 * 7.3f;
        float ambientNow = ambient.magnitude, ambientBefore = lastAmbient.magnitude;
        if (Mathf.Abs(ambientNow - ambientBefore) > 0.25f * Mathf.Max(ambientBefore, 0.05f)) { resetCache = true; resetReason = "ambient"; statAmbient++; }
        lastAmbient = ambient;
        if (resetCache) { parameters.flags |= BasisExternalRayTracer.FlagCacheReset; dirty.Clear(); }
        Vector4* outDirty = BasisExternalRayTracer.Dirty(frame);
        for (int i = 0; i < dirty.Count; i++) { outDirty[i] = dirty[i]; }
        parameters.dirtyCount = (uint)dirty.Count;
        dirty.Clear();
        if (resetCache) { statResets++; }
        resetCache = false;
        if (Time.realtimeSinceStartup >= statNext)
        {
            if (statNext > 0f) { Debug.Log($"[BasisGI] Path traced cache, last 5 s: hard dirty {statHard} (emission {statEmission}, lights {statLights}, appeared {statAppear}, removed {statRemoved}), soft dirty {statSoft}, full resets {statResets} (last: {resetReason}), instances {instanceCount}, emitters {emitterCount}, lights {lightCount}, gpu {BasisExternalRayTracer.GpuMicroseconds(0)} us"); }
            statNext = Time.realtimeSinceStartup + 5f;
            statHard = statSoft = statResets = statEmission = statLights = statAmbient = statAppear = statRemoved = 0;
        }
        BasisExternalRayCommand* outCommands = BasisExternalRayTracer.Commands(frame);
        for (int i = 0; i < commands.Count; i++) { outCommands[i] = commands[i]; }
        parameters.instanceCount = instanceCount;
        parameters.materialCount = materialCount;
        parameters.lightCount = (uint)lightCount;
        parameters.commandCount = (uint)commands.Count;
        parameters.emitterCount = (uint)emitterCount;
        parameters.uploadTexels = texelCursor;
    }

    private static bool Approximately(in Matrix4x4 a, in Matrix4x4 b)
    {
        for (int i = 0; i < 16; i++) { if (Mathf.Abs(a[i] - b[i]) > 1e-4f) { return false; } }
        return true;
    }

    private int FillLights(BasisExternalRayLight* outLights, Vector3 viewer, BasisGlobalIlluminationSettings settings)
    {
        if (!settings.rayTracedLights) { return 0; }
        lightsGone.Clear();
        foreach (KeyValuePair<Light, Vector4> kv in lightReach)
        {
            Light l = kv.Key;
            if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f || l.lightmapBakeType == LightmapBakeType.Baked) { lightsGone.Add(l); }
        }
        foreach (Light l in lightsGone)
        {
            Vector4 reach = lightReach[l];
            statLights++;
            if (reach.w < 0f) { resetCache = true; resetReason = "directional light off"; }
            else { MarkDirty(reach, reach.w * 1.5f + 1f); }
            lightReach.Remove(l);
            lightStates.Remove(l);
        }
        int count = 0;
        foreach (Light l in lights)
        {
            if (count >= BasisExternalRayTracer.MaxLights) { break; }
            if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f || l.lightmapBakeType == LightmapBakeType.Baked) { continue; }
            Color c = l.color.linear * l.intensity;
            if (GraphicsSettings.lightsUseColorTemperature && l.useColorTemperature) { c *= Mathf.CorrelatedColorTemperatureToRGB(l.colorTemperature); }
            Vector3 p = l.transform.position, f = l.transform.forward;
            Vector4 state = new Vector4(p.x + f.x * 0.37f + l.range * 0.013f, p.y + f.y * 0.37f + l.spotAngle * 0.011f, p.z + f.z * 0.37f, c.r + c.g * 1.7f + c.b * 2.9f);
            if (!lightStates.TryGetValue(l, out Vector4 previous) || (previous - state).sqrMagnitude > 1e-6f)
            {
                bool known = lightStates.ContainsKey(l);
                float before = known ? previous.w : 0f, now = state.w;
                bool large = !known || Mathf.Max(now, before) > 2f * Mathf.Max(Mathf.Min(now, before), 0.01f) || (new Vector3(previous.x, previous.y, previous.z) - new Vector3(state.x, state.y, state.z)).magnitude > 0.5f;
                statLights++;
                if (l.type == LightType.Directional) { if (large) { resetCache = true; resetReason = "directional light " + l.name; } else { MarkDirty(p, 1e4f, true); } }
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
