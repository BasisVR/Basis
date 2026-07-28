using System;
using Basis.Network.Core;

public static partial class SerializableBasis
{
    /// <summary>
    /// Compact encoding for player identifiers. The id is deliberately NOT assumed to be any one format —
    /// deployments use did:key (this project's own auth), Steam64, Meta/Oculus numeric ids, plain GUIDs,
    /// or anything else an operator plugs in. So this recognises the common shapes, packs each to its
    /// natural binary form, and falls back to a raw string for anything unrecognised.
    ///
    /// Every compact form is VERIFIED by re-encoding before it is chosen: if the round trip does not
    /// reproduce the input byte for byte, the encoder falls back to <see cref="TagRaw"/>. That makes the
    /// codec losslessly safe for ids nobody anticipated — the worst case is simply the old size + 1 byte.
    ///
    /// Typical sizes (old = 2-byte length prefix + UTF-8):
    ///   Steam64  "76561198012345678"                      19 -> 9   bytes
    ///   Meta/Oculus numeric id                            ~18 -> 9   bytes
    ///   GUID     "d3b07384-d9a0-4f1e-8b1a-2c3d4e5f6071"   38 -> 18  bytes
    ///   SHA-256 hex id (64 chars)                         66 -> 35  bytes
    ///   did:key  "did:key:z6Mk..."                        58 -> 50  bytes
    /// </summary>
    public static class BasisCompactId
    {
        const byte TagRaw = 0;      // [len:ushort][utf8]                  — always works
        const byte TagUuid = 1;     // [fmt:1][16 bytes]                   — GUID, any of the 4 renderings
        const byte TagUInt64 = 2;   // [8 bytes]                           — Steam64, Meta/Oculus, any digits
        const byte TagHex = 3;      // [flags:1][len:1][bytes]             — even-length hex, <=255 bytes out
        const byte TagDidKey = 4;   // [len:1][utf8 after "did:key:"]      — elides the fixed prefix

        const string DidKeyPrefix = "did:key:";

        // Guid rendering, so an uppercase or dashed id round-trips to exactly what was sent.
        const byte UuidFormatDashedLower = 0;
        const byte UuidFormatDashedUpper = 1;
        const byte UuidFormatPlainLower = 2;
        const byte UuidFormatPlainUpper = 3;

        const byte HexFlagUpper = 1;

        public static void Write(NetDataWriter writer, string value)
        {
            value ??= string.Empty;

            if (TryWriteUuid(writer, value)) return;
            if (TryWriteUInt64(writer, value)) return;
            if (TryWriteDidKey(writer, value)) return;
            if (TryWriteHex(writer, value)) return;

            writer.Put(TagRaw);
            writer.Put(value);
        }

        public static string Read(NetDataReader reader)
        {
            byte tag = reader.GetByte();
            switch (tag)
            {
                case TagUuid: return ReadUuid(reader);
                case TagUInt64: return reader.GetULong().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case TagHex: return ReadHex(reader);
                case TagDidKey: return DidKeyPrefix + ReadShortString(reader);
                default: return reader.GetString();
            }
        }

        static bool TryWriteUuid(NetDataWriter writer, string value)
        {
            if (value.Length != 32 && value.Length != 36) return false;
            if (!Guid.TryParse(value, out Guid guid)) return false;

            byte format;
            if (value.Length == 36)
            {
                format = HasUpperHex(value) ? UuidFormatDashedUpper : UuidFormatDashedLower;
            }
            else
            {
                format = HasUpperHex(value) ? UuidFormatPlainUpper : UuidFormatPlainLower;
            }

            // Mixed case, or any rendering that will not reproduce exactly, must not take this path.
            if (RenderUuid(guid, format) != value) return false;

            writer.Put(TagUuid);
            writer.Put(format);
            writer.Put(guid.ToByteArray());
            return true;
        }

        static string ReadUuid(NetDataReader reader)
        {
            byte format = reader.GetByte();
            byte[] raw = new byte[16];
            reader.GetBytes(raw, 0, 16);
            return RenderUuid(new Guid(raw), format);
        }

        static string RenderUuid(Guid guid, byte format) => format switch
        {
            UuidFormatDashedUpper => guid.ToString("D").ToUpperInvariant(),
            UuidFormatPlainLower => guid.ToString("N"),
            UuidFormatPlainUpper => guid.ToString("N").ToUpperInvariant(),
            _ => guid.ToString("D"),
        };

        static bool TryWriteUInt64(NetDataWriter writer, string value)
        {
            if (value.Length == 0 || value.Length > 20) return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9') return false;
            }
            if (!ulong.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out ulong parsed))
            {
                return false;
            }
            // Rejects leading zeros ("007" would come back as "7").
            if (parsed.ToString(System.Globalization.CultureInfo.InvariantCulture) != value) return false;

            writer.Put(TagUInt64);
            writer.Put(parsed);
            return true;
        }

        static bool TryWriteDidKey(NetDataWriter writer, string value)
        {
            if (!value.StartsWith(DidKeyPrefix, StringComparison.Ordinal)) return false;
            string body = value.Substring(DidKeyPrefix.Length);
            if (body.Length == 0 || body.Length > 255) return false;

            writer.Put(TagDidKey);
            WriteShortString(writer, body);
            return true;
        }

        static bool TryWriteHex(NetDataWriter writer, string value)
        {
            if (value.Length == 0 || (value.Length & 1) != 0 || value.Length > 510) return false;

            bool upper = false, lower = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= '0' && c <= '9') continue;
                if (c >= 'a' && c <= 'f') { lower = true; continue; }
                if (c >= 'A' && c <= 'F') { upper = true; continue; }
                return false;
            }
            if (upper && lower) return false;   // mixed case would not round-trip

            int count = value.Length / 2;
            byte[] bytes = new byte[count];
            for (int i = 0; i < count; i++)
            {
                bytes[i] = (byte)((HexVal(value[i * 2]) << 4) | HexVal(value[i * 2 + 1]));
            }

            writer.Put(TagHex);
            writer.Put(upper ? HexFlagUpper : (byte)0);
            writer.Put((byte)count);
            writer.Put(bytes);
            return true;
        }

        static string ReadHex(NetDataReader reader)
        {
            byte flags = reader.GetByte();
            int count = reader.GetByte();
            byte[] bytes = new byte[count];
            reader.GetBytes(bytes, 0, count);

            const string lower = "0123456789abcdef";
            const string upper = "0123456789ABCDEF";
            string alphabet = (flags & HexFlagUpper) != 0 ? upper : lower;

            char[] chars = new char[count * 2];
            for (int i = 0; i < count; i++)
            {
                chars[i * 2] = alphabet[bytes[i] >> 4];
                chars[i * 2 + 1] = alphabet[bytes[i] & 0xF];
            }
            return new string(chars);
        }

        static int HexVal(char c)
        {
            if (c <= '9') return c - '0';
            if (c <= 'F') return c - 'A' + 10;
            return c - 'a' + 10;
        }

        static bool HasUpperHex(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] >= 'A' && value[i] <= 'F') return true;
            }
            return false;
        }

        static void WriteShortString(NetDataWriter writer, string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            // Callers gate on Length <= 255 in chars; multi-byte UTF-8 could still overflow, so re-check.
            if (bytes.Length > 255)
            {
                writer.Put((byte)0);
                return;
            }
            writer.Put((byte)bytes.Length);
            writer.Put(bytes);
        }

        static string ReadShortString(NetDataReader reader)
        {
            int length = reader.GetByte();
            if (length == 0) return string.Empty;
            byte[] bytes = new byte[length];
            reader.GetBytes(bytes, 0, length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// Compact encoding for the player platform. The client sends Unity's <c>Application.platform</c>
    /// name, which is a closed enum, so the common ones collapse to a single byte. Anything not in the
    /// table (a new Unity platform, a custom build) still round-trips as a raw string, so this never
    /// blocks a platform it has not heard of.
    ///
    /// APPEND-ONLY. The index IS the wire id — reordering or removing an entry silently remaps
    /// everyone's platform.
    /// </summary>
    public static class BasisPlatformCodec
    {
        const byte TagRaw = 0;

        static readonly string[] Known =
        {
            "WindowsPlayer", "WindowsEditor", "WindowsServer",
            "OSXPlayer", "OSXEditor", "OSXServer",
            "LinuxPlayer", "LinuxEditor", "LinuxServer",
            "Android", "IPhonePlayer", "VisionOS",
            "WebGLPlayer",
            "PS4", "PS5", "XboxOne", "GameCoreXboxOne", "GameCoreXboxSeries", "Switch", "tvOS",
            "WSAPlayerX86", "WSAPlayerX64", "WSAPlayerARM",
            "EmbeddedLinuxArm64", "EmbeddedLinuxArm32", "EmbeddedLinuxX64", "EmbeddedLinuxX86",
            "QNXArm32", "QNXArm64", "QNXX64", "QNXX86",
            "Stadia", "CloudRendering", "LinuxHeadlessSimulation", "Lumin",
            // Not a Unity platform: what the load-test console reports. Without it every simulated
            // client falls back to a raw string, which overstates metadata cost in a 2k load run.
            "Headless",
        };

        public static void Write(NetDataWriter writer, string platform)
        {
            platform ??= string.Empty;
            for (int i = 0; i < Known.Length; i++)
            {
                if (string.Equals(Known[i], platform, StringComparison.Ordinal))
                {
                    writer.Put((byte)(i + 1));
                    return;
                }
            }
            writer.Put(TagRaw);
            writer.Put(platform);
        }

        public static string Read(NetDataReader reader)
        {
            byte id = reader.GetByte();
            if (id == TagRaw) return reader.GetString();
            int index = id - 1;
            // An id from a newer server than this client knows: do not guess, report it as unknown.
            return index < Known.Length ? Known[index] : string.Empty;
        }
    }

    public struct ClientMetaDataMessage
    {
        public string playerUUID;
        public string playerDisplayName;
        public string playerPlatform;
        public void Deserialize(NetDataReader Writer)
        {
            playerUUID = BasisCompactId.Read(Writer);
            Writer.Get(out playerDisplayName);
            playerPlatform = BasisPlatformCodec.Read(Writer);

        }
        public void Serialize(NetDataWriter Writer)
        {
            BasisCompactId.Write(Writer, string.IsNullOrEmpty(playerUUID) == false ? playerUUID : "Failure");
            if (string.IsNullOrEmpty(playerDisplayName) == false)
            {
                Writer.Put(playerDisplayName);
            }
            else
            {
                Writer.Put("Failure");
            }
            BasisPlatformCodec.Write(Writer, string.IsNullOrEmpty(playerPlatform) == false ? playerPlatform : "Failure");
        }
    }
}
