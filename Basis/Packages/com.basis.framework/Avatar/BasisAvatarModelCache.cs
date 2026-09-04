using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Centralized per-avatar-model runtime cache. Data that is deterministic for a given
/// humanoid Avatar asset (T-pose rotations, hand pose grids, calibration coords) is
/// stored here so that loading the same avatar a second time — whether for the local
/// player switching back, or multiple remote players sharing the same model — skips
/// the expensive bake/capture work and copies from cache instead.
///
/// Cache key: <c>Animator.avatar.GetEntityId()</c> — unique per loaded Avatar asset,
/// stable across instances of the same model within a session.
///
/// Subsystems store their data in typed slots on <see cref="Entry"/>. A null slot means
/// "not yet cached"; subsystems populate their slot after the first computation and
/// read from it on subsequent loads.
/// </summary>
public static class BasisAvatarModelCache
{
    /// <summary>
    /// Per-avatar-model cache entry. Each subsystem owns a slot.
    /// Slots are classes (reference types) so they can be populated independently.
    /// </summary>
    public class Entry
    {
        /// <summary>
        /// The Avatar asset this entry describes, held so lookups can prove the entry still
        /// belongs to it. Two things make that necessary: the asset dies when its AssetBundle is
        /// unloaded, and an entity id is only unique among LIVE objects — a later asset can be
        /// handed the id of a destroyed one. Without the check a reused id would serve a
        /// different avatar someone else's rest pose, which is a silent, small,
        /// everywhere-at-once error rather than a visible failure.
        /// </summary>
        public Avatar Asset;

        /// <summary>Baked finger pose grid data (BasisLocalHandDriver).</summary>
        public HandPoseGridData HandPoseGrid;

        /// <summary>
        /// This rig's generic->local decode operator pair per wire slot, in
        /// BONE_WRITE_ORDER. Derived purely from the cached rest tables and the character basis,
        /// so it is a property of the model — only the bone Transforms are per-instance.
        /// </summary>
        public BoneDecodeOperatorData BoneDecodeOperators;

        /// <summary>Authored bind localPositions of the body-fit bones. See <see cref="BodyFitRestData"/>.</summary>
        public BodyFitRestData BodyFitRest;

        /// <summary>T-pose local rotations for all 55 humanoid bones (BasisTransformMapping.TposeLocal).</summary>
        public TposeLocalData TposeLocal;

        /// <summary>T-pose root-relative coords for all 55 humanoid bones (BasisTransformMapping.TposeFromRoot).</summary>
        public TposeFromRootData TposeFromRoot;

        /// <summary>T-pose world-scale root-relative coords (no scale division). Used by foot IK.</summary>
        public TposeWorldData TposeWorld;

        /// <summary>Which bones exist on this avatar (from AutoDetectReferences). Indexed by HumanBodyBones.</summary>
        public BonePresenceData BonePresence;

        /// <summary>Local pose every transform under the animator root ends up in after the T-pose
        /// animator round trip. See <see cref="TposeHierarchyData"/>.</summary>
        public TposeHierarchyData TposeHierarchy;

        /// <summary>Animator.humanScale for this avatar model.</summary>
        public float HumanScale;
        public bool HasHumanScale;
    }

    // ────────────────────────────────────────────────────────────
    //  Slot data types — each subsystem defines what it caches
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Cached hand pose grid: 441 grid cells × 10 fingers × 3 joints = quaternion data,
    /// plus the T-pose muscle reference arrays. Produced by BasisLocalHandDriver.ReInitialize().
    /// </summary>
    public class HandPoseGridData
    {
        /// <summary>
        /// The baked cells, owned by this cache entry and SHARED by every grid that restores from
        /// it. 13,230 quaternions (~207 KB) at the default increment, immutable once baked — every
        /// consumer only ever samples it — so a crowd in matching avatars holds one copy between
        /// them instead of one each.
        /// <para>⚠️ Freed ONLY by <see cref="Clear"/>. Eviction retires it instead — see
        /// <c>RetireNative</c> for why disposing it while views exist is a crash.</para>
        /// </summary>
        public NativeArray<quaternion> SharedCells;
        public int GridWidth;
        public int GridHeight;
        public int FingerStride;
        public int TotalElements;
        public float Increment;

        // T-pose muscle arrays (4 floats per finger, 10 fingers)
        public float[] LeftThumb, LeftIndex, LeftMiddle, LeftRing, LeftLittle;
        public float[] RightThumb, RightIndex, RightMiddle, RightRing, RightLittle;

        // Initial pose recorded from T-pose transforms
        public BasisPoseData InitialPose;
    }

    /// <summary>
    /// Cached T-pose local rotations for all humanoid bones.
    /// Indexed by HumanBodyBones enum value (0..54). Identity for missing bones.
    /// Used by BasisNetworkAvatarCompressor.CaptureTPose() and CaptureReceiverBoneData().
    /// </summary>
    public class TposeLocalData
    {
        /// <summary>Local rotation per bone in T-pose. Length = 55 (HumanBodyBones.LastBone).</summary>
        public quaternion[] Rotations;

        /// <summary>Local position per bone in T-pose. Length = 55.</summary>
        public float3[] Positions;
    }

    /// <summary>
    /// Cached T-pose root-relative coordinates for all humanoid bones.
    /// Used by IK calibration and remote bone job authoring.
    /// </summary>
    public class TposeFromRootData
    {
        /// <summary>Position relative to animator root, per bone. Length = 55.</summary>
        public float3[] Positions;

        /// <summary>Rotation relative to animator root, per bone. Length = 55.</summary>
        public quaternion[] Rotations;

        /// <summary>Computed avatar forward/up/right from T-pose geometry.</summary>
        public float3 AvatarForward;
        public float3 AvatarUp;
        public float3 AvatarRight;
    }

    /// <summary>
    /// Cached T-pose world-scale root-relative coordinates.
    /// Like TposeFromRoot but without dividing by localScale, so positions are in meters.
    /// </summary>
    public class TposeWorldData
    {
        public float3[] Positions;
        public quaternion[] Rotations;

        /// <summary>
        /// Root world scale these positions were recorded at — the factor between this frame and
        /// TposeFromRoot. Cached rather than re-read from the live root because a cache hit restores
        /// another instance's arrays, and by then the caller's root may already carry a network scale.
        /// </summary>
        public float3 RootScale;
    }

    /// <summary>
    /// The folded generic->rig decode operators for every wire slot, indexed by slot in
    /// BasisBoneRotationCompression.BONE_WRITE_ORDER (NOT by HumanBodyBones).
    /// </summary>
    /// <remarks>
    /// Safe to key on the Avatar asset because every input is: the rest tables come from
    /// <see cref="TposeLocal"/>/<see cref="TposeFromRoot"/>, the character basis comes from the
    /// same capture, and which slots resolve a bone is fixed by the rig. The per-instance half of
    /// the same loop — the bone Transforms — stays per player.
    /// <para>The arrays double as the snapshot <c>RemoteBoneJobSystem.AddRemotePlayer</c> defers
    /// with. It used to <c>ToArray()</c> the receiver's two NativeArrays because those are owned
    /// by the receiver and can be disposed under a recalibration before the add commits; a
    /// cache-owned immutable array has no such lifetime problem, so the copy goes away.</para>
    /// </remarks>
    public class BoneDecodeOperatorData
    {
        public quaternion[] Pre;
        public quaternion[] Post;
        /// <summary>False for slots the rig has no bone for; those get a null Transform.</summary>
        public bool[] HasBone;
    }

    /// <summary>
    /// Authored bind localPositions of the bones the networked body fit scales, indexed to match
    /// <c>BasisBodyFitApply.CollectBones</c>.
    /// </summary>
    /// <remarks>
    /// Caching this is not only a saving — it removes a drift hazard. The live capture re-read
    /// <c>bone.localPosition</c> on every calibration, so a recalibration of an avatar that
    /// already had a fit applied would record the SCALED positions as "rest" and compound the fit
    /// on the next apply. An authored copy taken once cannot drift.
    /// </remarks>
    public class BodyFitRestData
    {
        public Vector3[] Local;
    }

    /// <summary>
    /// Which humanoid bones exist on this avatar model. Indexed by HumanBodyBones enum.
    /// Avoids repeated GetBoneTransform null checks across instances of the same model.
    /// </summary>
    public class BonePresenceData
    {
        /// <summary>True if the bone exists. Length = 55 (HumanBodyBones.LastBone).</summary>
        public bool[] HasBone;
    }

    /// <summary>
    /// The local position/rotation every transform under the animator root holds once the T-pose
    /// controller has been applied and evaluated, in <c>GetComponentsInChildren</c> order.
    /// </summary>
    /// <remarks>
    /// Posing an avatar into its T-pose costs two <c>runtimeAnimatorController</c> assignments (an
    /// animator rebind each) plus a full humanoid <c>Animator.Update</c> — the same pair
    /// <c>BasisRemoteAvatarDriver</c> already skips for far LOD avatars, and the dominant cost of
    /// installing the loading dummy inline on the transmit tick when a player leaves avatar range.
    /// The result is a property of the model, not the instance: the same prefab instantiated again
    /// starts from the same authored locals and the same clip drives it to the same pose. Recording
    /// it once lets every later install of that model write the locals back directly.
    /// <para>Index 0 is the animator root itself and is never replayed — its local transform is the
    /// instance's placement under <c>AvatarParent</c>, not a bone pose.</para>
    /// </remarks>
    public class TposeHierarchyData
    {
        public Vector3[] LocalPositions;
        public Quaternion[] LocalRotations;
    }

    // ────────────────────────────────────────────────────────────
    //  Storage
    // ────────────────────────────────────────────────────────────

    private static readonly Dictionary<EntityId, Entry> _cache = new Dictionary<EntityId, Entry>(16);

    /// <summary>
    /// Gets the cache key for an animator's avatar asset.
    /// Returns <see cref="EntityId.None"/> if the avatar is null (caller should skip caching).
    /// </summary>
    public static EntityId GetKey(Animator animator)
    {
        return animator != null && animator.avatar != null ? animator.avatar.GetEntityId() : EntityId.None;
    }

    /// <summary>
    /// Gets or creates the cache entry for the given avatar asset key.
    /// </summary>
    public static Entry GetOrCreate(EntityId key)
    {
        return GetOrCreate(key, null);
    }

    /// <summary>
    /// Gets or creates the entry for an avatar asset, stamping <paramref name="asset"/> so later
    /// lookups can prove the entry still describes it. Prefer this overload — an entry with no
    /// asset recorded can never be validated or swept.
    /// </summary>
    public static Entry GetOrCreate(EntityId key, Avatar asset)
    {
        if (!_cache.TryGetValue(key, out Entry entry))
        {
            entry = new Entry();
            _cache[key] = entry;
        }
        if (asset != null)
        {
            entry.Asset = asset;
        }
        return entry;
    }

    /// <summary>
    /// Tries to get an existing cache entry, dropping it if the Avatar asset it describes has been
    /// destroyed — which happens when the bundle it came from is unloaded, and is also what makes
    /// a reused entity id safe (the previous owner is destroyed, so the entry fails this check and
    /// is rebuilt for the new asset).
    /// </summary>
    public static bool TryGet(EntityId key, out Entry entry)
    {
        if (!_cache.TryGetValue(key, out entry))
        {
            return false;
        }
        // Unity-null, not reference-null: a destroyed asset is a live managed reference to a dead
        // native object. An entry created before Asset was recorded has a genuinely null reference
        // and is left alone rather than being thrown away on every lookup.
        if (!ReferenceEquals(entry.Asset, null) && entry.Asset == null)
        {
            RetireNative(entry);
            _cache.Remove(key);
            entry = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Drops every entry whose Avatar asset has been destroyed. <see cref="TryGet"/> only evicts
    /// an entry that something looks up again, so without this a session that cycles through many
    /// avatars keeps every dead one — and an entry now owns the shared hand-pose grid's native
    /// memory, which no GC will ever reclaim.
    /// </summary>
    public static void SweepDestroyed()
    {
        if (_cache.Count == 0)
        {
            return;
        }
        _sweepScratch.Clear();
        foreach (KeyValuePair<EntityId, Entry> pair in _cache)
        {
            Entry entry = pair.Value;
            if (!ReferenceEquals(entry.Asset, null) && entry.Asset == null)
            {
                _sweepScratch.Add(pair.Key);
            }
        }
        for (int Index = 0; Index < _sweepScratch.Count; Index++)
        {
            EntityId key = _sweepScratch[Index];
            if (_cache.TryGetValue(key, out Entry dead))
            {
                RetireNative(dead);
            }
            _cache.Remove(key);
        }
        _sweepScratch.Clear();
    }

    private static readonly List<EntityId> _sweepScratch = new List<EntityId>();

    /// <summary>
    /// Removes a specific avatar's cache entry (e.g., when its bundle is unloaded).
    /// </summary>
    public static void Remove(EntityId key)
    {
        if (_cache.TryGetValue(key, out Entry entry))
        {
            RetireNative(entry);
        }
        _cache.Remove(key);
    }

    /// <summary>
    /// Native buffers detached from evicted entries, freed only at <see cref="Clear"/>.
    /// </summary>
    private static readonly List<NativeArray<quaternion>> _retiredCells = new List<NativeArray<quaternion>>();

    /// <summary>
    /// Detaches an evicted entry's native buffers WITHOUT freeing them.
    /// </summary>
    /// <remarks>
    /// ⚠️ Freeing here is a crash, and it was one: every grid that restored from this entry holds a
    /// non-owning VIEW of <see cref="HandPoseGridData.SharedCells"/>, and a NativeArray view has no
    /// way to observe its owner being disposed — <c>IsCreated</c> keeps returning true, so
    /// <c>ExpandFingerChannels</c>' guard passes and the read throws ObjectDisposedException.
    ///
    /// Worse, eviction reaches here from <c>BasisAvatarFactory.DeleteLastAvatar</c>, which is
    /// async void: its continuation runs on the Unity synchronization context and can land between
    /// <c>BeginNetworkCompute</c> and <c>JoinPendingCompute</c> — with the parallel finger
    /// expansion mid-flight over these exact cells. There is no lock to take and no refcount to
    /// consult, so the buffer simply outlives the entry.
    ///
    /// Retaining is not a regression: before the cells were shared, every player allocated their
    /// own copy and nothing ever freed those either. This holds ONE per model instead of one per
    /// player, and the eviction still does the half that matters for correctness — the entry stops
    /// being handed out, so a destroyed asset (or a reused entity id) can never serve stale data.
    /// </remarks>
    private static void RetireNative(Entry entry)
    {
        HandPoseGridData grid = entry?.HandPoseGrid;
        if (grid != null && grid.SharedCells.IsCreated)
        {
            _retiredCells.Add(grid.SharedCells);
            grid.SharedCells = default;
        }
    }

    /// <summary>Number of cached avatar models.</summary>
    public static int Count => _cache.Count;

    /// <summary>
    /// Clears all cached data. Called when play mode starts, and in the Editor when it ends.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void Clear()
    {
        // The one point where freeing is safe: nothing is wearing an avatar and no compute is in
        // flight, so no grid can still be viewing these. Every other eviction path only retires.
        foreach (KeyValuePair<EntityId, Entry> pair in _cache)
        {
            RetireNative(pair.Value);
        }
        for (int Index = 0; Index < _retiredCells.Count; Index++)
        {
            NativeArray<quaternion> cells = _retiredCells[Index];
            if (cells.IsCreated)
            {
                cells.Dispose();
            }
        }
        _retiredCells.Clear();
        _cache.Clear();
    }

#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
    private static void HookEditorPlayModeClear()
    {
        UnityEditor.EditorApplication.playModeStateChanged -= OnEditorPlayModeStateChanged;
        UnityEditor.EditorApplication.playModeStateChanged += OnEditorPlayModeStateChanged;
    }

    private static void OnEditorPlayModeStateChanged(UnityEditor.PlayModeStateChange change)
    {
        if (change == UnityEditor.PlayModeStateChange.EnteredEditMode)
        {
            Clear();
        }
    }
#endif

    // ────────────────────────────────────────────────────────────
    //  T-pose hierarchy replay — see TposeHierarchyData
    // ────────────────────────────────────────────────────────────

    private static readonly List<Transform> _hierarchyScratch = new List<Transform>(256);

    /// <summary>
    /// Records the animator root's hierarchy exactly as the T-pose animator round trip left it.
    /// Call immediately after posing and before anything writes bone locals (body fit, pose snap).
    /// Keeps the first recording — every instance of the model produces the same one.
    /// </summary>
    public static void StoreTposeHierarchy(Animator animator)
    {
        EntityId key = GetKey(animator);
        if (key == EntityId.None)
        {
            return;
        }
        Entry entry = GetOrCreate(key, animator.avatar);
        if (entry.TposeHierarchy != null)
        {
            return;
        }
        animator.transform.GetComponentsInChildren(true, _hierarchyScratch);
        int count = _hierarchyScratch.Count;
        Vector3[] positions = new Vector3[count];
        Quaternion[] rotations = new Quaternion[count];
        for (int Index = 0; Index < count; Index++)
        {
            _hierarchyScratch[Index].GetLocalPositionAndRotation(out positions[Index], out rotations[Index]);
        }
        _hierarchyScratch.Clear();
        entry.TposeHierarchy = new TposeHierarchyData { LocalPositions = positions, LocalRotations = rotations };
    }

    /// <summary>
    /// Writes a previously recorded T-pose back onto this instance, replacing the animator round
    /// trip. Returns false when there is nothing recorded for the model — or when the live
    /// hierarchy does not match the recorded one, which can only happen if two different rigs
    /// share an Avatar asset — and the caller must pose through the animator instead.
    /// </summary>
    public static bool TryReplayTposeHierarchy(Animator animator)
    {
        EntityId key = GetKey(animator);
        if (key == EntityId.None || !TryGet(key, out Entry entry) || entry.TposeHierarchy == null)
        {
            return false;
        }
        Vector3[] positions = entry.TposeHierarchy.LocalPositions;
        Quaternion[] rotations = entry.TposeHierarchy.LocalRotations;
        animator.transform.GetComponentsInChildren(true, _hierarchyScratch);
        int count = _hierarchyScratch.Count;
        if (count != positions.Length)
        {
            _hierarchyScratch.Clear();
            return false;
        }
        const int firstBoneIndex = 1;
        for (int Index = firstBoneIndex; Index < count; Index++)
        {
            _hierarchyScratch[Index].SetLocalPositionAndRotation(positions[Index], rotations[Index]);
        }
        _hierarchyScratch.Clear();
        return true;
    }

    // ────────────────────────────────────────────────────────────
    //  RecordPoses helper — wraps BasisTransformMapping.RecordPoses
    //  with cache check/store. Lives here (framework) because
    //  BasisTransformMapping is in com.basis.common which can't
    //  reference the framework.
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls <see cref="Basis.Scripts.Common.BasisTransformMapping.RecordPoses"/> with caching.
    /// On cache hit, restores TposeLocal/TposeFromRoot from arrays instead of re-computing
    /// 55 bone transforms. On cache miss, runs the full computation and stores results.
    /// </summary>
    public static void RecordPosesCached(Basis.Scripts.Common.BasisTransformMapping mapping, Animator animator)
    {
        EntityId key = GetKey(animator);

        // Cache hit: restore from arrays
        if (key != EntityId.None && TryGet(key, out var entry) && entry.TposeLocal != null && entry.TposeFromRoot != null && entry.TposeWorld != null)
        {
            RestorePosesFromCache(mapping, animator, entry);
            return;
        }

        // Cache miss: full computation
        mapping.RecordPoses(animator);

        // Store for next time
        if (key != EntityId.None)
        {
            StorePosesToCache(key, mapping, animator.avatar);
        }
    }

    private static void StorePosesToCache(EntityId key, Basis.Scripts.Common.BasisTransformMapping mapping, Avatar asset)
    {
        var entry = GetOrCreate(key, asset);
        int boneCount = (int)HumanBodyBones.LastBone;

        if (entry.TposeLocal == null)
        {
            var rots = new quaternion[boneCount];
            var pos = new float3[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var bone = (HumanBodyBones)i;
                if (mapping.TposeLocal.TryGetValue(bone, out var c))
                {
                    rots[i] = c.rotation;
                    pos[i] = c.position;
                }
                else
                {
                    rots[i] = quaternion.identity;
                    pos[i] = float3.zero;
                }
            }
            entry.TposeLocal = new TposeLocalData { Rotations = rots, Positions = pos };
        }

        if (entry.TposeFromRoot == null)
        {
            var rots = new quaternion[boneCount];
            var pos = new float3[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var bone = (HumanBodyBones)i;
                if (mapping.TposeFromRoot.TryGetValue(bone, out var c))
                {
                    rots[i] = c.rotation;
                    pos[i] = c.position;
                }
                else
                {
                    rots[i] = quaternion.identity;
                    pos[i] = float3.zero;
                }
            }
            entry.TposeFromRoot = new TposeFromRootData
            {
                Rotations = rots,
                Positions = pos,
                AvatarForward = mapping.AvatarForwards,
                AvatarUp = mapping.AvatarUpwards,
                AvatarRight = mapping.AvatarRightwards
            };
        }

        if (entry.TposeWorld == null)
        {
            var rots = new quaternion[boneCount];
            var pos = new float3[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var bone = (HumanBodyBones)i;
                if (mapping.TposeWorld.TryGetValue(bone, out var c))
                {
                    rots[i] = c.rotation;
                    pos[i] = c.position;
                }
                else
                {
                    rots[i] = quaternion.identity;
                    pos[i] = float3.zero;
                }
            }
            entry.TposeWorld = new TposeWorldData { Rotations = rots, Positions = pos, RootScale = mapping.RootScale };
        }

        if (entry.BonePresence == null)
        {
            var has = new bool[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                if (mapping.TposeLocal.TryGetValue((HumanBodyBones)i, out var c))
                {
                    has[i] = c.rotation != Quaternion.identity || c.position != Vector3.zero;
                }
            }
            entry.BonePresence = new BonePresenceData { HasBone = has };
        }
    }

    private static void RestorePosesFromCache(Basis.Scripts.Common.BasisTransformMapping mapping, Animator animator, Entry entry)
    {
        animator.transform.GetPositionAndRotation(out mapping.RootPosition, out mapping.RootRotation);

        int boneCount = (int)HumanBodyBones.LastBone;
        var cachedLocal = entry.TposeLocal;
        var cachedRoot = entry.TposeFromRoot;
        var cachedWorld = entry.TposeWorld;

        for (int i = 0; i < boneCount; i++)
        {
            var bone = (HumanBodyBones)i;
            mapping.TposeLocal[bone] = new Basis.Scripts.Common.BasisCalibratedCoords
            {
                position = cachedLocal.Positions[i],
                rotation = cachedLocal.Rotations[i]
            };
            mapping.TposeFromRoot[bone] = new Basis.Scripts.Common.BasisCalibratedCoords
            {
                position = cachedRoot.Positions[i],
                rotation = cachedRoot.Rotations[i]
            };
            mapping.TposeWorld[bone] = new Basis.Scripts.Common.BasisCalibratedCoords
            {
                position = cachedWorld.Positions[i],
                rotation = cachedWorld.Rotations[i]
            };
        }

        mapping.AvatarForwards = cachedRoot.AvatarForward;
        mapping.AvatarUpwards = cachedRoot.AvatarUp;
        mapping.AvatarRightwards = cachedRoot.AvatarRight;
        // Belongs to the cached TposeWorld arrays, not to this instance's live root.
        mapping.RootScale = cachedWorld.RootScale;
    }
}
