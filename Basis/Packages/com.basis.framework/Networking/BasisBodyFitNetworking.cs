using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Transmitters;
using System.Collections.Generic;
using UnityEngine;
using static SerializableBasis;

namespace Basis.IK
{
    [AutoStaticsCleanup]
    public static partial class BasisBodyFitNetworking
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
