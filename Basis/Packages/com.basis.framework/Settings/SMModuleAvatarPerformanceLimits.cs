using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using UnityEngine;
using Basis.Scripts.Settings;
public static class SMModuleAvatarPerformanceLimits
{
    private const double DebounceSeconds = 0.35f;
    private static double _pendingFireTime = -1f;
    public static int RevalBudgetPerFrame = 10;
    public static bool RequiresPerformanceCheck = false;
    private static readonly Queue<BasisRemotePlayer> _revalQueue = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register()
    {
        ApplyAll();
        BasisSettingsSystem.OnSettingChanged -= OnSettingChanged;
        BasisSettingsSystem.OnSettingChanged += OnSettingChanged;
        BasisAvatarPerformanceLimits.OnBypassChanged -= OnBypassChanged;
        BasisAvatarPerformanceLimits.OnBypassChanged += OnBypassChanged;
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
    public static void Simulate()
    {
        if (RequiresPerformanceCheck != false)
        {
            if (_pendingFireTime >= 0f)
            {
                if (Time.realtimeSinceStartupAsDouble < _pendingFireTime)
                {
                    return;
                }
                _pendingFireTime = -1f;
            }

            int budget = RevalBudgetPerFrame;
            while (budget > 0 && _revalQueue.Count > 0)
            {
                BasisRemotePlayer player = _revalQueue.Dequeue();
                if (player == null || !player.RequiresPerformanceReval)
                {
                    continue;
                }
                player.RequiresPerformanceReval = false;
                ReconcilePlayer(player);
                budget--;
            }
            if (_revalQueue.Count == 0)
            {
                RequiresPerformanceCheck = false;
            }
        }
    }

    private static void OnSettingChanged(string settingName, string value)
    {
        string key = settingName?.ToLower();
        if (string.IsNullOrEmpty(key) || !IsPerfLimitKey(key))
        {
            return;
        }
        ApplyAll();
        ScheduleReconcile();
    }
    private static void ScheduleReconcile()
    {
        _pendingFireTime = Time.realtimeSinceStartupAsDouble + DebounceSeconds;

        foreach (var kvp in BasisNetworkPlayers.RemotePlayers)
        {
            BasisRemotePlayer player = kvp.Value;
            if (player == null || player.RequiresPerformanceReval)
            {
                continue;
            }
            player.RequiresPerformanceReval = true;
            _revalQueue.Enqueue(player);
            RequiresPerformanceCheck = true;
        }
    }
    private static void ReconcilePlayer(BasisRemotePlayer player)
    {
        if (player.BypassPerformanceLimits)
        {
            return;
        }
        BasisLoadableBundle bundle = player.AlwaysRequestedAvatar;
        if (bundle == null || bundle.BasisBundleConnector == null)
        {
            return;
        }
        if (BasisAvatarFactory.IsLoadingAvatar(bundle))
        {
            return;
        }
        if (player.BasisAvatar == null || player.IsConsideredFallBackAvatar)
        {
            bool wasBlocked = player.LastPerformanceInfo.Blocked;
            bool wouldBeBlocked = BasisAvatarPerformanceLimits.Evaluate(bundle.BasisBundleConnector).Blocked;
            if (wasBlocked != wouldBeBlocked)
            {
                player.ReloadAvatar();
            }
            return;
        }
        var action = BasisAvatarPerformanceLimits.DetermineAction(bundle.BasisBundleConnector, player.LastPerformanceInfo);
        switch (action)
        {
            case BasisAvatarPerformanceLimits.ReconcileAction.None:
                break;

            case BasisAvatarPerformanceLimits.ReconcileAction.TrimInPlace:
                {
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
