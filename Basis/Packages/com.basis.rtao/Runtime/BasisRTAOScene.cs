using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;

namespace Basis.Rendering.RTAO
{
    public enum BasisRTAOSkinnedMode
    {
        Off = 0,
        Static = 1,
        Dynamic = 2
    }

    [Serializable]
    public struct BasisRTAOSceneSettings
    {
        public LayerMask layerMask;
        public bool requireShadowCasting;
        [Min(0.1f)] public float rescanInterval;
        public BasisRTAOSkinnedMode skinnedMode;
        [Range(0, 128)] public int skinnedBakesPerFrame;
        [Range(1, 30)] public int skinnedBakeInterval;
        [Min(0f)] public float skinnedMaxDistance;

        public const string LocalAvatarLayer = "LocalPlayerAvatar";
        public const string RemoteAvatarLayer = "RemotePlayerAvatar";

        /// <summary>
        /// The two avatar layers. Everything this system is for lives on them, and tracing the rest of the
        /// world costs acceleration structure rebuilds for occlusion nobody asked for.
        ///
        /// A constant rather than a name lookup, because Default is a field initializer on BasisRTAOFeature
        /// and Unity forbids NameToLayer there: "NameToLayer is not allowed to be called from a
        /// ScriptableObject constructor (or instance field initializer)". Throwing there aborts the feature's
        /// construction, which aborts the renderer's, which leaves the pipeline half built and the Blitter
        /// initialized without an owner - the editor then throws on every Game view repaint.
        ///
        /// LayersMatchTheirNames in BasisRTAOSettingsTests pins these indices against the named layers, so
        /// reordering the layer list fails the suite instead of quietly tracing the wrong things.
        /// </summary>
        public const int AvatarLayerMask = (1 << 6) | (1 << 7);

        /// <summary>
        /// The same two layers resolved by name. Safe only where Unity allows the lookup - not from a field
        /// initializer - so this exists for tests and editor tooling to check the constant against.
        /// </summary>
        public static LayerMask AvatarLayers
        {
            get
            {
                int local = LayerMask.NameToLayer(LocalAvatarLayer);
                int remote = LayerMask.NameToLayer(RemoteAvatarLayer);

                int mask = 0;
                if (local >= 0)
                    mask |= 1 << local;
                if (remote >= 0)
                    mask |= 1 << remote;

                return mask == 0 ? ~0 : mask;
            }
        }

        public static BasisRTAOSceneSettings Default => FromQuality(BasisRTAOQuality.Medium);

        public static BasisRTAOSceneSettings FromQuality(BasisRTAOQuality quality)
        {
            BasisRTAOSceneSettings settings = new BasisRTAOSceneSettings
            {
                layerMask = AvatarLayerMask,
                requireShadowCasting = true,
                rescanInterval = 2f,
                skinnedMode = BasisRTAOSkinnedMode.Dynamic,
                skinnedMaxDistance = 15f
            };

            settings.skinnedBakesPerFrame = BakeBudgetForQuality(quality);
            settings.skinnedBakeInterval = BakeIntervalForQuality(quality);
            return settings;
        }

        // Re-posing an avatar is CPU skinning plus a BLAS rebuild, so this is the single most expensive
        // thing the quality level buys. Ultra re-poses a full instance every frame; Low keeps whoever is
        // nearest and lets everyone else wear a slightly older pose.
        public static int BakeBudgetForQuality(BasisRTAOQuality quality)
        {
            switch (quality)
            {
                case BasisRTAOQuality.Low: return 1;
                case BasisRTAOQuality.High: return 16;
                case BasisRTAOQuality.Ultra: return 100;
                default: return 4;
            }
        }

        // The budget is per frame and the interval is per avatar, so both have to move together: a budget of
        // 100 does nothing if every avatar is still rate limited to one re-pose every four frames.
        public static int BakeIntervalForQuality(BasisRTAOQuality quality)
        {
            switch (quality)
            {
                case BasisRTAOQuality.Low: return 8;
                case BasisRTAOQuality.High: return 2;
                case BasisRTAOQuality.Ultra: return 1;
                default: return 4;
            }
        }

        public BasisRTAOSceneSettings Validated()
        {
            BasisRTAOSceneSettings copy = this;
            copy.rescanInterval = Mathf.Max(0.1f, copy.rescanInterval);
            copy.skinnedBakesPerFrame = Mathf.Clamp(copy.skinnedBakesPerFrame, 0, 128);
            copy.skinnedBakeInterval = Mathf.Clamp(copy.skinnedBakeInterval, 1, 30);
            copy.skinnedMaxDistance = Mathf.Max(0f, copy.skinnedMaxDistance);
            return copy;
        }
    }

    public sealed class BasisRTAOScene : IDisposable
    {
        internal sealed class Entry
        {
            public EntityId id;
            public Renderer renderer;
            public Transform transform;
            public SkinnedMeshRenderer skinned;
            // The mesh the handles were registered against. Unity drops an instance itself when the mesh
            // behind it dies and hands the handle out again, so removing by handle is only safe while this
            // is alive.
            public Mesh sourceMesh, bakedMesh, instanceMesh;
            public Matrix4x4 matrix;
            public int[] handles;
            // A flag, not "skinned != null": a destroyed SkinnedMeshRenderer compares equal to null, so the
            // component answers no exactly when the entry has to come out of skinnedEntries.
            public bool isStatic, seen, isSkinned;
            public int lastBakeFrame;
        }

        private readonly BasisRTAOContext context;
        private readonly Dictionary<EntityId, Entry> entries = new Dictionary<EntityId, Entry>();
        private readonly List<EntityId> pendingRemoval = new List<EntityId>();
        private readonly List<Entry> skinnedEntries = new List<Entry>();
        private IRayTracingAccelStruct accelStruct;
        private float nextScanTime;
        private int lastRefreshFrame = int.MinValue;
        private bool forceRefresh = true;
        private int skinnedCursor;
        private bool structureDirty = true;
        private bool everBuilt;

        public IRayTracingAccelStruct AccelerationStructure => accelStruct;
        public int InstanceCount => entries.Count;
        public int SkinnedCount => skinnedEntries.Count;
        public int StaleSkinnedCount(int frameCount, int interval)
        {
            int count = 0;
            for (int i = 0; i < skinnedEntries.Count; i++)
            {
                if (frameCount - skinnedEntries[i].lastBakeFrame >= interval)
                    count++;
            }
            return count;
        }
        public bool NeedsBuild => structureDirty || !everBuilt;
        public bool HasGeometry => entries.Count > 0;

        public BasisRTAOScene(BasisRTAOContext context)
        {
            this.context = context;
            accelStruct = context.CreateAccelerationStructure();
        }

        public void MarkDirty()
        {
            nextScanTime = 0f;
            structureDirty = true;
            forceRefresh = true;
        }

        public static bool ShouldInclude(Renderer renderer, in BasisRTAOSceneSettings settings)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                return false;
            if ((settings.layerMask.value & (1 << renderer.gameObject.layer)) == 0)
                return false;
            if (settings.requireShadowCasting && !(renderer is SkinnedMeshRenderer) && renderer.shadowCastingMode == ShadowCastingMode.Off)
                return false;
            if (renderer.TryGetComponent(out BasisRTAOExclude _))
                return false;
            return true;
        }

        public static bool IsSupportedRendererType(Renderer renderer, BasisRTAOSkinnedMode skinnedMode)
        {
            if (renderer is SkinnedMeshRenderer)
                return skinnedMode != BasisRTAOSkinnedMode.Off;
            return renderer is MeshRenderer;
        }

        public static Mesh ResolveMesh(Renderer renderer)
        {
            if (renderer == null)
                return null;
            if (renderer is SkinnedMeshRenderer skinned)
                return skinned.sharedMesh;
            return renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
        }

        public static bool IsUsableMesh(Mesh mesh)
        {
            return mesh != null && mesh.subMeshCount > 0 && mesh.vertexCount > 0 && mesh.HasVertexAttribute(VertexAttribute.Position);
        }

        // Mirrors and the handheld camera each record their own passes, so without this the rescan, the
        // transform sweep and the avatar re-bakes would all run once per camera per frame.
        public void Refresh(in BasisRTAOSceneSettings settings, Vector3 viewerPosition, float time, int frameCount)
        {
            if (accelStruct == null)
                return;
            if (frameCount == lastRefreshFrame && !forceRefresh)
                return;

            lastRefreshFrame = frameCount;
            forceRefresh = false;

            if (time >= nextScanTime)
            {
                nextScanTime = time + Mathf.Max(0.1f, settings.rescanInterval);
                Rescan(settings);
            }

            UpdateTransforms();

            if (settings.skinnedMode == BasisRTAOSkinnedMode.Dynamic)
                UpdateSkinned(settings, viewerPosition, frameCount);
        }

        public void Rescan(in BasisRTAOSceneSettings settings)
        {
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
                pair.Value.seen = false;

            Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsSupportedRendererType(renderer, settings.skinnedMode))
                    continue;
                if (!ShouldInclude(renderer, settings))
                    continue;

                Mesh mesh = ResolveMesh(renderer);
                if (!IsUsableMesh(mesh))
                    continue;

                EntityId id = renderer.GetEntityId();
                if (entries.TryGetValue(id, out Entry existing))
                {
                    if (existing.sourceMesh == mesh)
                    {
                        existing.seen = true;
                        continue;
                    }
                    RemoveEntry(id, existing);
                }

                AddEntry(renderer, mesh, renderer as SkinnedMeshRenderer);
            }

            pendingRemoval.Clear();
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
            {
                if (!pair.Value.seen || pair.Value.renderer == null)
                    pendingRemoval.Add(pair.Key);
            }
            for (int i = 0; i < pendingRemoval.Count; i++)
            {
                if (entries.TryGetValue(pendingRemoval[i], out Entry dead))
                    RemoveEntry(pendingRemoval[i], dead);
            }
        }

        private void AddEntry(Renderer renderer, Mesh mesh, SkinnedMeshRenderer skinned)
        {
            Mesh geometry = mesh;
            Mesh baked = null;
            if (skinned != null)
            {
                baked = new Mesh
                {
                    name = "BasisRTAOBaked_" + renderer.name,
                    hideFlags = HideFlags.HideAndDontSave
                };
                try
                {
                    skinned.BakeMesh(baked, true);
                }
                catch (Exception)
                {
                    UnityEngine.Object.DestroyImmediate(baked);
                    return;
                }
                geometry = baked;
            }

            Matrix4x4 matrix = MatrixFor(renderer, skinned);
            int[] handles = AddInstances(geometry, matrix);
            if (handles == null)
            {
                if (baked != null)
                    UnityEngine.Object.DestroyImmediate(baked);
                return;
            }

            Entry entry = new Entry
            {
                id = renderer.GetEntityId(),
                renderer = renderer,
                transform = renderer.transform,
                skinned = skinned,
                sourceMesh = mesh,
                bakedMesh = baked,
                instanceMesh = geometry,
                matrix = matrix,
                handles = handles,
                isStatic = renderer.gameObject.isStatic && skinned == null,
                seen = true,
                isSkinned = skinned != null,
                lastBakeFrame = Time.frameCount
            };

            entries[entry.id] = entry;
            if (entry.isSkinned)
                skinnedEntries.Add(entry);
            structureDirty = true;
        }

        private static Matrix4x4 MatrixFor(Renderer renderer, SkinnedMeshRenderer skinned)
        {
            if (skinned == null)
                return renderer.transform.localToWorldMatrix;
            return Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
        }

        private int[] AddInstances(Mesh mesh, Matrix4x4 matrix)
        {
            int subMeshCount = mesh.subMeshCount;
            int[] handles = new int[subMeshCount];
            for (int i = 0; i < subMeshCount; i++)
            {
                try
                {
                    MeshInstanceDesc desc = new MeshInstanceDesc(mesh, i)
                    {
                        localToWorldMatrix = matrix,
                        mask = 0xff,
                        enableTriangleCulling = false,
                        opaqueGeometry = true
                    };
                    handles[i] = accelStruct.AddInstance(desc);
                }
                catch (Exception)
                {
                    for (int j = 0; j < i; j++)
                        accelStruct.RemoveInstance(handles[j]);
                    return null;
                }
            }
            return handles;
        }

        private void ReleaseInstances(Entry entry)
        {
            int[] handles = entry.handles;
            Mesh registered = entry.instanceMesh;
            entry.handles = null;
            entry.instanceMesh = null;
            if (handles == null || registered == null)
                return;

            for (int i = 0; i < handles.Length; i++)
            {
                try
                {
                    accelStruct.RemoveInstance(handles[i]);
                }
                catch (Exception)
                {
                }
            }
        }

        private void RemoveEntry(EntityId id, Entry entry)
        {
            ReleaseInstances(entry);
            if (entry.bakedMesh != null)
                UnityEngine.Object.DestroyImmediate(entry.bakedMesh);
            entry.bakedMesh = null;
            if (entry.isSkinned)
            {
                skinnedEntries.Remove(entry);
                entry.isSkinned = false;
            }
            entries.Remove(id);
            structureDirty = true;
        }

        // Dead entries are swept here rather than only on the rescan: an avatar is destroyed the moment it
        // is swapped, and the geometry it left behind was baked into a mesh this class owns, so it outlives
        // the avatar and keeps occluding from wherever the old body was standing.
        private void UpdateTransforms()
        {
            pendingRemoval.Clear();
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
            {
                Entry entry = pair.Value;
                if (entry.renderer == null || entry.transform == null || entry.instanceMesh == null)
                {
                    pendingRemoval.Add(pair.Key);
                    continue;
                }
                if (entry.isStatic || entry.isSkinned)
                    continue;

                Matrix4x4 matrix = entry.transform.localToWorldMatrix;
                if (matrix == entry.matrix)
                    continue;

                entry.matrix = matrix;
                for (int i = 0; i < entry.handles.Length; i++)
                    accelStruct.UpdateInstanceTransform(entry.handles[i], matrix);
                structureDirty = true;
            }

            for (int i = 0; i < pendingRemoval.Count; i++)
            {
                if (entries.TryGetValue(pendingRemoval[i], out Entry dead))
                    RemoveEntry(pendingRemoval[i], dead);
            }
            pendingRemoval.Clear();
        }

        private void UpdateSkinned(in BasisRTAOSceneSettings settings, Vector3 viewerPosition, int frameCount)
        {
            if (skinnedEntries.Count == 0 || settings.skinnedBakesPerFrame <= 0)
                return;

            float maxDistanceSq = settings.skinnedMaxDistance * settings.skinnedMaxDistance;

            // Every avatar follows its own transform every frame, near or far, so a remote never occludes
            // from where it used to be standing.
            for (int i = 0; i < skinnedEntries.Count; i++)
                FollowSkinnedTransform(skinnedEntries[i]);

            int budget = settings.skinnedBakesPerFrame;
            int examined = 0;

            while (budget > 0 && examined < skinnedEntries.Count)
            {
                skinnedCursor = (skinnedCursor + 1) % skinnedEntries.Count;
                examined++;

                Entry entry = skinnedEntries[skinnedCursor];
                if (entry.skinned == null || entry.bakedMesh == null || entry.transform == null)
                    continue;
                if (frameCount - entry.lastBakeFrame < settings.skinnedBakeInterval)
                    continue;
                if (maxDistanceSq > 0f && (entry.transform.position - viewerPosition).sqrMagnitude > maxDistanceSq)
                    continue;

                entry.lastBakeFrame = frameCount;
                budget--;
                RebakeSkinned(entry);
            }
        }

        private void FollowSkinnedTransform(Entry entry)
        {
            if (entry.transform == null || entry.handles == null)
                return;

            Matrix4x4 matrix = MatrixFor(entry.renderer, entry.skinned);
            if (matrix == entry.matrix)
                return;

            entry.matrix = matrix;
            for (int i = 0; i < entry.handles.Length; i++)
                accelStruct.UpdateInstanceTransform(entry.handles[i], matrix);
            structureDirty = true;
        }

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

            ReleaseInstances(entry);

            Matrix4x4 matrix = MatrixFor(entry.renderer, entry.skinned);
            int[] handles = AddInstances(entry.bakedMesh, matrix);
            if (handles == null)
            {
                RemoveEntry(entry.id, entry);
                return;
            }

            entry.handles = handles;
            entry.instanceMesh = entry.bakedMesh;
            entry.matrix = matrix;
            structureDirty = true;
        }

        public void Build(CommandBuffer cmd)
        {
            if (accelStruct == null || cmd == null)
                return;

            GraphicsBuffer scratch = context.GetBuildScratch(accelStruct);
            accelStruct.Build(cmd, scratch);
            structureDirty = false;
            everBuilt = true;
        }

        public void Dispose()
        {
            if (accelStruct != null)
            {
                foreach (KeyValuePair<EntityId, Entry> pair in entries)
                {
                    if (pair.Value.bakedMesh != null)
                        UnityEngine.Object.DestroyImmediate(pair.Value.bakedMesh);
                }
                accelStruct.Dispose();
                accelStruct = null;
            }
            entries.Clear();
            skinnedEntries.Clear();
            pendingRemoval.Clear();
        }
    }
}
