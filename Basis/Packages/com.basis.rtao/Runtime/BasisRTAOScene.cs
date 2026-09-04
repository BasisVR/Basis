using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;

namespace Basis.Rendering.RTAO
{
    /// <summary>
    /// Static and Dynamic are gone. Both re-baked a SkinnedMeshRenderer into a mesh of its own and swapped
    /// that mesh into the structure, which this backend can only do by removing and re-adding it - a full
    /// bottom level rebuild per pose. That had to be rationed by a per frame budget, so the body which
    /// occluded was each avatar's pose from up to several frames ago, staggered differently for every
    /// person in the room. Proxy costs one transform update per limb, so every avatar updates every frame
    /// for less than a single re-bake, and there is nothing the bake path did better.
    ///
    /// The numbering is left alone because the mode is serialized on BasisRTAOFeature: an asset saved with
    /// Proxy holds a 3, and renumbering would silently reinterpret it.
    /// </summary>
    public enum BasisRTAOSkinnedMode
    {
        Off = 0,
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
        /// The room without the people in it: everything traced by <see cref="EverythingButInterfaceLayers"/>
        /// minus the two avatar layers.
        ///
        /// Subtracting from the wide set rather than naming Default is deliberate - worlds put geometry on
        /// plenty of layers, and a "World" option that only traced Default would quietly miss most of a
        /// room. Defined this way the three player-facing sets partition cleanly: Avatars and World are
        /// disjoint and together they are exactly World + Avatars, which LayerSetsPartitionCleanly pins.
        /// </summary>
        public static LayerMask WorldLayers => EverythingButInterfaceLayers.value & ~AvatarLayers.value;

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

        // Nothing in here scales with quality any more. The bake budget was the one genuinely expensive
        // thing a quality level bought - CPU skinning plus a bottom level rebuild, per avatar - and the
        // proxy path replaces it with a transform update per limb, which costs the same trivial amount at
        // every level. The parameter stays so callers keep reading settings through one door.
        public static BasisRTAOSceneSettings FromQuality(BasisRTAOQuality quality)
        {
            return new BasisRTAOSceneSettings
            {
                layerMask = AvatarLayerMask,
                requireShadowCasting = true,
                rescanInterval = 2f,
                skinnedMode = BasisRTAOSkinnedMode.Proxy
            };
        }

        /// <summary>
        /// Which halves of the room this effect wants to trace, as instance mask bits.
        ///
        /// Separate from <see cref="layerMask"/> because the structure being traced may hold more than this
        /// effect asked for: it can be shared with global illumination, which answers the same question
        /// differently, and then the structure holds the union and the ray is what narrows it. Falls back
        /// to everything rather than nothing, so a mask that matches neither set still traces.
        /// </summary>
        public byte TraceCategories
        {
            get
            {
                byte categories = 0;
                if ((layerMask.value & AvatarLayers.value) != 0) { categories |= BasisTracedCategory.Avatar; }
                if ((layerMask.value & WorldLayers.value) != 0) { categories |= BasisTracedCategory.World; }
                return categories == 0 ? BasisTracedCategory.All : categories;
            }
        }

        public BasisRTAOSceneSettings Validated()
        {
            BasisRTAOSceneSettings copy = this;
            copy.rescanInterval = Mathf.Max(0.1f, copy.rescanInterval);
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
            // The mesh the handles were registered against. Unity drops an instance itself when the mesh
            // behind it dies and hands the handle out again, so removing by handle is only safe while this
            // is alive.
            public Mesh sourceMesh, instanceMesh;
            public Matrix4x4 matrix;
            public int[] handles;
            public bool isStatic, seen;
            // Remembered rather than re-derived: ResetStructure re-registers every entry against a cleared
            // structure, and the renderer it came from may already be gone by then.
            public byte category;
            // Slot in the pre-render matrix gather, -1 while not part of it.
            public int gatherIndex = -1;
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

        /// <summary>
        /// The most capsule instances the avatars in a room may take, matching global illumination's own
        /// BasisGlobalIlluminationRayScene.MaxInstances.
        ///
        /// Nothing else here has a ceiling because nothing else scales with the player count: a world's
        /// geometry is whatever the world author built, and it is registered once. Bodies are not - a
        /// public instance can hold far more people than a room holds props, every humanoid animator in it
        /// is discovered by the rescan, and each one is fifteen more instances in the top level structure
        /// that a moving room rebuilds every frame. Past some number the acceleration structure costs more
        /// than the occlusion is worth, and the honest failure is for the people beyond it to stop
        /// occluding rather than for the frame to fall over. Counted over proxies alone rather than over
        /// every instance, because this is the only part that grows without bound.
        /// </summary>
        public const int MaxProxyInstances = 8192;

        public int ProxyCount => proxies.Count;
        private readonly List<EntityId> pendingRemoval = new List<EntityId>();
        private IRayTracingAccelStruct accelStruct;
        private float nextScanTime;
        /// <summary>
        /// How many candidates the geometry pass walks per frame. See the twin in
        /// BasisGlobalIlluminationRayScene for why the walk rather than the scene scan in front of it is
        /// what had to be spread out.
        /// </summary>
        private const int ScanBudget = 256;
        private Renderer[] scanBatch;
        private int scanCursor;
        private bool scanning;
        private float nextProxyScanTime;
        private bool proxyScanPhased;
        private int lastRefreshFrame = int.MinValue;
        private bool forceRefresh = true;
        private bool structureDirty = true;
        private bool everBuilt;
        // Set when an entry had to be dropped while the mesh its instances were registered against was
        // already destroyed, which is the one case a per handle removal cannot recover from. See
        // ReleaseInstances.
        private bool needsReset;
        private int resetCount;

        public IRayTracingAccelStruct AccelerationStructure => accelStruct;
        public int InstanceCount => entries.Count;
        public bool NeedsBuild => structureDirty || !everBuilt;
        public bool HasGeometry => entries.Count > 0;
        /// <summary>
        /// How many times a destroyed registered mesh has forced the structure to be rebuilt from
        /// scratch. Only moves when an avatar's bundle unloaded before its entry was released, so a
        /// number that climbs every swap means something is releasing too late.
        /// </summary>
        public int StructureResetCount => resetCount;

        public BasisRTAOScene(BasisRTAOContext context)
        {
            this.context = context;
            accelStruct = context.CreateAccelerationStructure();
            Application.onBeforeRender += ScheduleTransformGather;
        }

        // The dynamic entries' localToWorldMatrix reads as one transform job scheduled
        // at onBeforeRender, mirroring BasisGlobalIlluminationRayScene — a job must
        // never be SCHEDULED from inside the render pipeline (see the ZBinning note on
        // BasisAvatarProxyJobs). UpdateTransforms only joins and compares; the dead
        // sweep it also does stays a main-thread walk.
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
            CompleteTransformGather();
            if (dynamicListDirty || !dynamicAccess.isCreated || dynamicEntries.Count == 0)
                return;
            dynamicHandle = new GatherWorldMatricesJob { Matrices = dynamicMatrices }.Schedule(dynamicAccess);
            dynamicScheduled = true;
            JobHandle.ScheduleBatchedJobs();
        }

        private void CompleteTransformGather()
        {
            if (!dynamicScheduled)
                return;
            dynamicHandle.Complete();
            dynamicScheduled = false;
        }

        private void RebuildDynamicList()
        {
            CompleteTransformGather();
            dynamicEntries.Clear();
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
            {
                Entry entry = pair.Value;
                entry.gatherIndex = -1;
                if (entry.isStatic || entry.transform == null)
                    continue;
                entry.gatherIndex = dynamicEntries.Count;
                dynamicEntries.Add(entry);
            }
            if (dynamicAccess.isCreated)
                dynamicAccess.Dispose();
            if (dynamicMatrices.IsCreated)
                dynamicMatrices.Dispose();
            dynamicListDirty = false;
            int count = dynamicEntries.Count;
            if (count == 0)
                return;
            Transform[] transforms = new Transform[count];
            for (int index = 0; index < count; index++)
                transforms[index] = dynamicEntries[index].transform;
            dynamicAccess = new TransformAccessArray(transforms);
            dynamicMatrices = new NativeArray<Matrix4x4>(count, Allocator.Persistent);
        }

        public void MarkDirty()
        {
            nextScanTime = 0f;
            nextProxyScanTime = 0f;
            // A pass in flight is walking a snapshot from before whatever changed, so it is abandoned
            // rather than finished. Nothing is left half applied: the next pass re-marks every entry
            // unseen and walks the whole set again, and the sweep only runs at the end of a completed
            // pass. Invalidate so that next pass takes a fresh walk rather than the cached one this call
            // is saying is out of date.
            scanning = false;
            scanBatch = null;
            scanCursor = 0;
            BasisSceneScan.Invalidate();
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

        /// <summary>
        /// Never a SkinnedMeshRenderer. An avatar reaches the structure as proxy capsules or not at all, so
        /// there is no deforming entry left to go stale - which is what retired the whole bake path. The
        /// mode is still taken so callers do not have to know that, and so a future third answer has a seat.
        /// </summary>
        public static bool IsSupportedRendererType(Renderer renderer, BasisRTAOSkinnedMode skinnedMode)
        {
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

            float interval = Mathf.Max(0.1f, settings.rescanInterval);

            if (!scanning && time >= nextScanTime)
            {
                nextScanTime = time + interval;
                BeginScan(interval);
            }
            if (scanning)
                StepScan(settings, ScanBudget);

            if (settings.skinnedMode == BasisRTAOSkinnedMode.Proxy)
            {
                if (time >= nextProxyScanTime)
                {
                    // The first reschedule is a half interval longer than the rest, which parks the
                    // animator walk permanently between two geometry walks instead of on the same frame
                    // as one. Both still run immediately at startup.
                    nextProxyScanTime = time + (proxyScanPhased ? interval : interval * 1.5f);
                    proxyScanPhased = true;
                    RescanProxies(settings, interval);
                }
            }
            else if (proxies.Count > 0)
                ClearProxies();

            UpdateTransforms();

            if (settings.skinnedMode == BasisRTAOSkinnedMode.Proxy)
                UpdateProxies(frameCount);

            // Last, so it sees everything the sweep and the re-bakes dropped this frame, and so the
            // structure handed to Build is already whole again.
            ResetStructure();
        }

        /// <summary>
        /// The whole geometry pass at once. Refresh drives the sliced form below instead; this is for a
        /// caller that has just changed the world and wants the structure to agree before the next frame.
        /// </summary>
        public void Rescan(in BasisRTAOSceneSettings settings)
        {
            BeginScan(Mathf.Max(0.1f, settings.rescanInterval));
            StepScan(settings, int.MaxValue);
        }

        private void BeginScan(float interval)
        {
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
                pair.Value.seen = false;

            // Shared with global illumination, which wants the same set on the same cadence: whichever of
            // the two asks first in the window pays for the walk and the other reads its array.
            scanBatch = BasisSceneScan.Take<Renderer>(interval);
            scanCursor = 0;
            scanning = true;
        }

        private void StepScan(in BasisRTAOSceneSettings settings, int budget)
        {
            if (!scanning)
                return;
            if (scanBatch == null)
            {
                FinishScan();
                return;
            }

            int end = budget >= scanBatch.Length - scanCursor ? scanBatch.Length : scanCursor + budget;
            for (; scanCursor < end; scanCursor++)
            {
                Renderer renderer = scanBatch[scanCursor];
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

                AddEntry(renderer, mesh);
            }

            if (scanCursor >= scanBatch.Length)
                FinishScan();
        }

        /// <summary>
        /// Drops whatever the pass did not find. Only at the END of a pass: an entry the cursor has not
        /// reached yet is unvisited, not missing, and sweeping mid-pass would delete the whole structure
        /// and rebuild it a slice at a time.
        /// </summary>
        private void FinishScan()
        {
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

            scanBatch = null;
            scanCursor = 0;
            scanning = false;

            ResetStructure();
        }

        private void AddEntry(Renderer renderer, Mesh mesh)
        {
            byte category = BasisTracedCategory.For(renderer.gameObject.layer, BasisRTAOSceneSettings.AvatarLayers.value);
            Matrix4x4 matrix = renderer.transform.localToWorldMatrix;
            int[] handles = AddInstances(mesh, matrix, category);
            if (handles == null)
                return;

            Entry entry = new Entry
            {
                id = renderer.GetEntityId(),
                renderer = renderer,
                transform = renderer.transform,
                sourceMesh = mesh,
                instanceMesh = mesh,
                matrix = matrix,
                handles = handles,
                isStatic = renderer.gameObject.isStatic,
                seen = true,
                category = category
            };

            entries[entry.id] = entry;
            structureDirty = true;
            if (!entry.isStatic)
                dynamicListDirty = true;
        }

        /// <summary>
        /// Finds the humanoids whose capsules belong in the structure and drops the ones that have gone.
        /// Runs on the rescan cadence; the poses themselves are updated every frame by UpdateProxies.
        /// </summary>
        private void RescanProxies(in BasisRTAOSceneSettings settings, float interval)
        {
            foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
                pair.Value.seen = false;

            // Shared with global illumination, which discovers the same humanoids the same way.
            Animator[] animators = BasisSceneScan.Take<Animator>(interval);
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
                // Every avatar carries the same limb set, so the count already registered is proxies.Count
                // times that, and there is nothing to track separately. Already-registered avatars are
                // never dropped by this - they took their slots first and keep them, exactly as global
                // illumination's ceiling behaves.
                if ((proxies.Count + 1) * pose.Count > MaxProxyInstances)
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
            int[] handles = AddProxyInstances(pose);
            if (handles == null)
                return;

            proxies[animator.GetEntityId()] = new ProxyEntry { animator = animator, pose = pose, handles = handles, seen = true };
            structureDirty = true;
        }

        /// <summary>
        /// Issues one instance per limb against the shared capsule. Also used by ResetStructure, which
        /// clears every instance in the structure and has to hand the proxies new handles the same way it
        /// hands the mesh entries new ones - an old handle after that clear is not stale, it is somebody
        /// else's, and driving a transform through it is what took the editor down on a layer change.
        /// </summary>
        private int[] AddProxyInstances(BasisAvatarProxyPose pose)
        {
            Mesh capsule = BasisAvatarProxy.SharedCapsule();
            if (capsule == null || pose == null || pose.Count == 0)
                return null;

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
                        // A proxy capsule is always a person, never the room - and a capsule rather than
                        // geometry anybody drew, which is its own bit. See BasisTracedCategory.
                        mask = BasisTracedCategory.AvatarProxy,
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
                    return null;
                }
            }
            return handles;
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
            ReleaseProxyHandles(entry);
            proxies.Remove(id);
            structureDirty = true;
        }

        /// <summary>
        /// Hands one avatar's limb instances back, if there is still a structure to hand them back to.
        ///
        /// The null check is the teardown path. Dispose releases the structure, and releasing the proxies
        /// after that used to dereference it - which nothing hit while the shipped default put avatars in
        /// as re-baked meshes, because then this dictionary was always empty. Nothing leaks either way:
        /// disposing the structure invalidates every handle in it at once.
        /// </summary>
        private void ReleaseProxyHandles(ProxyEntry entry)
        {
            if (entry.handles == null)
                return;

            for (int i = 0; i < entry.handles.Length; i++)
            {
                if (entry.handles[i] >= 0 && accelStruct != null)
                    accelStruct.RemoveInstance(entry.handles[i]);
                entry.handles[i] = -1;
            }
        }

        private void ClearProxies()
        {
            foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
                ReleaseProxyHandles(pair.Value);

            proxies.Clear();
            proxyRemoval.Clear();
            structureDirty = true;
        }

        /// <summary>
        /// Registers one mesh, tagged with which half of the room it is. The tag costs nothing while this
        /// structure is the only one tracing it - a ray asking for everything still hits every instance -
        /// and it is what allows a single structure to serve two effects that want different halves.
        /// </summary>
        private int[] AddInstances(Mesh mesh, Matrix4x4 matrix, byte category)
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
                        // Which half of the room this instance belongs to, so a structure shared with global
                        // illumination can be traced with only the half this effect asked for. See BasisTracedCategory.
                        mask = category,
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

            // The same reasoning as the entries below, and the reason a layer change took the editor
            // down: ClearInstances invalidated every proxy handle too, and UpdateProxies would have gone
            // on driving transforms through whatever instance inherited each id.
            proxyRemoval.Clear();
            foreach (KeyValuePair<EntityId, ProxyEntry> pair in proxies)
            {
                ProxyEntry proxy = pair.Value;
                proxy.handles = null;
                if (proxy.animator == null)
                {
                    proxyRemoval.Add(pair.Key);
                    continue;
                }
                proxy.handles = AddProxyInstances(proxy.pose);
                if (proxy.handles == null)
                    proxyRemoval.Add(pair.Key);
            }
            for (int i = 0; i < proxyRemoval.Count; i++)
                proxies.Remove(proxyRemoval[i]);
            proxyRemoval.Clear();

            pendingRemoval.Clear();
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
            {
                Entry entry = pair.Value;
                // The clear above already invalidated these. Dropping them first is what keeps the
                // RemoveEntry sweep below — and so ReleaseInstances — from arming the flag again.
                entry.handles = null;
                entry.instanceMesh = null;

                Mesh geometry = entry.sourceMesh;
                if (entry.renderer == null || entry.transform == null || !IsUsableMesh(geometry))
                {
                    pendingRemoval.Add(pair.Key);
                    continue;
                }

                entry.matrix = entry.transform.localToWorldMatrix;
                entry.handles = AddInstances(geometry, entry.matrix, entry.category);
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
            entries.Remove(id);
            structureDirty = true;
            if (!entry.isStatic)
                dynamicListDirty = true;
        }

        // Dead entries are swept here rather than only on the rescan: a renderer is destroyed the moment
        // its avatar is swapped, and waiting out the rescan interval leaves it occluding from wherever the
        // old body was standing.
        private void UpdateTransforms()
        {
            if (dynamicListDirty)
                RebuildDynamicList();
            bool gathered = false;
            if (dynamicScheduled)
            {
                dynamicHandle.Complete();
                dynamicScheduled = false;
                gathered = true;
            }

            pendingRemoval.Clear();
            foreach (KeyValuePair<EntityId, Entry> pair in entries)
            {
                Entry entry = pair.Value;
                if (entry.renderer == null || entry.transform == null || entry.instanceMesh == null)
                {
                    pendingRemoval.Add(pair.Key);
                    continue;
                }
                if (entry.isStatic)
                    continue;

                Matrix4x4 matrix = gathered && entry.gatherIndex >= 0
                    ? dynamicMatrices[entry.gatherIndex]
                    : entry.transform.localToWorldMatrix;
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
            Application.onBeforeRender -= ScheduleTransformGather;
            CompleteTransformGather();
            if (dynamicAccess.isCreated)
                dynamicAccess.Dispose();
            if (dynamicMatrices.IsCreated)
                dynamicMatrices.Dispose();
            dynamicEntries.Clear();
            dynamicListDirty = true;

            // Proxies first, while there is still a structure to release them from. Nothing here owns a
            // mesh - an entry registers the renderer's own shared mesh and the proxies share one capsule -
            // so once the instances are back there is nothing else for a torn down scene to destroy.
            ClearProxies();

            if (accelStruct != null)
            {
                accelStruct.Dispose();
                accelStruct = null;
            }
            needsReset = false;
            entries.Clear();
            pendingRemoval.Clear();
        }
    }
}
