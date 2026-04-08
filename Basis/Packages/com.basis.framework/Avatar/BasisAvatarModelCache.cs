using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Centralized per-avatar-model runtime cache. Data that is deterministic for a given
/// humanoid Avatar asset (T-pose rotations, hand pose grids, calibration coords) is
/// stored here so that loading the same avatar a second time — whether for the local
/// player switching back, or multiple remote players sharing the same model — skips
/// the expensive bake/capture work and copies from cache instead.
///
/// Cache key: <c>Animator.avatar.GetInstanceID()</c> — unique per loaded Avatar asset,
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
        /// <summary>Baked finger pose grid data (BasisLocalHandDriver).</summary>
        public HandPoseGridData HandPoseGrid;

        /// <summary>T-pose local rotations for all 55 humanoid bones (BasisTransformMapping.TposeLocal).</summary>
        public TposeLocalData TposeLocal;

        /// <summary>T-pose root-relative coords for all 55 humanoid bones (BasisTransformMapping.TposeFromRoot).</summary>
        public TposeFromRootData TposeFromRoot;

        /// <summary>T-pose world-scale root-relative coords (no scale division). Used by foot IK.</summary>
        public TposeWorldData TposeWorld;

        /// <summary>Which bones exist on this avatar (from AutoDetectReferences). Indexed by HumanBodyBones.</summary>
        public BonePresenceData BonePresence;

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
        public float[] NativeGridSnapshot; // raw quaternion floats (x,y,z,w per element)
        public int GridWidth;
        public int GridHeight;
        public int FingerStride;
        public int TotalElements;

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

    // ────────────────────────────────────────────────────────────
    //  Storage
    // ────────────────────────────────────────────────────────────

    private static readonly Dictionary<int, Entry> _cache = new Dictionary<int, Entry>(16);

    /// <summary>
    /// Gets the cache key for an animator's avatar asset.
    /// Returns 0 if the avatar is null (caller should skip caching).
    /// </summary>
    public static int GetKey(Animator animator)
    {
        return animator != null && animator.avatar != null ? animator.avatar.GetInstanceID() : 0;
    }

    /// <summary>
    /// Gets or creates the cache entry for the given avatar asset key.
    /// </summary>
    public static Entry GetOrCreate(int key)
    {
        if (!_cache.TryGetValue(key, out Entry entry))
        {
            entry = new Entry();
            _cache[key] = entry;
        }
        return entry;
    }

    /// <summary>
    /// Tries to get an existing cache entry. Returns false if not cached.
    /// </summary>
    public static bool TryGet(int key, out Entry entry)
    {
        return _cache.TryGetValue(key, out entry);
    }

    /// <summary>
    /// Removes a specific avatar's cache entry (e.g., when its bundle is unloaded).
    /// </summary>
    public static void Remove(int key)
    {
        _cache.Remove(key);
    }

    /// <summary>Number of cached avatar models.</summary>
    public static int Count => _cache.Count;

    /// <summary>
    /// Clears all cached data. Called on domain reload.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void Clear()
    {
        _cache.Clear();
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
        int key = GetKey(animator);

        // Cache hit: restore from arrays
        if (key != 0 && TryGet(key, out var entry) && entry.TposeLocal != null && entry.TposeFromRoot != null && entry.TposeWorld != null)
        {
            RestorePosesFromCache(mapping, animator, entry);
            return;
        }

        // Cache miss: full computation
        mapping.RecordPoses(animator);

        // Store for next time
        if (key != 0)
        {
            StorePosesToCache(key, mapping);
        }
    }

    private static void StorePosesToCache(int key, Basis.Scripts.Common.BasisTransformMapping mapping)
    {
        var entry = GetOrCreate(key);
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
            entry.TposeWorld = new TposeWorldData { Rotations = rots, Positions = pos };
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
        mapping.RootRotation = animator.transform.rotation;
        mapping.RootPosition = animator.transform.position;
        mapping.TposeFromRoot.Clear();
        mapping.TposeLocal.Clear();
        mapping.TposeWorld.Clear();

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
    }
}
