using System;
using Basis.Scripts.BasisSdk;
using GatorDragonGames.JigglePhysics;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using Basis.Scripts.Settings;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Client-side avatar performance filter. Limits are opt-in via a Use* flag plus a
    /// numeric threshold, pushed in by <c>SMModuleAvatarPerformanceLimits</c>.
    /// Enforcement is split across three stages so each limit runs at the earliest
    /// point it can cleanly be caught:
    ///
    ///   • <see cref="Evaluate"/> — the "content profiler" stage. Pre-load metadata
    ///     hard-block check. Covers triangles, bounds, texture memory, material slots,
    ///     skinned bones, and mesh counts (both skinned and basic). Runs before any
    ///     instantiation, so rejected avatars never pay download/decompress cost.
    ///
    ///   • <c>BasisRemoteAvatarDriver.RemoteCalibration</c> — jiggle rig ingestion
    ///     hook. The driver iterates <c>JiggleRig</c> components to call
    ///     <c>OnInitialize</c>; excess rigs are destroyed there so they leave the
    ///     JigglePhysics tree via their own <c>OnDisable</c> path instead of being
    ///     reaped by a reflection scan after the fact.
    ///
    ///   • <see cref="TrimExcessComponents"/> — post-load component trim for
    ///     everything that doesn't have a better hook: animators, lights, particle
    ///     systems, trail/line renderers, cloth, unity physics colliders, and
    ///     user-placed jiggle colliders. Destroys excess instances so the avatar
    ///     still renders instead of being fully blocked.
    /// </summary>
    public static class BasisAvatarPerformanceLimits
    {
        /// <summary>
        /// What the reconcile pass should do with a single remote player after a
        /// limit change. Chosen so the cheapest correct path wins: doing nothing
        /// beats trimming in-place, and trimming in-place beats a full avatar reload.
        /// </summary>
        public enum ReconcileAction
        {
            /// <summary>No effective change — skip this player entirely.</summary>
            None,
            /// <summary>One or more trim categories got tighter and no category
            /// needs a restore. Destroy the extras on the live avatar; no reload.</summary>
            TrimInPlace,
            /// <summary>A trim category relaxed (destroyed components need to come
            /// back), or the hard-block flag flipped. Full reload required.</summary>
            Reload,
        }

        /// <summary>
        /// Outcome of a single metadata evaluation. <see cref="Blocked"/> is true when the
        /// avatar tripped at least one enabled limit; <see cref="Reason"/> is a
        /// human-readable summary of the first trip (for nameplate / log display).
        /// </summary>
        public readonly struct Result
        {
            public readonly bool Blocked;
            public readonly string Reason;

            public Result(bool blocked, string reason)
            {
                Blocked = blocked;
                Reason = reason;
            }

            public static readonly Result Pass = new Result(false, null);
        }

        /// <summary>
        /// Per-player summary of what the performance filter did to a single remote
        /// avatar. Stored on <c>BasisRemotePlayer</c> so the individual-player menu
        /// can show the user exactly why a specific avatar was blocked or how many
        /// of each component type got trimmed.
        /// </summary>
        public struct PerformanceInfo
        {
            /// <summary>True if <see cref="Evaluate"/> rejected the avatar outright.</summary>
            public bool Blocked;
            /// <summary>Human-readable reason for the hard block (null if not blocked).</summary>
            public string BlockReason;

            // Trim counts. Each is the number of components destroyed in the category.
            // The corresponding Use* flag is recorded too so the UI can distinguish
            // "limit enabled but nothing to trim" from "limit disabled entirely".
            public int AnimatorsTrimmed;
            public int LightsTrimmed;
            public int ParticleSystemsTrimmed;
            public int TrailRenderersTrimmed;
            public int LineRenderersTrimmed;
            public int ClothTrimmed;
            public int CollidersTrimmed;
            public int JiggleCollidersTrimmed;
            /// <summary>Jiggle rigs destroyed at driver ingestion (not by TrimExcessComponents).</summary>
            public int JiggleRigsTrimmed;
            /// <summary>CilboxProxy script behaviours destroyed by the trim pass.</summary>
            public int CilboxBehavioursTrimmed;

            public bool AnythingTrimmed =>
                AnimatorsTrimmed > 0 || LightsTrimmed > 0 || ParticleSystemsTrimmed > 0
                || TrailRenderersTrimmed > 0 || LineRenderersTrimmed > 0 || ClothTrimmed > 0
                || CollidersTrimmed > 0 || JiggleCollidersTrimmed > 0 || JiggleRigsTrimmed > 0
                || CilboxBehavioursTrimmed > 0;
        }

        // Component names as emitted by BasisBundleBuild.GenerateMetaData (= Type.Name).
        // Mesh counts are read from the metadata header for hard-block checks.
        // JiggleColliderExample is matched by short type name at trim time so the
        // Avatar assembly doesn't need a direct asmdef dependency on JigglePhysics.
        private const string CompSkinnedMeshRenderer = "SkinnedMeshRenderer";
        private const string CompMeshFilter = "MeshFilter";
        private const string CompJiggleColliderExample = "JiggleColliderExample";
        // CilboxProxy lives in the com.cnlohr.cilbox package; this assembly does not
        // reference it, so the trim and metadata lookups go through the short type name.
        private const string CompCilboxProxy = "CilboxProxy";

        // Flag pair per limit. Kept as simple static fields rather than a struct so the
        // settings bridge can patch one value at a time without having to copy + replace
        // the whole block (which would invite torn reads during concurrent updates).
        public static bool UseLimitTriangles;
        public static long LimitTriangles;

        public static bool UseLimitBoundsSize;
        public static float LimitBoundsSize;

        public static bool UseLimitTextureMemory;
        public static long LimitTextureMemoryBytes;

        public static bool UseLimitSkinnedMeshes;
        public static int LimitSkinnedMeshes;

        public static bool UseLimitBasicMeshes;
        public static int LimitBasicMeshes;

        public static bool UseLimitMaterialSlots;
        public static int LimitMaterialSlots;

        public static bool UseLimitJiggleBones;
        public static int LimitJiggleBones;

        public static bool UseLimitJiggleColliders;
        public static int LimitJiggleColliders;

        public static bool UseLimitAnimators;
        public static int LimitAnimators;

        public static bool UseLimitBones;
        public static long LimitBones;

        public static bool UseLimitLights;
        public static int LimitLights;

        public static bool UseLimitParticleSystems;
        public static int LimitParticleSystems;

        public static bool UseLimitTrailRenderers;
        public static int LimitTrailRenderers;

        public static bool UseLimitLineRenderers;
        public static int LimitLineRenderers;

        public static bool UseLimitCloth;
        public static int LimitCloth;

        public static bool UseLimitColliders;
        public static int LimitColliders;

        public static bool UseLimitCilboxBehaviours;
        public static int LimitCilboxBehaviours;

        /// <summary>
        /// Session-only "show me everything" switch. When true, <see cref="Evaluate"/>
        /// and <see cref="TrimExcessComponents"/> both short-circuit to no-ops and
        /// <see cref="DetermineAction"/> tells the reconcile pass to restore any
        /// currently-blocked or currently-trimmed avatar. Not persisted through
        /// <see cref="BasisSettingsSystem"/> — resets to false every launch — so
        /// it's safe to use as a quick "what does this actually look like?" diagnostic
        /// without risk of the user accidentally leaving it on forever.
        /// </summary>
        private static bool _bypassAllLimits;
        public static bool BypassAllLimits
        {
            get => _bypassAllLimits;
            set
            {
                if (_bypassAllLimits == value) return;
                _bypassAllLimits = value;
                OnBypassChanged?.Invoke();
            }
        }

        /// <summary>
        /// Raised when <see cref="BypassAllLimits"/> changes. The settings bridge
        /// subscribes to this so toggling bypass runs the same debounced reconcile
        /// pass as any regular limit change, immediately restoring or re-trimming
        /// every loaded remote avatar.
        /// </summary>
        public static event Action OnBypassChanged;

        /// <summary>
        /// Any hard-block limit enabled (metadata-only checks that reject the whole
        /// avatar). These can't be fixed at runtime — avatar falls back to the loading
        /// mesh until the limit is relaxed or the avatar is replaced. Returns false
        /// whenever <see cref="BypassAllLimits"/> is set so <see cref="Evaluate"/>
        /// short-circuits to Pass.
        /// </summary>
        public static bool AnyHardLimitEnabled()
        {
            if (_bypassAllLimits) return false;
            return UseLimitTriangles
                || UseLimitBoundsSize
                || UseLimitTextureMemory
                || UseLimitMaterialSlots
                || UseLimitBones
                || UseLimitSkinnedMeshes
                || UseLimitBasicMeshes;
        }

        /// <summary>
        /// Any trim limit enabled. Trim limits don't block the avatar — they allow the
        /// load to succeed and then destroy excess component instances on the
        /// instantiated tree. Returns false when <see cref="BypassAllLimits"/> is set
        /// so <see cref="TrimExcessComponents"/> short-circuits to a no-op.
        /// </summary>
        public static bool AnyTrimLimitEnabled()
        {
            if (_bypassAllLimits) return false;
            return UseLimitJiggleBones
                || UseLimitJiggleColliders
                || UseLimitAnimators
                || UseLimitLights
                || UseLimitParticleSystems
                || UseLimitTrailRenderers
                || UseLimitLineRenderers
                || UseLimitCloth
                || UseLimitColliders
                || UseLimitCilboxBehaviours;
        }

        /// <summary>
        /// True if *any* limit (hard-block or trim) is currently active.
        /// </summary>
        public static bool AnyLimitEnabled() => AnyHardLimitEnabled() || AnyTrimLimitEnabled();

        /// <summary>
        /// Pre-load hard-block check. Inspects the metadata header and returns a blocked
        /// result if any category the runtime can't trim is over its limit. Component
        /// counts (animators, lights, etc.) are NOT checked here — those are handled by
        /// <see cref="TrimExcessComponents"/> after load.
        /// </summary>
        public static Result Evaluate(BasisBundleConnector connector)
        {
            if (connector == null) return Result.Pass;
            if (_bypassAllLimits) return Result.Pass;

            // Content-tag block runs before perf checks because tag rejection is a
            // stricter user intent than a perf threshold (the user explicitly opted
            // out of this content category) and the comparison is cheap — small
            // user blocklist × small authored tag list.
            if (BasisContentTagFilter.AnyBlocked()
                && connector.BasisBundleDescription != null
                && BasisContentTagFilter.IsBlocked(connector.BasisBundleDescription.Tags, out string matchedTag))
            {
                return FailTag(matchedTag);
            }

            if (!AnyHardLimitEnabled())
            {
                return Result.Pass;
            }

            BasisBundleConnector.BasisMetaData meta = connector.MetaData;

            if (UseLimitTriangles && meta.TrianglesCount > LimitTriangles)
            {
                return Fail("triangles", meta.TrianglesCount, LimitTriangles);
            }

            if (UseLimitBoundsSize)
            {
                // Full axis-aligned diagonal length. Mirrors how Unity's Bounds.size is
                // 2 * extents; magnitude gives a single intuitive number to compare.
                Vector3 size = connector.Bounds.m_Extents * 2f;
                float boundsSize = size.magnitude;
                if (boundsSize > LimitBoundsSize)
                {
                    return Fail("bounds size", boundsSize, LimitBoundsSize);
                }
            }

            // TextureMemoryBytes is 0 on bundles built before the field existed. Treat
            // 0 as "unknown" and let it through so we don't blanket-block old avatars.
            if (UseLimitTextureMemory && meta.TextureMemoryBytes > 0 && meta.TextureMemoryBytes > LimitTextureMemoryBytes)
            {
                return Fail("texture memory", meta.TextureMemoryBytes, LimitTextureMemoryBytes);
            }

            if (UseLimitMaterialSlots && meta.MaterialCount > LimitMaterialSlots)
            {
                return Fail("material slots", meta.MaterialCount, LimitMaterialSlots);
            }

            if (UseLimitBones && meta.BonesCount > LimitBones)
            {
                return Fail("bones", meta.BonesCount, LimitBones);
            }

            // Mesh count checks read from the flat ComponentNames array. We don't trim
            // meshes at runtime because deleting one of a small number of renderers
            // typically destroys the body or head — fall back entirely instead.
            if (UseLimitSkinnedMeshes || UseLimitBasicMeshes)
            {
                if (UseLimitSkinnedMeshes)
                {
                    int skinned = GetComponentCount(meta.ComponentNames, CompSkinnedMeshRenderer);
                    if (skinned > LimitSkinnedMeshes)
                    {
                        return Fail("skinned meshes", skinned, LimitSkinnedMeshes);
                    }
                }

                if (UseLimitBasicMeshes)
                {
                    int meshes = GetComponentCount(meta.ComponentNames, CompMeshFilter);
                    if (meshes > LimitBasicMeshes)
                    {
                        return Fail("basic meshes", meshes, LimitBasicMeshes);
                    }
                }
            }

            return Result.Pass;
        }

        private static int GetComponentCount(BasisBundleConnector.BasisComponentName[] names, string typeName)
        {
            if (names == null) return 0;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].Name == typeName)
                {
                    return names[i].count;
                }
            }
            return 0;
        }

        // Unity physics colliders split into distinct subclasses in the metadata
        // ComponentNames table (MeshCollider, BoxCollider, …). The runtime trim uses
        // GetComponentsInChildren<Collider>() which sums all subtypes, so we sum the
        // same set here when predicting whether a limit change would flip anything.
        // JiggleColliderExample is intentionally excluded — it has its own limit.
        private static readonly string[] UnityColliderMetadataNames =
        {
            "MeshCollider",
            "BoxCollider",
            "SphereCollider",
            "CapsuleCollider",
            "TerrainCollider",
            "WheelCollider",
            "CharacterController",
        };

        private static int GetUnityColliderCount(BasisBundleConnector.BasisComponentName[] names)
        {
            int total = 0;
            for (int i = 0; i < UnityColliderMetadataNames.Length; i++)
            {
                total += GetComponentCount(names, UnityColliderMetadataNames[i]);
            }
            return total;
        }

        /// <summary>
        /// Decide what the settings bridge should do with a player after a limit
        /// change. Compares each category's currently-kept count (derived from
        /// metadata + stored trim counts) against what the new limits would keep
        /// and picks the cheapest correct response:
        ///
        ///   • <see cref="ReconcileAction.None"/> — no category would change and
        ///     the hard-block flag didn't flip. Skip the player entirely.
        ///   • <see cref="ReconcileAction.TrimInPlace"/> — every change tightens
        ///     an existing count. Destroy the extras on the live avatar; no reload,
        ///     no bundle I/O, no setup pass.
        ///   • <see cref="ReconcileAction.Reload"/> — a trim category relaxed
        ///     (destroyed components need to come back), the limit got disabled,
        ///     or the hard-block flag flipped. Full <c>ReloadAvatar</c> required.
        /// </summary>
        /// <param name="connector">The bundle metadata for the loaded avatar.</param>
        /// <param name="lastInfo">The player's cached <see cref="PerformanceInfo"/>
        /// from the last load — what was actually trimmed on disk.</param>
        public static ReconcileAction DetermineAction(BasisBundleConnector connector, in PerformanceInfo lastInfo)
        {
            if (connector == null)
            {
                return ReconcileAction.None;
            }

            // Bypass is live: every limit acts as if it's disabled. A player that was
            // hard-blocked or had any trim applied needs a reload to restore the full
            // avatar; everyone else is already in the right state.
            if (_bypassAllLimits)
            {
                return (lastInfo.Blocked || lastInfo.AnythingTrimmed)
                    ? ReconcileAction.Reload
                    : ReconcileAction.None;
            }

            // Hard-block status: if Evaluate flips, the avatar must move between
            // fallback and real. If it stays blocked, trim comparisons are moot — the
            // avatar is on the fallback mesh either way.
            bool wasBlocked = lastInfo.Blocked;
            bool wouldBeBlocked = Evaluate(connector).Blocked;
            if (wasBlocked != wouldBeBlocked)
            {
                return ReconcileAction.Reload;
            }
            if (wouldBeBlocked)
            {
                return ReconcileAction.None;
            }

            // Trim categories. A single helper returns the signed delta
            // (current_kept - new_kept): positive means "need to destroy more"
            // (in-place trim will handle it), negative means "need to restore"
            // (must reload). Any negative short-circuits to Reload.
            var names = connector.MetaData.ComponentNames;
            bool anyTighten = false;

            // Animators get protectedCount=1 because BasisAvatar.Animator is disabled
            // by BasisRemoteAvatarDriver.RemoteCalibration on remotes — it doesn't
            // tick per frame, so it shouldn't consume one of the user's slots.
            if (!TryAccumulateTrim(GetComponentCount(names, "Animator"), lastInfo.AnimatorsTrimmed,
                UseLimitAnimators, LimitAnimators, protectedCount: 1, ref anyTighten))
                return ReconcileAction.Reload;

            if (!TryAccumulateTrim(GetComponentCount(names, "Light"), lastInfo.LightsTrimmed,
                UseLimitLights, LimitLights, protectedCount: 0, ref anyTighten))
                return ReconcileAction.Reload;

            if (!TryAccumulateTrim(GetComponentCount(names, "ParticleSystem"), lastInfo.ParticleSystemsTrimmed,
                UseLimitParticleSystems, LimitParticleSystems, protectedCount: 0, ref anyTighten))
                return ReconcileAction.Reload;

            if (!TryAccumulateTrim(GetComponentCount(names, "TrailRenderer"), lastInfo.TrailRenderersTrimmed,
                UseLimitTrailRenderers, LimitTrailRenderers, protectedCount: 0, ref anyTighten))
                return ReconcileAction.Reload;

            if (!TryAccumulateTrim(GetComponentCount(names, "LineRenderer"), lastInfo.LineRenderersTrimmed,
                UseLimitLineRenderers, LimitLineRenderers, protectedCount: 0, ref anyTighten))
                return ReconcileAction.Reload;

            if (!TryAccumulateTrim(GetComponentCount(names, "Cloth"), lastInfo.ClothTrimmed,
                UseLimitCloth, LimitCloth, protectedCount: 0, ref anyTighten))
                return ReconcileAction.Reload;

            if (!TryAccumulateTrim(GetUnityColliderCount(names), lastInfo.CollidersTrimmed,
                UseLimitColliders, LimitColliders, protectedCount: 0, ref anyTighten))
                return ReconcileAction.Reload;

            if (!TryAccumulateTrim(GetComponentCount(names, CompJiggleColliderExample), lastInfo.JiggleCollidersTrimmed,
                UseLimitJiggleColliders, LimitJiggleColliders, protectedCount: 0, ref anyTighten))
                return ReconcileAction.Reload;

            if (!TryAccumulateTrim(GetComponentCount(names, "JiggleRig"), lastInfo.JiggleRigsTrimmed,
                UseLimitJiggleBones, LimitJiggleBones, protectedCount: 0, ref anyTighten))
                return ReconcileAction.Reload;

            if (!TryAccumulateTrim(GetComponentCount(names, CompCilboxProxy), lastInfo.CilboxBehavioursTrimmed,
                UseLimitCilboxBehaviours, LimitCilboxBehaviours, protectedCount: 0, ref anyTighten))
                return ReconcileAction.Reload;

            return anyTighten ? ReconcileAction.TrimInPlace : ReconcileAction.None;
        }

        /// <summary>
        /// Per-category classification step shared by <see cref="DetermineAction"/>.
        /// Returns false when the category needs a restore (new target &gt; currently
        /// kept), which forces the caller to pick <see cref="ReconcileAction.Reload"/>.
        /// Otherwise flips <paramref name="anyTighten"/> to true if this category has
        /// components to destroy and returns true.
        ///
        /// <paramref name="protectedCount"/> is the number of instances the runtime
        /// always keeps *and* ignores when counting against the limit. Animators use
        /// 1 for <see cref="BasisAvatar.Animator"/>, which the remote driver disables
        /// so it never ticks. The effective target becomes
        /// <c>min(original, limit + protectedCount)</c> — the user's cap applies only
        /// to the instances that are actually running.
        /// </summary>
        private static bool TryAccumulateTrim(int original, int currentlyTrimmed, bool newEnabled, int newLimit, int protectedCount, ref bool anyTighten)
        {
            int currentlyKept = original - currentlyTrimmed;
            int newKept = newEnabled ? Mathf.Min(original, newLimit + protectedCount) : original;
            if (newKept > currentlyKept)
            {
                // Need to restore components we already destroyed — can't be done
                // in-place, the caller must reload the bundle.
                return false;
            }
            if (newKept < currentlyKept)
            {
                anyTighten = true;
            }
            return true;
        }

        /// <summary>
        /// Walk the instantiated avatar tree and destroy over-limit component instances
        /// for every enabled trim category. Intended to be called once, synchronously,
        /// right after a successful avatar load and before the avatar driver has
        /// cached renderer/component references. Uses <see cref="UnityEngine.Object.DestroyImmediate"/>
        /// so subsequent <c>GetComponentsInChildren</c> scans see the trimmed tree.
        /// Returns a <see cref="PerformanceInfo"/> describing what was destroyed so
        /// the caller can surface it in per-player UI.
        /// </summary>
        /// <param name="root">The instantiated avatar root GameObject.</param>
        public static PerformanceInfo TrimExcessComponents(GameObject root)
        {
            PerformanceInfo info = default;

            if (root == null || !AnyTrimLimitEnabled())
            {
                return info;
            }

            // BasisAvatar holds a serialized reference to the canonical Animator the
            // remote humanoid driver relies on. Snapshot it so TrimAnimators can
            // promote it above everything else regardless of hierarchy order.
            Animator protectedAnimator = null;
            if (root.TryGetComponent(out BasisAvatar avatarRef))
            {
                protectedAnimator = avatarRef.Animator;
            }

            // Animators: the canonical BasisAvatar.Animator is protected. Without it the
            // remote humanoid driver can't run at all (AvatarAnimatorTransform is set
            // from avatar.Animator.transform during SetupPlayerAvatar).
            if (UseLimitAnimators)
            {
                info.AnimatorsTrimmed = TrimAnimators(root, LimitAnimators, protectedAnimator);
            }

            if (UseLimitLights)
            {
                info.LightsTrimmed = TrimComponents<Light>(root, LimitLights);
            }

            if (UseLimitParticleSystems)
            {
                info.ParticleSystemsTrimmed = TrimComponents<ParticleSystem>(root, LimitParticleSystems);
            }

            if (UseLimitTrailRenderers)
            {
                info.TrailRenderersTrimmed = TrimComponents<TrailRenderer>(root, LimitTrailRenderers);
            }

            if (UseLimitLineRenderers)
            {
                info.LineRenderersTrimmed = TrimComponents<LineRenderer>(root, LimitLineRenderers);
            }

            if (UseLimitCloth)
            {
                info.ClothTrimmed = TrimComponents<Cloth>(root, LimitCloth);
            }

            // Unity physics colliders only — JiggleColliderExample is a plain
            // MonoBehaviour and is not a Collider subclass, so it's not picked up here.
            if (UseLimitColliders)
            {
                info.CollidersTrimmed = TrimComponents<Collider>(root, LimitColliders);
            }

            // Jiggle rigs. Previously enforced at driver ingestion time, but moved
            // here so a single code path covers both initial load (called from
            // BasisAvatarFactory.InitializePlayerAvatar) and in-place reconcile
            // (called from SMModuleAvatarPerformanceLimits after a limit tightened).
            // DestroyImmediate on a live rig triggers JiggleRig.OnDisable → OnRemove
            // which cleanly un-registers the segment from the JigglePhysics tree.
            if (UseLimitJiggleBones)
            {
                info.JiggleRigsTrimmed = TrimComponents<JiggleRig>(root, LimitJiggleBones);
            }

            // CilboxProxy is in the com.cnlohr.cilbox package which this assembly does
            // not reference, so trim by short type name instead of generic component type.
            if (UseLimitCilboxBehaviours)
            {
                info.CilboxBehavioursTrimmed = TrimComponentsByName(root, CompCilboxProxy, LimitCilboxBehaviours);
            }

            return info;
        }

        /// <summary>
        /// Walk every MonoBehaviour on the avatar tree and destroy instances whose
        /// <see cref="Type.Name"/> matches <paramref name="typeName"/> beyond the
        /// keep limit. Used for components living in packages this assembly cannot
        /// reference at compile time (e.g. <c>CilboxProxy</c>).
        /// </summary>
        private static int TrimComponentsByName(GameObject root, string typeName, int limit)
        {
            MonoBehaviour[] all = root.GetComponentsInChildren<MonoBehaviour>(false);
            int kept = 0;
            int destroyed = 0;
            for (int i = 0; i < all.Length; i++)
            {
                MonoBehaviour mb = all[i];
                if (mb == null) continue;
                if (mb.GetType().Name != typeName) continue;
                if (kept < limit)
                {
                    kept++;
                    continue;
                }
                UnityEngine.Object.DestroyImmediate(mb);
                destroyed++;
            }
            return destroyed;
        }

        private static int TrimComponents<T>(GameObject root, int limit) where T : Component
        {
            // includeInactive=false matches the metadata enumeration in
            // BasisBundleBuild.GenerateMetaData, so the runtime trim sees exactly
            // the same set the metadata counted. Inactive variant components stay
            // in place — they don't tick or render, so they don't consume budget.
            T[] all = root.GetComponentsInChildren<T>(false);
            if (all.Length <= limit) return 0;

            int destroyed = 0;
            for (int i = limit; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                UnityEngine.Object.DestroyImmediate(all[i]);
                destroyed++;
            }
            return destroyed;
        }

        /// <summary>
        /// Animator-specific trim. The <see cref="BasisAvatar.Animator"/> reference
        /// is <b>always</b> kept and does <b>not</b> count against the user's limit:
        /// <see cref="Basis.Scripts.Drivers.BasisRemoteAvatarDriver.RemoteCalibration"/>
        /// disables that Animator on remotes so it never actually ticks per frame.
        /// The user's "Max Animators" is therefore a cap on *ticking* animators,
        /// and the protected one gets a free pass.
        /// </summary>
        private static int TrimAnimators(GameObject root, int limit, Animator protectedAnimator)
        {
            Animator[] all = root.GetComponentsInChildren<Animator>(false);

            // Count non-protected animators — those are the ones the limit applies
            // to. The protected one is always kept and accounts for 0 slots.
            int nonProtectedCount = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i] != protectedAnimator)
                {
                    nonProtectedCount++;
                }
            }

            if (nonProtectedCount <= limit) return 0;

            // Walk in hierarchy order; keep the first `limit` non-protected
            // animators and destroy the rest. The protected one is skipped over
            // without consuming a keep slot.
            int keptNonProtected = 0;
            int destroyed = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Animator animator = all[i];
                if (animator == null) continue;
                if (animator == protectedAnimator) continue;

                if (keptNonProtected < limit)
                {
                    keptNonProtected++;
                    continue;
                }

                DestroyAnimatorAndDependents(animator);
                destroyed++;
            }
            return destroyed;
        }

        /// <summary>
        /// Destroy an Animator along with any components that <c>[RequireComponent]</c>
        /// it. Unity refuses to remove a component while another declares it as a
        /// requirement — it logs "Can't remove Animator because X depends on it" and
        /// the destroy silently fails. The Animation Rigging package's
        /// <see cref="RigBuilder"/> is the common offender on avatars authored with
        /// runtime IK constraints, so we strip it explicitly before the Animator goes.
        /// Any other dependents are handled by the generic fallback sweep.
        /// </summary>
        private static void DestroyAnimatorAndDependents(Animator animator)
        {
            if (animator == null) return;
            GameObject go = animator.gameObject;

            // Known offender: RigBuilder from Animation Rigging.
            if (go.TryGetComponent(out RigBuilder rigBuilder))
            {
                UnityEngine.Object.DestroyImmediate(rigBuilder);
            }

            // Generic fallback — any other MonoBehaviour on this GameObject that
            // has [RequireComponent(Animator)] would also block the destroy. Walk
            // the component list once via reflection so an unknown dependency on a
            // customer avatar doesn't cascade into a silent "Can't remove" log.
            Component[] siblings = go.GetComponents<Component>();
            for (int i = 0; i < siblings.Length; i++)
            {
                Component sibling = siblings[i];
                if (sibling == null || sibling == animator) continue;
                if (ComponentRequiresAnimator(sibling.GetType()))
                {
                    UnityEngine.Object.DestroyImmediate(sibling);
                }
            }

            UnityEngine.Object.DestroyImmediate(animator);
        }

        private static bool ComponentRequiresAnimator(Type componentType)
        {
            // Walk the RequireComponent attributes on this type (and up its hierarchy,
            // inherit = true) looking for anything that names Animator as a required
            // sibling. Cheap enough to run once per animator being destroyed.
            object[] attrs = componentType.GetCustomAttributes(typeof(RequireComponent), inherit: true);
            for (int i = 0; i < attrs.Length; i++)
            {
                var rc = (RequireComponent)attrs[i];
                if (rc.m_Type0 == typeof(Animator) || rc.m_Type1 == typeof(Animator) || rc.m_Type2 == typeof(Animator))
                {
                    return true;
                }
            }
            return false;
        }

        private static Result Fail(string metric, double actual, double limit)
        {
            return new Result(true, $"Exceeds {metric} limit ({Format(actual)} > {Format(limit)})");
        }

        private static Result FailTag(string tag)
        {
            return new Result(true, $"Blocked content tag '{tag}'");
        }

        private static string Format(double v)
        {
            if (v >= 1_000_000) return (v / 1_000_000d).ToString("0.##") + "M";
            if (v >= 1_000) return (v / 1_000d).ToString("0.##") + "k";
            return v.ToString("0.##");
        }
    }
}
