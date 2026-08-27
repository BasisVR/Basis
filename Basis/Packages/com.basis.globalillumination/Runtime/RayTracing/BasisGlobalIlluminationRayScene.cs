using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;

public enum BasisGlobalIlluminationRaySkinnedMode
{
    Off = 0,
    Static = 1,
    Dynamic = 2
}

[Serializable]
public struct BasisGlobalIlluminationRaySceneSettings
{
    public LayerMask layerMask;
    public bool shadowCastersOnly;
    public float rescanInterval;
    public BasisGlobalIlluminationRaySkinnedMode skinnedMode;
    public int skinnedBakesPerFrame;
    public int skinnedBakeInterval;
    public float skinnedMaxDistance;
    public bool textureAlbedo;
    public bool emissiveSurfaces;
    /// <summary>
    /// Skip the emission of a baked-emissive surface that already carries a lightmap, because that
    /// light is in the lightmap and injecting it again lights the room twice from one lamp.
    /// </summary>
    public bool respectBakedEmission;

    public static BasisGlobalIlluminationRaySceneSettings Default => new BasisGlobalIlluminationRaySceneSettings
    {
        layerMask = ~0,
        shadowCastersOnly = false,
        rescanInterval = 2f,
        skinnedMode = BasisGlobalIlluminationRaySkinnedMode.Dynamic,
        skinnedBakesPerFrame = 2,
        skinnedBakeInterval = 4,
        skinnedMaxDistance = 16f,
        textureAlbedo = true,
        emissiveSurfaces = true,
        respectBakedEmission = true
    };
}

/// <summary>Per sub-mesh surface data a ray hit resolves. Must match BasisGIRtInstance in the trace kernel.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BasisGlobalIlluminationRayInstance
{
    public const int Stride = 96;
    public const uint FlagHasNormals = 1u;

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
        public SkinnedMeshRenderer skinned;
        public Mesh sourceMesh, bakedMesh;
        public MeshGeometry geometry;
        public bool sharedGeometry;
        public Matrix4x4 matrix;
        public int[] handles;
        public int[] instanceIds;
        public bool isStatic, seen;
        public int lastBakeFrame;
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
    private readonly List<Entry> skinnedEntries = new List<Entry>();
    private readonly List<EntityId> pendingRemoval = new List<EntityId>();
    private readonly List<int> freeInstanceIds = new List<int>();
    private readonly List<Vector3> normalScratch = new List<Vector3>();
    private readonly List<int> indexScratch = new List<int>();
    private readonly List<Material> materialScratch = new List<Material>();
    // One shared block: the surface read runs over every instance in the scene, and a fresh
    // MaterialPropertyBlock per surface would be garbage on a path that now runs every frame.
    private static readonly MaterialPropertyBlock blockScratch = new MaterialPropertyBlock();

    private BasisGlobalIlluminationRayInstance[] instances = new BasisGlobalIlluminationRayInstance[256];
    private GraphicsBuffer instanceBuffer;
    private IRayTracingAccelStruct accelStruct;
    private int instanceHighWater;
    private int instanceDirtyStart = int.MaxValue, instanceDirtyEnd = -1;
    private bool instanceBufferResized = true;
    private float nextScanTime;
    private int skinnedCursor;
    private int textureVersion = -1;
    private bool structureDirty = true;
    private bool everBuilt;

    public IRayTracingAccelStruct AccelerationStructure => accelStruct;
    public GraphicsBuffer InstanceBuffer => instanceBuffer;
    public GraphicsBuffer NormalBuffer => normalArena.Buffer;
    public GraphicsBuffer IndexBuffer => indexArena.Buffer;
    public int EntryCount => entries.Count;
    public int InstanceCount => instanceHighWater;
    public int SkinnedCount => skinnedEntries.Count;
    public bool NeedsBuild => structureDirty || !everBuilt;
    public bool HasGeometry => instanceHighWater > 0 && instanceBuffer != null;

    public BasisGlobalIlluminationRayScene(BasisGlobalIlluminationRayContext context)
    {
        this.context = context;
        accelStruct = context.CreateAccelerationStructure();
    }

    public void MarkDirty()
    {
        nextScanTime = 0f;
        structureDirty = true;
    }

    public static bool ShouldInclude(Renderer renderer, in BasisGlobalIlluminationRaySceneSettings settings)
    {
        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) { return false; }
        if ((settings.layerMask.value & (1 << renderer.gameObject.layer)) == 0) { return false; }
        if (settings.shadowCastersOnly && renderer.shadowCastingMode == ShadowCastingMode.Off) { return false; }
        return renderer.GetComponent<BasisGlobalIlluminationRayExclude>() == null;
    }

    public static bool IsSupportedRendererType(Renderer renderer, BasisGlobalIlluminationRaySkinnedMode skinnedMode)
    {
        if (renderer is SkinnedMeshRenderer) { return skinnedMode != BasisGlobalIlluminationRaySkinnedMode.Off; }
        return renderer is MeshRenderer;
    }

    public static Mesh ResolveMesh(Renderer renderer)
    {
        if (renderer == null) { return null; }
        if (renderer is SkinnedMeshRenderer skinned) { return skinned.sharedMesh; }
        MeshFilter filter = renderer.GetComponent<MeshFilter>();
        return filter != null ? filter.sharedMesh : null;
    }

    public static bool IsUsableMesh(Mesh mesh)
    {
        return mesh != null && mesh.subMeshCount > 0 && mesh.vertexCount > 0 && mesh.HasVertexAttribute(VertexAttribute.Position);
    }

    public void Refresh(in BasisGlobalIlluminationRaySceneSettings settings, Vector3 viewerPosition, float time, int frameCount)
    {
        if (accelStruct == null) { return; }

        if (time >= nextScanTime)
        {
            nextScanTime = time + Mathf.Max(0.1f, settings.rescanInterval);
            Rescan(settings);
        }
        else if (textureVersion != textures.Version)
        {
            RefreshMaterials(settings);
        }
        else
        {
            RefreshBlockMaterials(settings);
        }

        UpdateTransforms();

        if (settings.skinnedMode == BasisGlobalIlluminationRaySkinnedMode.Dynamic)
        {
            UpdateSkinned(settings, viewerPosition, frameCount);
        }

        Upload();
    }

    public void Rescan(in BasisGlobalIlluminationRaySceneSettings settings)
    {
        textureVersion = textures.Version;
        foreach (KeyValuePair<EntityId, Entry> pair in entries) { pair.Value.seen = false; }

        Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
        for (int index = 0; index < renderers.Length; index++)
        {
            Renderer renderer = renderers[index];
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
            AddEntry(renderer, mesh, renderer as SkinnedMeshRenderer, settings);
        }

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

    private void AddEntry(Renderer renderer, Mesh mesh, SkinnedMeshRenderer skinned, in BasisGlobalIlluminationRaySceneSettings settings)
    {
        Mesh geometryMesh = mesh;
        Mesh baked = null;
        if (skinned != null)
        {
            baked = new Mesh { name = "BasisGIRayBaked_" + renderer.name, hideFlags = HideFlags.HideAndDontSave };
            try
            {
                skinned.BakeMesh(baked, true);
            }
            catch (Exception)
            {
                UnityEngine.Object.DestroyImmediate(baked);
                return;
            }
            geometryMesh = baked;
        }

        MeshGeometry geometry = skinned != null ? BuildGeometry(geometryMesh) : AcquireGeometry(geometryMesh);
        if (geometry == null)
        {
            if (baked != null) { UnityEngine.Object.DestroyImmediate(baked); }
            return;
        }

        Matrix4x4 matrix = MatrixFor(renderer, skinned);
        Entry entry = new Entry
        {
            renderer = renderer,
            transform = renderer.transform,
            skinned = skinned,
            sourceMesh = mesh,
            bakedMesh = baked,
            geometry = geometry,
            sharedGeometry = skinned == null,
            matrix = matrix,
            isStatic = renderer.gameObject.isStatic && skinned == null,
            seen = true,
            lastBakeFrame = Time.frameCount
        };

        if (!AddInstances(entry, geometryMesh, matrix))
        {
            ReleaseGeometry(entry);
            if (baked != null) { UnityEngine.Object.DestroyImmediate(baked); }
            return;
        }

        entries[renderer.GetEntityId()] = entry;
        if (skinned != null) { skinnedEntries.Add(entry); }
        WriteMaterials(entry, settings);
        structureDirty = true;
    }

    private static Matrix4x4 MatrixFor(Renderer renderer, SkinnedMeshRenderer skinned)
    {
        if (skinned == null) { return renderer.transform.localToWorldMatrix; }
        return Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
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
                    mask = 0xff,
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
        for (int index = 0; index < entry.instanceIds.Length; index++)
        {
            int instanceId = entry.instanceIds[index];
            if (instanceId < 0) { continue; }

            Material material = index < materialScratch.Count ? materialScratch[index] : null;
            int blockIndex = index < materialScratch.Count ? index : -1;
            ReadSurface(material, entry.renderer, blockIndex, settings, textures, out Color albedo, out Color emission);
            Vector4 packedAlbedo = new Vector4(albedo.r, albedo.g, albedo.b, 1f);
            Vector4 packedEmission = new Vector4(emission.r, emission.g, emission.b, 0f);
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
        albedo = Color.white;
        emission = Color.black;
        if (material == null) { return; }

        MaterialPropertyBlock block = ResolveBlock(renderer, materialIndex);

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
        LocalKeyword emissionKeyword = material.shader.keywordSpace.FindKeyword(EmissionKeyword);
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

    /// <summary>
    /// The renderer's property block for this material slot, or null when it carries none. A block set for
    /// one slot and a block set for the whole renderer are both reachable here, and the per slot one wins
    /// where both exist - the order the renderer itself applies them.
    /// </summary>
    private static MaterialPropertyBlock ResolveBlock(Renderer renderer, int materialIndex)
    {
        if (renderer == null || !renderer.HasPropertyBlock()) { return null; }

        if (materialIndex >= 0)
        {
            blockScratch.Clear();
            renderer.GetPropertyBlock(blockScratch, materialIndex);
            if (!blockScratch.isEmpty) { return blockScratch; }
        }

        blockScratch.Clear();
        renderer.GetPropertyBlock(blockScratch);
        return blockScratch.isEmpty ? null : blockScratch;
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
        if (entry.bakedMesh != null) { UnityEngine.Object.DestroyImmediate(entry.bakedMesh); }
        if (entry.skinned != null) { skinnedEntries.Remove(entry); }
        entries.Remove(id);
        structureDirty = true;
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
        foreach (KeyValuePair<EntityId, Entry> pair in entries)
        {
            Entry entry = pair.Value;
            if (entry.isStatic || entry.transform == null || entry.skinned != null) { continue; }

            Matrix4x4 matrix = entry.transform.localToWorldMatrix;
            if (matrix == entry.matrix) { continue; }

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
    }

    private void UpdateSkinned(in BasisGlobalIlluminationRaySceneSettings settings, Vector3 viewerPosition, int frameCount)
    {
        if (skinnedEntries.Count == 0 || settings.skinnedBakesPerFrame <= 0) { return; }

        int budget = settings.skinnedBakesPerFrame;
        int examined = 0;
        float maxDistanceSquared = settings.skinnedMaxDistance * settings.skinnedMaxDistance;

        while (budget > 0 && examined < skinnedEntries.Count)
        {
            skinnedCursor = (skinnedCursor + 1) % skinnedEntries.Count;
            examined++;

            Entry entry = skinnedEntries[skinnedCursor];
            if (entry.skinned == null || entry.bakedMesh == null || entry.transform == null) { continue; }
            if (frameCount - entry.lastBakeFrame < settings.skinnedBakeInterval) { continue; }
            if (maxDistanceSquared > 0f && (entry.transform.position - viewerPosition).sqrMagnitude > maxDistanceSquared) { continue; }

            entry.lastBakeFrame = frameCount;
            budget--;
            RebakeSkinned(entry);
        }
    }

    /// <summary>
    /// Re-bakes one skinned renderer into its own mesh and re-adds it to the structure. The topology never
    /// changes across a pose, so the arena blocks and the instance ids survive the rebake and only the
    /// normals are rewritten - which is what keeps the ids the trace kernel resolves stable.
    /// </summary>
    private void RebakeSkinned(Entry entry)
    {
        try
        {
            entry.skinned.BakeMesh(entry.bakedMesh, true);
        }
        catch (Exception)
        {
            return;
        }

        if (entry.geometry.hasNormals && entry.bakedMesh.isReadable)
        {
            try
            {
                entry.bakedMesh.GetNormals(normalScratch);
                if (normalScratch.Count == entry.geometry.vertexCount) { WriteNormals(entry.geometry.normals); }
            }
            catch (Exception)
            {
                // Keep the previous pose's normals rather than dropping the avatar out of the trace.
            }
        }

        Matrix4x4 matrix = MatrixFor(entry.renderer, entry.skinned);
        for (int index = 0; index < entry.handles.Length; index++)
        {
            if (entry.handles[index] >= 0)
            {
                accelStruct.RemoveInstance(entry.handles[index]);
                entry.handles[index] = -1;
            }
            if (entry.instanceIds[index] < 0) { continue; }

            try
            {
                MeshInstanceDesc desc = new MeshInstanceDesc(entry.bakedMesh, index)
                {
                    localToWorldMatrix = matrix,
                    mask = 0xff,
                    instanceID = (uint)entry.instanceIds[index],
                    enableTriangleCulling = false,
                    opaqueGeometry = true
                };
                entry.handles[index] = accelStruct.AddInstance(desc);
            }
            catch (Exception)
            {
                continue;
            }

            instances[entry.instanceIds[index]].SetNormalMatrix(matrix);
            MarkInstanceDirty(entry.instanceIds[index]);
        }

        entry.matrix = matrix;
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
        foreach (KeyValuePair<EntityId, Entry> pair in entries)
        {
            if (pair.Value.bakedMesh != null) { UnityEngine.Object.DestroyImmediate(pair.Value.bakedMesh); }
        }
        entries.Clear();
        meshCache.Clear();
        skinnedEntries.Clear();
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
