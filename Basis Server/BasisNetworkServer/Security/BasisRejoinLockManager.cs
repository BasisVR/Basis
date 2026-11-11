using Basis.Network.Core;
using System.Collections.Concurrent;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Runtime-only "rejoin-only" lockdown. While the restriction mode is
    /// <see cref="BasisNetworkCore.Security.BasisUserRestrictionMode.RejoinOnly"/>, only the UUIDs
    /// captured at the moment the mode was enabled may (re)connect — nobody new. The set persists
    /// through disconnects (so a captured player can leave and rejoin) and never grows. It is not
    /// persisted to disk; a restart normalizes the mode back to Normal and the set starts empty.
    /// </summary>
    public static class BasisRejoinLockManager
    {
        private static readonly ConcurrentDictionary<string, byte> _allowed = new ConcurrentDictionary<string, byte>();

        public static int Count => _allowed.Count;

        /// <summary>
        /// Snapshot every currently-authenticated peer's UUID as the allowed set.
        /// Called when the restriction mode transitions to RejoinOnly.
        /// </summary>
        public static void CaptureCurrentPopulation()
        {
            Captured = true;
            _allowed.Clear();
            if (NetworkServer.AuthIdentity == null) return;

            foreach (NetPeer peer in NetworkServer.AuthenticatedPeers.Values)
            {
                if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) && !string.IsNullOrEmpty(uuid))
                {
                    _allowed.TryAdd(uuid, 0);
                }
            }

            BNL.Log($"Rejoin-only lockdown enabled — captured {_allowed.Count} current player(s).");
        }

        /// <summary>Drop the captured set (mode changed away from RejoinOnly).</summary>
        public static void Clear()
        {
            Captured = false;
            _allowed.Clear();
        }

        public static bool Captured { get; private set; }

        public static bool Admit(string uuid) => !string.IsNullOrEmpty(uuid) && _allowed.TryAdd(uuid, 0);

        /// <summary>True if this UUID was connected when the lockdown was enabled.</summary>
        public static bool IsAllowed(string uuid) => !string.IsNullOrEmpty(uuid) && _allowed.ContainsKey(uuid);

        public static bool IsStaff(string uuid) => !string.IsNullOrEmpty(uuid)
            && (BasisPermissions.PermissionManager.PermissionIntegration.HasValidRequirement(uuid, BasisPermissions.PermNodes.ConfigurationEditor)
            || BasisPermissions.PermissionManager.PermissionIntegration.HasValidRequirement(uuid, BasisPermissions.PermNodes.ModerationAllowlist));
    }
}
