using Basis.BasisUI;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using UnityEngine;
using Basis.Scripts.Settings;

/// <summary>
/// Settings bridge that forwards the performance-limit sliders/toggles into
/// <see cref="BasisAvatarPerformanceLimits"/>. Mirrors <c>SMModuleAvatarLoadGates</c>:
/// subscribes once at runtime init, seeds the static state from the saved settings,
/// and reacts to <see cref="BasisSettingsSystem.OnSettingChanged"/>.
///
/// Setting values are applied immediately (cheap field writes), but the resulting
/// avatar reload pass is debounced — rapid toggling would otherwise fire one full
/// reconcile per click, and reconcile reloads every remote avatar in the room. The
/// debounce collapses a burst of changes into a single reconcile after the user has
/// settled, so flipping ten toggles in a second reloads each avatar once, not ten
/// times.
/// </summary>
public static class SMModuleAvatarPerformanceLimits
{
    /// <summary>
    /// How long to wait after the last perf-limit setting change before running the
    /// reconcile pass. Must be long enough to absorb a fast click-through of the
    /// Performance Limits tab but short enough that the user sees the effect of a
    /// single change without obvious latency.
    /// </summary>
    private const float DebounceSeconds = 0.35f;

    /// <summary>
    /// Hidden MonoBehaviour that runs the debounce timer. We need a Unity
    /// component because settings changes fire on the main thread but have no
    /// natural per-frame update hook, and the reconcile pass MUST run on the main
    /// thread (it touches Unity objects via <see cref="BasisRemotePlayer.ReloadAvatar"/>).
    /// The runner is created once at <see cref="Register"/> and survives scene
    /// loads via <see cref="Object.DontDestroyOnLoad(Object)"/>.
    /// </summary>
    private sealed class DebounceRunner : MonoBehaviour
    {
        // Negative = nothing pending. Uses realtimeSinceStartup so the debounce
        // still fires when Time.timeScale is 0 (pause menu etc.).
        public float PendingFireTime = -1f;

        private void Update()
        {
            if (PendingFireTime < 0f) return;
            if (Time.realtimeSinceStartup < PendingFireTime) return;
            PendingFireTime = -1f;
            ReconcileLoadedAvatars();
        }
    }

    private static DebounceRunner _runner;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register()
    {
        // Seed every limit from the current settings store. The binding layer already
        // ran its LoadBindingValue during static init, so RawValue is authoritative
        // for the saved value (or platform default) by the time we read it here.
        ApplyAll();

        EnsureRunner();

        BasisSettingsSystem.OnSettingChanged -= OnSettingChanged;
        BasisSettingsSystem.OnSettingChanged += OnSettingChanged;

        // The session-only bypass toggle doesn't go through BasisSettingsSystem,
        // so we get a dedicated event from the evaluator and route it into the
        // same debounced reconcile pass as a regular setting change.
        BasisAvatarPerformanceLimits.OnBypassChanged -= OnBypassChanged;
        BasisAvatarPerformanceLimits.OnBypassChanged += OnBypassChanged;

        // Content-tag filter changes also flow into the perf reconcile pass —
        // Evaluate now consults the tag list, so a freshly-blocked tag flips
        // wouldBeBlocked for any loaded avatar carrying it (and the inverse on
        // unblock), which DetermineAction maps to Reload exactly like a hard-block
        // limit change.
        BasisContentTagFilter.OnChanged -= OnTagFilterChanged;
        BasisContentTagFilter.OnChanged += OnTagFilterChanged;
    }

    private static void OnBypassChanged()
    {
        ScheduleReconcile();
    }

    private static void OnTagFilterChanged()
    {
        ScheduleReconcile();
    }

    private static void EnsureRunner()
    {
        if (_runner != null) return;

        // HideAndDontSave keeps this out of the hierarchy inspector and prevents it
        // from being serialized into any scene. DontDestroyOnLoad makes it survive
        // the scene transitions that happen during world loads.
        var go = new GameObject("BasisPerformanceLimitsDebounce");
        go.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(go);
        _runner = go.AddComponent<DebounceRunner>();
    }

    private static void OnSettingChanged(string settingName, string value)
    {
        string key = settingName?.ToLower();
        if (string.IsNullOrEmpty(key) || !IsPerfLimitKey(key))
        {
            return;
        }

        // Setting values are applied immediately — new avatar loads that happen
        // during the debounce window still see the freshest limits. Only the
        // reconcile-existing-avatars pass is deferred.
        ApplyAll();
        ScheduleReconcile();
    }

    /// <summary>
    /// Push the debounced reconcile firing time forward. Each new setting change
    /// resets the timer so a fast click-through only runs one reconcile at the end.
    /// </summary>
    private static void ScheduleReconcile()
    {
        EnsureRunner();
        if (_runner == null)
        {
            // Extremely unlikely — if we can't create the runner, fall back to
            // running the reconcile inline so at least correctness is preserved.
            ReconcileLoadedAvatars();
            return;
        }
        _runner.PendingFireTime = Time.realtimeSinceStartup + DebounceSeconds;
    }

    /// <summary>
    /// Walk every currently-known remote player and take the cheapest correct
    /// action for each given the new limits. <see cref="BasisAvatarPerformanceLimits.DetermineAction"/>
    /// classifies each player as None, TrimInPlace, or Reload; we then dispatch:
    ///
    ///   • None — skip entirely (nothing would change).
    ///   • TrimInPlace — re-run <c>TrimExcessComponents</c> on the live avatar
    ///     and accumulate the destroyed counts into the player's stored
    ///     <c>LastPerformanceInfo</c>. No bundle I/O, no instantiation, no setup
    ///     pass; this is the fast path that makes fast toggling feel instant.
    ///   • Reload — full <c>ReloadAvatar</c>, needed only when a trim relaxed
    ///     (destroyed components need to come back) or the hard-block flag flipped.
    ///
    /// Called by the debounced <see cref="DebounceRunner"/> so a burst of toggle
    /// changes collapses into a single reconcile pass.
    /// </summary>
    private static void ReconcileLoadedAvatars()
    {
        foreach (var kvp in BasisNetworkPlayers.RemotePlayers)
        {
            BasisNetworkReceiver receiver = kvp.Value;
            if (receiver == null) continue;

            BasisRemotePlayer player = receiver.RemotePlayer;
            if (player == null) continue;

            BasisLoadableBundle bundle = player.AlwaysRequestedAvatar;
            if (bundle == null || bundle.BasisBundleConnector == null)
            {
                continue;
            }

            // Skip players whose cached request IS the fallback avatar — reloading
            // would just destroy and re-instantiate the same loading mesh.
            if (BasisAvatarFactory.IsLoadingAvatar(bundle))
            {
                continue;
            }

            // Players currently showing the fallback (either perf-blocked, out of
            // range, or load failure): there's no real instantiated avatar to trim
            // in-place. Only a hard-block flip that newly un-blocks them needs a
            // reload — trim changes take effect automatically when the range-change
            // path next reloads them. Test via a direct Evaluate instead of the
            // full DetermineAction so we don't traverse trim categories we can't
            // act on from here anyway.
            if (player.BasisAvatar == null || player.IsConsideredFallBackAvatar)
            {
                bool wasBlocked = player.LastPerformanceInfo.Blocked;
                bool wouldBeBlocked = BasisAvatarPerformanceLimits.Evaluate(bundle.BasisBundleConnector).Blocked;
                if (wasBlocked != wouldBeBlocked)
                {
                    player.ReloadAvatar();
                }
                continue;
            }

            var action = BasisAvatarPerformanceLimits.DetermineAction(bundle.BasisBundleConnector, player.LastPerformanceInfo);
            switch (action)
            {
                case BasisAvatarPerformanceLimits.ReconcileAction.None:
                    // No effective change for this player — don't touch them.
                    break;

                case BasisAvatarPerformanceLimits.ReconcileAction.TrimInPlace:
                {
                    // Mutate the already-instantiated tree in place. TrimExcessComponents
                    // uses DestroyImmediate so the result is visible on the same frame,
                    // no double buffering required. Accumulate the delta into the
                    // player's cached PerformanceInfo — the trim is strictly additive
                    // (we only ever destroy, never restore) so summing matches reality.
                    var delta = BasisAvatarPerformanceLimits.TrimExcessComponents(player.BasisAvatar.gameObject);
                    var info = player.LastPerformanceInfo;
                    info.AnimatorsTrimmed += delta.AnimatorsTrimmed;
                    info.LightsTrimmed += delta.LightsTrimmed;
                    info.ParticleSystemsTrimmed += delta.ParticleSystemsTrimmed;
                    info.TrailRenderersTrimmed += delta.TrailRenderersTrimmed;
                    info.LineRenderersTrimmed += delta.LineRenderersTrimmed;
                    info.ClothTrimmed += delta.ClothTrimmed;
                    info.CollidersTrimmed += delta.CollidersTrimmed;
                    info.JiggleCollidersTrimmed += delta.JiggleCollidersTrimmed;
                    info.JiggleRigsTrimmed += delta.JiggleRigsTrimmed;
                    player.LastPerformanceInfo = info;
                    break;
                }

                case BasisAvatarPerformanceLimits.ReconcileAction.Reload:
                    player.ReloadAvatar();
                    break;
            }
        }
    }

    private static bool IsPerfLimitKey(string key)
    {
        return key.StartsWith("useperflimit") || key.StartsWith("maxperf");
    }

    private static void ApplyAll()
    {
        // Triangles
        BasisAvatarPerformanceLimits.UseLimitTriangles = BasisSettingsDefaults.UsePerfLimitTriangles.RawValue;
        BasisAvatarPerformanceLimits.LimitTriangles = (long)BasisSettingsDefaults.MaxPerfTriangles.RawValue;

        // Bounds size (metres, diagonal length).
        BasisAvatarPerformanceLimits.UseLimitBoundsSize = BasisSettingsDefaults.UsePerfLimitBoundsSize.RawValue;
        BasisAvatarPerformanceLimits.LimitBoundsSize = BasisSettingsDefaults.MaxPerfBoundsSize.RawValue;

        // Texture memory — slider is in megabytes, limit is stored in bytes.
        BasisAvatarPerformanceLimits.UseLimitTextureMemory = BasisSettingsDefaults.UsePerfLimitTextureMemory.RawValue;
        BasisAvatarPerformanceLimits.LimitTextureMemoryBytes = (long)(BasisSettingsDefaults.MaxPerfTextureMemoryMB.RawValue * 1024L * 1024L);

        // Renderers / material counts.
        BasisAvatarPerformanceLimits.UseLimitSkinnedMeshes = BasisSettingsDefaults.UsePerfLimitSkinnedMeshes.RawValue;
        BasisAvatarPerformanceLimits.LimitSkinnedMeshes = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfSkinnedMeshes.RawValue));

        BasisAvatarPerformanceLimits.UseLimitBasicMeshes = BasisSettingsDefaults.UsePerfLimitBasicMeshes.RawValue;
        BasisAvatarPerformanceLimits.LimitBasicMeshes = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfBasicMeshes.RawValue));

        BasisAvatarPerformanceLimits.UseLimitMaterialSlots = BasisSettingsDefaults.UsePerfLimitMaterialSlots.RawValue;
        BasisAvatarPerformanceLimits.LimitMaterialSlots = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfMaterialSlots.RawValue));

        // Jiggle physics: we track JiggleRig count as the "bone chain" unit and
        // JiggleColliderExample as the collider unit.
        BasisAvatarPerformanceLimits.UseLimitJiggleBones = BasisSettingsDefaults.UsePerfLimitJiggleBones.RawValue;
        BasisAvatarPerformanceLimits.LimitJiggleBones = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfJiggleBones.RawValue));

        BasisAvatarPerformanceLimits.UseLimitJiggleColliders = BasisSettingsDefaults.UsePerfLimitJiggleColliders.RawValue;
        BasisAvatarPerformanceLimits.LimitJiggleColliders = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfJiggleColliders.RawValue));

        // Animators: the remote driver disables the root animator for remotes, but
        // child Animator components still tick — we count every Animator regardless.
        BasisAvatarPerformanceLimits.UseLimitAnimators = BasisSettingsDefaults.UsePerfLimitAnimators.RawValue;
        BasisAvatarPerformanceLimits.LimitAnimators = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfAnimators.RawValue));

        BasisAvatarPerformanceLimits.UseLimitBones = BasisSettingsDefaults.UsePerfLimitBones.RawValue;
        BasisAvatarPerformanceLimits.LimitBones = (long)BasisSettingsDefaults.MaxPerfBones.RawValue;

        BasisAvatarPerformanceLimits.UseLimitLights = BasisSettingsDefaults.UsePerfLimitLights.RawValue;
        BasisAvatarPerformanceLimits.LimitLights = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfLights.RawValue));

        BasisAvatarPerformanceLimits.UseLimitParticleSystems = BasisSettingsDefaults.UsePerfLimitParticleSystems.RawValue;
        BasisAvatarPerformanceLimits.LimitParticleSystems = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfParticleSystems.RawValue));

        BasisAvatarPerformanceLimits.UseLimitTrailRenderers = BasisSettingsDefaults.UsePerfLimitTrailRenderers.RawValue;
        BasisAvatarPerformanceLimits.LimitTrailRenderers = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfTrailRenderers.RawValue));

        BasisAvatarPerformanceLimits.UseLimitLineRenderers = BasisSettingsDefaults.UsePerfLimitLineRenderers.RawValue;
        BasisAvatarPerformanceLimits.LimitLineRenderers = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfLineRenderers.RawValue));

        BasisAvatarPerformanceLimits.UseLimitCloth = BasisSettingsDefaults.UsePerfLimitCloth.RawValue;
        BasisAvatarPerformanceLimits.LimitCloth = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfCloth.RawValue));

        BasisAvatarPerformanceLimits.UseLimitColliders = BasisSettingsDefaults.UsePerfLimitColliders.RawValue;
        BasisAvatarPerformanceLimits.LimitColliders = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfColliders.RawValue));

        BasisAvatarPerformanceLimits.UseLimitCilboxBehaviours = BasisSettingsDefaults.UsePerfLimitCilboxBehaviours.RawValue;
        BasisAvatarPerformanceLimits.LimitCilboxBehaviours = Mathf.Max(0, Mathf.RoundToInt(BasisSettingsDefaults.MaxPerfCilboxBehaviours.RawValue));
    }
}
