using Basis.Network.Core;
using LiteNetLib;
using System;
using static SerializableBasis;

namespace Basis.Network.Server.Generic
{
    public static class BasisSavedState
    {
        // Chunked arrays for each type of data
        private static readonly ChunkedSyncedToPlayerPulseArray<LocalAvatarSyncMessage> avatarSyncStates = new ChunkedSyncedToPlayerPulseArray<LocalAvatarSyncMessage>(64);
        private static readonly ChunkedSyncedToPlayerPulseArray<ClientAvatarChangeMessage> avatarChangeStates = new ChunkedSyncedToPlayerPulseArray<ClientAvatarChangeMessage>(64);
        private static readonly ChunkedSyncedToPlayerPulseArray<ClientMetaDataMessage> playerMetaDataMessages = new ChunkedSyncedToPlayerPulseArray<ClientMetaDataMessage>(64);
        private static readonly ChunkedSyncedToPlayerPulseArray<VoiceReceiversMessage> voiceReceiversMessages = new ChunkedSyncedToPlayerPulseArray<VoiceReceiversMessage>(64);

        /// <summary>
        /// Removes all state data for a specific player.
        /// </summary>
        public static void RemovePlayer(NetPeer client)
        {
            int id = client.Id;
            avatarSyncStates.SetPulse(id, default);
            avatarChangeStates.SetPulse(id, default);
            playerMetaDataMessages.SetPulse(id, default);
            voiceReceiversMessages.SetPulse(id, default);
        }

        /// <summary>
        /// Adds or updates the LocalAvatarSyncMessage for a player.
        /// </summary>
        public static void AddLastData(NetPeer client, LocalAvatarSyncMessage avatarSyncMessage)
        {
            avatarSyncStates.SetPulse(client.Id, avatarSyncMessage);
        }

        /// <summary>
        /// Adds or updates the ReadyMessage for a player.
        /// </summary>
        public static void AddLastData(NetPeer client, ReadyMessage readyMessage)
        {
            int id = client.Id;
            avatarSyncStates.SetPulse(id, readyMessage.localAvatarSyncMessage);
            avatarChangeStates.SetPulse(id, readyMessage.clientAvatarChangeMessage);
            playerMetaDataMessages.SetPulse(id, readyMessage.playerMetaDataMessage);

            BNL.Log($"Updated {id} with AvatarID {readyMessage.clientAvatarChangeMessage.byteArray.Length}");
        }

        /// <summary>
        /// Adds or updates the VoiceReceiversMessage for a player.
        /// </summary>
        public static void AddLastData(NetPeer client, VoiceReceiversMessage voiceReceiversMessage)
        {
            voiceReceiversMessages.SetPulse(client.Id, voiceReceiversMessage);
        }

        /// <summary>
        /// Adds or updates the ClientAvatarChangeMessage for a player.
        /// </summary>
        public static void AddLastData(NetPeer client, ClientAvatarChangeMessage avatarChangeMessage)
        {
            avatarChangeStates.SetPulse(client.Id, avatarChangeMessage);
        }

        /// <summary>
        /// Retrieves the last LocalAvatarSyncMessage for a player.
        /// </summary>
        public static bool GetLastAvatarSyncState(NetPeer client, out LocalAvatarSyncMessage message)
        {
            message = avatarSyncStates.GetPulse(client.Id);
            return !message.Equals(default);
        }

        /// <summary>
        /// Retrieves the last ClientAvatarChangeMessage for a player.
        /// </summary>
        public static bool GetLastAvatarChangeState(NetPeer client, out ClientAvatarChangeMessage message)
        {
            message = avatarChangeStates.GetPulse(client.Id);
            return !message.Equals(default);
        }

        /// <summary>
        /// Retrieves the last PlayerMetaDataMessage for a player.
        /// </summary>
        public static bool GetLastPlayerMetaData(NetPeer client, out ClientMetaDataMessage message)
        {
            message = playerMetaDataMessages.GetPulse(client.Id);
            return !message.Equals(default);
        }

        /// <summary>
        /// Retrieves the last VoiceReceiversMessage for a player.
        /// </summary>
        public static bool GetLastVoiceReceivers(NetPeer client, out VoiceReceiversMessage message)
        {
            message = voiceReceiversMessages.GetPulse(client.Id);
            return !message.Equals(default);
        }
    }

    // Generic implementation of ChunkedSyncedToPlayerPulseArray
    public class ChunkedSyncedToPlayerPulseArray<T> where T : struct
    {
        private readonly object[] _chunkLocks;
        private readonly T[][] _chunks;
        private readonly int _chunkSize;
        private readonly int _numChunks;
        public ChunkedSyncedToPlayerPulseArray(int chunkSize = 256)
        {
            if (BasisNetworkCommons.MaxConnections <= 0)
                throw new ArgumentOutOfRangeException(nameof(BasisNetworkCommons.MaxConnections), "Total size must be greater than zero.");
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be greater than zero.");

            _chunkSize = chunkSize;
            _numChunks = (int)Math.Ceiling((double)BasisNetworkCommons.MaxConnections / chunkSize);

            _chunks = new T[_numChunks][];
            _chunkLocks = new object[_numChunks];

            for (int i = 0; i < _numChunks; i++)
            {
                _chunks[i] = new T[chunkSize];
                _chunkLocks[i] = new object();
            }
        }

        public void SetPulse(int index, T pulse)
        {
            if (index < 0 || index >= BasisNetworkCommons.MaxConnections)
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

            int chunkIndex = index / _chunkSize;
            int localIndex = index % _chunkSize;

            lock (_chunkLocks[chunkIndex])
            {
                _chunks[chunkIndex][localIndex] = pulse;
            }
        }

        public T GetPulse(int index)
        {
            if (index < 0 || index >= BasisNetworkCommons.MaxConnections)
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

            int chunkIndex = index / _chunkSize;
            int localIndex = index % _chunkSize;

            lock (_chunkLocks[chunkIndex])
            {
                return _chunks[chunkIndex][localIndex];
            }
        }
    }

}
