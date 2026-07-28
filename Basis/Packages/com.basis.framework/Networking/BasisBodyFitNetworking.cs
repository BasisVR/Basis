using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Transmitters;
using System.Collections.Generic;
using UnityEngine;
using static SerializableBasis;

namespace Basis.IK
{
    /// <summary>
    /// Networks the local player's body fit (per-segment arm/leg/torso scales; see BasisBodyFitCore) so
    /// remotes render the wearer at their calibrated proportions instead of the avatar's authored ones.
    ///
    /// It rides AvatarChangeMessageChannel under a kind discriminator rather than a channel of its own —
    /// all 64 LiteNetLib channels are assigned, and that channel already has the two properties this
    /// needs: the server stores the record per player and replays it to late joiners. So this class only
    /// has to do change detection on the way out and application on the way in; it deliberately does NOT
    /// re-broadcast to newly seen players the way the older spine-proportion networking had to, because
    /// the server is now the one answering joiners.
    /// </summary>
    public static class BasisBodyFitNetworking
    {
        // A recalibration that moves a segment by less than this is not worth a packet. 0.1% of a ~0.3 m
        // forearm is ~0.3 mm — well under what is visible on a remote at conversational distance.
        const float k_SendEpsilon = 0.001f;

        static BasisBodyFitResult _lastSent = BasisBodyFitResult.Identity;
        static bool _hasSent;

        // A fit that arrived before the sending player's BasisRemotePlayer existed here (join race);
        // applied when that player joins.
        static readonly Dictionary<ushort, BasisBodyFitResult> _pending = new Dictionary<ushort, BasisBodyFitResult>();

        [RuntimeInitializeOnLoadMethod]
        static void Init()
        {
            _pending.Clear();
            _lastSent = BasisBodyFitResult.Identity;
            _hasSent = false;
            BasisNetworkPlayer.OnRemotePlayerJoined -= HandleRemotePlayerJoined;
            BasisNetworkPlayer.OnRemotePlayerJoined += HandleRemotePlayerJoined;
            BasisNetworkPlayer.OnRemotePlayerLeft -= HandleRemotePlayerLeft;
            BasisNetworkPlayer.OnRemotePlayerLeft += HandleRemotePlayerLeft;
        }

        /// <summary>
        /// Rebuilds a fit result from three wire scales. Anything that is not a real deformation reports
        /// as unfitted, so a remote with an identity fit takes the same path as one that never sent.
        /// </summary>
        public static BasisBodyFitResult ToFitResult(float arm, float leg, float torso)
        {
            arm = ClientAvatarChangeMessage.SanitizeFitScale(arm);
            leg = ClientAvatarChangeMessage.SanitizeFitScale(leg);
            torso = ClientAvatarChangeMessage.SanitizeFitScale(torso);

            return new BasisBodyFitResult
            {
                ArmScale = arm,
                LegScale = leg,
                TorsoScale = torso,
                ArmStatus = Mathf.Approximately(arm, 1f) ? BasisBodyFitStatus.Disabled : BasisBodyFitStatus.Fitted,
                BodyStatus = Mathf.Approximately(leg, 1f) && Mathf.Approximately(torso, 1f)
                    ? BasisBodyFitStatus.Disabled
                    : BasisBodyFitStatus.Fitted,
            };
        }

        /// <summary>
        /// Called from BasisLocalRigDriver.RefreshBodyFit with the fit that was just applied locally.
        /// Sends only when a scale actually moved — recalibration is rare, but RefreshBodyFit also runs
        /// on every rig build and settings change, and those are usually no-ops.
        /// </summary>
        public static void UpdateLocalFit(in BasisBodyFitResult fit)
        {
            float arm = fit.HasArmFit ? fit.ArmScale : 1f;
            float leg = fit.HasBodyFit ? fit.LegScale : 1f;
            float torso = fit.HasBodyFit ? fit.TorsoScale : 1f;

            if (_hasSent &&
                Mathf.Abs(arm - _lastSent.ArmScale) < k_SendEpsilon &&
                Mathf.Abs(leg - _lastSent.LegScale) < k_SendEpsilon &&
                Mathf.Abs(torso - _lastSent.TorsoScale) < k_SendEpsilon)
            {
                return;
            }

            // Latch only on an actual send. Calibration commonly runs before connecting, and marking it
            // sent while offline would suppress the real packet forever — the value would never differ
            // from itself again. (The pre-connect fit still reaches the server: BasisNetworkConnection
            // stamps it into the initial ReadyMessage.)
            if (!BasisNetworkTransmitter.SendOutBodyFit(in fit))
            {
                return;
            }

            _lastSent = new BasisBodyFitResult
            {
                ArmScale = arm,
                LegScale = leg,
                TorsoScale = torso,
                ArmStatus = fit.ArmStatus,
                BodyStatus = fit.BodyStatus,
            };
            _hasSent = true;
        }

        /// <summary>
        /// Handles an inbound body-fit-only update. Applying touches Transform.localPosition, which is
        /// main-thread only; server-relayed messages dispatch on the polled main thread but this marshals
        /// defensively for any off-thread path.
        /// </summary>
        public static void Receive(ushort senderId, ClientBodyFitMessage message)
        {
            BasisBodyFitResult fit = ToFitResult(message.ArmScale, message.LegScale, message.TorsoScale);

            if (BasisNetworkManagement.IsMainThread())
            {
                Apply(senderId, fit);
            }
            else
            {
                Basis.Scripts.Device_Management.BasisDeviceManagement.EnqueueOnMainThread(() => Apply(senderId, fit));
            }
        }

        static void Apply(ushort senderId, BasisBodyFitResult fit)
        {
            if (BasisNetworkPlayers.Players.TryGetValue(senderId, out BasisNetworkPlayer np) &&
                np != null && np.Player is BasisRemotePlayer remote && remote.RemoteAvatarDriver != null)
            {
                // Keep the record in step too: a later avatar load reseeds the driver from CACM, and
                // without this that reseed would resurrect the proportions this update replaced.
                remote.CACM.ArmScale = fit.ArmScale;
                remote.CACM.LegScale = fit.LegScale;
                remote.CACM.TorsoScale = fit.TorsoScale;
                remote.RemoteAvatarDriver.SetBodyFit(in fit);
            }
            else
            {
                _pending[senderId] = fit;
            }
        }

        static void HandleRemotePlayerJoined(BasisNetworkPlayer networkPlayer, BasisRemotePlayer remotePlayer)
        {
            if (networkPlayer == null)
            {
                return;
            }
            if (_pending.TryGetValue(networkPlayer.playerId, out BasisBodyFitResult fit))
            {
                _pending.Remove(networkPlayer.playerId);
                Apply(networkPlayer.playerId, fit);
            }
        }

        static void HandleRemotePlayerLeft(BasisNetworkPlayer networkPlayer, BasisRemotePlayer remotePlayer)
        {
            if (networkPlayer != null)
            {
                _pending.Remove(networkPlayer.playerId);
            }
        }
    }
}
