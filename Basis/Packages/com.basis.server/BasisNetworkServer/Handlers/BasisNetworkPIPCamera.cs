using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using static SerializableBasis;

namespace BasisNetworkServer
{
    public class CameraPIPState
    {
        public bool IsActive;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;
        // Stopwatch timestamp of the newest position data, 0 while none. Recipients whose
        // LastSentTimes entry is >= this already hold the latest position, so a camera that
        // stopped moving stops costing sends; a late joiner has no entry (reads as 0) and
        // still receives the parked position.
        public long DataTicks;
        // ConcurrentDictionary, not Dictionary: this is written from the reduction-system tick
        // thread (UpdatePIPPositions) while HandlePIPStateChange clears it from the network receive
        // thread and RemovePlayer removes from it on disconnect. A plain Dictionary under a
        // concurrent write+clear corrupts its bucket chain — the classic symptom is a hang inside
        // FindEntry or a spurious IndexOutOfRangeException, not a clean failure.
        public ConcurrentDictionary<int, long> LastSentTimes = new();
    }

    public static class BasisNetworkPIPCamera
    {
        public static ConcurrentDictionary<int, CameraPIPState> PIPStates = new();
        private static readonly double MsToTick = Stopwatch.Frequency / 1000.0;

        /// <summary>
        /// Client says their PIP camera was created or destroyed.
        /// </summary>
        public static void HandlePIPStateChange(NetPacketReader reader, NetPeer peer)
        {
            ClientCameraPIPStateMessage clientMsg = new ClientCameraPIPStateMessage();
            clientMsg.Deserialize(reader);
            reader.Recycle();

            ushort peerId = (ushort)peer.Id;

            if (clientMsg.IsActive)
            {
                var state = PIPStates.GetOrAdd(peer.Id, _ => new CameraPIPState());
                state.IsActive = true;
                state.PositionX = clientMsg.PositionX;
                state.PositionY = clientMsg.PositionY;
                state.PositionZ = clientMsg.PositionZ;
                state.RotationX = clientMsg.RotationX;
                state.RotationY = clientMsg.RotationY;
                state.RotationZ = clientMsg.RotationZ;
                state.RotationW = clientMsg.RotationW;
                state.DataTicks = Stopwatch.GetTimestamp();

                BNL.Log($"PIP camera created for player {peerId}");
            }
            else
            {
                if (PIPStates.TryGetValue(peer.Id, out var state))
                {
                    state.IsActive = false;
                    state.DataTicks = 0;
                    state.LastSentTimes.Clear();
                }

                BNL.Log($"PIP camera destroyed for player {peerId}");
            }

            // Broadcast state to all peers
            CameraPIPStateMessage outMsg = new CameraPIPStateMessage
            {
                PlayerID = peerId,
                IsActive = clientMsg.IsActive,
                PositionX = clientMsg.PositionX,
                PositionY = clientMsg.PositionY,
                PositionZ = clientMsg.PositionZ,
                RotationX = clientMsg.RotationX,
                RotationY = clientMsg.RotationY,
                RotationZ = clientMsg.RotationZ,
                RotationW = clientMsg.RotationW,
            };

            NetDataWriter writer = NetworkServer.RentWriter();
            outMsg.Serialize(writer);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.CameraPIPStateChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Client sends a position update for their PIP camera.
        /// </summary>
        public static void HandlePIPPositionUpdate(NetPacketReader reader, NetPeer peer)
        {
            ClientCameraPIPPositionMessage clientMsg = new ClientCameraPIPPositionMessage();
            clientMsg.Deserialize(reader);
            reader.Recycle();

            if (!PIPStates.TryGetValue(peer.Id, out var state) || !state.IsActive)
            {
                return; // ignore position updates for non-existent PIPs
            }

            state.PositionX = clientMsg.PositionX;
            state.PositionY = clientMsg.PositionY;
            state.PositionZ = clientMsg.PositionZ;
            state.RotationX = clientMsg.RotationX;
            state.RotationY = clientMsg.RotationY;
            state.RotationZ = clientMsg.RotationZ;
            state.RotationW = clientMsg.RotationW;
            state.DataTicks = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Called from the reduction system tick loop. Sends PIP position updates
        /// to recipients using the same distance-based interval as avatar movement.
        /// </summary>
        public static void UpdatePIPPositions(long nowTicks)
        {
            NetPeer[] peers = NetworkServer.PeerSnapshot;
            if (peers == null || PIPStates.IsEmpty) return;

            // Cameras are independent (each owns its LastSentTimes), so the sweep fans out
            // across the reduction system's tuned worker budget instead of running
            // O(cameras x peers) serially on the tick thread. A single camera stays inline —
            // parallel dispatch would cost more than the sweep.
            if (PIPStates.Count == 1)
            {
                foreach (var pipKvp in PIPStates)
                    SweepCamera(pipKvp.Key, pipKvp.Value, peers, nowTicks);
                return;
            }

            Parallel.ForEach(PIPStates, BasisServerReductionSystemEvents.SharedParallelOptions,
                pipKvp => SweepCamera(pipKvp.Key, pipKvp.Value, peers, nowTicks));
        }

        private static void SweepCamera(int ownerId, CameraPIPState pipState, NetPeer[] peers, long nowTicks)
        {
            long dataTicks = pipState.DataTicks;
            if (!pipState.IsActive || dataTicks == 0)
                return;

            // Get the PIP owner's player position for distance calc
            if (!BasisServerReductionSystemEvents.playerStates.TryGetValue(ownerId, out PlayerState ownerPlayerState))
                return;

            if (!ownerPlayerState.IsActive)
                return;

            // Build the outbound message once
            CameraPIPPositionMessage posMsg = new CameraPIPPositionMessage
            {
                PlayerID = (ushort)ownerId,
                PositionX = pipState.PositionX,
                PositionY = pipState.PositionY,
                PositionZ = pipState.PositionZ,
                RotationX = pipState.RotationX,
                RotationY = pipState.RotationY,
                RotationZ = pipState.RotationZ,
                RotationW = pipState.RotationW,
            };

            NetDataWriter writer = NetworkServer.RentWriter();
            posMsg.Serialize(writer);

            for (int i = 0; i < peers.Length; i++)
            {
                NetPeer recipientPeer = peers[i];
                int recipientId = recipientPeer.Id;

                if (recipientId == ownerId)
                    continue;

                if (!pipState.LastSentTimes.TryGetValue(recipientId, out long lastSent))
                    lastSent = 0;

                // Already holds the latest position — nothing to send until new data lands.
                if (lastSent >= dataTicks)
                    continue;

                if (!BasisServerReductionSystemEvents.playerStates.TryGetValue(recipientId, out PlayerState recipientState))
                    continue;

                if (!recipientState.IsActive)
                    continue;

                // Distance between recipient and PIP owner
                float distSq = DistanceSquared(recipientState.Position, ownerPlayerState.Position);
                CalculateIntervalFromDistanceSq(distSq, out int actualInterval);

                long elapsed = Math.Max(0, nowTicks - lastSent);
                long required = (long)(actualInterval * MsToTick);

                if (elapsed >= required)
                {
                    recipientPeer.Send(writer, BasisNetworkCommons.CameraPIPPositionChannel, DeliveryMethod.Sequenced);
                    BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.CameraPIPPositionChannel, writer.Length);
                    pipState.LastSentTimes[recipientId] = nowTicks;
                }
            }

            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Send all active PIP camera states to a newly joined peer.
        /// </summary>
        public static void SendPIPStateToPeer(NetPeer newPeer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();

            foreach (var kvp in PIPStates)
            {
                CameraPIPState state = kvp.Value;
                if (!state.IsActive)
                    continue;

                writer.Reset();
                CameraPIPStateMessage msg = new CameraPIPStateMessage
                {
                    PlayerID = (ushort)kvp.Key,
                    IsActive = true,
                    PositionX = state.PositionX,
                    PositionY = state.PositionY,
                    PositionZ = state.PositionZ,
                    RotationX = state.RotationX,
                    RotationY = state.RotationY,
                    RotationZ = state.RotationZ,
                    RotationW = state.RotationW,
                };
                msg.Serialize(writer);
                NetworkServer.TrySend(newPeer, writer, BasisNetworkCommons.CameraPIPStateChannel, DeliveryMethod.ReliableOrdered);
            }

            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// On disconnect: if this player had an active PIP, broadcast destroy to all.
        /// </summary>
        public static void RemovePlayer(int peerId)
        {
            if (PIPStates.TryRemove(peerId, out var state) && state.IsActive)
            {
                CameraPIPStateMessage destroyMsg = new CameraPIPStateMessage
                {
                    PlayerID = (ushort)peerId,
                    IsActive = false,
                };

                NetDataWriter writer = NetworkServer.RentWriter();
                destroyMsg.Serialize(writer);
                NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.CameraPIPStateChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
                NetworkServer.ReturnWriter(writer);

                BNL.Log($"PIP camera auto-destroyed for disconnected player {peerId}");
            }

            foreach (var kvp in PIPStates)
            {
                kvp.Value.LastSentTimes.TryRemove(peerId, out _);
            }
        }

        public static void Reset()
        {
            PIPStates.Clear();
        }

        private static float DistanceSquared(Basis.Scripts.Networking.Compression.Vector3 a, Basis.Scripts.Networking.Compression.Vector3 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            float dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static void CalculateIntervalFromDistanceSq(float distanceSq, out int actualInterval)
        {
            int rawInterval = (int)(BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval *
                (BasisServerReductionSystemEvents.BSRBaseMultiplier + (distanceSq * BasisServerReductionSystemEvents.BSRSIncreaseRate)));
            int encodedInterval = rawInterval - BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval;
            byte offsetByte = (byte)Math.Clamp(encodedInterval, 0, byte.MaxValue);
            actualInterval = offsetByte + BasisServerReductionSystemEvents.BSRSMillisecondDefaultInterval;
        }
    }
}
