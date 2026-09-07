using Basis.Network.Core;
using BasisPermissions;
using BasisNetworkServer.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisNetworkServer.BasisNetworking
{
    /// <summary>
    /// Server-side handler for chat messages. Deserializes incoming chat,
    /// applies word filtering, and broadcasts to all other authenticated peers.
    /// </summary>
    public static class BasisNetworkChat
    {
        private static readonly HashSet<string> BlockedWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string[] _blockedWordsArray = Array.Empty<string>();
        private static readonly string WordFilterFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName, "chat_word_filter.txt");

        /// <summary>
        /// Every accepted message is an N-1 reliable fan-out, so cap the send rate well above
        /// human typing (burst absorbs pastes and quick corrections) and drop the rest silently.
        /// </summary>
        private static readonly BasisPeerRateLimiter MessageLimiter = new BasisPeerRateLimiter(tokensPerSecond: 2f, tokenBurst: 6f);

        /// <summary>
        /// Typing state is edge-triggered by well-behaved clients; anything sustained beyond a
        /// couple of transitions per second is a broadcast storm, not a person typing.
        /// </summary>
        public static readonly BasisPeerRateLimiter TypingLimiter = new BasisPeerRateLimiter(tokensPerSecond: 2f, tokenBurst: 8f);

        /// <summary>
        /// Loads the word filter list from disk. Each line in the file is a blocked word/phrase.
        /// Creates an empty file if none exists.
        /// </summary>
        public static void LoadWordFilter(Configuration Configuration)
        {
            if (Configuration.HasFileSupport)
            {
                try
                {
                    if (!File.Exists(WordFilterFilePath))
                    {
                        // Create empty filter file with instructions
                        string configDir = Path.GetDirectoryName(WordFilterFilePath);
                        if (!Directory.Exists(configDir))
                        {
                            Directory.CreateDirectory(configDir);
                        }
                        File.WriteAllText(WordFilterFilePath,
                            "# Chat word filter - one word or phrase per line\n" +
                            "# Lines starting with # are comments\n" +
                            "# Words are case-insensitive\n" +
                            "fuck\n" +
                            "fucking\n" +
                            "fucker\n" +
                            "fucked\n" +
                            "motherfucker\n" +
                            "shit\n" +
                            "shitting\n" +
                            "bullshit\n" +
                            "bitch\n" +
                            "bitches\n" +
                            "ass\n" +
                            "asshole\n" +
                            "bastard\n" +
                            "damn\n" +
                            "damned\n" +
                            "cunt\n" +
                            "dick\n" +
                            "dickhead\n" +
                            "cock\n" +
                            "cocksucker\n" +
                            "pussy\n" +
                            "whore\n" +
                            "slut\n" +
                            "piss\n" +
                            "pissed\n" +
                            "crap\n" +
                            "wanker\n" +
                            "twat\n" +
                            "prick\n" +
                            "douche\n" +
                            "douchebag\n" +
                            "# Slurs\n" +
                            "nigger\n" +
                            "nigga\n" +
                            "faggot\n" +
                            "fag\n" +
                            "retard\n" +
                            "retarded\n" +
                            "tranny\n" +
                            "kike\n" +
                            "spic\n" +
                            "chink\n" +
                            "gook\n" +
                            "wetback\n" +
                            "beaner\n" +
                            "coon\n" +
                            "dyke\n" +
                            "# Threats / harassment\n" +
                            "kill yourself\n" +
                            "kys\n" +
                            "neck yourself\n" +
                            "go die\n" +
                            "rape\n" +
                            "raping\n" +
                            "rapist\n");
                        BNL.Log("Created default chat word filter file: " + WordFilterFilePath);
                        // Reload so the defaults are active immediately
                        LoadWordFilter(Configuration);
                        return;
                    }

                    BlockedWords.Clear();
                    string[] lines = File.ReadAllLines(WordFilterFilePath);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                        {
                            BlockedWords.Add(trimmed);
                        }
                    }
                    _blockedWordsArray = new string[BlockedWords.Count];
                    BlockedWords.CopyTo(_blockedWordsArray);
                    BNL.Log($"Loaded {BlockedWords.Count} words into chat filter (using homoglyph + trigram detection)");
                }
                catch (Exception ex)
                {
                    BNL.LogError($"Failed to load chat word filter: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Applies the word filter to a message, replacing blocked words with asterisks.
        /// Uses homoglyph detection (Unicode lookalike characters) and trigram-based
        /// false positive prevention to catch evasion attempts while avoiding
        /// incorrect matches (e.g. won't match "ass" in "assignment").
        /// </summary>
        public static string FilterMessage(string message)
        {
            if (_blockedWordsArray.Length == 0 || string.IsNullOrEmpty(message))
            {
                return message;
            }

            return BasisWordFilter.Filter(message, _blockedWordsArray);
        }

        /// <summary>
        /// True when this peer may not send text chat: the global text-chat lock is on and they
        /// lack basis.chat.lockbypass, or a moderator text-muted them. Shared by the chat and
        /// typing-state paths so a blocked peer can't leak "is typing" activity while their
        /// messages are being dropped.
        /// </summary>
        public static bool IsChatBlockedFor(NetPeer peer)
        {
            if (BasisPlayerMuteManager.IsTextMutedFor(peer)) return true;
            return BasisGlobalLockManager.TextChatLocked &&
                !PermissionIntegration.HasValidRequirement(peer, PermNodes.ChatLockBypass);
        }

        /// <summary>
        /// UUID-keyed form of <see cref="IsChatBlockedFor(NetPeer)"/>, mirroring the two
        /// HasValidRequirement overloads. Lets callers that already resolved the UUID skip the
        /// peer lookup.
        /// </summary>
        public static bool IsChatBlockedForUuid(string uuid)
        {
            if (BasisPlayerMuteManager.IsTextMuted(uuid)) return true;
            return BasisGlobalLockManager.TextChatLocked &&
                !PermissionIntegration.HasValidRequirement(uuid, PermNodes.ChatLockBypass);
        }

        /// <summary>
        /// Handles an incoming chat message from a client peer.
        /// Deserializes, filters, re-serializes, and broadcasts to all other peers.
        /// </summary>
        public static void HandleChatMessage(NetPacketReader reader, NetPeer sender)
        {
            if (!MessageLimiter.TryConsume(sender))
            {
                reader.Recycle();
                return;
            }

            if (IsChatBlockedFor(sender))
            {
                // Dropped silently: clients already grey out their composer from the broadcast lock
                // state, so anything arriving here is an old or modified client. Chat can arrive far
                // faster than content shares, so neither a per-message reply nor a log line is safe —
                // both would hand a blocked peer a cheap amplification vector.
                reader.Recycle();
                return;
            }

            ChatMessage chatMessage = new ChatMessage();
            chatMessage.Deserialize(reader);
            reader.Recycle();

            // Decode, filter, re-encode
            if (chatMessage.payload != null && chatMessage.payloadSize > 0)
            {
                string text = Encoding.UTF8.GetString(chatMessage.payload, 0, chatMessage.payloadSize);

                // Apply word filter
                text = FilterMessage(text);
                text = BasisChatSanitizer.Sanitize(text);

                byte[] filtered = Encoding.UTF8.GetBytes(text);
                chatMessage.payload = filtered;
                chatMessage.payloadSize = (ushort)filtered.Length;
            }

            // Wrap with sender ID
            ServerChatMessage serverChatMessage = new ServerChatMessage
            {
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)sender.Id
                },
                chatMessage = chatMessage
            };

            // Serialize and broadcast to all except sender
            NetDataWriter writer = NetworkServer.RentWriter();
            serverChatMessage.Serialize(writer);
            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.ChatChannel, sender, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
