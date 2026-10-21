namespace BasisPermissions
{
    // Lives in BasisNetworkCore (not BasisNetworkServer) because the client checks these
    // node names too; keeping them here lets client code use them without the server assembly.
    // =========================
    // Permission Node Constants
    // =========================
    public static class PermNodes
    {
        public const string All = "*";
        public const string help = "basis.command.help";
        public const string ServerStats = "basis.server.stats";

        public const string ResourceLoadWorld = "basis.resource.load.world";
        public const string ResourceUnloadWorld = "basis.resource.unload.world";

        public const string ResourceLoadProp = "basis.resource.load.prop";
        public const string ResourceUnloadProp = "basis.resource.unload.prop";

        public const string ResourceLoadAvatar = "basis.resource.load.avatar";
        public const string ResourceUnloadAvatar = "basis.resource.unload.avatar";

        // Bypass the global lockouts (BasisGlobalLockManager). Users without
        // the matching bypass node are blocked from loading while the lock is on.
        public const string ResourceLockBypassAvatar = "basis.resource.lockbypass.avatar";
        public const string ResourceLockBypassProp = "basis.resource.lockbypass.prop";
        public const string ResourceLockBypassWorld = "basis.resource.lockbypass.world";
        /// <summary>Bypass <c>ServersLocked</c> when initiating a server share.</summary>
        public const string ResourceLockBypassServer = "basis.resource.lockbypass.server";
        /// <summary>Bypass <c>TextChatLocked</c> — keep sending text chat while the global chat lock is on.</summary>
        public const string ChatLockBypass = "basis.chat.lockbypass";
        /// <summary>Bypass <c>VoiceChatLocked</c> — keep transmitting voice while the global voice lock is on.</summary>
        public const string VoiceLockBypass = "basis.voice.lockbypass";

        public const string OwnershipTransfer = "basis.ownership.transfer";
        public const string OwnershipRemove = "basis.ownership.remove";
        public const string OwnershipGet = "basis.ownership.get";

        public const string ContentShareDelete = "basis.contentshare.delete";
        public const string ContentShareCreate = "basis.contentshare.create";

        public const string StorageUse = "basis.storage.use";

        /// <summary>
        /// used to indicate that this persons actions are protected from interference
        /// </summary>
        public const string protection = "basis.protection";

        public const string ConfigurationEditor = "basis.configuration";

        public const string PlayerModeration = "basis.moderation";

        public const string ModerationBan = "basis.moderation.ban";
        public const string ModerationKick = "basis.moderation.kick";
        public const string ModerationIpBan = "basis.moderation.ipban";
        public const string ModerationUnban = "basis.moderation.unban";
        public const string ModerationUnbanIp = "basis.moderation.unbanip";
        public const string ModerationMessage = "basis.moderation.message";
        public const string ModerationMessageAll = "basis.moderation.messageall";
        public const string ModerationTeleport = "basis.moderation.teleport";
        public const string ModerationAnnounce = "basis.moderation.announce";
        public const string ModerationGlobalLock = "basis.moderation.globallock";
        public const string ModerationHeadlessAudio = "basis.moderation.headlessaudio";
        public const string ModerationOpusBitrate = "basis.moderation.opusbitrate";
        public const string ModerationFullQualityBroadcast = "basis.moderation.fullqualitybroadcast";
        /// <summary>Push a specific avatar onto another player.</summary>
        public const string ModerationForceAvatar = "basis.moderation.forceavatar";
        /// <summary>Override another player's jump height, movement speeds, gravity and character controller mode.</summary>
        public const string ModerationLocomotion = "basis.moderation.locomotion";
        /// <summary>Mute/unmute another player's voice or text chat server-wide.</summary>
        public const string ModerationMute = "basis.moderation.mute";
        public const string ModerationRename = "basis.moderation.rename";
        /// <summary>Add/remove UUIDs on the server's allow-list (separate from ban management).</summary>
        public const string ModerationAllowlist = "basis.moderation.whitelist";
        public const string AdminLogs = "basis.admin.logs";

        public const string PermissionsView = "basis.permissions.view";
        public const string PermissionsEdit = "basis.permissions.edit";
    }
}
