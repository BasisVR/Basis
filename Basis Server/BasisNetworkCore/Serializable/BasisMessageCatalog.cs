using Basis.Network.Core;
using System.Collections.Generic;

public static partial class SerializableBasis
{
    /// <summary>
    /// The canonical set of core message descriptors (channels 0-60). The server combines
    /// this with any registered plugin descriptors to build the manifest it supplies to each
    /// client on connect, so a client can be told what every message index means without a
    /// shared compiled constant table. Core ids equal their dedicated channel.
    /// </summary>
    public static class BasisMessageCatalog
    {
        /// <summary>Schema version of the core message set. Bump when a core payload layout changes.</summary>
        public const byte CoreVersion = 1;

        // Core never changes at runtime; build the descriptor array once and share it (read-only).
        private static volatile BasisMessageDescriptor[] _core;

        public static BasisMessageDescriptor[] BuildCore()
        {
            BasisMessageDescriptor[] cached = _core;
            if (cached != null)
            {
                return cached;
            }

            List<BasisMessageDescriptor> list = new List<BasisMessageDescriptor>(64);

            void Add(byte channel, string name)
            {
                list.Add(new BasisMessageDescriptor
                {
                    Id = channel,
                    Version = CoreVersion,
                    Channel = channel,
                    Flags = (byte)BasisMessageFlags.None,
                    Name = name,
                });
            }

            Add(BasisNetworkCommons.AuthIdentityChannel, "basis.core.auth.identity");
            Add(BasisNetworkCommons.metaDataChannel, "basis.core.metadata");
            Add(BasisNetworkCommons.DisconnectionChannel, "basis.core.disconnection");
            Add(BasisNetworkCommons.VoiceChannel, "basis.core.voice");
            Add(BasisNetworkCommons.ShoutVoiceChannel, "basis.core.voice.shout");
            Add(BasisNetworkCommons.AudioRecipientsChannel, "basis.core.voice.recipients");
            Add(BasisNetworkCommons.PlayerAvatarVeryLowChannel, "basis.core.avatar.verylow");
            Add(BasisNetworkCommons.PlayerAvatarVeryLowAdditionalChannel, "basis.core.avatar.verylow.additional");
            Add(BasisNetworkCommons.PlayerAvatarLowChannel, "basis.core.avatar.low");
            Add(BasisNetworkCommons.PlayerAvatarLowAdditionalChannel, "basis.core.avatar.low.additional");
            Add(BasisNetworkCommons.PlayerAvatarMediumChannel, "basis.core.avatar.medium");
            Add(BasisNetworkCommons.PlayerAvatarMediumAdditionalChannel, "basis.core.avatar.medium.additional");
            Add(BasisNetworkCommons.PlayerAvatarHighChannel, "basis.core.avatar.high");
            Add(BasisNetworkCommons.PlayerAvatarHighAdditionalChannel, "basis.core.avatar.high.additional");
            Add(BasisNetworkCommons.AvatarChangeMessageChannel, "basis.core.avatar.change");
            Add(BasisNetworkCommons.AvatarChannel, "basis.core.avatar.data");
            Add(BasisNetworkCommons.CreateRemotePlayerChannel, "basis.core.player.create");
            Add(BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, "basis.core.player.create.bulk");
            Add(BasisNetworkCommons.ChatChannel, "basis.core.chat");
            Add(BasisNetworkCommons.GetCurrentOwnerRequestChannel, "basis.core.ownership.get");
            Add(BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, "basis.core.ownership.change");
            Add(BasisNetworkCommons.RemoveCurrentOwnerRequestChannel, "basis.core.ownership.remove");
            Add(BasisNetworkCommons.netIDAssignChannel, "basis.core.netid.assign");
            Add(BasisNetworkCommons.NetIDAssignsChannel, "basis.core.netid.assigns");
            Add(BasisNetworkCommons.SceneChannel, "basis.core.scene.data");
            Add(BasisNetworkCommons.LoadResourceChannel, "basis.core.resource.load");
            Add(BasisNetworkCommons.UnloadResourceChannel, "basis.core.resource.unload");
            Add(BasisNetworkCommons.PreloadReadyChannel, "basis.core.resource.preloadready");
            Add(BasisNetworkCommons.SpawnPreloadedChannel, "basis.core.resource.spawnpreloaded");
            Add(BasisNetworkCommons.ContentShareChannel, "basis.core.contentshare");
            Add(BasisNetworkCommons.DeltaAvatarChannel, "basis.core.avatar.delta");
            Add(BasisNetworkCommons.ServerBoundChannel, "basis.core.serverbound");
            Add(BasisNetworkCommons.StoreDatabaseChannel, "basis.core.database.store");
            Add(BasisNetworkCommons.RequestStoreDatabaseChannel, "basis.core.database.request");
            Add(BasisNetworkCommons.AdminChannel, "basis.core.admin");
            Add(BasisNetworkCommons.ServerStatisticsChannel, "basis.core.statistics");
            Add(BasisNetworkCommons.CameraPIPStateChannel, "basis.core.camera.pip.state");
            Add(BasisNetworkCommons.CameraPIPPositionChannel, "basis.core.camera.pip.position");
            Add(BasisNetworkCommons.EventsChannel, "basis.core.events");
            Add(BasisNetworkCommons.AudioRecipientsLargeChannel, "basis.core.voice.recipients.large");
            Add(BasisNetworkCommons.VoiceLargeChannel, "basis.core.voice.large");
            Add(BasisNetworkCommons.PlayerAvatarVeryLowLargeChannel, "basis.core.avatar.verylow.large");
            Add(BasisNetworkCommons.PlayerAvatarVeryLowAdditionalLargeChannel, "basis.core.avatar.verylow.additional.large");
            Add(BasisNetworkCommons.PlayerAvatarLowLargeChannel, "basis.core.avatar.low.large");
            Add(BasisNetworkCommons.PlayerAvatarLowAdditionalLargeChannel, "basis.core.avatar.low.additional.large");
            Add(BasisNetworkCommons.PlayerAvatarMediumLargeChannel, "basis.core.avatar.medium.large");
            Add(BasisNetworkCommons.PlayerAvatarMediumAdditionalLargeChannel, "basis.core.avatar.medium.additional.large");
            Add(BasisNetworkCommons.PlayerAvatarHighLargeChannel, "basis.core.avatar.high.large");
            Add(BasisNetworkCommons.PlayerAvatarHighAdditionalLargeChannel, "basis.core.avatar.high.additional.large");
            Add(BasisNetworkCommons.AudioRecipientsInvertedChannel, "basis.core.voice.recipients.inverted");
            Add(BasisNetworkCommons.AudioRecipientsInvertedLargeChannel, "basis.core.voice.recipients.inverted.large");
            Add(BasisNetworkCommons.AudioRecipientsBitfieldChannel, "basis.core.voice.recipients.bitfield");
            Add(BasisNetworkCommons.CompressedAvatarBundleChannel, "basis.core.avatar.bundle.compressed");
            Add(BasisNetworkCommons.ServerLibraryChannel, "basis.core.library");
            Add(BasisNetworkCommons.P2PChannel, "basis.core.p2p");
            Add(BasisNetworkCommons.ModifyResourceChannel, "basis.core.resource.modify");
            Add(BasisNetworkCommons.DirectSceneChannel, "basis.core.scene.direct");
            Add(BasisNetworkCommons.DirectSceneServerChannel, "basis.core.scene.direct.server");
            Add(BasisNetworkCommons.DirectAvatarChannel, "basis.core.avatar.direct");
            Add(BasisNetworkCommons.DirectAvatarServerChannel, "basis.core.avatar.direct.server");
            Add(BasisNetworkCommons.RegistryControlChannel, "basis.core.registry.control");

            _core = list.ToArray();
            return _core;
        }
    }
}
