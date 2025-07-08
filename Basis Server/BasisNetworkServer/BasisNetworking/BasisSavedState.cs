using LiteNetLib;
using static SerializableBasis;

namespace Basis.Network.Server.Generic
{
    public static class BasisSavedState
    {
        // Chunked arrays for each type of data
        private static readonly ChunkedSyncedToPlayerPulseArray<LocalAvatarSyncMessage> avatarSyncStates = new ChunkedSyncedToPlayerPulseArray<LocalAvatarSyncMessage>();
        private static readonly ChunkedSyncedToPlayerPulseArray<ClientAvatarChangeMessage> avatarChangeStates = new ChunkedSyncedToPlayerPulseArray<ClientAvatarChangeMessage>();
        private static readonly ChunkedSyncedToPlayerPulseArray<ClientMetaDataMessage> playerMetaDataMessages = new ChunkedSyncedToPlayerPulseArray<ClientMetaDataMessage>();
        private static readonly ChunkedSyncedToPlayerPulseArray<VoiceReceiversMessage> voiceReceiversMessages = new ChunkedSyncedToPlayerPulseArray<VoiceReceiversMessage>();

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
}
