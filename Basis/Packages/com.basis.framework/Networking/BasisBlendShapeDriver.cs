using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using System.Collections.Generic;

/// <summary>
/// Static driver that orchestrates blendshape sync for all active instances.
/// Follows the Simulate/Apply pattern used throughout Basis.
/// Wired into BasisEventDriver — do NOT call from MonoBehaviour Update/LateUpdate.
///
/// Simulate: captures local blendshape weights, delta-encodes, stages for network send.
/// Apply: writes received remote blendshape weights to meshes.
/// </summary>
public static class BasisBlendShapeDriver
{
    private static readonly List<BasisBlendShapeSync> _instances = new List<BasisBlendShapeSync>(8);

    public static void Register(BasisBlendShapeSync sync)
    {
        if (!_instances.Contains(sync))
            _instances.Add(sync);
    }

    public static void Unregister(BasisBlendShapeSync sync)
    {
        _instances.Remove(sync);
    }

    /// <summary>
    /// Capture + encode local blendshape data and stage for piggyback on player movement.
    /// Called from BasisEventDriver.LateUpdate after face tracking has written weights.
    /// </summary>
    public static void Simulate()
    {
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            var sync = _instances[i];
            if (sync == null || !sync.IsReady)
            {
                _instances.RemoveAt(i);
                continue;
            }
            if (sync.IsLocal)
                sync.CaptureAndEncode();
        }
    }

    /// <summary>
    /// Apply received remote blendshape weights to meshes.
    /// Called from BasisEventDriver.LateUpdate before shadow clone reads.
    /// </summary>
    public static void Apply()
    {
        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            var sync = _instances[i];
            if (sync == null || !sync.IsReady)
            {
                _instances.RemoveAt(i);
                continue;
            }
            if (!sync.IsLocal && sync.HasPendingData)
            {
                // Skip blendshape writes for players out of avatar range
                if (sync.NetworkedPlayer?.Player is BasisRemotePlayer rp && !rp.InAvatarRange)
                    continue;
                sync.ApplyReceivedWeights();
            }
        }
    }
}
