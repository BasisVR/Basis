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
        Dynamic = 2,
        /// <summary>
        /// Avatars are traced as capsules on their bones rather than as their own deforming mesh, so every
        /// avatar updates every frame instead of waiting its turn in a bake budget. Shares its poses with
        /// the global illumination tracer - see BasisAvatarProxy in Common.
        /// </summary>
        Proxy = 3
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

        /// <summary>
        /// Avatars plus the world geometry around them. Occlusion under furniture and in corners, which the
        /// avatar-only set cannot produce because the surfaces casting it are not in the structure.
        /// </summary>
        public static LayerMask AvatarAndWorldLayers
        {
            get
            {
                int mask = AvatarLayers.value;
                int world = LayerMask.NameToLayer("Default");
                if (world >= 0) { mask |= 1 << world; }
                return mask;
            }
        }

        /// <summary>
        /// Everything except the interface layers. A menu panel in the acceleration structure occludes the
        /// room behind a surface the player reads as an overlay - the same trap the global illumination
        /// trace hit, and the reason neither of them ever means literally everything.
        /// </summary>
        public static LayerMask EverythingButInterfaceLayers
        {
            get
            {
                int mask = ~0;
                string[] interfaceLayers = { "UI", "OverlayUI", "HandHeldCameraUI" };
                for (int index = 0; index < interfaceLayers.Length; index++)
                {
                    int layer = LayerMask.NameToLayer(interfaceLayers[index]);
                    if (layer >= 0) { mask &= ~(1 << layer); }
                }
                return mask;
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
                skinnedMode = BasisRTAOSkinnedMode.Proxy,
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
            // The bake AddEntry takes is of a body that has not been posed yet — a freshly instantiated
            // avatar still stands in the pose its mesh was imported in. The first re-bake therefore ignores
            // both the interval and the distance gate. Without that, an avatar installing further away than
            // skinnedMaxDistance wears that import pose as its occlusion for good, because the distance gate
            // is exactly what stops distant avatars from ever being re-posed.
            public bool needsPosedBake;
            public int lastBakeFrame;
        }

        private readonly BasisRTAOContext context;
        private readonly Dictionary<EntityId, Entry> entries = new Dictionary<EntityId, Entry>();

        /// <summary>
        /// One avatar's capsules. The pose behind it is shared with every other tracer looking at the same
        /// avatar, so a room costs one set of bone reads per frame however many effects are tracing it.
        /// </summary>
        private sealed class ProxyEntry
        {
            public Animator animator;
            public BasisAvatarProxyPose pose;
            public int[] handles;
            public bool seen;
        }

        private readonly Dictionary<EntityId, ProxyEntry> proxies = new Dictionary<EntityId, ProxyEntry>();
        private readonly List<EntityId> proxyRemoval = new List<EntityId>();

        public int ProxyCount => proxies.Count;
        private readonly List<EntityId> pendingRemoval = new List<EntityId>();
        private readonly List<Entry> skinnedEntries = new List<Entry>();
        private IRayTracingAccelStruct accelStruct;
        private float nextScanTime;
        private int lastRefreshFrame = int.MinValue;
        private bool forceRefresh = true;
        private int skinnedCursor;
        private bool structureDirty = true;
        private bool everBuilt;
        // Set when an entry had to be dropped while the mesh its instances were registered against was
        // already destroyed, which is the one case a per handle removal cannot recover from. See
        // ReleaseInstances.
        private bool needsReset;
        private int resetCount;

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
        /// <summary>
        /// How many times a destroyed registered mesh has forced the structure to be rebuilt from
        /// scratch. Only moves when an avatar's bundle unloaded before its entry was released, so a
        /// number that climbs every swap means something is releasing too late.
        /// </summary>
        public int StructureResetCount => resetCount;

        /// <summary>
        /// The mesh this scene baked for <paramref name="renderer"/>, or null if it holds no skinned entry
        /// for it. The bake is owned here and lives exactly as long as its entry, so this is what shows
        /// whether a swap replaced an avatar's geometry or merely added the new body alongside the old one.
        /// </summary>
        internal Mesh BakedMeshFor(Renderer renderer)
        {
            if (renderer == null)
                return null;
            return entries.TryGetValue(renderer.GetEntityId(), out Entry entry) ? entry.bakedMesh : null;
        }

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
                return skinnedMode != BasisRTAOSkinnedMode.Off && skinnedMode != BasisRTAOSkinnedMode.Proxy;
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
                if (settings.skinnedMode == BasisRTAOSkinnedMode.Proxy)
                    RescanProxies(settings);
                else if (proxies.Count > 0)
                    ClearProxies();
            }

            UpdateTransforms();

            if (settings.skinnedMode == BasisRTAOSkinnedMode.Dynamic)
                UpdateSkinned(settings, viewerPosition, frameCount);
            else if (settings.skinnedMode == BasisRTAOSkinnedMode.Static)
                BakeFirstPoses(settings, frameCount);
            else if (settings.skinnedMode == BasisRTAOSkinnedMode.Proxy)
                UpdateProxies(frameCount);

            // Last, so it sees everything the sweep and the re-bakes dropped this frame, and so the
            // structure handed to Build is already whole again.
            ResetStructure();
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

            ResetStructure();
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
                needsPosedBake = skinned != null,
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

        /// <summary>
        /// Finds the humanoids whose capsules belong in the structure and drops the ones that have gone.
        /// Runs on the rescan cadence; the poses themselves are updated every frame by UpdateProxies.
        /// </summary>
        private void RescanProxies(in BasisRTAOSceneSettings settings)
        {
            foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
                pair.Value.seen = false;

            Animator[] animators = UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsInactive.Exclude);
            for (int i = 0; i < animators.Length; i++)
            {
                Animator animator = animators[i];
                if (animator == null || !animator.isHuman)
                    continue;
                if ((settings.layerMask.value & (1 << animator.gameObject.layer)) == 0)
                    continue;

                EntityId id = animator.GetEntityId();
                if (proxies.TryGetValue(id, out ProxyEntry existing))
                {
                    existing.seen = true;
                    continue;
                }

                BasisAvatarProxyPose pose = BasisAvatarProxy.PoseFor(animator);
                if (pose == null || pose.Count == 0)
                    continue;
                AddProxy(animator, pose);
            }

            proxyRemoval.Clear();
            foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
            {
                if (!pair.Value.seen || pair.Value.animator == null)
                    proxyRemoval.Add(pair.Key);
            }
            for (int i = 0; i < proxyRemoval.Count; i++)
            {
                if (proxies.TryGetValue(proxyRemoval[i], out ProxyEntry dead))
                    RemoveProxy(proxyRemoval[i], dead);
            }
            proxyRemoval.Clear();
        }

        private void AddProxy(Animator animator, BasisAvatarProxyPose pose)
        {
            Mesh capsule = BasisAvatarProxy.SharedCapsule();
            if (capsule == null)
                return;

            pose.Update(Time.renderedFrameCount);
            int[] handles = new int[pose.Count];
            for (int i = 0; i < pose.Count; i++)
                handles[i] = -1;

            for (int i = 0; i < pose.Count; i++)
            {
                try
                {
                    MeshInstanceDesc desc = new MeshInstanceDesc(capsule, 0)
                    {
                        localToWorldMatrix = pose.MatrixAt(i),
                        mask = 0xff,
                        enableTriangleCulling = false,
                        opaqueGeometry = true
                    };
                    handles[i] = accelStruct.AddInstance(desc);
                }
                catch (Exception)
                {
                    for (int j = 0; j < i; j++)
                    {
                        if (handles[j] >= 0)
                            accelStruct.RemoveInstance(handles[j]);
                    }
                    return;
                }
            }

            proxies[animator.GetEntityId()] = new ProxyEntry { animator = animator, pose = pose, handles = handles, seen = true };
            structureDirty = true;
        }

        /// <summary>
        /// Every limb of every avatar, every frame. No bake, no readback, no geometry change and so no
        /// BLAS rebuild - only the transform each capsule sits at.
        /// </summary>
        private void UpdateProxies(int frameCount)
        {
            foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
            {
                ProxyEntry entry = pair.Value;
                if (entry.animator == null || entry.handles == null || entry.pose == null)
                    continue;

                // Idempotent. Normally the frame hook has already sampled this avatar and the global
                // illumination tracer is reading the same matrices - one set of bone reads for the room.
                entry.pose.Update(frameCount);

                for (int i = 0; i < entry.handles.Length && i < entry.pose.Count; i++)
                {
                    if (entry.handles[i] < 0)
                        continue;
                    accelStruct.UpdateInstanceTransform(entry.handles[i], entry.pose.MatrixAt(i));
                }
                structureDirty = true;
            }
        }

        private void RemoveProxy(EntityId id, ProxyEntry entry)
        {
            if (entry.handles != null)
            {
                for (int i = 0; i < entry.handles.Length; i++)
                {
                    if (entry.handles[i] >= 0)
                        accelStruct.RemoveInstance(entry.handles[i]);
                    entry.handles[i] = -1;
                }
            }
            proxies.Remove(id);
            structureDirty = true;
        }

        private void ClearProxies()
        {
            foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
            {
                if (pair.Value.handles == null)
                    continue;
                for (int i = 0; i < pair.Value.handles.Length; i++)
                {
                    if (pair.Value.handles[i] >= 0)
                        accelStruct.RemoveInstance(pair.Value.handles[i]);
                }
            }
            proxies.Clear();
            proxyRemoval.Clear();
            structureDirty = true;
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

        /// <summary>
        /// Takes an entry's instances back out of the structure.
        ///
        /// Removing by handle is only correct while the mesh the instances were registered against is
        /// still alive, and the two backends fail in opposite directions once it is not. The hardware
        /// one drops the instance itself the moment Unity destroys its mesh and hands the handle straight
        /// back out, so a late RemoveInstance takes whichever instance inherited it — on an avatar swap,
        /// usually the body that just replaced this one. The compute one does the reverse: it copied the
        /// geometry into its own BLAS pool, keyed by the mesh's instance id, and never hears that the mesh
        /// died, so skipping the removal leaves the old body occluding from where it stood for the rest of
        /// the session, and hands that stale BLAS to the next mesh Unity gives the recycled id to.
        ///
        /// Neither is recoverable one handle at a time, so a dead registered mesh escalates to
        /// <see cref="ResetStructure"/> instead of guessing.
        /// </summary>
        private void ReleaseInstances(Entry entry)
        {
            int[] handles = entry.handles;
            Mesh registered = entry.instanceMesh;
            entry.handles = null;
            entry.instanceMesh = null;
            if (handles == null)
                return;

            if (registered == null)
            {
                needsReset = true;
                return;
            }

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

        /// <summary>
        /// Re-registers every live entry against a cleared structure, and drops the ones that no longer
        /// resolve to geometry.
        ///
        /// This is the recovery for an entry whose registered mesh was destroyed before it could be
        /// released — an avatar bundle unloading out from under a swap is how that happens — where a per
        /// handle removal would either miss it or hit the wrong instance. Clearing invalidates every
        /// handle at once, which is the only statement both backends agree on, and re-adding rebuilds the
        /// bottom level structures from the meshes that are actually still here.
        /// </summary>
        private void ResetStructure()
        {
            if (!needsReset)
                return;

            needsReset = false;
            if (accelStruct == null)
                return;

            try
            {
                accelStruct.ClearInstances();
            }
            catch (Exception)
            {
            }

            pendingRemoval.Clear();
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
            {
                Entry entry = pair.Value;
                // The clear above already invalidated these. Dropping them first is what keeps the
                // RemoveEntry sweep below — and so ReleaseInstances — from arming the flag again.
                entry.handles = null;
                entry.instanceMesh = null;

                Mesh geometry = entry.isSkinned ? entry.bakedMesh : entry.sourceMesh;
                if (entry.renderer == null || entry.transform == null || !IsUsableMesh(geometry))
                {
                    pendingRemoval.Add(pair.Key);
                    continue;
                }

                entry.matrix = MatrixFor(entry.renderer, entry.skinned);
                entry.handles = AddInstances(geometry, entry.matrix);
                if (entry.handles == null)
                {
                    pendingRemoval.Add(pair.Key);
                    continue;
                }

                entry.instanceMesh = geometry;
            }

            for (int i = 0; i < pendingRemoval.Count; i++)
            {
                if (entries.TryGetValue(pendingRemoval[i], out Entry dead))
                    RemoveEntry(pendingRemoval[i], dead);
            }
            pendingRemoval.Clear();

            structureDirty = true;
            resetCount++;
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
                // Both gates are about re-posing, and neither applies to an avatar that has never been
                // posed at all. See Entry.needsPosedBake.
                if (!entry.needsPosedBake)
                {
                    if (frameCount - entry.lastBakeFrame < settings.skinnedBakeInterval)
                        continue;
                    if (maxDistanceSq > 0f && (entry.transform.position - viewerPosition).sqrMagnitude > maxDistanceSq)
                        continue;
                }

                entry.lastBakeFrame = frameCount;
                budget--;
                RebakeSkinned(entry);
            }
        }

        /// <summary>
        /// The one thing Static mode still has to do every frame: give an avatar that has never been posed
        /// its first real bake.
        ///
        /// Static bakes an avatar once and never re-poses it, and the bake AddEntry takes is of a body that
        /// was instantiated moments earlier and is still standing in the pose its mesh was imported in.
        /// Without this pass that import pose IS the avatar's occlusion for the rest of the session — a
        /// T-pose worth of limbs casting from where no limb is. Budgeted the same way Dynamic is, because a
        /// room filling up is a room full of first bakes.
        /// </summary>
        private void BakeFirstPoses(in BasisRTAOSceneSettings settings, int frameCount)
        {
            int budget = settings.skinnedBakesPerFrame;
            if (budget <= 0)
                return;

            // Backwards: RebakeSkinned drops the entry if the re-add fails, and that compacts this list.
            for (int i = skinnedEntries.Count - 1; i >= 0 && budget > 0; i--)
            {
                Entry entry = skinnedEntries[i];
                if (!entry.needsPosedBake)
                    continue;
                if (entry.skinned == null || entry.bakedMesh == null || entry.transform == null)
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

            // Only once the bake actually landed: a throw leaves the import pose in place, and that entry
            // still has to be first in line next frame.
            entry.needsPosedBake = false;

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
            // Outside the null check: the baked meshes are HideAndDontSave, so nothing else will ever
            // collect them, and a scene torn down after its structure had already gone would leak one
            // per avatar it was tracing.
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
            {
                if (pair.Value.bakedMesh != null)
                    UnityEngine.Object.DestroyImmediate(pair.Value.bakedMesh);
            }

            if (accelStruct != null)
            {
                accelStruct.Dispose();
                accelStruct = null;
            }
            needsReset = false;
            entries.Clear();
            ClearProxies();
            skinnedEntries.Clear();
            pendingRemoval.Clear();
        }
    }
}
