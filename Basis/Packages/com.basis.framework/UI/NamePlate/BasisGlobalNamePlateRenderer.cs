using System.Collections.Generic;
using System.Runtime.InteropServices;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.UI.NamePlate
{
    /// <summary>
    /// Merges every remote nameplate into a single panel mesh plus one text mesh per font atlas,
    /// so the whole lobby's name labels cost ~2 draw calls instead of ~2 per player.
    ///
    /// The merged geometry (topology + all vertex channels) is built with CombineMeshes only when
    /// the baked-plate set changes — a setup-time op, never per frame. Each layer is billboarded one
    /// of two ways:
    ///  - GPU (<see cref="GpuBillboardPanel"/> / <see cref="GpuBillboardText"/>): plate-local
    ///    positions + a per-vertex plate id are uploaded once, and each frame the CPU pushes only the
    ///    small per-plate matrix (+ panel color) buffers — the vertex shader does the transform.
    ///  - CPU (fallback): a Burst job transforms the cached local positions by the plates' current
    ///    matrices and pushes the position buffer with revalidation disabled.
    /// Hidden plates collapse to a degenerate point via a zero matrix, so visibility needs no rebuild.
    /// </summary>
    public static class BasisGlobalNamePlateRenderer
    {
        private const float NoCullExtent = 100000f;

        // Below this vertex count the text-merge job runs inline (still Burst-compiled via .Run) —
        // scheduling overhead would dominate the trivial work for tiny rebuilds.
        private const int ParallelVertexThreshold = 2048;

        /// <summary>
        /// GPU-billboard the panel layer in its vertex shader instead of transforming + re-uploading
        /// its vertices on the CPU every frame. Flip to A/B against the CPU path (rebuilds next frame).
        /// </summary>
        public static bool GpuBillboardPanel = true;
        /// <summary>
        /// GPU-billboard the text layers (forked TMP SDF shader). Flip to A/B against the CPU path;
        /// the text layers are torn down + rebuilt on change (GPU/CPU use different materials).
        /// </summary>
        public static bool GpuBillboardText = true;

        private const string GpuKeyword = "BASIS_NAMEPLATE_GPU";
        private const string TextBillboardShaderName = "Basis/NamePlate/Text";
        private static readonly int PlateMatricesId = Shader.PropertyToID("_PlateMatrices");
        private static readonly int PlateColorsId = Shader.PropertyToID("_PlateColors");
        private static Shader textBillboardShader;

        private static GameObject root;
        private static int layer = 5;
        private static Material panelMaterial;
        private static bool initialized;
        private static bool dirty;
        private static bool panelKeywordApplied;
        private static bool textGpuApplied;

        // ---- Per-frame visibility culling (culled plates collapse to a degenerate point) ----
        /// <summary>Cull plates farther than this many metres from the viewer. 0 disables.</summary>
        public static float MaxDistance = 0f;
        /// <summary>Cull plates behind the viewer (and far off to the sides).</summary>
        public static bool CullBehind = true;
        /// <summary>dot(camForward, dirToPlate) below this is culled (-0.25 ≈ 105° off-forward).</summary>
        public static float BehindDotThreshold = -0.25f;
        /// <summary>Cull plates the viewer can't see through walls (linecast on <see cref="OcclusionMask"/>).</summary>
        public static bool CullOccluded = true;
        /// <summary>Environment layers for through-wall occlusion. Empty (0) disables it — set to your world/scenery layers.</summary>
        public static LayerMask OcclusionMask;

        // Per-frame plate data, indexed by snapshot order.
        private static readonly List<BasisRemoteNamePlate> snapshot = new(64);
        private static NativeArray<Matrix4x4> matrices;
        private static NativeArray<Color> plateColors;
        private static int plateCapacity;

        // Plate-order → playerId (cached at topology rebuild) and → dense sOut slot (resolved each
        // frame). Lets each plate's matrix be rebuilt from RemoteBoneJobSystem's already-computed
        // pose (sOut: hips + height) in Burst, instead of reading the plate Transform on the main
        // thread. The nameplate is a standalone scene root scaled by NamePlateSize, so its full
        // world matrix is hips-position + yaw-to-camera + that uniform scale.
        private static NativeArray<int> plateKey;
        private static NativeArray<int> plateSlot;
        // Per-plate world position copied out of sOut during the gather, so the scheduled
        // matrix job never dereferences the bone system's array while deferred to before-render.
        private static NativeArray<float3> platePos;
        // Snapshot mirrored as a plain array so the per-frame gather indexes a T[] instead of
        // List<T>.this[] (the List indexer's bounds-check showed up in the gather).
        private static BasisRemoteNamePlate[] snapArr = System.Array.Empty<BasisRemoteNamePlate>();
        // Reused scratch for the per-vertex GPU plate-id UV fill (avoids a per-rebuild Temp alloc).
        private static NativeArray<Vector2> uvScratch;

        // Topology rebuild (CombineMeshes) is heavy and allocates; coalesce it during join storms so
        // it runs at most once per this many frames while plates are streaming in. A lone join after
        // an idle gap still rebuilds immediately. Set to 1 to rebuild on every dirty frame.
        public static int RebuildMinFrameGap = 4;
        private static int lastRebuildFrame = -1000;

        // Manual panel merge: build the merged panel vertex+index buffers directly from each plate's
        // quad into reused NativeArrays via SetVertexBufferData — no CombineMeshes, no per-rebuild
        // CombineInstance[] GC. Flip false to fall back to the CombineMeshes path. (Text still uses
        // CombineMeshes.)
        public static bool ManualMergePanel = true;
        private static NativeArray<PanelVertex> panelScratch;
        private static NativeArray<uint> indexScratch;
        private static NativeArray<Vector3> tmpPos;
        private static NativeArray<Vector2> tmpUv0;
        private static NativeArray<int> tmpIdx;
        private static readonly List<Mesh> srcMeshList = new(64);
        private static readonly VertexAttributeDescriptor[] PanelLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2),
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct PanelVertex
        {
            public float3 Pos;
            public float2 Uv0;
            public float2 Uv1;
        }

        // Manual text merge (same idea as the panel, but the forked TMP SDF vertex is multi-channel:
        // position + normal + color + 4-component UV0 (atlas + SDF scale in .w) + the plate id in UV2).
        public static bool ManualMergeText = true;
        private static NativeArray<TextVertex> textScratch;
        // Per-source-mesh write offsets + plate id, filled on the main thread and read by MergeTextJob.
        private static NativeArray<int> textVOff;
        private static NativeArray<int> textIOff;
        private static NativeArray<int> textPlateId;
        private static readonly VertexAttributeDescriptor[] TextLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 2),
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct TextVertex
        {
            public float3 Pos;
            public float3 Normal;
            public float4 Color;
            public float4 Uv0;
            public float2 Uv2;
        }
        /// <summary>
        /// Build per-plate matrices from RemoteBoneJobSystem's computed pose (no Transform /
        /// isActiveAndEnabled reads). Falls back to the Transform path when the bone system isn't
        /// ready; flip to force the Transform path for A/B.
        /// </summary>
        public static bool UseBoneSystemMatrices = true;

        // Shared GPU per-plate buffers (matrices stored as 4 float4 columns each).
        private static GraphicsBuffer plateMatrixBuffer;
        private static GraphicsBuffer plateColorBuffer;
        private static int gpuBufferCapacity;

        private static Layer panel;
        private static readonly Dictionary<Material, Layer> textLayers = new();
        private static readonly List<Layer> textLayerList = new();
        // TMP atlas material -> our billboard clone (forked shader, same props/keywords/atlas).
        private static readonly Dictionary<Material, Material> textBillboardMats = new();

        private const MeshUpdateFlags FastUpdate =
            MeshUpdateFlags.DontRecalculateBounds |
            MeshUpdateFlags.DontValidateIndices |
            MeshUpdateFlags.DontNotifyMeshUsers;

        private sealed class Layer
        {
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public bool Gpu;
            public NativeArray<Vector3> LocalPos;
            public NativeArray<int> PlateIdx;
            public NativeArray<Vector3> WorldPos;
            public NativeArray<Color> ColorBuf; // CPU panel path only
            public int VertexCount;
            public int Capacity;

            // Reused during a topology rebuild.
            public readonly List<CombineInstance> Combine = new(64);
            public readonly List<int> SrcPlate = new(64);
            public readonly List<int> SrcVertexCount = new(64);
            private CombineInstance[] combineArray;

            public CombineInstance[] CombineExact()
            {
                int n = Combine.Count;
                if (combineArray == null || combineArray.Length != n) combineArray = new CombineInstance[n];
                for (int i = 0; i < n; i++) combineArray[i] = Combine[i];
                return combineArray;
            }

            public void EnsureCapacity(int vc, bool withColor)
            {
                if (LocalPos.IsCreated && Capacity >= vc) return;
                DisposeBuffers();
                Capacity = vc;
                LocalPos = new NativeArray<Vector3>(vc, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                PlateIdx = new NativeArray<int>(vc, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                WorldPos = new NativeArray<Vector3>(vc, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                if (withColor) ColorBuf = new NativeArray<Color>(vc, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            public void DisposeBuffers()
            {
                if (LocalPos.IsCreated) LocalPos.Dispose();
                if (PlateIdx.IsCreated) PlateIdx.Dispose();
                if (WorldPos.IsCreated) WorldPos.Dispose();
                if (ColorBuf.IsCreated) ColorBuf.Dispose();
                Capacity = 0;
            }
        }

        public static bool IsInitialized => initialized;

        /// <summary>Flags the merged geometry for a rebuild next frame (plate baked / removed).</summary>
        public static void MarkDirty() => dirty = true;

        public static void EnsureInitialized(Material panelMat, int plateLayer)
        {
            panelMaterial = panelMat;
            layer = plateLayer;

            if (initialized)
            {
                if (panel != null && panel.Renderer != null && panel.Renderer.sharedMaterial != panelMaterial)
                    panel.Renderer.sharedMaterial = panelMaterial;
                ApplyPanelKeyword();
                return;
            }

            if (GpuBillboardText && textBillboardShader == null)
            {
                textBillboardShader = Shader.Find(TextBillboardShaderName);
                if (textBillboardShader == null) GpuBillboardText = false; // shader stripped -> CPU text
            }
            textGpuApplied = GpuBillboardText;

            // Standalone world-identity root: the merged meshes are built in world space (plate
            // matrices are world poses), so root needs no transform. Parenting it under a moving
            // transform would only force a per-frame root.worldToLocalMatrix read that cancels root's
            // own localToWorld anyway.
            root = new GameObject("BasisGlobalNamePlates");
            Object.DontDestroyOnLoad(root);
            root.layer = layer;

            panel = NewLayer("Panels", panelMaterial);
            ApplyPanelKeyword();

            dirty = true;
            initialized = true;
        }

        // In-flight matrix-build + CPU vertex-transform chain, completed and published by
        // FinishFrame at before-render — or joined early by CompletePendingVertexJobs when
        // something needs to mutate the buffers first.
        private static JobHandle pendingVertexJobs;
        private static bool vertexJobsPending;
        // Layers awaiting publish (GPU buffer upload + renderer enable / CPU vertex push) at
        // FinishFrame. Set every UpdateFrame; cleared unpublished when a rebuild or dispose
        // joins early (that frame simply keeps the previous upload).
        private static bool publishPending;
        private static int publishPlateCount;

        /// <summary>
        /// Joins the in-flight jobs without publishing their output. Must run before anything
        /// writes or frees the buffers the jobs touch — the top of Rebuild, and Dispose.
        /// </summary>
        private static void CompletePendingVertexJobs()
        {
            publishPending = false;
            if (!vertexJobsPending) return;
            vertexJobsPending = false;
            pendingVertexJobs.Complete();
            pendingVertexJobs = default;
        }

        /// <summary>
        /// Completes the frame's scheduled matrix/vertex jobs and publishes: uploads the
        /// per-plate GPU buffers and pushes/enables the layers. Called from before-render, so
        /// the jobs overlap the tail of LateUpdate and Unity's internal post-late work — and
        /// the GraphicsBuffer uploads land here instead of inside the NamePlate.Complete marker.
        /// </summary>
        public static void FinishFrame()
        {
            if (!vertexJobsPending && !publishPending) return;
            if (vertexJobsPending)
            {
                vertexJobsPending = false;
                pendingVertexJobs.Complete();
                pendingVertexJobs = default;
            }
            if (publishPending)
            {
                publishPending = false;
                PublishGpuBuffers();
                FinalizeLayers();
            }
        }

        /// <summary>Uploads the per-plate matrix (+ panel color) buffers for the GPU layers.
        /// Runs after the matrix job completed, so nothing reads the arrays concurrently.</summary>
        private static void PublishGpuBuffers()
        {
            int plateCount = publishPlateCount;
            if (plateCount == 0) return;

            bool anyGpu = panel.Gpu;
            for (int i = 0; i < textLayerList.Count; i++) anyGpu |= textLayerList[i].Gpu;
            if (!anyGpu) return;

            EnsureGpuBuffers(plateCapacity);
            // Matrix4x4 is column-major in memory, so reinterpreting to float4 yields the 4 columns
            // per plate the shader expects. Colors are Color (== float4 layout).
            plateMatrixBuffer.SetData(matrices.Reinterpret<float4>(UnsafeUtility.SizeOf<Matrix4x4>()), 0, 0, plateCount * 4);
            if (panel.Gpu) plateColorBuffer.SetData(plateColors.Reinterpret<float4>(), 0, 0, plateCount);
        }

        /// <summary>
        /// Rebuilds topology if the plate set changed, then transforms positions for the frame.
        /// Call once per frame after the plate transforms and colors are final.
        /// </summary>
        public static void Rebuild(BasisRemoteNamePlate[] plates, int count)
        {
            if (!initialized || panelMaterial == null) return;

            // Normally a no-op (FinishFrame ran at before-render); catches a frame where
            // before-render never fired before the gather below rewrites the matrices.
            CompletePendingVertexJobs();

            if (panelKeywordApplied != GpuBillboardPanel)
            {
                ApplyPanelKeyword();
                dirty = true;
            }
            if (textGpuApplied != GpuBillboardText)
            {
                TearDownTextLayers(); // GPU/CPU text use different materials; recreate them
                textGpuApplied = GpuBillboardText;
                dirty = true;
            }

            if (dirty && Time.frameCount - lastRebuildFrame >= RebuildMinFrameGap)
            {
                RebuildTopology(plates, count);
                dirty = false;
                lastRebuildFrame = Time.frameCount;
            }

            if (snapshot.Count == 0)
            {
                SetLayerEnabled(panel, false);
                for (int i = 0; i < textLayerList.Count; i++) SetLayerEnabled(textLayerList[i], false);
                return;
            }

            UpdateFrame();
        }

        private static void RebuildTopology(BasisRemoteNamePlate[] plates, int count)
        {
            snapshot.Clear();
            for (int i = 0; i < count; i++)
            {
                BasisRemoteNamePlate p = plates[i];
                if (p != null && p.HasGlobalParts && p.GlobalPanelMesh != null) snapshot.Add(p);
            }

            int plateCount = snapshot.Count;
            EnsurePlateCapacity(plateCount);

            // Cache each plate (as a plain array) + its network playerId once (stable per plate) for
            // the per-frame sOut-slot resolution in GatherFromBoneSystem.
            for (int gi = 0; gi < plateCount; gi++)
            {
                var plate = snapshot[gi];
                snapArr[gi] = plate;
                var rp = plate.BasisRemotePlayer;
                var recv = rp != null ? rp.NetworkReceiver : null;
                plateKey[gi] = recv != null ? recv.playerId : -1;
            }

            // Panel layer: one quad per plate.
            panel.Combine.Clear();
            panel.SrcPlate.Clear();
            panel.SrcVertexCount.Clear();
            for (int gi = 0; gi < plateCount; gi++)
            {
                Mesh m = snapshot[gi].GlobalPanelMesh;
                panel.Combine.Add(new CombineInstance { mesh = m });
                panel.SrcPlate.Add(gi);
                panel.SrcVertexCount.Add(m.vertexCount);
            }
            BuildLayerGeometry(panel, true);

            // Text layers: bucket every plate's per-atlas meshes by material.
            for (int i = 0; i < textLayerList.Count; i++)
            {
                textLayerList[i].Combine.Clear();
                textLayerList[i].SrcPlate.Clear();
                textLayerList[i].SrcVertexCount.Clear();
            }
            for (int gi = 0; gi < plateCount; gi++)
            {
                BasisRemoteNamePlate p = snapshot[gi];
                Mesh[] meshes = p.GlobalTextMeshes;
                Material[] mats = p.GlobalTextMaterials;
                if (meshes == null || mats == null) continue;

                int parts = Mathf.Min(meshes.Length, mats.Length);
                for (int k = 0; k < parts; k++)
                {
                    if (meshes[k] == null || mats[k] == null) continue;
                    Layer textLayer = GetTextLayer(mats[k]);
                    textLayer.Combine.Add(new CombineInstance { mesh = meshes[k] });
                    textLayer.SrcPlate.Add(gi);
                    textLayer.SrcVertexCount.Add(meshes[k].vertexCount);
                }
            }
            for (int i = 0; i < textLayerList.Count; i++)
            {
                BuildLayerGeometry(textLayerList[i], false);
            }
        }

        /// <summary>
        /// Runs CombineMeshes once to produce the merged channels + topology. For the GPU path it
        /// writes the owning plate id into a UV channel and leaves positions plate-local; for the CPU
        /// path it caches each vertex's local position and plate index for the per-frame transform.
        /// </summary>
        private static unsafe void BuildLayerGeometry(Layer l, bool isPanel)
        {
            l.Gpu = isPanel ? GpuBillboardPanel : (GpuBillboardText && textBillboardShader != null);

            if (l.Combine.Count == 0)
            {
                l.VertexCount = 0;
                SetLayerEnabled(l, false);
                return;
            }

            // Build the merged buffers by hand (no CombineMeshes / CombineInstance[] GC).
            if (l.Gpu && (isPanel ? ManualMergePanel : ManualMergeText))
            {
                if (isPanel) BuildPanelMerged(l);
                else BuildTextMerged(l);
                l.Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * NoCullExtent);
                return;
            }

            // useMatrices:false keeps each plate's geometry in its own local space.
            l.Mesh.Clear();
            l.Mesh.CombineMeshes(l.CombineExact(), true, false);

            int vc = l.Mesh.vertexCount;
            l.VertexCount = vc;
            int entries = l.SrcPlate.Count;

            if (l.Gpu)
            {
                // Positions stay plate-local in the mesh; only the per-vertex plate id is needed.
                // Panel uses UV1 (mesh has UV0 only); text uses UV2 (TMP already uses UV0 + UV1).
                // Unsafe pointer + reused scratch: no per-vertex NativeArray indexer, no Temp alloc.
                int channel = isPanel ? 1 : 2;
                EnsureScratch(ref uvScratch, vc);
                Vector2* idPtr = (Vector2*)uvScratch.GetUnsafePtr();
                int o = 0;
                for (int e = 0; e < entries; e++)
                {
                    int evc = l.SrcVertexCount[e];
                    Vector2 id = new Vector2(l.SrcPlate[e], 0f);
                    for (int j = 0; j < evc; j++) idPtr[o + j] = id;
                    o += evc;
                }
                l.Mesh.SetUVs(channel, uvScratch, 0, vc);
            }
            else
            {
                l.EnsureCapacity(vc, isPanel);

                // Read the combined local positions straight into the native buffer — no List indexer.
                using (Mesh.MeshDataArray mda = Mesh.AcquireReadOnlyMeshData(l.Mesh))
                {
                    mda[0].GetVertices(l.LocalPos.GetSubArray(0, vc));
                }

                // Fill per-vertex plate index from each combine entry's cached vertex span.
                int* idxPtr = (int*)l.PlateIdx.GetUnsafePtr();
                int offset = 0;
                for (int e = 0; e < entries; e++)
                {
                    int evc = l.SrcVertexCount[e];
                    int gi = l.SrcPlate[e];
                    for (int j = 0; j < evc; j++) idxPtr[offset + j] = gi;
                    offset += evc;
                }
            }

            l.Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * NoCullExtent);
        }

        /// <summary>
        /// Merges every plate's panel quad into the layer mesh by hand: concatenates positions + UV0
        /// into a reused interleaved buffer (plus the per-vertex plate id in UV1) and offsets each
        /// plate's indices, then uploads via SetVertexBufferData/SetIndexBufferData. Avoids
        /// Mesh.CombineMeshes and the per-rebuild CombineInstance[] allocation.
        /// </summary>
        private static unsafe void BuildPanelMerged(Layer l)
        {
            int n = l.Combine.Count;
            srcMeshList.Clear();
            for (int i = 0; i < n; i++) srcMeshList.Add(l.Combine[i].mesh);

            using Mesh.MeshDataArray mda = Mesh.AcquireReadOnlyMeshData(srcMeshList);

            int totalV = 0, totalI = 0, maxV = 0, maxI = 0;
            for (int i = 0; i < n; i++)
            {
                int v = mda[i].vertexCount;
                int ic = mda[i].GetSubMesh(0).indexCount;
                totalV += v;
                totalI += ic;
                if (v > maxV) maxV = v;
                if (ic > maxI) maxI = ic;
            }

            l.VertexCount = totalV;
            if (totalV == 0)
            {
                SetLayerEnabled(l, false);
                return;
            }

            EnsureScratch(ref panelScratch, totalV);
            EnsureScratch(ref indexScratch, totalI);
            EnsureScratch(ref tmpPos, maxV);
            EnsureScratch(ref tmpUv0, maxV);
            EnsureScratch(ref tmpIdx, maxI);

            PanelVertex* vp = (PanelVertex*)panelScratch.GetUnsafePtr();
            uint* ip = (uint*)indexScratch.GetUnsafePtr();

            int vOff = 0, iOff = 0;
            for (int i = 0; i < n; i++)
            {
                Mesh.MeshData md = mda[i];
                int vc = md.vertexCount;
                int ic = md.GetSubMesh(0).indexCount;

                md.GetVertices(tmpPos.GetSubArray(0, vc));
                md.GetUVs(0, tmpUv0.GetSubArray(0, vc));
                md.GetIndices(tmpIdx.GetSubArray(0, ic), 0);

                Vector3* pp = (Vector3*)tmpPos.GetUnsafeReadOnlyPtr();
                Vector2* up = (Vector2*)tmpUv0.GetUnsafeReadOnlyPtr();
                int* xp = (int*)tmpIdx.GetUnsafeReadOnlyPtr();

                float2 uv1 = new float2(l.SrcPlate[i], 0f);
                for (int j = 0; j < vc; j++)
                {
                    vp[vOff + j] = new PanelVertex { Pos = pp[j], Uv0 = up[j], Uv1 = uv1 };
                }
                for (int k = 0; k < ic; k++) ip[iOff + k] = (uint)(xp[k] + vOff);

                vOff += vc;
                iOff += ic;
            }

            l.Mesh.Clear();
            l.Mesh.SetVertexBufferParams(totalV, PanelLayout);
            l.Mesh.SetVertexBufferData(panelScratch, 0, 0, totalV);
            l.Mesh.SetIndexBufferParams(totalI, IndexFormat.UInt32);
            l.Mesh.SetIndexBufferData(indexScratch, 0, 0, totalI);
            l.Mesh.subMeshCount = 1;
            l.Mesh.SetSubMesh(0, new SubMeshDescriptor(0, totalI), MeshUpdateFlags.DontRecalculateBounds);
        }

        /// <summary>
        /// Same as <see cref="BuildPanelMerged"/> for a text layer: concatenates the TMP SDF channels
        /// (position + normal + color + 4-component UV0) plus the per-vertex plate id in UV2. The
        /// forked TMP shader ignores TEXCOORD1, so it's omitted. Reads via MeshData typed getters so
        /// channel formats convert safely.
        /// </summary>
        private static void BuildTextMerged(Layer l)
        {
            int n = l.Combine.Count;
            srcMeshList.Clear();
            for (int i = 0; i < n; i++) srcMeshList.Add(l.Combine[i].mesh);

            using Mesh.MeshDataArray mda = Mesh.AcquireReadOnlyMeshData(srcMeshList);

            EnsureScratch(ref textVOff, n);
            EnsureScratch(ref textIOff, n);
            EnsureScratch(ref textPlateId, n);

            int totalV = 0, totalI = 0;
            for (int i = 0; i < n; i++)
            {
                textVOff[i] = totalV;
                textIOff[i] = totalI;
                textPlateId[i] = l.SrcPlate[i];
                totalV += mda[i].vertexCount;
                totalI += mda[i].GetSubMesh(0).indexCount;
            }

            l.VertexCount = totalV;
            if (totalV == 0)
            {
                SetLayerEnabled(l, false);
                return;
            }

            EnsureScratch(ref textScratch, totalV);
            EnsureScratch(ref indexScratch, totalI);

            // Each plate's channel reads (the per-mesh MeshData getters) + interleave run in Burst on a
            // worker, writing into the plate's own disjoint slice of the merged buffers (offsets above).
            var job = new MergeTextJob
            {
                Src = mda,
                VOff = textVOff,
                IOff = textIOff,
                PlateId = textPlateId,
                OutVerts = textScratch,
                OutIdx = indexScratch
            };
            if (totalV >= ParallelVertexThreshold) job.Schedule(n, 16).Complete();
            else job.Run(n);

            l.Mesh.Clear();
            l.Mesh.SetVertexBufferParams(totalV, TextLayout);
            l.Mesh.SetVertexBufferData(textScratch, 0, 0, totalV);
            l.Mesh.SetIndexBufferParams(totalI, IndexFormat.UInt32);
            l.Mesh.SetIndexBufferData(indexScratch, 0, 0, totalI);
            l.Mesh.subMeshCount = 1;
            l.Mesh.SetSubMesh(0, new SubMeshDescriptor(0, totalI), MeshUpdateFlags.DontRecalculateBounds);
        }

        private static void EnsureScratch<T>(ref NativeArray<T> a, int n) where T : struct
        {
            if (a.IsCreated && a.Length >= n) return;
            if (a.IsCreated) a.Dispose();
            a = new NativeArray<T>(Mathf.Max(64, Mathf.NextPowerOfTwo(n)), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        private static void UpdateFrame()
        {
            if (!matrices.IsCreated) return;

            int plateCount = snapshot.Count;

            bool canCull = BasisLocalCameraDriver.CameraInstance != null;
            Vector3 camPos = canCull ? BasisLocalCameraDriver.Position : Vector3.zero;
            Vector3 camFwd = canCull ? BasisLocalCameraDriver.Forward().normalized : Vector3.forward;
            float maxDistSqr = (canCull && MaxDistance > 0f) ? MaxDistance * MaxDistance : float.MaxValue;
            bool cullBehind = canCull && CullBehind;
            bool cullOccluded = canCull && CullOccluded && OcclusionMask.value != 0;

            JobHandle matrixJob = default;
            if (!UseBoneSystemMatrices ||
                !GatherFromBoneSystem(plateCount, camPos, camFwd, maxDistSqr, cullBehind, cullOccluded, out matrixJob))
            {
                GatherFromTransforms(plateCount, camPos, camFwd, maxDistSqr, cullBehind, cullOccluded);
            }

            NativeArray<float4x4> mtx = matrices.Reinterpret<float4x4>();

            // CPU transform for any layer not GPU-billboarded, chained on the matrix build.
            JobHandle deps = matrixJob;
            if (!panel.Gpu) deps = JobHandle.CombineDependencies(deps, ScheduleLayer(panel, true, mtx, matrixJob));
            for (int i = 0; i < textLayerList.Count; i++)
                if (!textLayerList[i].Gpu) deps = JobHandle.CombineDependencies(deps, ScheduleLayer(textLayerList[i], false, mtx, matrixJob));

            // Published by FinishFrame at before-render instead of fenced right here — nothing
            // reads the matrices or transformed vertices before rendering does. The kick starts
            // workers now; the GPU buffer uploads happen at publish, after the jobs completed.
            pendingVertexJobs = deps;
            vertexJobsPending = true;
            publishPending = true;
            publishPlateCount = plateCount;
            JobHandle.ScheduleBatchedJobs();
        }

        private static void FinalizeLayers()
        {
            FinalizeLayer(panel, true);
            for (int i = 0; i < textLayerList.Count; i++) FinalizeLayer(textLayerList[i], false);
        }

        /// <summary>GPU layers just toggle their renderer; CPU layers push the transformed buffers.</summary>
        private static void FinalizeLayer(Layer l, bool isPanel)
        {
            if (l.Gpu) SetLayerEnabled(l, l.VertexCount > 0);
            else PushLayer(l, isPanel);
        }

        private static JobHandle ScheduleLayer(Layer l, bool isPanel, NativeArray<float4x4> mtx, JobHandle dep)
        {
            int vc = l.VertexCount;
            if (vc == 0) return dep;

            NativeArray<float3> local = l.LocalPos.Reinterpret<float3>();
            NativeArray<float3> world = l.WorldPos.Reinterpret<float3>();

            if (isPanel)
            {
                return new TransformColorJob
                {
                    Matrices = mtx,
                    PlateIdx = l.PlateIdx,
                    Local = local,
                    World = world,
                    PlateColors = plateColors.Reinterpret<float4>(),
                    Colors = l.ColorBuf.Reinterpret<float4>()
                }.Schedule(vc, 512, dep);
            }
            return new TransformJob
            {
                Matrices = mtx,
                PlateIdx = l.PlateIdx,
                Local = local,
                World = world
            }.Schedule(vc, 512, dep);
        }

        private static void PushLayer(Layer l, bool isPanel)
        {
            int vc = l.VertexCount;
            if (vc == 0)
            {
                SetLayerEnabled(l, false);
                return;
            }

            l.Mesh.SetVertices(l.WorldPos, 0, vc, FastUpdate);
            if (isPanel) l.Mesh.SetColors(l.ColorBuf, 0, vc, FastUpdate);
            SetLayerEnabled(l, true);
        }

        /// <summary>
        /// Builds every plate matrix from RemoteBoneJobSystem's already-computed pose (sOut) in Burst
        /// instead of reading the plate Transform on the main thread. Per-plate main-thread work drops
        /// to a slot lookup + cached active flag + color + a pose copy. Returns false (caller falls
        /// back to the Transform path) when the bone system isn't ready this frame.
        /// </summary>
        private static unsafe bool GatherFromBoneSystem(int plateCount, Vector3 camPos,
            Vector3 camFwd, float maxDistSqr, bool cullBehind, bool cullOccluded, out JobHandle matrixJob)
        {
            matrixJob = default;

            // The bone pipeline is completed earlier in the tick (CompleteRemoteBoneJobSystemJobs runs
            // before CompleteNamePlates), so this returns the final pose array with no stall.
            NativeArray<RemoteFrameOutput> frames = RemoteBoneJobSystem.GetRemoteFrameArray();
            if (!frames.IsCreated) return false;
            int[] indexMap = RemoteBoneJobSystem.GetSOutIndexMap();
            if (indexMap == null) return false;

            int mapLen = indexMap.Length;
            int frameLen = frames.Length;

            // Phase 1 (main, managed): resolve each plate's dense sOut slot + visibility + color, copy
            // the plate position out of sOut, plus the occlusion linecast. The copy decouples the
            // scheduled matrix job below from the bone system's array — it stays in flight until
            // before-render, past points where sOut may be rebuilt. Unsafe pointers skip the
            // NativeArray bounds/safety-handle overhead; snapArr avoids the List indexer.
            int* keyPtr = (int*)plateKey.GetUnsafeReadOnlyPtr();
            int* slotPtr = (int*)plateSlot.GetUnsafePtr();
            Color* colPtr = (Color*)plateColors.GetUnsafePtr();
            float3* posPtr = (float3*)platePos.GetUnsafePtr();
            RemoteFrameOutput* framePtr = (RemoteFrameOutput*)frames.GetUnsafeReadOnlyPtr();

            for (int gi = 0; gi < plateCount; gi++)
            {
                BasisRemoteNamePlate p = snapArr[gi];
                int pid = keyPtr[gi];
                // ReferenceEquals + the cached RenderActive flag (cleared in DeInitialize) stand in for
                // Unity's `p != null`, so the per-plate gather makes no managed→native op_Inequality call.
                int slot = (!ReferenceEquals(p, null) && p.RenderActive && (uint)pid < (uint)mapLen) ? indexMap[pid] : -1;
                if ((uint)slot >= (uint)frameLen) slot = -1;

                if (slot >= 0)
                {
                    colPtr[gi] = p.CurrentColor;

                    RemoteFrameOutput f = framePtr[slot];
                    Vector3 platePosition = new Vector3(f.pos_Hips.x, f.pos_Hips.y + f.HeightAvatarHipCoord, f.pos_Hips.z);
                    posPtr[gi] = platePosition;

                    if (cullOccluded)
                    {
                        // Reject on the cheap tests BEFORE paying for a scene query. Physics.Linecast
                        // is a synchronous main-thread query costing microseconds each; at high player
                        // counts running one per plate dominates the frame. The matrix job below
                        // already culls by distance and by behind-camera, but it runs after this loop
                        // — so without these two lines every plate behind you, or hundreds of metres
                        // away, still paid for a raycast that its own result was about to discard.
                        // These two tests MIRROR BuildMatricesJob below exactly (including the
                        // distance-normalized behind test) so the visible result is unchanged — the
                        // only difference is not paying for a query whose answer gets thrown away.
                        Vector3 toPlate = platePosition - camPos;
                        float distSqr = toPlate.sqrMagnitude;
                        bool wouldBeCulled = distSqr > maxDistSqr;
                        if (!wouldBeCulled && cullBehind && distSqr > 1e-6f)
                        {
                            float along = Vector3.Dot(camFwd, toPlate);
                            if (along < BehindDotThreshold * Mathf.Sqrt(distSqr)) wouldBeCulled = true;
                        }

                        if (!wouldBeCulled &&
                            Physics.Linecast(camPos, platePosition, OcclusionMask, QueryTriggerInteraction.Ignore))
                        {
                            slot = -1;
                        }
                    }
                }
                slotPtr[gi] = slot;
            }

            // Phase 2: distance/angle cull + per-plate matrix math, scheduled to a worker. The fence
            // sits at FinishFrame (before-render), so the math and the GPU upload both leave the
            // NamePlate.Complete marker; UpdateFrame's kick starts it immediately.
            matrixJob = new BuildMatricesJob
            {
                PlatePos = platePos,
                PlateSlot = plateSlot,
                Matrices = matrices.Reinterpret<float4x4>(),
                Scale = BasisRemoteNamePlateDriver.PlateWorldScale(),
                CamPos = camPos,
                CamFwd = camFwd,
                MaxDistSqr = maxDistSqr,
                BehindDot = BehindDotThreshold,
                CullBehind = cullBehind
            }.Schedule(plateCount, 32);

            return true;
        }

        /// <summary>Fallback gather: reads each plate's Unity Transform (used when the bone system
        /// isn't ready or <see cref="UseBoneSystemMatrices"/> is off).</summary>
        private static void GatherFromTransforms(int plateCount, Vector3 camPos,
            Vector3 camFwd, float maxDistSqr, bool cullBehind, bool cullOccluded)
        {
            for (int gi = 0; gi < plateCount; gi++)
            {
                BasisRemoteNamePlate p = snapArr[gi];
                if (p != null && p.IsGloballyRenderable &&
                    IsPlateVisible(p, camPos, camFwd, maxDistSqr, cullBehind, cullOccluded))
                {
                    matrices[gi] = p.Self.localToWorldMatrix;
                    plateColors[gi] = p.CurrentColor;
                }
                else
                {
                    // Collapse hidden / culled / destroyed plates to a point (zero-area, not rasterized).
                    matrices[gi] = ZeroMatrix;
                }
            }
        }

        private static bool IsPlateVisible(BasisRemoteNamePlate p, Vector3 camPos, Vector3 camFwd,
            float maxDistSqr, bool cullBehind, bool cullOccluded)
        {
            Vector3 platePos = p.Self.position;
            Vector3 toPlate = platePos - camPos;

            float distSqr = toPlate.sqrMagnitude;
            if (distSqr > maxDistSqr) return false; // too far

            if (cullBehind && distSqr > 1e-6f)
            {
                // cos(angle to plate) = dot / dist; cull when below threshold (behind / far to the side).
                float along = Vector3.Dot(camFwd, toPlate);
                if (along < BehindDotThreshold * Mathf.Sqrt(distSqr)) return false;
            }

            if (cullOccluded && Physics.Linecast(camPos, platePos, OcclusionMask, QueryTriggerInteraction.Ignore))
                return false; // blocked by a wall

            return true;
        }

        private static readonly Matrix4x4 ZeroMatrix = new Matrix4x4(
            Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero);

        private static void EnsurePlateCapacity(int n)
        {
            if (matrices.IsCreated && plateCapacity >= n) return;
            if (matrices.IsCreated) matrices.Dispose();
            if (plateColors.IsCreated) plateColors.Dispose();
            if (plateKey.IsCreated) plateKey.Dispose();
            if (plateSlot.IsCreated) plateSlot.Dispose();
            if (platePos.IsCreated) platePos.Dispose();
            plateCapacity = Mathf.Max(16, Mathf.NextPowerOfTwo(n));
            matrices = new NativeArray<Matrix4x4>(plateCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            plateColors = new NativeArray<Color>(plateCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            plateKey = new NativeArray<int>(plateCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            plateSlot = new NativeArray<int>(plateCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            platePos = new NativeArray<float3>(plateCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            snapArr = new BasisRemoteNamePlate[plateCapacity];
        }

        private static void EnsureGpuBuffers(int n)
        {
            if (plateMatrixBuffer != null && gpuBufferCapacity >= n) return;
            plateMatrixBuffer?.Dispose();
            plateColorBuffer?.Dispose();
            gpuBufferCapacity = Mathf.Max(16, Mathf.NextPowerOfTwo(n));
            int f4 = UnsafeUtility.SizeOf<float4>();
            plateMatrixBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gpuBufferCapacity * 4, f4);
            plateColorBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gpuBufferCapacity, f4);
            RebindGpuBuffers();
        }

        private static void RebindGpuBuffers()
        {
            if (plateMatrixBuffer == null) return;
            if (panelMaterial != null)
            {
                panelMaterial.SetBuffer(PlateMatricesId, plateMatrixBuffer);
                panelMaterial.SetBuffer(PlateColorsId, plateColorBuffer);
            }
            foreach (Material m in textBillboardMats.Values)
                if (m != null) m.SetBuffer(PlateMatricesId, plateMatrixBuffer);
        }

        private static void ApplyPanelKeyword()
        {
            panelKeywordApplied = GpuBillboardPanel;
            if (panelMaterial == null) return;
            if (GpuBillboardPanel) panelMaterial.EnableKeyword(GpuKeyword);
            else panelMaterial.DisableKeyword(GpuKeyword);
        }

        private static Layer GetTextLayer(Material tmpMat)
        {
            if (textLayers.TryGetValue(tmpMat, out Layer existing)) return existing;

            Material renderMat = (GpuBillboardText && textBillboardShader != null)
                ? GetTextBillboardMaterial(tmpMat)
                : tmpMat;

            // Keep the panel just below the text so text paints on top within the transparent queue.
            if (panelMaterial != null && panelMaterial.renderQueue >= renderMat.renderQueue)
                panelMaterial.renderQueue = renderMat.renderQueue - 1;

            Layer l = NewLayer("Text", renderMat);
            textLayers.Add(tmpMat, l);
            textLayerList.Add(l);
            return l;
        }

        /// <summary>Clones a TMP atlas material onto the billboard shader (same props/keywords/atlas).</summary>
        private static Material GetTextBillboardMaterial(Material tmpMat)
        {
            if (textBillboardMats.TryGetValue(tmpMat, out Material existing) && existing != null)
                return existing;

            var bm = new Material(tmpMat) { name = tmpMat.name + " (NamePlate GPU)" };
            bm.shader = textBillboardShader;
            if (plateMatrixBuffer != null) bm.SetBuffer(PlateMatricesId, plateMatrixBuffer);
            textBillboardMats[tmpMat] = bm;
            return bm;
        }

        private static Layer NewLayer(string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.layer = layer;

            var mesh = new Mesh { name = "Global" + name + "Mesh", indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            mr.enabled = false;

            return new Layer { Renderer = mr, Mesh = mesh };
        }

        private static void SetLayerEnabled(Layer l, bool on)
        {
            if (l != null && l.Renderer != null && l.Renderer.enabled != on) l.Renderer.enabled = on;
        }

        /// <summary>Destroys the text layer GameObjects/meshes so the next rebuild recreates them with
        /// the materials matching the current <see cref="GpuBillboardText"/> mode.</summary>
        private static void TearDownTextLayers()
        {
            for (int i = 0; i < textLayerList.Count; i++)
            {
                Layer l = textLayerList[i];
                l.DisposeBuffers();
                if (l.Mesh != null) Object.Destroy(l.Mesh);
                if (l.Renderer != null) Object.Destroy(l.Renderer.gameObject);
            }
            textLayerList.Clear();
            textLayers.Clear();
        }

        public static void Dispose()
        {
            if (!initialized) return;

            // The buffers below are freed while a deferred vertex chain may still read them.
            CompletePendingVertexJobs();

            if (panel != null)
            {
                if (panel.Mesh != null) Object.Destroy(panel.Mesh);
                panel.DisposeBuffers();
            }
            foreach (Layer l in textLayerList)
            {
                if (l.Mesh != null) Object.Destroy(l.Mesh);
                l.DisposeBuffers();
            }
            if (root != null) Object.Destroy(root);

            if (matrices.IsCreated) matrices.Dispose();
            if (plateColors.IsCreated) plateColors.Dispose();
            if (plateKey.IsCreated) plateKey.Dispose();
            if (plateSlot.IsCreated) plateSlot.Dispose();
            if (platePos.IsCreated) platePos.Dispose();
            if (uvScratch.IsCreated) uvScratch.Dispose();
            if (panelScratch.IsCreated) panelScratch.Dispose();
            if (textScratch.IsCreated) textScratch.Dispose();
            if (indexScratch.IsCreated) indexScratch.Dispose();
            if (tmpPos.IsCreated) tmpPos.Dispose();
            if (tmpUv0.IsCreated) tmpUv0.Dispose();
            if (tmpIdx.IsCreated) tmpIdx.Dispose();
            if (textVOff.IsCreated) textVOff.Dispose();
            if (textIOff.IsCreated) textIOff.Dispose();
            if (textPlateId.IsCreated) textPlateId.Dispose();
            srcMeshList.Clear();
            snapArr = System.Array.Empty<BasisRemoteNamePlate>();
            plateCapacity = 0;

            plateMatrixBuffer?.Dispose();
            plateMatrixBuffer = null;
            plateColorBuffer?.Dispose();
            plateColorBuffer = null;
            gpuBufferCapacity = 0;

            foreach (Material m in textBillboardMats.Values)
                if (m != null) Object.Destroy(m);
            textBillboardMats.Clear();

            panel = null;
            textLayers.Clear();
            textLayerList.Clear();
            snapshot.Clear();
            panelMaterial = null;
            panelKeywordApplied = false;
            initialized = false;
            dirty = false;
        }

        [BurstCompile]
        private struct TransformJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4x4> Matrices;
            [ReadOnly] public NativeArray<int> PlateIdx;
            [ReadOnly] public NativeArray<float3> Local;
            [WriteOnly] public NativeArray<float3> World;

            public void Execute(int v)
            {
                World[v] = math.transform(Matrices[PlateIdx[v]], Local[v]);
            }
        }

        [BurstCompile]
        private struct TransformColorJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4x4> Matrices;
            [ReadOnly] public NativeArray<int> PlateIdx;
            [ReadOnly] public NativeArray<float3> Local;
            [WriteOnly] public NativeArray<float3> World;
            [ReadOnly] public NativeArray<float4> PlateColors;
            [WriteOnly] public NativeArray<float4> Colors;

            public void Execute(int v)
            {
                int gi = PlateIdx[v];
                World[v] = math.transform(Matrices[gi], Local[v]);
                Colors[v] = PlateColors[gi];
            }
        }

        /// <summary>
        /// Reads each source text mesh's channels (position/normal/color/UV0) via the MeshData typed
        /// getters and writes the interleaved <see cref="TextVertex"/> plus offset indices into the
        /// merged buffers at the plate's precomputed slice. Burst across worker threads replaces the
        /// single-threaded per-mesh read + interleave that dominated the topology rebuild.
        /// </summary>
        [BurstCompile]
        private struct MergeTextJob : IJobParallelFor
        {
            [ReadOnly] public Mesh.MeshDataArray Src;
            [ReadOnly] public NativeArray<int> VOff;
            [ReadOnly] public NativeArray<int> IOff;
            [ReadOnly] public NativeArray<int> PlateId;
            [NativeDisableParallelForRestriction] public NativeArray<TextVertex> OutVerts;
            [NativeDisableParallelForRestriction] public NativeArray<uint> OutIdx;

            public void Execute(int i)
            {
                Mesh.MeshData md = Src[i];
                int vc = md.vertexCount;
                int vo = VOff[i];

                var pos = new NativeArray<Vector3>(vc, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                var uv0 = new NativeArray<Vector4>(vc, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                md.GetVertices(pos);
                md.GetUVs(0, uv0);

                var nrm = new NativeArray<Vector3>(vc, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                if (md.HasVertexAttribute(VertexAttribute.Normal)) md.GetNormals(nrm);
                else for (int j = 0; j < vc; j++) nrm[j] = new Vector3(0f, 0f, -1f);

                var col = new NativeArray<Color>(vc, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                if (md.HasVertexAttribute(VertexAttribute.Color)) md.GetColors(col);
                else for (int j = 0; j < vc; j++) col[j] = new Color(1f, 1f, 1f, 1f);

                NativeArray<float3> pp = pos.Reinterpret<float3>();
                NativeArray<float3> np = nrm.Reinterpret<float3>();
                NativeArray<float4> cp = col.Reinterpret<float4>();   // Color is r,g,b,a floats == float4
                NativeArray<float4> u0 = uv0.Reinterpret<float4>();

                float2 uv2 = new float2(PlateId[i], 0f);
                for (int j = 0; j < vc; j++)
                {
                    OutVerts[vo + j] = new TextVertex
                    {
                        Pos = pp[j],
                        Normal = np[j],
                        Color = cp[j],
                        Uv0 = u0[j],
                        Uv2 = uv2
                    };
                }

                int ic = md.GetSubMesh(0).indexCount;
                int io = IOff[i];
                var idx = new NativeArray<int>(ic, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                md.GetIndices(idx, 0);
                for (int k = 0; k < ic; k++) OutIdx[io + k] = (uint)(idx[k] + vo);

                pos.Dispose();
                uv0.Dispose();
                nrm.Dispose();
                col.Dispose();
                idx.Dispose();
            }
        }

        /// <summary>
        /// Per-plate distance/angle cull + local→root matrix from the gathered plate position
        /// (hips + height, copied out of RemoteBoneJobSystem's pose in phase 1) with a
        /// yaw-to-camera billboard and the uniform nameplate scale — replicating
        /// MappedNameplateApplyJob. Scheduled by GatherFromBoneSystem, fenced at FinishFrame.
        /// Culled / invisible plates (slot &lt; 0) collapse to zero.
        /// </summary>
        [BurstCompile]
        private struct BuildMatricesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> PlatePos;
            [ReadOnly] public NativeArray<int> PlateSlot;
            [WriteOnly] public NativeArray<float4x4> Matrices;
            public float3 Scale;
            public float3 CamPos;
            public float3 CamFwd;
            public float MaxDistSqr;
            public float BehindDot;
            public bool CullBehind;

            public void Execute(int gi)
            {
                if (PlateSlot[gi] < 0)
                {
                    Matrices[gi] = float4x4.zero;
                    return;
                }

                float3 pos = PlatePos[gi];

                float3 toPlate = pos - CamPos;
                float distSqr = math.lengthsq(toPlate);
                bool culled = distSqr > MaxDistSqr;
                if (!culled && CullBehind && distSqr > 1e-6f)
                {
                    float along = math.dot(CamFwd, toPlate);
                    if (along < BehindDot * math.sqrt(distSqr)) culled = true;
                }
                if (culled)
                {
                    Matrices[gi] = float4x4.zero;
                    return;
                }

                // Yaw-only billboard toward the camera (matches MappedNameplateApplyJob).
                float3 toCam = CamPos - pos;
                float2 xz = new float2(toCam.x, toCam.z);
                float yaw = math.lengthsq(xz) > 1e-12f ? math.atan2(xz.x, xz.y) : 0f;
                quaternion rot = quaternion.RotateY(yaw);

                // root is a world-identity transform, so the plate matrix is the plate's world pose.
                Matrices[gi] = float4x4.TRS(pos, rot, Scale);
            }
        }
    }
}
