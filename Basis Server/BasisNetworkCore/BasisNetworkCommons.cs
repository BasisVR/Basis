namespace Basis.Network.Core
{
    public static class BasisNetworkCommons
    {
        /// <summary>
        /// this is the maximum connectinos that can occur under the hood.
        /// </summary>
        public const int MaxConnections = 1024;

        public const int NetworkIntervalPoll = 15;
        public const int PingInterval = 1500;
        public const int ReceivePollingTime = 50000;
        public const int PacketPoolSize = 700;
        /// <summary>
        /// when adding a new message we need to increase this
        /// will function up to 64
        /// </summary>
        public const byte TotalChannels = 24;
        /// <summary>
        /// channel zero is only used for unreliable methods
        /// we fall it through to stop bugs
        /// </summary>
        public const byte FallChannel = 0;
        /// <summary>
        /// Auth Identity Message
        /// </summary>
        public const byte AuthIdentityChannel = 1;
        /// <summary>
        /// this is normally avatar movement
        /// </summary>
        public const byte PlayerAvatarChannel = 2;
        /// <summary>
        /// this is what people use voice data only can be used once!
        /// </summary>
        public const byte VoiceChannel = 3;
        /// <summary>
        /// this is what people use to send data on the scene network
        /// </summary>
        public const byte SceneChannel = 4;
        /// <summary>
        /// this is what people use to send data on there avatar
        /// </summary>
        public const byte AvatarChannel = 5;
        /// <summary>
        /// Message to create a remote player entity
        /// </summary>
        public const byte CreateRemotePlayerChannel = 6;
        /// <summary>
        /// Message to create a remote player entity
        /// </summary>
        public const byte CreateRemotePlayersForNewPeerChannel = 7;
        /// <summary>
        /// message to swap to a different avatar
        /// </summary>
        public const byte AvatarChangeMessageChannel = 8;
        /// <summary>
        /// Ownership Response is when we get the current owner
        /// </summary>
        public const byte GetCurrentOwnerRequestChannel = 9;
        /// <summary>
        /// changes current owner of a string
        /// </summary>
        public const byte ChangeCurrentOwnerRequestChannel = 10;
        /// <summary>
        /// Remove Current Ownership
        /// </summary>
        public const byte RemoveCurrentOwnerRequestChannel = 11;
        /// <summary>
        /// the audio recipients that can here
        /// </summary>
        public const byte AudioRecipientsChannel = 12;
        /// <summary>
        /// Removes a players entity
        /// </summary>
        public const byte DisconnectionChannel = 13;
        /// <summary>
        /// assign a net id (string to ushort)
        /// </summary>
        public const byte netIDAssignChannel = 14;
        /// <summary>
        /// assign a array of net id (string to ushort)
        /// </summary>
        public const byte NetIDAssignsChannel = 15;
        /// <summary>
        /// load a resource (scene,gameobject,script,asset) whatever the implementation is
        /// </summary>
        public const byte LoadResourceChannel = 16;
        /// <summary>
        /// Unload a Resource
        /// </summary>
        public const byte UnloadResourceChannel = 17;
        /// <summary>
        /// Client sends a admin message and the server needs to respond accordingly
        /// </summary>
        public const byte AdminChannel = 18;
        /// <summary>
        /// Avatar Request Channel
        /// </summary>
        public const byte AvatarCloneRequestChannel = 19;
        /// <summary>
        /// Avatar Response Channel
        /// </summary>
        public const byte AvatarCloneResponseChannel = 20;
        /// <summary>
        /// requires implementation from a developer,
        /// ground work for hooking in code that only gets delivered to the server
        /// </summary>
        public const byte ServerBoundChannel = 21;
        /// <summary>
        /// this contains all meta data that the player requires
        /// </summary>
        public const byte metaDataChannel = 22;
    }
}
