using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// Composes every gate that decides whether a jiggle grab may exist between two players:
    /// the global setting, persisted blocks, the session temp block, the per-player jiggle
    /// toggle, and a short-lived deny cache fed by target-authoritative GrabDeny events.
    /// The per-player values are read from the mirrors on <see cref="BasisRemotePlayer"/>,
    /// never from the async settings manager.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisJiggleGrabPermissions
    {
        public static bool MasterEnabled = true;

        public const float DenyCacheSeconds = 30f;

        private static readonly Dictionary<uint, float> denyUntil = new Dictionary<uint, float>();
        private static readonly List<uint> denyScratch = new List<uint>();
        private static float nextDenyPrune;

        private static uint DenyKey(ushort grabberId, ushort targetId) => ((uint)grabberId << 16) | targetId;

        public static void RegisterDeny(ushort grabberId, ushort targetId)
        {
            denyUntil[DenyKey(grabberId, targetId)] = Time.unscaledTime + DenyCacheSeconds;
        }

        public static bool IsDenied(ushort grabberId, ushort targetId)
        {
            if (!denyUntil.TryGetValue(DenyKey(grabberId, targetId), out float until))
            {
                return false;
            }
            if (Time.unscaledTime > until)
            {
                denyUntil.Remove(DenyKey(grabberId, targetId));
                return false;
            }
            return true;
        }

        public static void PruneDenies()
        {
            float now = Time.unscaledTime;
            if (now < nextDenyPrune)
            {
                return;
            }
            nextDenyPrune = now + DenyCacheSeconds;
            denyScratch.Clear();
            foreach (KeyValuePair<uint, float> pair in denyUntil)
            {
                if (now > pair.Value)
                {
                    denyScratch.Add(pair.Key);
                }
            }
            int count = denyScratch.Count;
            for (int Index = 0; Index < count; Index++)
            {
                denyUntil.Remove(denyScratch[Index]);
            }
        }

        public static void Clear()
        {
            denyUntil.Clear();
            denyScratch.Clear();
            nextDenyPrune = 0f;
        }

        /// <summary>Grabber-side gate: may the local player start a grab on this target? Null target = self.</summary>
        public static bool CanLocalGrab(BasisRemotePlayer target)
        {
            if (!MasterEnabled)
            {
                return false;
            }
            if (target == null)
            {
                return true;
            }
            if (target.IsEffectivelyBlocked)
            {
                return false;
            }
            return target.AvatarInteractionAllowed && target.JiggleGrabAllowed;
        }

        /// <summary>Target-side gate: does the local player refuse this remote grabbing them?</summary>
        public static bool LocalPlayerDenies(ushort grabberId)
        {
            if (!MasterEnabled)
            {
                return true;
            }
            if (BasisNetworkPlayers.RemotePlayers.TryGetValue(grabberId, out BasisRemotePlayer grabber))
            {
                if (grabber.IsEffectivelyBlocked)
                {
                    return true;
                }
                return !grabber.AvatarInteractionAllowed || !grabber.JiggleGrabAllowed;
            }
            return false;
        }

        /// <summary>Observer-side gate: should this client hold and apply a grab between these two players?</summary>
        public static bool ObserverAllows(ushort grabberId, ushort targetId, ushort localPlayerId)
        {
            if (!MasterEnabled)
            {
                return false;
            }
            if (IsDenied(grabberId, targetId))
            {
                return false;
            }
            if (targetId == localPlayerId && grabberId != localPlayerId)
            {
                return !LocalPlayerDenies(grabberId);
            }
            if (grabberId != localPlayerId
                && BasisNetworkPlayers.RemotePlayers.TryGetValue(grabberId, out BasisRemotePlayer grabber)
                && grabber.IsEffectivelyBlocked)
            {
                return false;
            }
            if (targetId != localPlayerId
                && BasisNetworkPlayers.RemotePlayers.TryGetValue(targetId, out BasisRemotePlayer target)
                && target.IsEffectivelyBlocked)
            {
                return false;
            }
            return true;
        }
    }
}
