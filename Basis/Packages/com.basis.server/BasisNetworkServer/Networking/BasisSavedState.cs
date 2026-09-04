using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Basis.Network.Core;
using static SerializableBasis;

namespace Basis.Network.Server.Generic
{
    public static class BasisSavedState
    {
        // Thread-safe dictionaries for each type of data
        private static readonly ConcurrentDictionary<int, ClientAvatarChangeMessage> avatarChangeStates = new();
        private static readonly ConcurrentDictionary<int, ClientMetaDataMessage> playerMetaDataMessages = new();
        private static readonly ConcurrentDictionary<int, List<NetPeer>> resolvedVoicePeers = new();
        private static readonly ConcurrentDictionary<int, bool> announceModeStates = new();
        private static readonly ConcurrentDictionary<int, bool> shoutModeStates = new();

        /// <summary>
        /// Removes all state data for a specific player and purges them
        /// from every other player's cached voice-peer list.
        /// </summary>
        public static void RemovePlayer(int id)
        {
            avatarChangeStates.TryRemove(id, out _);
            playerMetaDataMessages.TryRemove(id, out _);
            resolvedVoicePeers.TryRemove(id, out _);
            announceModeStates.TryRemove(id, out _);
            shoutModeStates.TryRemove(id, out _);

            // Purge the disconnected peer from all other players' cached lists
            // so voice packets aren't sent to a dead peer until the next recipient update.
            foreach (var kvp in resolvedVoicePeers)
            {
                List<NetPeer> peers = kvp.Value;
                if (peers == null) continue;

                lock (peers)
                {
                    for (int i = peers.Count - 1; i >= 0; i--)
                    {
                        NetPeer p = peers[i];
                        if (p != null && p.Id == id)
                        {
                            peers.RemoveAt(i);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Adds or updates the ReadyMessage for a player.
        /// </summary>
        public static void AddLastData(NetPeer client, ReadyMessage readyMessage)
        {
            int id = client.Id;
            avatarChangeStates[id] = readyMessage.clientAvatarChangeMessage;
            playerMetaDataMessages[id] = readyMessage.playerMetaDataMessage;

          // BNL.Log($"Updated {id} with AvatarID {readyMessage.clientAvatarChangeMessage.byteArray.Length}");
        }

        /// <summary>
        /// Resolves a VoiceReceiversMessage into cached NetPeer list.
        /// </summary>
        public static void AddLastData(NetPeer client, VoiceReceiversMessage voiceReceiversMessage)
        {
            var peers = GetOrCreateResolvedList(client.Id);

            if (voiceReceiversMessage.Users != null)
            {
                lock (peers)
                {
                    peers.Clear();
                    for (int i = 0; i < voiceReceiversMessage.UsersLength; i++)
                    {
                        if (NetworkServer.AuthenticatedPeers.TryGetValue(voiceReceiversMessage.Users[i], out NetPeer found))
                        {
                            peers.Add(found);
                        }
                    }
                }
                voiceReceiversMessage.ReturnPool();
            }
        }

        /// <summary>
        /// Adds or updates the ClientAvatarChangeMessage for a player.
        /// </summary>
        public static void AddLastData(NetPeer client, ClientAvatarChangeMessage avatarChangeMessage)
        {
            avatarChangeStates[client.Id] = avatarChangeMessage;
        }

        /// <summary>
        /// Merges a body-fit update into a player's saved avatar record, leaving the avatar itself
        /// untouched. Stored on the same record so the late-join replay carries the wearer's current
        /// proportions; if the fit lands before any avatar change (a recalibration during load), it is
        /// held on a byteArray-less placeholder that a later avatar change fills in.
        /// </summary>
        public static void UpdateBodyFit(NetPeer client, ClientBodyFitMessage bodyFit)
        {
            avatarChangeStates.AddOrUpdate(
                client.Id,
                _ => new ClientAvatarChangeMessage
                {
                    loadMode = 0,
                    byteArray = null,
                    LocalAvatarIndex = 0,
                    ArmScale = bodyFit.ArmScale,
                    LegScale = bodyFit.LegScale,
                    TorsoScale = bodyFit.TorsoScale,
                },
                (_, existing) =>
                {
                    existing.ArmScale = bodyFit.ArmScale;
                    existing.LegScale = bodyFit.LegScale;
                    existing.TorsoScale = bodyFit.TorsoScale;
                    return existing;
                });
        }

        /// <summary>
        /// Retrieves the last ClientAvatarChangeMessage for a player.
        /// </summary>
        public static bool GetLastAvatarChangeState(NetPeer client, out ClientAvatarChangeMessage message)
        {
            return avatarChangeStates.TryGetValue(client.Id, out message);
        }

        /// <summary>
        /// Retrieves the last PlayerMetaDataMessage for a player.
        /// </summary>
        public static bool GetLastPlayerMetaData(NetPeer client, out ClientMetaDataMessage message)
        {
            return playerMetaDataMessages.TryGetValue(client.Id, out message);
        }

        /// <summary>
        /// Retrieves the cached resolved peer list for a player's voice receivers.
        /// This list is rebuilt each time the voice receivers message is updated, not per voice packet.
        /// </summary>
        public static bool GetResolvedVoicePeers(NetPeer client, out List<NetPeer> peers)
        {
            return resolvedVoicePeers.TryGetValue(client.Id, out peers);
        }

        /// <summary>
        /// Directly sets the resolved voice peer list for a player.
        /// Used by inverted-list and bitfield modes which resolve peers during deserialization
        /// rather than storing a ushort[] first.
        /// </summary>
        public static List<NetPeer> GetOrCreateResolvedList(int clientId)
        {
            return resolvedVoicePeers.GetOrAdd(clientId, _ => new List<NetPeer>(64));
        }

        /// <summary>
        /// Sets announce mode state for a player.
        /// </summary>
        public static void SetAnnounceMode(int peerId, bool enabled)
        {
            if (enabled)
            {
                announceModeStates[peerId] = true;
            }
            else
            {
                announceModeStates.TryRemove(peerId, out _);
            }
        }

        /// <summary>
        /// Returns true if the player is currently in announce mode.
        /// </summary>
        public static bool IsInAnnounceMode(int peerId)
        {
            return announceModeStates.TryGetValue(peerId, out _);
        }

        /// <summary>
        /// Returns all player IDs currently in announce mode.
        /// </summary>
        public static int[] GetAllAnnounceModePlayers()
        {
            return announceModeStates.Keys.ToArray();
        }

        /// <summary>
        /// Sets admin-granted shout mode state for a player.
        /// </summary>
        public static void SetShoutMode(int peerId, bool enabled)
        {
            if (enabled)
            {
                shoutModeStates[peerId] = true;
            }
            else
            {
                shoutModeStates.TryRemove(peerId, out _);
            }
        }

        /// <summary>
        /// Returns true if an admin currently has the player in shout mode.
        /// </summary>
        public static bool IsInShoutMode(int peerId)
        {
            return shoutModeStates.TryGetValue(peerId, out _);
        }

        /// <summary>
        /// Returns all player IDs an admin currently has in shout mode.
        /// </summary>
        public static int[] GetAllShoutModePlayers()
        {
            return shoutModeStates.Keys.ToArray();
        }
    }
}
