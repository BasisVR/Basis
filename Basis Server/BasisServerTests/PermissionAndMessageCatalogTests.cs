using System.Text;
using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

internal static class WireTestHelpers
{
    public static NetDataReader Rewind(NetDataWriter writer)
        => new(writer.AsReadOnlySpan().ToArray());

    public static NetDataReader Truncated(NetDataWriter writer, int size)
        => new(writer.AsReadOnlySpan().ToArray(), 0, size);
}

/// <summary>
/// Pins the permission-node → bit-index table in PermissionBitsetMap. The map is append-only
/// wire format: any reorder/removal must break these tests.
/// </summary>
public class PermissionBitsetMapTests
{
    // Hand-written pin table. Must mirror PermissionBitsetMap.IndexToNode exactly.
    internal static readonly string[] KnownNodes =
    {
        "*",                              // 0
        "basis.server.stats",             // 1
        "basis.resource.load.world",      // 2
        "basis.resource.unload.world",    // 3
        "basis.resource.load.prop",       // 4
        "basis.resource.unload.prop",     // 5
        "basis.resource.load.avatar",     // 6
        "basis.resource.unload.avatar",   // 7
        "basis.ownership.transfer",       // 8
        "basis.ownership.remove",         // 9
        "basis.ownership.get",            // 10
        "basis.contentshare.delete",      // 11
        "basis.contentshare.create",      // 12
        "basis.protection",               // 13
        "basis.configuration",            // 14
        "basis.moderation",               // 15
        "basis.moderation.ban",           // 16
        "basis.moderation.kick",          // 17
        "basis.moderation.ipban",         // 18
        "basis.moderation.unban",         // 19
        "basis.moderation.unbanip",       // 20
        "basis.moderation.message",       // 21
        "basis.moderation.messageall",    // 22
        "basis.moderation.teleport",      // 23
        "basis.moderation.shout",         // 24
        "basis.permissions.view",         // 25
        "basis.permissions.edit",         // 26
        "basis.moderation.headlessaudio", // 27
    };

    public static TheoryData<int, string> AllPermissionBits()
    {
        TheoryData<int, string> data = new();
        for (int i = 0; i < KnownNodes.Length; i++)
        {
            data.Add(i, KnownNodes[i]);
        }
        return data;
    }

    public static TheoryData<int, string> NonWildcardPermissionBits()
    {
        TheoryData<int, string> data = new();
        for (int i = 1; i < KnownNodes.Length; i++)
        {
            data.Add(i, KnownNodes[i]);
        }
        return data;
    }

    private static byte[] SingleBit(int index)
    {
        byte[] bitset = new byte[PermissionBitsetMap.ByteCount];
        bitset[index >> 3] |= (byte)(1 << (index & 7));
        return bitset;
    }

    [Fact]
    public void KnownCountAndByteCount_ArePinned()
    {
        Assert.Equal(28, PermissionBitsetMap.KnownCount);
        Assert.Equal(4, PermissionBitsetMap.ByteCount);
        Assert.Equal(KnownNodes.Length, PermissionBitsetMap.KnownCount);
    }

    [Theory]
    [MemberData(nameof(NonWildcardPermissionBits))]
    public void EncodingSingleNode_SetsExactlyItsPinnedBit(int index, string node)
    {
        PermissionBitsetMap.Encode(new[] { node }, out byte[] bitset, out string[] extras);
        Assert.Empty(extras);
        Assert.Equal(SingleBit(index), bitset);
    }

    [Theory]
    [MemberData(nameof(AllPermissionBits))]
    public void DecodingSingleBit_YieldsExactlyItsPinnedNode(int index, string node)
    {
        HashSet<string> decoded = PermissionBitsetMap.Decode(SingleBit(index), Array.Empty<string>());
        string single = Assert.Single(decoded);
        Assert.Equal(node, single);
    }

    [Fact]
    public void Wildcard_SetsEveryKnownBit_AndDecodesToFullSet()
    {
        PermissionBitsetMap.Encode(new[] { "*" }, out byte[] bitset, out string[] extras);
        Assert.Empty(extras);
        Assert.Equal(PermissionBitsetMap.ByteCount, bitset.Length);
        for (int i = 0; i < PermissionBitsetMap.KnownCount; i++)
        {
            Assert.True((bitset[i >> 3] & (1 << (i & 7))) != 0, $"bit {i} should be set by wildcard");
        }
        // Bits 28..31 of the last byte stay clear.
        Assert.Equal(0, bitset[3] & 0xF0);

        HashSet<string> decoded = PermissionBitsetMap.Decode(bitset, extras);
        Assert.Equal(KnownNodes.Length, decoded.Count);
        foreach (string node in KnownNodes)
        {
            Assert.Contains(node, decoded);
        }
    }

    [Fact]
    public void AllKnownNodesExplicitly_RoundTripToFullSet()
    {
        PermissionBitsetMap.Encode(KnownNodes, out byte[] bitset, out string[] extras);
        Assert.Empty(extras);
        HashSet<string> decoded = PermissionBitsetMap.Decode(bitset, extras);
        Assert.Equal(KnownNodes.Length, decoded.Count);
    }

    [Fact]
    public void EmptyAllowedList_ProducesAllClearBitset_AndEmptyDecode()
    {
        PermissionBitsetMap.Encode(Array.Empty<string>(), out byte[] bitset, out string[] extras);
        Assert.Equal(new byte[PermissionBitsetMap.ByteCount], bitset);
        Assert.Empty(extras);
        Assert.Empty(PermissionBitsetMap.Decode(bitset, extras));
    }

    [Fact]
    public void EachNonWildcardNode_RoundTripsAlone()
    {
        for (int i = 1; i < KnownNodes.Length; i++)
        {
            PermissionBitsetMap.Encode(new[] { KnownNodes[i] }, out byte[] bitset, out string[] extras);
            HashSet<string> decoded = PermissionBitsetMap.Decode(bitset, extras);
            Assert.Equal(KnownNodes[i], Assert.Single(decoded));
        }
    }

    [Fact]
    public void ExhaustiveSubsets_OfBits1Through12_RoundTripExactly()
    {
        const int Width = 12;
        for (int mask = 0; mask < 1 << Width; mask++)
        {
            List<string> allowed = new();
            byte[] expected = new byte[PermissionBitsetMap.ByteCount];
            for (int bit = 0; bit < Width; bit++)
            {
                if ((mask & (1 << bit)) == 0)
                {
                    continue;
                }
                int index = bit + 1; // skip the wildcard slot
                allowed.Add(KnownNodes[index]);
                expected[index >> 3] |= (byte)(1 << (index & 7));
            }

            PermissionBitsetMap.Encode(allowed, out byte[] bitset, out string[] extras);
            Assert.Empty(extras);
            Assert.Equal(expected, bitset);

            HashSet<string> decoded = PermissionBitsetMap.Decode(bitset, extras);
            Assert.Equal(allowed.Count, decoded.Count);
            foreach (string node in allowed)
            {
                Assert.Contains(node, decoded);
            }
        }
    }

    [Fact]
    public void SeededRandomSubsets_OfAllNonWildcardNodes_RoundTrip()
    {
        Random rng = new(4242);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            List<string> allowed = new();
            for (int index = 1; index < KnownNodes.Length; index++)
            {
                if (rng.Next(2) == 0)
                {
                    allowed.Add(KnownNodes[index]);
                }
            }

            PermissionBitsetMap.Encode(allowed, out byte[] bitset, out string[] extras);
            Assert.Empty(extras);
            HashSet<string> decoded = PermissionBitsetMap.Decode(bitset, extras);
            Assert.Equal(allowed.Count, decoded.Count);
            foreach (string node in allowed)
            {
                Assert.Contains(node, decoded);
            }
        }
    }

    [Fact]
    public void UnknownNodes_BecomeExtras_InInputOrder()
    {
        string[] allowed =
        {
            "basis.moderation",
            "com.acme.custom.a",
            "basis.protection",
            "com.acme.custom.b",
        };
        PermissionBitsetMap.Encode(allowed, out byte[] bitset, out string[] extras);

        Assert.Equal(new[] { "com.acme.custom.a", "com.acme.custom.b" }, extras);

        byte[] expected = new byte[PermissionBitsetMap.ByteCount];
        expected[15 >> 3] |= 1 << (15 & 7); // basis.moderation
        expected[13 >> 3] |= 1 << (13 & 7); // basis.protection
        Assert.Equal(expected, bitset);

        HashSet<string> decoded = PermissionBitsetMap.Decode(bitset, extras);
        Assert.Equal(4, decoded.Count);
        foreach (string node in allowed)
        {
            Assert.Contains(node, decoded);
        }
    }

    [Fact]
    public void KnownNodeLookup_IsCaseInsensitive_AndDecodesCanonicalCasing()
    {
        PermissionBitsetMap.Encode(new[] { "BASIS.MODERATION.KICK" }, out byte[] bitset, out string[] extras);
        Assert.Empty(extras);
        Assert.Equal(SingleBit(17), bitset);
        Assert.Equal("basis.moderation.kick", Assert.Single(PermissionBitsetMap.Decode(bitset, extras)));
    }

    [Fact]
    public void DeniedNodes_ClearTheirBits_FromWildcardGrant()
    {
        PermissionBitsetMap.Encode(
            new[] { "*" },
            out byte[] bitset,
            out string[] extras,
            new[] { "basis.moderation.ban", "basis.moderation.kick" });

        Assert.Empty(extras);
        Assert.Equal(0, bitset[16 >> 3] & (1 << (16 & 7)));
        Assert.Equal(0, bitset[17 >> 3] & (1 << (17 & 7)));

        HashSet<string> decoded = PermissionBitsetMap.Decode(bitset, extras);
        Assert.Equal(KnownNodes.Length - 2, decoded.Count);
        Assert.DoesNotContain("basis.moderation.ban", decoded);
        Assert.DoesNotContain("basis.moderation.kick", decoded);
    }

    [Fact]
    public void DeniedUnknownNodes_AreIgnored()
    {
        PermissionBitsetMap.Encode(
            new[] { "basis.moderation" },
            out byte[] bitset,
            out string[] extras,
            new[] { "com.unknown.node" });

        Assert.Empty(extras);
        Assert.Equal(SingleBit(15), bitset);
    }

    [Fact]
    public void DenyingWildcard_OnlyClearsBitZero()
    {
        PermissionBitsetMap.Encode(new[] { "*" }, out byte[] bitset, out string[] extras, new[] { "*" });
        Assert.Empty(extras);
        Assert.Equal(0, bitset[0] & 1);

        HashSet<string> decoded = PermissionBitsetMap.Decode(bitset, extras);
        Assert.Equal(KnownNodes.Length - 1, decoded.Count);
        Assert.DoesNotContain("*", decoded);
    }

    [Fact]
    public void Decode_IgnoresBitsAboveKnownCount()
    {
        byte[] allOnesExact = { 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.Equal(KnownNodes.Length, PermissionBitsetMap.Decode(allOnesExact, Array.Empty<string>()).Count);

        byte[] allOnesOversized = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.Equal(KnownNodes.Length, PermissionBitsetMap.Decode(allOnesOversized, Array.Empty<string>()).Count);
    }

    [Fact]
    public void Decode_ToleratesShortAndNullInputs()
    {
        byte[] oneByte = { 0b1000_0001 };
        HashSet<string> decoded = PermissionBitsetMap.Decode(oneByte, Array.Empty<string>());
        Assert.Equal(2, decoded.Count);
        Assert.Contains(KnownNodes[0], decoded);
        Assert.Contains(KnownNodes[7], decoded);

        Assert.Empty(PermissionBitsetMap.Decode(null!, null!));
        Assert.Equal("com.only.extra", Assert.Single(PermissionBitsetMap.Decode(null!, new[] { "com.only.extra" })));
        Assert.Equal(KnownNodes[3], Assert.Single(PermissionBitsetMap.Decode(SingleBit(3), null!)));
    }
}

/// <summary>
/// Deflate wire codec for the dynamic (non-bitset) permission strings:
/// [flag byte 0=raw 1=deflate][payload of NUL-joined UTF8].
/// </summary>
public class PermissionCompressionTests
{
    public static TheoryData<string[]> RepresentativeExtras()
    {
        return new TheoryData<string[]>
        {
            new[] { "a" },
            new[] { "basis.custom.one", "basis.custom.two" },
            new[] { "com.acme.plugin.permission" },
            new[] { "first", "", "third" },
            new[] { "" },
            new[] { "日本語のノード", "ünïcode.pérm", "emoji.\U0001F600.node" },
        };
    }

    [Theory]
    [MemberData(nameof(RepresentativeExtras))]
    public void RoundTrip_RepresentativeArrays(string[] strings)
    {
        byte[] payload = PermissionCompression.CompressExtras(strings);
        string[] decoded = PermissionCompression.DecompressExtras(payload, strings.Length);
        Assert.Equal(strings, decoded);
    }

    [Fact]
    public void RoundTrip_SeededRandomStrings()
    {
        Random rng = new(9001);
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz.-_0123456789";
        string[] strings = new string[100];
        for (int i = 0; i < strings.Length; i++)
        {
            int length = 1 + rng.Next(30);
            StringBuilder sb = new(length);
            for (int c = 0; c < length; c++)
            {
                sb.Append(Alphabet[rng.Next(Alphabet.Length)]);
            }
            strings[i] = sb.ToString();
        }

        byte[] payload = PermissionCompression.CompressExtras(strings);
        Assert.Equal(strings, PermissionCompression.DecompressExtras(payload, strings.Length));
    }

    [Fact]
    public void NullOrEmptyInput_CompressesToEmptyPayload()
    {
        Assert.Empty(PermissionCompression.CompressExtras(null!));
        Assert.Empty(PermissionCompression.CompressExtras(Array.Empty<string>()));
    }

    [Fact]
    public void Decompress_NullEmptyOrZeroCount_ReturnsEmpty()
    {
        Assert.Empty(PermissionCompression.DecompressExtras(null!, 3));
        Assert.Empty(PermissionCompression.DecompressExtras(Array.Empty<byte>(), 3));

        byte[] valid = PermissionCompression.CompressExtras(new[] { "x", "y" });
        Assert.Empty(PermissionCompression.DecompressExtras(valid, 0));
    }

    [Fact]
    public void TinyInput_UsesRawFlag_WithPinnedSize()
    {
        byte[] payload = PermissionCompression.CompressExtras(new[] { "a" });
        Assert.Equal(2, payload.Length);
        Assert.Equal(0, payload[0]);
        Assert.Equal((byte)'a', payload[1]);
    }

    [Fact]
    public void RepetitiveInput_UsesDeflateFlag_AndShrinks()
    {
        string[] strings = new string[64];
        for (int i = 0; i < strings.Length; i++)
        {
            strings[i] = "basis.custom.permission.node.repeated";
        }
        int rawLength = Encoding.UTF8.GetByteCount(string.Join("\0", strings));

        byte[] payload = PermissionCompression.CompressExtras(strings);
        Assert.Equal(1, payload[0]);
        Assert.True(payload.Length < rawLength + 1, $"deflate payload {payload.Length} should undercut raw {rawLength + 1}");
        Assert.Equal(strings, PermissionCompression.DecompressExtras(payload, strings.Length));
    }

    [Fact]
    public void Payload_NeverExceedsRawSizePlusFlagByte()
    {
        string[][] inputs =
        {
            new[] { "a" },
            new[] { "z", "y", "x" },
            new[] { "com.acme.one", "com.acme.two", "com.acme.three" },
            new[] { new string('q', 500) },
        };
        foreach (string[] strings in inputs)
        {
            int rawLength = Encoding.UTF8.GetByteCount(string.Join("\0", strings));
            byte[] payload = PermissionCompression.CompressExtras(strings);
            Assert.True(payload.Length <= rawLength + 1,
                $"payload {payload.Length} exceeds raw {rawLength} + 1 flag byte");
        }
    }

    [Fact]
    public void ExpectedCount_IsAdvisory_OnlyZeroShortCircuits()
    {
        byte[] payload = PermissionCompression.CompressExtras(new[] { "x", "y" });
        Assert.Equal(new[] { "x", "y" }, PermissionCompression.DecompressExtras(payload, 5));
        Assert.Equal(new[] { "x", "y" }, PermissionCompression.DecompressExtras(payload, 1));
    }

    [Fact]
    public void DecompressGuard_RejectsPayloadsOverOneMiB()
    {
        string oversized = new('a', 1024 * 1024 + 1);
        byte[] payload = PermissionCompression.CompressExtras(new[] { oversized });
        Assert.Equal(1, payload[0]);
        Assert.Empty(PermissionCompression.DecompressExtras(payload, 1));
    }

    [Fact]
    public void DecompressGuard_AllowsExactlyOneMiB()
    {
        string boundary = new('a', 1024 * 1024);
        byte[] payload = PermissionCompression.CompressExtras(new[] { boundary });
        string[] decoded = PermissionCompression.DecompressExtras(payload, 1);
        Assert.True(boundary == Assert.Single(decoded));
    }
}

/// <summary>
/// Full wire path the server actually uses: bitset + compressed extras inside
/// ServerMetaDataMessage.Serialize/Deserialize.
/// </summary>
public class PermissionMetaDataWireTests
{
    private static SerializableBasis.ServerMetaDataMessage MakeMessage() => new()
    {
        ClientMetaDataMessage = new SerializableBasis.ClientMetaDataMessage
        {
            playerUUID = "uuid-1234-abcd",
            playerDisplayName = "Wire Tester",
            playerPlatform = "OpenXR",
        },
        SyncInterval = 50,
        BaseMultiplier = 1,
        IncreaseRate = 0.005f,
        SlowestSendRate = 2.55f,
        PeerLimit = 128,
        UplinkDeltaEnabled = true,
    };

    private static SerializableBasis.ServerMetaDataMessage RoundTrip(SerializableBasis.ServerMetaDataMessage source)
    {
        NetDataWriter writer = new();
        source.Serialize(writer);
        NetDataReader reader = WireTestHelpers.Rewind(writer);
        SerializableBasis.ServerMetaDataMessage decoded = new();
        decoded.Deserialize(reader);
        Assert.True(reader.EndOfData, "deserialize should consume the full payload");
        return decoded;
    }

    [Fact]
    public void KnownAndUnknownNodes_RoundTripThroughMetaDataMessage()
    {
        string[] allowed =
        {
            "basis.server.stats",
            "basis.moderation.kick",
            "com.acme.plugin.alpha",
            "com.acme.plugin.beta",
        };
        SerializableBasis.ServerMetaDataMessage source = MakeMessage();
        source.SetPermissions(allowed);

        SerializableBasis.ServerMetaDataMessage decoded = RoundTrip(source);

        Assert.Equal(source.ClientMetaDataMessage.playerUUID, decoded.ClientMetaDataMessage.playerUUID);
        Assert.Equal(source.ClientMetaDataMessage.playerDisplayName, decoded.ClientMetaDataMessage.playerDisplayName);
        Assert.Equal(source.ClientMetaDataMessage.playerPlatform, decoded.ClientMetaDataMessage.playerPlatform);
        Assert.Equal(source.SyncInterval, decoded.SyncInterval);
        Assert.Equal(source.BaseMultiplier, decoded.BaseMultiplier);
        Assert.Equal(source.IncreaseRate, decoded.IncreaseRate);
        Assert.Equal(source.SlowestSendRate, decoded.SlowestSendRate);
        Assert.Equal(source.PeerLimit, decoded.PeerLimit);
        Assert.True(decoded.UplinkDeltaEnabled);

        Assert.Equal(source.PermissionsBitset, decoded.PermissionsBitset);
        Assert.Equal(new[] { "com.acme.plugin.alpha", "com.acme.plugin.beta" }, decoded.ExtraPermissions);

        HashSet<string> expected = new(allowed, StringComparer.OrdinalIgnoreCase);
        Assert.True(expected.SetEquals(decoded.GetPermissions()));
    }

    [Fact]
    public void Wildcard_RoundTripsToFullKnownSet()
    {
        SerializableBasis.ServerMetaDataMessage source = MakeMessage();
        source.SetPermissions(new[] { "*" });

        SerializableBasis.ServerMetaDataMessage decoded = RoundTrip(source);

        HashSet<string> expected = new(PermissionBitsetMapTests.KnownNodes, StringComparer.OrdinalIgnoreCase);
        Assert.True(expected.SetEquals(decoded.GetPermissions()));
        Assert.Empty(decoded.ExtraPermissions);
    }

    [Fact]
    public void NoPermissions_RoundTripEmpty_AndUplinkFalseSurvives()
    {
        SerializableBasis.ServerMetaDataMessage source = MakeMessage();
        source.UplinkDeltaEnabled = false;
        source.SetPermissions(Array.Empty<string>());

        SerializableBasis.ServerMetaDataMessage decoded = RoundTrip(source);

        Assert.Empty(decoded.GetPermissions());
        Assert.Empty(decoded.ExtraPermissions);
        Assert.False(decoded.UplinkDeltaEnabled);
    }

    [Fact]
    public void ManyExtras_SurviveTheDeflateWirePath()
    {
        string[] allowed = new string[40];
        for (int i = 0; i < allowed.Length; i++)
        {
            allowed[i] = $"com.acme.plugin.permission.{i:D3}";
        }
        SerializableBasis.ServerMetaDataMessage source = MakeMessage();
        source.SetPermissions(allowed);

        SerializableBasis.ServerMetaDataMessage decoded = RoundTrip(source);

        Assert.Equal(allowed, decoded.ExtraPermissions);
        HashSet<string> expected = new(allowed, StringComparer.OrdinalIgnoreCase);
        Assert.True(expected.SetEquals(decoded.GetPermissions()));
    }

    [Fact]
    public void LegacyStream_WithoutPermissionBlock_YieldsEmptyPermissions()
    {
        NetDataWriter writer = new();
        new SerializableBasis.ClientMetaDataMessage
        {
            playerUUID = "legacy-uuid",
            playerDisplayName = "Legacy",
            playerPlatform = "Desktop",
        }.Serialize(writer);
        writer.Put(50);
        writer.Put(1);
        writer.Put(0.005f);
        writer.Put(2.55f);
        writer.Put(32);

        SerializableBasis.ServerMetaDataMessage decoded = new();
        decoded.Deserialize(WireTestHelpers.Rewind(writer));

        Assert.Equal(50, decoded.SyncInterval);
        Assert.Equal(32, decoded.PeerLimit);
        Assert.NotNull(decoded.PermissionsBitset);
        Assert.Empty(decoded.PermissionsBitset);
        Assert.Empty(decoded.ExtraPermissions);
        Assert.False(decoded.UplinkDeltaEnabled);
        Assert.Empty(decoded.GetPermissions());
    }
}

/// <summary>
/// Integrity of the core message catalog: one descriptor per core channel, unique ids/names,
/// and a full name→channel pin so accidental renumbering breaks a test.
/// </summary>
public class MessageCatalogTests
{
    private static readonly (byte Channel, string Name)[] PinnedCoreCatalog =
    {
        (0, "basis.core.auth.identity"),
        (1, "basis.core.metadata"),
        (2, "basis.core.disconnection"),
        (3, "basis.core.voice"),
        (4, "basis.core.voice.shout"),
        (5, "basis.core.voice.recipients"),
        (6, "basis.core.avatar.verylow"),
        (7, "basis.core.avatar.verylow.additional"),
        (8, "basis.core.avatar.low"),
        (9, "basis.core.avatar.low.additional"),
        (10, "basis.core.avatar.medium"),
        (11, "basis.core.avatar.medium.additional"),
        (12, "basis.core.avatar.high"),
        (13, "basis.core.avatar.high.additional"),
        (14, "basis.core.avatar.change"),
        (15, "basis.core.avatar.data"),
        (16, "basis.core.player.create"),
        (17, "basis.core.player.create.bulk"),
        (18, "basis.core.chat"),
        (19, "basis.core.ownership.get"),
        (20, "basis.core.ownership.change"),
        (21, "basis.core.ownership.remove"),
        (22, "basis.core.netid.assign"),
        (23, "basis.core.netid.assigns"),
        (24, "basis.core.scene.data"),
        (25, "basis.core.resource.load"),
        (26, "basis.core.resource.unload"),
        (27, "basis.core.resource.preloadready"),
        (28, "basis.core.resource.spawnpreloaded"),
        (29, "basis.core.contentshare"),
        (30, "basis.core.avatar.delta"),
        (31, "basis.core.serverbound"),
        (32, "basis.core.database.store"),
        (33, "basis.core.database.request"),
        (34, "basis.core.admin"),
        (35, "basis.core.statistics"),
        (36, "basis.core.camera.pip.state"),
        (37, "basis.core.camera.pip.position"),
        (38, "basis.core.events"),
        (39, "basis.core.voice.recipients.large"),
        (40, "basis.core.voice.large"),
        (41, "basis.core.avatar.verylow.large"),
        (42, "basis.core.avatar.verylow.additional.large"),
        (43, "basis.core.avatar.low.large"),
        (44, "basis.core.avatar.low.additional.large"),
        (45, "basis.core.avatar.medium.large"),
        (46, "basis.core.avatar.medium.additional.large"),
        (47, "basis.core.avatar.high.large"),
        (48, "basis.core.avatar.high.additional.large"),
        (49, "basis.core.voice.recipients.inverted"),
        (50, "basis.core.voice.recipients.inverted.large"),
        (51, "basis.core.voice.recipients.bitfield"),
        (52, "basis.core.avatar.bundle.compressed"),
        (53, "basis.core.library"),
        (54, "basis.core.p2p"),
        (55, "basis.core.resource.modify"),
        (56, "basis.core.scene.direct"),
        (57, "basis.core.scene.direct.server"),
        (58, "basis.core.avatar.direct"),
        (59, "basis.core.avatar.direct.server"),
        (60, "basis.core.registry.control"),
    };

    [Fact]
    public void Core_CoversChannels0Through60_Exactly()
    {
        SerializableBasis.BasisMessageDescriptor[] core = SerializableBasis.BasisMessageCatalog.BuildCore();
        Assert.Equal(61, core.Length);

        HashSet<byte> channels = new();
        foreach (SerializableBasis.BasisMessageDescriptor descriptor in core)
        {
            Assert.True(channels.Add(descriptor.Channel), $"duplicate channel {descriptor.Channel}");
        }
        for (byte channel = 0; channel <= 60; channel++)
        {
            Assert.Contains(channel, channels);
        }
    }

    [Fact]
    public void CoreIds_EqualTheirChannel_AndAreUnique()
    {
        SerializableBasis.BasisMessageDescriptor[] core = SerializableBasis.BasisMessageCatalog.BuildCore();
        HashSet<ushort> ids = new();
        foreach (SerializableBasis.BasisMessageDescriptor descriptor in core)
        {
            Assert.Equal((ushort)descriptor.Channel, descriptor.Id);
            Assert.True(ids.Add(descriptor.Id), $"duplicate id {descriptor.Id}");
        }
    }

    [Fact]
    public void CoreNames_AreUniqueNonEmptyAndCorePrefixed()
    {
        SerializableBasis.BasisMessageDescriptor[] core = SerializableBasis.BasisMessageCatalog.BuildCore();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (SerializableBasis.BasisMessageDescriptor descriptor in core)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Name), $"channel {descriptor.Channel} has a blank name");
            Assert.StartsWith("basis.core.", descriptor.Name, StringComparison.Ordinal);
            Assert.True(names.Add(descriptor.Name), $"duplicate name {descriptor.Name}");
        }
    }

    [Fact]
    public void CoreVersionAndFlags_AreUniform()
    {
        Assert.Equal(1, SerializableBasis.BasisMessageCatalog.CoreVersion);
        foreach (SerializableBasis.BasisMessageDescriptor descriptor in SerializableBasis.BasisMessageCatalog.BuildCore())
        {
            Assert.Equal(SerializableBasis.BasisMessageCatalog.CoreVersion, descriptor.Version);
            Assert.Equal((byte)SerializableBasis.BasisMessageFlags.None, descriptor.Flags);
        }
    }

    [Fact]
    public void CoreChannels_StayBelowThePluginAndTotalRange()
    {
        foreach (SerializableBasis.BasisMessageDescriptor descriptor in SerializableBasis.BasisMessageCatalog.BuildCore())
        {
            Assert.True(descriptor.Channel < BasisNetworkCommons.TotalChannels);
            Assert.True(descriptor.Channel < BasisNetworkCommons.PluginReliableChannel,
                $"core channel {descriptor.Channel} intrudes on the multiplexed plugin range");
            Assert.False(BasisNetworkCommons.IsPluginChannel(descriptor.Channel));
        }
    }

    [Fact]
    public void CoreNameToChannelBindings_ArePinned()
    {
        SerializableBasis.BasisMessageDescriptor[] core = SerializableBasis.BasisMessageCatalog.BuildCore();
        Assert.Equal(PinnedCoreCatalog.Length, core.Length);
        foreach ((byte channel, string name) in PinnedCoreCatalog)
        {
            SerializableBasis.BasisMessageDescriptor match = core.Single(d => d.Channel == channel);
            Assert.Equal(name, match.Name);
            Assert.Equal((ushort)channel, match.Id);
        }
    }

    [Fact]
    public void BuildCore_IsStableAcrossCalls()
    {
        SerializableBasis.BasisMessageDescriptor[] first = SerializableBasis.BasisMessageCatalog.BuildCore();
        SerializableBasis.BasisMessageDescriptor[] second = SerializableBasis.BasisMessageCatalog.BuildCore();
        Assert.Equal(first.Length, second.Length);
        for (int i = 0; i < first.Length; i++)
        {
            Assert.Equal(first[i].Id, second[i].Id);
            Assert.Equal(first[i].Channel, second[i].Channel);
            Assert.Equal(first[i].Version, second[i].Version);
            Assert.Equal(first[i].Flags, second[i].Flags);
            Assert.Equal(first[i].Name, second[i].Name);
        }
    }
}

/// <summary>
/// Wire round-trips for the registry manifest structs (descriptor, supply, subscribe),
/// including exact wire sizes and truncation failure paths.
/// </summary>
public class MessageManifestSerializationTests
{
    public static TheoryData<ushort, byte, byte, byte, string> DescriptorCases()
    {
        return new TheoryData<ushort, byte, byte, byte, string>
        {
            { 0, 1, 0, (byte)SerializableBasis.BasisMessageFlags.None, "basis.core.auth.identity" },
            { 64, 1, 61, (byte)(SerializableBasis.BasisMessageFlags.Multiplexed | SerializableBasis.BasisMessageFlags.Required), "com.acme.plugin.foo" },
            { 300, 7, 62, (byte)(SerializableBasis.BasisMessageFlags.Multiplexed | SerializableBasis.BasisMessageFlags.ServerToClient | SerializableBasis.BasisMessageFlags.ClientToServer), "com.例.プラグイン" },
            { ushort.MaxValue, byte.MaxValue, 63, byte.MaxValue, "x" },
            { 1, 0, 1, 0, "" },
        };
    }

    [Theory]
    [MemberData(nameof(DescriptorCases))]
    public void Descriptor_RoundTrips(ushort id, byte version, byte channel, byte flags, string name)
    {
        SerializableBasis.BasisMessageDescriptor source = new()
        {
            Id = id,
            Version = version,
            Channel = channel,
            Flags = flags,
            Name = name,
        };
        NetDataWriter writer = new();
        source.Serialize(writer);

        NetDataReader reader = WireTestHelpers.Rewind(writer);
        SerializableBasis.BasisMessageDescriptor decoded = default;
        Assert.True(decoded.Deserialize(reader));
        Assert.True(reader.EndOfData);

        Assert.Equal(id, decoded.Id);
        Assert.Equal(version, decoded.Version);
        Assert.Equal(channel, decoded.Channel);
        Assert.Equal(flags, decoded.Flags);
        Assert.Equal(name, decoded.Name);
    }

    [Fact]
    public void Descriptor_WireSize_IsSevenPlusUtf8NameBytes()
    {
        SerializableBasis.BasisMessageDescriptor ascii = new() { Id = 3, Version = 1, Channel = 3, Flags = 0, Name = "basis.core.voice" };
        NetDataWriter writer = new();
        ascii.Serialize(writer);
        Assert.Equal(7 + "basis.core.voice".Length, writer.Length);

        SerializableBasis.BasisMessageDescriptor unicode = new() { Id = 9, Version = 1, Channel = 9, Flags = 0, Name = "é" };
        writer = new NetDataWriter();
        unicode.Serialize(writer);
        Assert.Equal(7 + Encoding.UTF8.GetByteCount("é"), writer.Length);

        SerializableBasis.BasisMessageDescriptor empty = new() { Id = 1, Version = 1, Channel = 1, Flags = 0, Name = "" };
        writer = new NetDataWriter();
        empty.Serialize(writer);
        Assert.Equal(7, writer.Length);
    }

    [Fact]
    public void Descriptor_NullName_SerializesLikeEmpty()
    {
        SerializableBasis.BasisMessageDescriptor source = new() { Id = 2, Version = 1, Channel = 2, Flags = 0, Name = null! };
        NetDataWriter writer = new();
        source.Serialize(writer);
        Assert.Equal(7, writer.Length);

        SerializableBasis.BasisMessageDescriptor decoded = default;
        Assert.True(decoded.Deserialize(WireTestHelpers.Rewind(writer)));
        Assert.Equal(string.Empty, decoded.Name);
    }

    [Fact]
    public void Descriptor_DeserializeFails_OnTruncatedBuffers()
    {
        SerializableBasis.BasisMessageDescriptor source = new() { Id = 42, Version = 1, Channel = 30, Flags = 2, Name = "basis.core.avatar.delta" };
        NetDataWriter writer = new();
        source.Serialize(writer);

        foreach (int size in new[] { 0, 1, 2, 3, 4, 5, 6, writer.Length - 1 })
        {
            SerializableBasis.BasisMessageDescriptor decoded = default;
            Assert.False(decoded.Deserialize(WireTestHelpers.Truncated(writer, size)), $"size {size} should fail");
        }
    }

    [Fact]
    public void Supply_RoundTrips_FullCoreCatalog_PreservingOrder()
    {
        SerializableBasis.BasisMessageSupply source = new()
        {
            Descriptors = SerializableBasis.BasisMessageCatalog.BuildCore(),
        };
        NetDataWriter writer = new();
        source.Serialize(writer);

        NetDataReader reader = WireTestHelpers.Rewind(writer);
        SerializableBasis.BasisMessageSupply decoded = default;
        Assert.True(decoded.Deserialize(reader));
        Assert.True(reader.EndOfData);

        Assert.Equal(source.Descriptors.Length, decoded.Descriptors.Length);
        for (int i = 0; i < source.Descriptors.Length; i++)
        {
            Assert.Equal(source.Descriptors[i].Id, decoded.Descriptors[i].Id);
            Assert.Equal(source.Descriptors[i].Version, decoded.Descriptors[i].Version);
            Assert.Equal(source.Descriptors[i].Channel, decoded.Descriptors[i].Channel);
            Assert.Equal(source.Descriptors[i].Flags, decoded.Descriptors[i].Flags);
            Assert.Equal(source.Descriptors[i].Name, decoded.Descriptors[i].Name);
        }
    }

    [Fact]
    public void Supply_NullDescriptors_SerializeAsEmpty()
    {
        SerializableBasis.BasisMessageSupply source = new() { Descriptors = null! };
        NetDataWriter writer = new();
        source.Serialize(writer);
        Assert.Equal(2, writer.Length);

        SerializableBasis.BasisMessageSupply decoded = default;
        Assert.True(decoded.Deserialize(WireTestHelpers.Rewind(writer)));
        Assert.NotNull(decoded.Descriptors);
        Assert.Empty(decoded.Descriptors);
    }

    [Fact]
    public void Supply_DeserializeFails_OnEmptyOrTruncatedBuffers()
    {
        SerializableBasis.BasisMessageSupply empty = default;
        Assert.False(empty.Deserialize(new NetDataReader(Array.Empty<byte>())));
        Assert.NotNull(empty.Descriptors);
        Assert.Empty(empty.Descriptors);

        SerializableBasis.BasisMessageSupply source = new()
        {
            Descriptors = SerializableBasis.BasisMessageCatalog.BuildCore(),
        };
        NetDataWriter writer = new();
        source.Serialize(writer);
        foreach (int size in new[] { 1, 2, 10, writer.Length - 1 })
        {
            SerializableBasis.BasisMessageSupply decoded = default;
            Assert.False(decoded.Deserialize(WireTestHelpers.Truncated(writer, size)), $"size {size} should fail");
        }
    }

    [Fact]
    public void Subscribe_RoundTrips_IncludingEdgeIdsAndDuplicates()
    {
        ushort[] ids = { 0, 1, 64, 64, 300, ushort.MaxValue };
        SerializableBasis.BasisMessageSubscribe source = new() { Ids = ids };
        NetDataWriter writer = new();
        source.Serialize(writer);
        Assert.Equal(2 + ids.Length * 2, writer.Length);

        NetDataReader reader = WireTestHelpers.Rewind(writer);
        SerializableBasis.BasisMessageSubscribe decoded = default;
        Assert.True(decoded.Deserialize(reader));
        Assert.True(reader.EndOfData);
        Assert.Equal(ids, decoded.Ids);
    }

    [Fact]
    public void Subscribe_NullIds_SerializeAsEmpty()
    {
        SerializableBasis.BasisMessageSubscribe source = new() { Ids = null! };
        NetDataWriter writer = new();
        source.Serialize(writer);
        Assert.Equal(2, writer.Length);

        SerializableBasis.BasisMessageSubscribe decoded = default;
        Assert.True(decoded.Deserialize(WireTestHelpers.Rewind(writer)));
        Assert.NotNull(decoded.Ids);
        Assert.Empty(decoded.Ids);
    }

    [Fact]
    public void Subscribe_DeserializeFails_OnTruncatedBuffers()
    {
        SerializableBasis.BasisMessageSubscribe source = new() { Ids = new ushort[] { 1, 2, 3 } };
        NetDataWriter writer = new();
        source.Serialize(writer);

        foreach (int size in new[] { 0, 1, 3, writer.Length - 1 })
        {
            SerializableBasis.BasisMessageSubscribe decoded = default;
            Assert.False(decoded.Deserialize(WireTestHelpers.Truncated(writer, size)), $"size {size} should fail");
        }
    }
}

/// <summary>
/// Pins the channel constant layout in BasisNetworkCommons: every named channel keeps its
/// wire value, all 64 are distinct and below TotalChannels, and the helper mappings agree.
/// </summary>
public class NetworkChannelConstantTests
{
    private static readonly (string Name, byte Actual, byte Expected)[] ChannelPins =
    {
        (nameof(BasisNetworkCommons.AuthIdentityChannel), BasisNetworkCommons.AuthIdentityChannel, 0),
        (nameof(BasisNetworkCommons.metaDataChannel), BasisNetworkCommons.metaDataChannel, 1),
        (nameof(BasisNetworkCommons.DisconnectionChannel), BasisNetworkCommons.DisconnectionChannel, 2),
        (nameof(BasisNetworkCommons.VoiceChannel), BasisNetworkCommons.VoiceChannel, 3),
        (nameof(BasisNetworkCommons.ShoutVoiceChannel), BasisNetworkCommons.ShoutVoiceChannel, 4),
        (nameof(BasisNetworkCommons.AudioRecipientsChannel), BasisNetworkCommons.AudioRecipientsChannel, 5),
        (nameof(BasisNetworkCommons.PlayerAvatarVeryLowChannel), BasisNetworkCommons.PlayerAvatarVeryLowChannel, 6),
        (nameof(BasisNetworkCommons.PlayerAvatarVeryLowAdditionalChannel), BasisNetworkCommons.PlayerAvatarVeryLowAdditionalChannel, 7),
        (nameof(BasisNetworkCommons.PlayerAvatarLowChannel), BasisNetworkCommons.PlayerAvatarLowChannel, 8),
        (nameof(BasisNetworkCommons.PlayerAvatarLowAdditionalChannel), BasisNetworkCommons.PlayerAvatarLowAdditionalChannel, 9),
        (nameof(BasisNetworkCommons.PlayerAvatarMediumChannel), BasisNetworkCommons.PlayerAvatarMediumChannel, 10),
        (nameof(BasisNetworkCommons.PlayerAvatarMediumAdditionalChannel), BasisNetworkCommons.PlayerAvatarMediumAdditionalChannel, 11),
        (nameof(BasisNetworkCommons.PlayerAvatarHighChannel), BasisNetworkCommons.PlayerAvatarHighChannel, 12),
        (nameof(BasisNetworkCommons.PlayerAvatarHighAdditionalChannel), BasisNetworkCommons.PlayerAvatarHighAdditionalChannel, 13),
        (nameof(BasisNetworkCommons.AvatarChangeMessageChannel), BasisNetworkCommons.AvatarChangeMessageChannel, 14),
        (nameof(BasisNetworkCommons.AvatarChannel), BasisNetworkCommons.AvatarChannel, 15),
        (nameof(BasisNetworkCommons.CreateRemotePlayerChannel), BasisNetworkCommons.CreateRemotePlayerChannel, 16),
        (nameof(BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel), BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, 17),
        (nameof(BasisNetworkCommons.ChatChannel), BasisNetworkCommons.ChatChannel, 18),
        (nameof(BasisNetworkCommons.GetCurrentOwnerRequestChannel), BasisNetworkCommons.GetCurrentOwnerRequestChannel, 19),
        (nameof(BasisNetworkCommons.ChangeCurrentOwnerRequestChannel), BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, 20),
        (nameof(BasisNetworkCommons.RemoveCurrentOwnerRequestChannel), BasisNetworkCommons.RemoveCurrentOwnerRequestChannel, 21),
        (nameof(BasisNetworkCommons.netIDAssignChannel), BasisNetworkCommons.netIDAssignChannel, 22),
        (nameof(BasisNetworkCommons.NetIDAssignsChannel), BasisNetworkCommons.NetIDAssignsChannel, 23),
        (nameof(BasisNetworkCommons.SceneChannel), BasisNetworkCommons.SceneChannel, 24),
        (nameof(BasisNetworkCommons.LoadResourceChannel), BasisNetworkCommons.LoadResourceChannel, 25),
        (nameof(BasisNetworkCommons.UnloadResourceChannel), BasisNetworkCommons.UnloadResourceChannel, 26),
        (nameof(BasisNetworkCommons.PreloadReadyChannel), BasisNetworkCommons.PreloadReadyChannel, 27),
        (nameof(BasisNetworkCommons.SpawnPreloadedChannel), BasisNetworkCommons.SpawnPreloadedChannel, 28),
        (nameof(BasisNetworkCommons.ContentShareChannel), BasisNetworkCommons.ContentShareChannel, 29),
        (nameof(BasisNetworkCommons.DeltaAvatarChannel), BasisNetworkCommons.DeltaAvatarChannel, 30),
        (nameof(BasisNetworkCommons.ServerBoundChannel), BasisNetworkCommons.ServerBoundChannel, 31),
        (nameof(BasisNetworkCommons.StoreDatabaseChannel), BasisNetworkCommons.StoreDatabaseChannel, 32),
        (nameof(BasisNetworkCommons.RequestStoreDatabaseChannel), BasisNetworkCommons.RequestStoreDatabaseChannel, 33),
        (nameof(BasisNetworkCommons.AdminChannel), BasisNetworkCommons.AdminChannel, 34),
        (nameof(BasisNetworkCommons.ServerStatisticsChannel), BasisNetworkCommons.ServerStatisticsChannel, 35),
        (nameof(BasisNetworkCommons.CameraPIPStateChannel), BasisNetworkCommons.CameraPIPStateChannel, 36),
        (nameof(BasisNetworkCommons.CameraPIPPositionChannel), BasisNetworkCommons.CameraPIPPositionChannel, 37),
        (nameof(BasisNetworkCommons.EventsChannel), BasisNetworkCommons.EventsChannel, 38),
        (nameof(BasisNetworkCommons.AudioRecipientsLargeChannel), BasisNetworkCommons.AudioRecipientsLargeChannel, 39),
        (nameof(BasisNetworkCommons.VoiceLargeChannel), BasisNetworkCommons.VoiceLargeChannel, 40),
        (nameof(BasisNetworkCommons.PlayerAvatarVeryLowLargeChannel), BasisNetworkCommons.PlayerAvatarVeryLowLargeChannel, 41),
        (nameof(BasisNetworkCommons.PlayerAvatarVeryLowAdditionalLargeChannel), BasisNetworkCommons.PlayerAvatarVeryLowAdditionalLargeChannel, 42),
        (nameof(BasisNetworkCommons.PlayerAvatarLowLargeChannel), BasisNetworkCommons.PlayerAvatarLowLargeChannel, 43),
        (nameof(BasisNetworkCommons.PlayerAvatarLowAdditionalLargeChannel), BasisNetworkCommons.PlayerAvatarLowAdditionalLargeChannel, 44),
        (nameof(BasisNetworkCommons.PlayerAvatarMediumLargeChannel), BasisNetworkCommons.PlayerAvatarMediumLargeChannel, 45),
        (nameof(BasisNetworkCommons.PlayerAvatarMediumAdditionalLargeChannel), BasisNetworkCommons.PlayerAvatarMediumAdditionalLargeChannel, 46),
        (nameof(BasisNetworkCommons.PlayerAvatarHighLargeChannel), BasisNetworkCommons.PlayerAvatarHighLargeChannel, 47),
        (nameof(BasisNetworkCommons.PlayerAvatarHighAdditionalLargeChannel), BasisNetworkCommons.PlayerAvatarHighAdditionalLargeChannel, 48),
        (nameof(BasisNetworkCommons.AudioRecipientsInvertedChannel), BasisNetworkCommons.AudioRecipientsInvertedChannel, 49),
        (nameof(BasisNetworkCommons.AudioRecipientsInvertedLargeChannel), BasisNetworkCommons.AudioRecipientsInvertedLargeChannel, 50),
        (nameof(BasisNetworkCommons.AudioRecipientsBitfieldChannel), BasisNetworkCommons.AudioRecipientsBitfieldChannel, 51),
        (nameof(BasisNetworkCommons.CompressedAvatarBundleChannel), BasisNetworkCommons.CompressedAvatarBundleChannel, 52),
        (nameof(BasisNetworkCommons.ServerLibraryChannel), BasisNetworkCommons.ServerLibraryChannel, 53),
        (nameof(BasisNetworkCommons.P2PChannel), BasisNetworkCommons.P2PChannel, 54),
        (nameof(BasisNetworkCommons.ModifyResourceChannel), BasisNetworkCommons.ModifyResourceChannel, 55),
        (nameof(BasisNetworkCommons.DirectSceneChannel), BasisNetworkCommons.DirectSceneChannel, 56),
        (nameof(BasisNetworkCommons.DirectSceneServerChannel), BasisNetworkCommons.DirectSceneServerChannel, 57),
        (nameof(BasisNetworkCommons.DirectAvatarChannel), BasisNetworkCommons.DirectAvatarChannel, 58),
        (nameof(BasisNetworkCommons.DirectAvatarServerChannel), BasisNetworkCommons.DirectAvatarServerChannel, 59),
        (nameof(BasisNetworkCommons.RegistryControlChannel), BasisNetworkCommons.RegistryControlChannel, 60),
        (nameof(BasisNetworkCommons.PluginReliableChannel), BasisNetworkCommons.PluginReliableChannel, 61),
        (nameof(BasisNetworkCommons.PluginSequencedChannel), BasisNetworkCommons.PluginSequencedChannel, 62),
        (nameof(BasisNetworkCommons.PluginUnreliableChannel), BasisNetworkCommons.PluginUnreliableChannel, 63),
    };

    [Fact]
    public void TotalChannels_IsPinnedAt64()
    {
        Assert.Equal(64, BasisNetworkCommons.TotalChannels);
    }

    [Fact]
    public void EveryNamedChannel_HasItsPinnedValue()
    {
        foreach ((string name, byte actual, byte expected) in ChannelPins)
        {
            Assert.True(expected == actual, $"{name} expected {expected} but was {actual}");
        }
    }

    [Fact]
    public void AllNamedChannels_AreDistinct_AndCoverZeroToSixtyThree()
    {
        HashSet<byte> seen = new();
        foreach ((string name, byte actual, _) in ChannelPins)
        {
            Assert.True(seen.Add(actual), $"{name} duplicates channel value {actual}");
            Assert.True(actual < BasisNetworkCommons.TotalChannels, $"{name} = {actual} exceeds TotalChannels");
        }
        Assert.Equal(BasisNetworkCommons.TotalChannels, (byte)seen.Count);
    }

    [Fact]
    public void AvatarQualityChannelMath_RoundTripsBothIdWidths()
    {
        foreach (bool additional in new[] { false, true })
        {
            for (int quality = 0; quality < 4; quality++)
            {
                byte small = BasisNetworkCommons.GetPlayerAvatarChannelForQuality(quality, additional);
                Assert.Equal(BasisNetworkCommons.PlayerAvatarVeryLowChannel + quality * 2 + (additional ? 1 : 0), small);
                Assert.Equal((byte)quality, BasisNetworkCommons.GetQualityFromChannel(small));
                Assert.Equal(additional, BasisNetworkCommons.ChannelHasAdditionalData(small));
                Assert.False(BasisNetworkCommons.IsLargePlayerIdChannel(small));

                byte large = BasisNetworkCommons.GetPlayerAvatarLargeChannelForQuality(quality, additional);
                Assert.Equal(BasisNetworkCommons.PlayerAvatarVeryLowLargeChannel + quality * 2 + (additional ? 1 : 0), large);
                Assert.Equal((byte)quality, BasisNetworkCommons.GetQualityFromChannel(large));
                Assert.Equal(additional, BasisNetworkCommons.ChannelHasAdditionalData(large));
                Assert.True(BasisNetworkCommons.IsLargePlayerIdChannel(large));
            }
        }

        Assert.True(BasisNetworkCommons.IsLargePlayerIdChannel(BasisNetworkCommons.VoiceLargeChannel));
        Assert.False(BasisNetworkCommons.IsLargePlayerIdChannel(BasisNetworkCommons.AudioRecipientsLargeChannel));
        Assert.False(BasisNetworkCommons.IsLargePlayerIdChannel(BasisNetworkCommons.AudioRecipientsInvertedChannel));
    }

    [Fact]
    public void PlayerAvatarQualityChannels_ListsAll16InPinnedOrder()
    {
        byte[] expected = { 6, 7, 8, 9, 10, 11, 12, 13, 41, 42, 43, 44, 45, 46, 47, 48 };
        Assert.Equal(expected, BasisNetworkCommons.PlayerAvatarQualityChannels);
    }

    [Fact]
    public void PluginChannelDeliveryMapping_IsConsistentBothWays()
    {
        Assert.Equal(BasisNetworkCommons.PluginReliableChannel, BasisNetworkCommons.GetPluginChannelForDelivery(DeliveryMethod.ReliableOrdered));
        Assert.Equal(BasisNetworkCommons.PluginReliableChannel, BasisNetworkCommons.GetPluginChannelForDelivery(DeliveryMethod.ReliableUnordered));
        Assert.Equal(BasisNetworkCommons.PluginReliableChannel, BasisNetworkCommons.GetPluginChannelForDelivery(DeliveryMethod.ReliableSequenced));
        Assert.Equal(BasisNetworkCommons.PluginSequencedChannel, BasisNetworkCommons.GetPluginChannelForDelivery(DeliveryMethod.Sequenced));
        Assert.Equal(BasisNetworkCommons.PluginUnreliableChannel, BasisNetworkCommons.GetPluginChannelForDelivery(DeliveryMethod.Unreliable));
        Assert.Equal(BasisNetworkCommons.RegistryControlChannel, BasisNetworkCommons.GetPluginChannelForDelivery((DeliveryMethod)200));

        Assert.Equal(DeliveryMethod.ReliableOrdered, BasisNetworkCommons.GetDeliveryForPluginChannel(BasisNetworkCommons.PluginReliableChannel));
        Assert.Equal(DeliveryMethod.Sequenced, BasisNetworkCommons.GetDeliveryForPluginChannel(BasisNetworkCommons.PluginSequencedChannel));
        Assert.Equal(DeliveryMethod.Unreliable, BasisNetworkCommons.GetDeliveryForPluginChannel(BasisNetworkCommons.PluginUnreliableChannel));

        for (byte channel = BasisNetworkCommons.PluginReliableChannel; channel <= BasisNetworkCommons.PluginUnreliableChannel; channel++)
        {
            Assert.Equal(channel, BasisNetworkCommons.GetPluginChannelForDelivery(BasisNetworkCommons.GetDeliveryForPluginChannel(channel)));
        }

        Assert.False(BasisNetworkCommons.IsPluginChannel(0));
        Assert.False(BasisNetworkCommons.IsPluginChannel(59));
        Assert.False(BasisNetworkCommons.IsPluginChannel(BasisNetworkCommons.RegistryControlChannel));
        Assert.True(BasisNetworkCommons.IsPluginChannel(61));
        Assert.True(BasisNetworkCommons.IsPluginChannel(62));
        Assert.True(BasisNetworkCommons.IsPluginChannel(63));
        Assert.False(BasisNetworkCommons.IsPluginChannel(64));
        Assert.False(BasisNetworkCommons.IsPluginChannel(byte.MaxValue));
    }

    [Fact]
    public void SubTypeConstants_ArePinnedAndDistinctWithinTheirGroup()
    {
        Assert.Equal(0, BasisNetworkCommons.ContentShareSub_Drop);
        Assert.Equal(1, BasisNetworkCommons.ContentShareSub_Cleanup);

        Assert.Equal(0, BasisNetworkCommons.RegistrySub_Supply);
        Assert.Equal(1, BasisNetworkCommons.RegistrySub_Subscribe);

        byte[] p2p =
        {
            BasisNetworkCommons.P2PSub_Request,
            BasisNetworkCommons.P2PSub_Accept,
            BasisNetworkCommons.P2PSub_Decline,
            BasisNetworkCommons.P2PSub_Cancel,
            BasisNetworkCommons.P2PSub_LinkLost,
            BasisNetworkCommons.P2PSub_ServerArmed,
            BasisNetworkCommons.P2PSub_LinkUp,
            BasisNetworkCommons.P2PSub_Offloaded,
        };
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }, p2p);

        byte[] eventTypes =
        {
            BasisNetworkCommons.EventType_CameraShutterSound,
            BasisNetworkCommons.EventType_CameraCountdown,
            BasisNetworkCommons.EventType_PlayerTempBlock,
            BasisNetworkCommons.EventType_AvatarRateChange,
            BasisNetworkCommons.EventType_TalkModeChanged,
            BasisNetworkCommons.EventType_MuteStateChanged,
            BasisNetworkCommons.EventType_PlayerChatTyping,
            BasisNetworkCommons.EventType_ErrorReport,
            BasisNetworkCommons.EventType_VoiceRecordRequest,
            BasisNetworkCommons.EventType_VoiceRecordConsent,
        };
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, eventTypes);
    }

    [Fact]
    public void ServerInfoAndRejectWireConstants_ArePinned()
    {
        Assert.Equal(0xBA515101u, BasisNetworkCommons.ServerInfoQueryMagic);
        Assert.Equal(0xBA515102u, BasisNetworkCommons.ServerInfoResponseMagic);
        Assert.NotEqual(BasisNetworkCommons.ServerInfoQueryMagic, BasisNetworkCommons.ServerInfoResponseMagic);
        Assert.Equal(1, BasisNetworkCommons.ServerInfoProtocolVersion);
        Assert.Equal(64, BasisNetworkCommons.ServerInfoNameMaxLength);
        Assert.Equal(256, BasisNetworkCommons.ServerInfoMotdMaxLength);
        Assert.Equal(384, BasisNetworkCommons.ServerInfoMinRequestBytes);

        Assert.Equal(0xBA515CE1u, BasisNetworkCommons.RejectMagic);
        Assert.Equal(1, BasisNetworkCommons.RejectKind_VersionMismatch);
        Assert.Equal(2, BasisNetworkCommons.RejectKind_ServerFull);
    }
}

/// <summary>
/// The server-side registry built on top of the catalog: lookups return what was registered,
/// plugin ids stay above the core range, and the supplied manifest tracks (un)registration.
/// All mutation stays inside this class so tests remain order-independent.
/// </summary>
public class ServerMessageRegistryTests
{
    private static readonly BasisServerMessageHandler NoOpHandler = (peer, reader, channel, dm) => { };

    [Fact]
    public void CoreInboundHandlers_AreBoundAfterInit()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        Assert.NotNull(BasisServerMessageRegistry.ResolveCore(BasisNetworkCommons.AuthIdentityChannel));
        Assert.NotNull(BasisServerMessageRegistry.ResolveCore(BasisNetworkCommons.VoiceChannel));
        Assert.NotNull(BasisServerMessageRegistry.ResolveCore(BasisNetworkCommons.DeltaAvatarChannel));
        Assert.NotNull(BasisServerMessageRegistry.ResolveCore(BasisNetworkCommons.ChatChannel));
        Assert.NotNull(BasisServerMessageRegistry.ResolveCore(BasisNetworkCommons.RegistryControlChannel));
    }

    [Fact]
    public void RegisterCore_ResolveCore_ReturnsTheRegisteredHandler()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        byte channel = BasisNetworkCommons.DisconnectionChannel;
        BasisServerMessageHandler? original = BasisServerMessageRegistry.ResolveCore(channel);
        try
        {
            BasisServerMessageHandler custom = (peer, reader, ch, dm) => { };
            BasisServerMessageRegistry.RegisterCore(channel, custom);
            Assert.Same(custom, BasisServerMessageRegistry.ResolveCore(channel));
        }
        finally
        {
            BasisServerMessageRegistry.RegisterCore(channel, original!);
        }
    }

    [Fact]
    public void RegisterServerPlugin_AssignsIdsAboveCoreRange_AndAdvertisesDescriptor()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        const string NameA = "com.basistests.registry.alpha";
        const string NameB = "com.basistests.registry.beta";

        ushort idA = BasisServerMessageRegistry.RegisterServerPlugin(NameA, DeliveryMethod.Sequenced, NoOpHandler, version: 3);
        ushort idB = BasisServerMessageRegistry.RegisterServerPlugin(NameB, DeliveryMethod.Unreliable, NoOpHandler);
        try
        {
            Assert.True(idA >= BasisNetworkCommons.TotalChannels, $"plugin id {idA} must clear the core channel range");
            Assert.True(idB >= BasisNetworkCommons.TotalChannels);
            Assert.NotEqual(idA, idB);

            Assert.Equal(idA, BasisServerMessageRegistry.RegisterServerPlugin(NameA, DeliveryMethod.Sequenced, NoOpHandler, version: 3));

            Assert.True(BasisServerMessageRegistry.TryGetPluginId(NameA, out ushort lookedUp));
            Assert.Equal(idA, lookedUp);

            SerializableBasis.BasisMessageDescriptor[] supply = BasisServerMessageRegistry.BuildSupply();
            SerializableBasis.BasisMessageDescriptor descriptorA = supply.Single(d => d.Name == NameA);
            Assert.Equal(idA, descriptorA.Id);
            Assert.Equal(BasisNetworkCommons.PluginSequencedChannel, descriptorA.Channel);
            Assert.Equal((byte)3, descriptorA.Version);
            Assert.NotEqual(0, descriptorA.Flags & (byte)SerializableBasis.BasisMessageFlags.Multiplexed);

            SerializableBasis.BasisMessageDescriptor descriptorB = supply.Single(d => d.Name == NameB);
            Assert.Equal(BasisNetworkCommons.PluginUnreliableChannel, descriptorB.Channel);
        }
        finally
        {
            BasisServerMessageRegistry.UnregisterPlugin(idA);
            BasisServerMessageRegistry.UnregisterPlugin(idB);
        }
    }

    [Fact]
    public void UnregisterPlugin_RemovesFromSupply_ButNameKeepsItsStableId()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        const string Name = "com.basistests.registry.gamma";

        ushort id = BasisServerMessageRegistry.RegisterServerPlugin(Name, DeliveryMethod.ReliableOrdered, NoOpHandler);
        Assert.Contains(BasisServerMessageRegistry.BuildSupply(), d => d.Name == Name);

        Assert.True(BasisServerMessageRegistry.UnregisterPlugin(id));
        Assert.False(BasisServerMessageRegistry.UnregisterPlugin(id));
        Assert.DoesNotContain(BasisServerMessageRegistry.BuildSupply(), d => d.Name == Name);

        Assert.True(BasisServerMessageRegistry.TryGetPluginId(Name, out ushort keptId));
        Assert.Equal(id, keptId);

        ushort again = BasisServerMessageRegistry.RegisterServerPlugin(Name, DeliveryMethod.ReliableOrdered, NoOpHandler);
        try
        {
            Assert.Equal(id, again);
        }
        finally
        {
            BasisServerMessageRegistry.UnregisterPlugin(again);
        }
    }

    [Fact]
    public void BuildSupply_AlwaysContainsTheFullCoreCatalog()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        SerializableBasis.BasisMessageDescriptor[] supply = BasisServerMessageRegistry.BuildSupply();
        foreach (SerializableBasis.BasisMessageDescriptor core in SerializableBasis.BasisMessageCatalog.BuildCore())
        {
            Assert.Contains(supply, d => d.Id == core.Id && d.Channel == core.Channel && d.Name == core.Name);
        }
    }

    [Fact]
    public void Subscriptions_FilterOnlyAfterAPeerReports()
    {
        BasisServerMessageRegistry.EnsureInitialized();
        const int PeerId = 987654;
        try
        {
            Assert.True(BasisServerMessageRegistry.IsSubscribed(PeerId, 64));

            BasisServerMessageRegistry.SetSubscription(PeerId, new ushort[] { 64, 70 });
            Assert.True(BasisServerMessageRegistry.IsSubscribed(PeerId, 64));
            Assert.True(BasisServerMessageRegistry.IsSubscribed(PeerId, 70));
            Assert.False(BasisServerMessageRegistry.IsSubscribed(PeerId, 65));

            BasisServerMessageRegistry.SetSubscription(PeerId, Array.Empty<ushort>());
            Assert.False(BasisServerMessageRegistry.IsSubscribed(PeerId, 64));

            BasisServerMessageRegistry.SetSubscription(PeerId, null!);
            Assert.False(BasisServerMessageRegistry.IsSubscribed(PeerId, 64));

            BasisServerMessageRegistry.ClearSubscription(PeerId);
            Assert.True(BasisServerMessageRegistry.IsSubscribed(PeerId, 64));
        }
        finally
        {
            BasisServerMessageRegistry.ClearSubscription(PeerId);
        }
    }
}
