using Basis.Network.Core;

namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct AdminRequest
        {
            private byte messageIndex;
            public AdminRequestMode GetAdminRequestMode()
            {
                return (AdminRequestMode)messageIndex;
            }
            public void Deserialize(NetDataReader reader)
            {
                int bytesAvailable = reader.AvailableBytes;
                if (bytesAvailable > 0)
                {
                    messageIndex = reader.GetByte();
                }
                else
                {
                    BNL.LogError($"Unable to read remaining bytes, available: {bytesAvailable}");
                }
            }

            public void Serialize(NetDataWriter writer, AdminRequestMode AdminRequestMode)
            {
                messageIndex = (byte)AdminRequestMode;
                writer.Put(messageIndex);
            }
        }
        public enum AdminRequestMode : byte
        {
            Ban,//bans a player
            Kick,//kicks a player
            IpAndBan,// bans and ip bans a player
            Message,// sends a message to a user
            MessageAll,// sends a message to all users
            UnBanIP,// unbans a user and unbans a associated ip
            UnBan,// unbans a user
          //  RequestBannedPlayers,// gets a list of banned players
           // TeleportTo,// teleport to a player
            TeleportAll,// teleports everyone
            TeleportPlayer,

            // Permission management (any user can request, only admins can modify)
            GetPermissions,     // request full permission snapshot (read-only for non-admins)
            SetUserGroup,       // admin: add/remove user from a group
            SetUserNode,        // admin: add/remove permission node from a user
            SetGroupNode,       // admin: add/remove permission node from a group
            CreateGroup,        // admin: create a new permission group
            DeleteGroup,        // admin: delete a permission group
            SetGroupParent,     // admin: add/remove a parent group from a group

            EnableShoutMode,    // admin: enable shout mode for a player (non-spatialized broadcast voice)
            DisableShoutMode,   // admin: disable shout mode for a player

            GlobalToggleAvatars, // admin: toggle global avatar loading lock
            GlobalToggleProps,   // admin: toggle global prop loading lock
            GlobalToggleWorlds,  // admin: toggle global world loading lock
            GlobalGetLockState,  // server→client: current global lock state
            GlobalToggleHeadlessAudio, // admin: toggle headless audio clip playback for headless clients
            GlobalGetHeadlessAudioState, // server→client: current global headless audio state
            SetGlobalHeadlessAudio, // admin: explicitly set headless audio clip playback state for headless clients
        }
    }
}
