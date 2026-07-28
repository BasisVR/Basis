using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.VoiceRecording;
using UnityEngine;

namespace Basis.Shims
{
    /// <summary>
    /// Cilbox-facing facade for the consent-gated voice system (issue #911). A world script
    /// can ask that a player's voice play from a world object, and stop it again.
    ///
    /// Security: everything privacy- or exfiltration-sensitive is withheld. Scripts never
    /// receive PCM, cannot write recordings to disk, and cannot bypass or forge the
    /// recordee's consent. This facade only asks the trusted <see cref="BasisVoiceRecording"/>
    /// to route already-consented audio to a source that FOLLOWS (never parents under) the
    /// caller's anchor — so a script cannot reach the AudioSource via the anchor's hierarchy
    /// and read its samples. Routes are referenced by opaque integer ids, never host objects.
    /// This type is method-restricted in <c>CilboxSceneBasis.extraMethodWhitelist</c>.
    ///
    /// This shim adds no PCM/exfiltration surface of its own. It does NOT address the separate,
    /// pre-existing platform issue that AudioListener/AudioSource PCM-read APIs are whitelisted
    /// for scene scripts (see the Cilbox audio whitelist) — that must be closed independently.
    /// </summary>
    public static class BasisVoiceRoutingShim
    {
        private static int _nextRouteId = 1;
        private static readonly Dictionary<int, BasisVoiceObjectSource> _routes = new Dictionary<int, BasisVoiceObjectSource>();
        private static readonly HashSet<int> _cancelledBeforeReady = new HashSet<int>();

        /// <summary>Whether the local user already holds this player's consent to route their voice.</summary>
        public static bool HasConsent(BasisNetworkPlayer player)
        {
            return player != null && BasisVoiceRecording.HasRouteConsent(player.playerId);
        }

        /// <summary>
        /// Asks <paramref name="player"/> for permission and, once granted, plays their voice as
        /// a 3D source that follows <paramref name="anchor"/>. Returns an opaque route id (0 on
        /// immediate failure). The caller never receives audio samples.
        /// </summary>
        public static int RouteVoiceToObject(BasisNetworkPlayer player, Transform anchor)
        {
            return RouteVoiceToObject(player, anchor, 1f, 20f);
        }

        public static int RouteVoiceToObject(BasisNetworkPlayer player, Transform anchor, float minDistance, float maxDistance)
        {
            if (player == null || anchor == null)
            {
                return 0;
            }
            if (!(player.Player is BasisRemotePlayer remote))
            {
                return 0;
            }
            int id = _nextRouteId++;
            BeginRoute(id, remote, anchor, Mathf.Max(0.1f, minDistance), Mathf.Max(minDistance, maxDistance));
            return id;
        }

        public static void StopVoiceRoute(int routeId)
        {
            if (_routes.TryGetValue(routeId, out BasisVoiceObjectSource source))
            {
                _routes.Remove(routeId);
                BasisVoiceRecording.StopPlayFromTransform(source);
            }
            else
            {
                _cancelledBeforeReady.Add(routeId);
            }
        }

        public static void StopAllRoutesFor(BasisNetworkPlayer player)
        {
            if (player == null)
            {
                return;
            }
            ushort playerId = player.playerId;
            List<int> toStop = null;
            foreach (KeyValuePair<int, BasisVoiceObjectSource> kv in _routes)
            {
                if (kv.Value.PlayerId == playerId)
                {
                    (toStop ??= new List<int>()).Add(kv.Key);
                }
            }
            if (toStop == null)
            {
                return;
            }
            for (int i = 0; i < toStop.Count; i++)
            {
                if (_routes.TryGetValue(toStop[i], out BasisVoiceObjectSource source))
                {
                    _routes.Remove(toStop[i]);
                    BasisVoiceRecording.StopPlayFromTransform(source);
                }
            }
        }

        private static async void BeginRoute(int id, BasisRemotePlayer remote, Transform anchor, float minDistance, float maxDistance)
        {
            BasisVoiceObjectSource source;
            try
            {
                source = await BasisVoiceRecording.RequestPlayFromTransform(remote, anchor, minDistance, maxDistance);
            }
            catch (System.Exception ex)
            {
                BasisDebug.LogError($"[BasisVoiceRoutingShim] Route {id} failed: {ex}");
                _cancelledBeforeReady.Remove(id);
                return;
            }
            if (source == null)
            {
                _cancelledBeforeReady.Remove(id);
                return;
            }
            if (_cancelledBeforeReady.Remove(id))
            {
                BasisVoiceRecording.StopPlayFromTransform(source);
                return;
            }
            _routes[id] = source;
        }
    }
}
