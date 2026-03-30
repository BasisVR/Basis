using System;
using System.Collections.Concurrent;
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
        private static readonly ConcurrentDictionary<int, VoiceReceiversMessage> voiceReceiversMessages = new();
        private static readonly ConcurrentDictionary<int, bool> shoutModeStates = new();

        /// <summary>
        /// Removes all state data for a specific player.
        /// </summary>
        public static void RemovePlayer(int id)
        {
            avatarChangeStates.TryRemove(id, out _);
            playerMetaDataMessages.TryRemove(id, out _);
            voiceReceiversMessages.TryRemove(id, out _);
            shoutModeStates.TryRemove(id, out _);
        }

        public static void Reset()
        {
            avatarChangeStates.Clear();
            playerMetaDataMessages.Clear();
            voiceReceiversMessages.Clear();
            shoutModeStates.Clear();
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
        /// Adds or updates the VoiceReceiversMessage for a player.
        /// </summary>
        public static void AddLastData(NetPeer client, VoiceReceiversMessage voiceReceiversMessage)
        {
            voiceReceiversMessages[client.Id] = voiceReceiversMessage;

        }

        /// <summary>
        /// Adds or updates the ClientAvatarChangeMessage for a player.
        /// </summary>
        public static void AddLastData(NetPeer client, ClientAvatarChangeMessage avatarChangeMessage)
        {
            avatarChangeStates[client.Id] = avatarChangeMessage;
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
        /// Retrieves the last VoiceReceiversMessage for a player.
        /// </summary>
        public static bool GetLastVoiceReceivers(NetPeer client, out VoiceReceiversMessage message)
        {
            return voiceReceiversMessages.TryGetValue(client.Id, out message);
        }

        /// <summary>
        /// Sets shout mode state for a player.
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
        /// Returns true if the player is currently in shout mode.
        /// </summary>
        public static bool IsInShoutMode(int peerId)
        {
            return shoutModeStates.TryGetValue(peerId, out _);
        }

        /// <summary>
        /// Returns all player IDs currently in shout mode.
        /// </summary>
        public static int[] GetAllShoutModePlayers()
        {
            return shoutModeStates.Keys.ToArray();
        }
    }
}
