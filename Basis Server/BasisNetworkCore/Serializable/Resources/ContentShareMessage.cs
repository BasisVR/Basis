using Basis.Network.Core;
using System;
using System.Globalization;

public static partial class SerializableBasis
{
    /// <summary>
    /// Content types that can be shared via a content sphere.
    /// </summary>
    public enum ContentShareType : byte
    {
        Avatar = 0,
        Prop = 1,
        World = 2,
        /// <summary>
        /// A saved-server entry. ContentURL carries the connection string
        /// (address[:port][#password]); UnlockPassword is unused for this type.
        /// Receivers don't spawn an in-world orb — they get a confirmation
        /// dialog asking whether to add the server to their saved list.
        /// </summary>
        Server = 3,
        /// <summary>
        /// A saved dolly track. ContentURL carries the preset itself as JSON rather
        /// than a bundle address, so there is nothing to fetch and nothing to host:
        /// a track is a few dozen points and rides inside the share message.
        /// UnlockPassword is unused. Receivers are offered it for their saved list.
        /// </summary>
        DollyTrack = 4,
        Link = 5,
        Text = 6,
        Folder = 7,
        Setting = 8,
    }

    /// <summary>
    /// Content types whose <see cref="ContentShareMessage.ContentURL"/> holds the shared thing
    /// itself instead of an address to fetch it from.
    /// </summary>
    public static class ContentSharePayload
    {
        /// <summary>
        /// Ceiling on an inline payload, enforced by both the server and the receiving client.
        /// A full 32-point dolly track serialises to about 5 KB; the rest is headroom for a
        /// format that grows, and a bound on what one message can cost everybody in the room.
        /// </summary>
        public const int MaxLength = 16384;
        public const int MaxTextLength = 4096;
        public const int MaxSphereIdLength = 64;
        public const int MaxPasswordLength = 1024;

        public static bool IsPayloadType(ContentShareType type) => type == ContentShareType.DollyTrack || type == ContentShareType.Folder || type == ContentShareType.Setting || IsTextType(type);
        public static bool IsTextType(ContentShareType type) => type == ContentShareType.Link || type == ContentShareType.Text;
        public static int MaxLengthFor(ContentShareType type) => IsTextType(type) || type == ContentShareType.Setting ? MaxTextLength : MaxLength;

        /// <summary>
        /// Whether <paramref name="value"/> is one plain web address and nothing else. The server
        /// runs this before relaying a link and every client runs it again before showing or
        /// opening one, so a modified client cannot talk anybody past it. Beyond http/https only,
        /// it refuses the shapes people misread: a name ahead of an @ that looks like the site
        /// (https://trusted.example@evil.example goes to evil.example), backslashes browsers turn
        /// into slashes, quotes and angle brackets, invisible formatting characters, numbers
        /// dressed up as site names, and hosts that are not a plain name or address.
        /// </summary>
        public static bool TryParseLink(string value, out Uri link)
        {
            link = null;
            if (string.IsNullOrEmpty(value) || value.Length > MaxTextLength) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\' || c == '"' || c == '<' || c == '>') return false;
                if (char.IsSurrogate(c))
                {
                    // Judged as the whole character: tag characters (U+E0000 block) are invisible
                    // formatting that only shows up as a pair.
                    if (!char.IsSurrogatePair(value, i) || CharUnicodeInfo.GetUnicodeCategory(value, i) == UnicodeCategory.Format) return false;
                    i++;
                    continue;
                }
                // CharUnicodeInfo, not char.GetUnicodeCategory: Mono's Latin-1 shortcut files the
                // invisible soft hyphen under dashes, so client and server would disagree.
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) return false;
            }
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
            if (!IsPlainHost(parsed)) return false;

            // The browser parses the address again. Re-reading our own canonical form has to land
            // on the same site, or what the confirmation names is not where the browser goes.
            if (!Uri.TryCreate(parsed.AbsoluteUri, UriKind.Absolute, out Uri reparsed)) return false;
            if (reparsed.Scheme != parsed.Scheme || reparsed.Port != parsed.Port || !string.IsNullOrEmpty(reparsed.UserInfo)) return false;
            if (!TryGetIdnHost(reparsed, out string reparsedHost) || !TryGetIdnHost(parsed, out string parsedHost) || !string.Equals(reparsedHost, parsedHost, StringComparison.OrdinalIgnoreCase)) return false;
            link = parsed;
            return true;
        }

        private static bool TryGetIdnHost(Uri link, out string host)
        {
            try
            {
                host = link.IdnHost;
                return true;
            }
            catch (FormatException)
            {
            }
            catch (ArgumentException)
            {
            }
            host = null;
            return false;
        }

        private static bool IsPlainHost(Uri link)
        {
            switch (link.HostNameType)
            {
                case UriHostNameType.IPv4:
                    return true;
                case UriHostNameType.IPv6:
                    // A zone id (fe80::1%eth0) names a network card on the reader's own machine.
                    // Host drops it silently; IdnHost keeps it.
                    return TryGetIdnHost(link, out string address) && address.IndexOf('%') < 0 && link.Host.IndexOf('%') < 0;
                case UriHostNameType.Dns:
                    if (!TryGetIdnHost(link, out string host) || string.IsNullOrEmpty(host) || host.Length > 254 || host[0] == '.' || host.Contains("..")) return false;
                    for (int i = 0; i < host.Length; i++)
                    {
                        char c = host[i];
                        bool plain = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
                        if (!plain) return false;
                    }
                    // Browsers read a host whose last label is a number as an IPv4 address, so
                    // 2130706433 and 0x7f.1 both mean 127.0.0.1. Real sites never end in a number;
                    // anything this parser did not already turn into an address is refused.
                    return !EndsInNumber(host);
                default:
                    return false;
            }
        }

        private static bool EndsInNumber(string host)
        {
            string trimmed = host.EndsWith(".") ? host.Substring(0, host.Length - 1) : host;
            int dot = trimmed.LastIndexOf('.');
            string last = dot >= 0 ? trimmed.Substring(dot + 1) : trimmed;
            if (last.Length == 0) return false;
            bool hex = last.Length >= 2 && last[0] == '0' && (last[1] == 'x' || last[1] == 'X');
            for (int i = hex ? 2 : 0; i < last.Length; i++)
            {
                char c = last[i];
                bool digit = c >= '0' && c <= '9';
                bool hexDigit = hex && ((c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
                if (!digit && !hexDigit) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Sent by a client to drop a content share sphere into the world.
    /// Contains everything another player needs to load the content.
    /// </summary>
    public struct ContentShareMessage
    {
        /// <summary>
        /// Unique ID for this sphere instance (GUID string).
        /// </summary>
        public string SphereNetID;

        /// <summary>
        /// The URL to the content bundle.
        /// </summary>
        public string ContentURL;

        /// <summary>
        /// Password to unlock/decrypt the content bundle.
        /// </summary>
        public string UnlockPassword;

        /// <summary>
        /// What kind of content this sphere represents.
        /// </summary>
        public ContentShareType ContentType;

        /// <summary>
        /// World position where the sphere was dropped.
        /// </summary>
        public float PositionX;
        public float PositionY;
        public float PositionZ;

        public void Deserialize(NetDataReader reader)
        {
            SphereNetID = reader.GetString();
            ContentURL = reader.GetString();
            UnlockPassword = reader.GetString();
            ContentType = (ContentShareType)reader.GetByte();
            PositionX = reader.GetFloat();
            PositionY = reader.GetFloat();
            PositionZ = reader.GetFloat();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SphereNetID);
            writer.Put(ContentURL);
            writer.Put(UnlockPassword);
            writer.Put((byte)ContentType);
            writer.Put(PositionX);
            writer.Put(PositionY);
            writer.Put(PositionZ);
        }
    }

    /// <summary>
    /// Server wraps the client's ContentShareMessage with the sender's player ID
    /// and authoritative identity (UUID + display name) resolved from saved state.
    /// </summary>
    public struct ServerContentShareMessage
    {
        public PlayerIdMessage playerIdMessage;
        public string SharerUUID;
        public string SharerDisplayName;
        public ContentShareMessage contentShareMessage;
        public bool SharerProtected;

        public void Deserialize(NetDataReader reader)
        {
            playerIdMessage.Deserialize(reader);
            SharerUUID = reader.GetString();
            SharerDisplayName = reader.GetString();
            contentShareMessage.Deserialize(reader);
            SharerProtected = reader.AvailableBytes > 0 && reader.GetBool();
        }

        public void Serialize(NetDataWriter writer)
        {
            playerIdMessage.Serialize(writer);
            writer.Put(SharerUUID ?? string.Empty);
            writer.Put(SharerDisplayName ?? string.Empty);
            contentShareMessage.Serialize(writer);
            writer.Put(SharerProtected);
        }
    }

    /// <summary>
    /// Sent to remove a content share sphere from the world.
    /// </summary>
    public struct ContentShareCleanupMessage
    {
        public string SphereNetID;

        public void Deserialize(NetDataReader reader)
        {
            SphereNetID = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SphereNetID);
        }
    }

    /// <summary>
    /// Server wraps cleanup with sender player ID.
    /// </summary>
    public struct ServerContentShareCleanupMessage
    {
        public PlayerIdMessage playerIdMessage;
        public ContentShareCleanupMessage contentShareCleanupMessage;

        public void Deserialize(NetDataReader reader)
        {
            playerIdMessage.Deserialize(reader);
            contentShareCleanupMessage.Deserialize(reader);
        }

        public void Serialize(NetDataWriter writer)
        {
            playerIdMessage.Serialize(writer);
            contentShareCleanupMessage.Serialize(writer);
        }
    }
}
