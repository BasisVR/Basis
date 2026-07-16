using Basis.Network.Core;
using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;
using System.Net;
using System.Text;
using Xunit;

namespace BasisServerTests;

public sealed class BasisLanDiscoveryTests
{
    [Fact]
    public void LimitUtf8_DoesNotSplitSurrogatePair()
    {
        string value = "abc😀def";

        string limited = BasisLanDiscoveryProtocol.LimitUtf8(value, 6);

        Assert.Equal("abc", limited);
        Assert.True(Encoding.UTF8.GetByteCount(limited) <= 6);
        Assert.False(limited.Length > 0 && char.IsHighSurrogate(limited[^1]));
    }

    [Fact]
    public void LongAsciiMotd_UsesOnlyEncodedChunksAndRoundTripsAtFullLimit()
    {
        string motd = new string('M', BasisLanDiscoveryProtocol.MaxMotdBytes);
        Guid id = Guid.NewGuid();
        ServiceProfile profile = CreateProfile(id, "ASCII Server", motd, requiresPassword: false);
        Dictionary<string, string> properties = ReadProperties(profile);

        Assert.DoesNotContain("motd", properties.Keys);
        Assert.Equal(3, properties.Keys.Count(key => key.StartsWith("motd64-", StringComparison.Ordinal)));

        BasisLanAdvertisement advertisement = Extract(profile);
        Assert.Equal(id, advertisement.InstanceId);
        Assert.Equal(motd, advertisement.Motd);
        Assert.False(advertisement.RequiresPassword);
    }

    [Fact]
    public void UnicodeMetadata_RoundTripsWithinUtf8Limits()
    {
        string rawName = string.Concat(Enumerable.Repeat("世界🌐", 20));
        string rawMotd = string.Concat(Enumerable.Repeat("こんにちは世界🌐", 50));
        string expectedName = BasisLanDiscoveryProtocol.LimitUtf8(
            rawName,
            BasisLanDiscoveryProtocol.MaxServerNameBytes);
        string expectedMotd = BasisLanDiscoveryProtocol.LimitUtf8(
            rawMotd,
            BasisLanDiscoveryProtocol.MaxMotdBytes);

        ServiceProfile profile = CreateProfile(Guid.NewGuid(), rawName, rawMotd, requiresPassword: true);
        BasisLanAdvertisement advertisement = Extract(profile);

        Assert.Equal(expectedName, advertisement.ServerName);
        Assert.Equal(expectedMotd, advertisement.Motd);
        Assert.True(advertisement.RequiresPassword);
        Assert.True(Encoding.UTF8.GetByteCount(advertisement.ServerName) <= BasisLanDiscoveryProtocol.MaxServerNameBytes);
        Assert.True(Encoding.UTF8.GetByteCount(advertisement.Motd) <= BasisLanDiscoveryProtocol.MaxMotdBytes);
    }

    [Fact]
    public void MalformedEncodedMetadata_ReturnsEmpty()
    {
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase)
        {
            ["name64-0"] = "not valid base64!",
        };

        string value = BasisLanDiscoveryProtocol.ReadMetadata(
            properties,
            "name64",
            BasisLanDiscoveryProtocol.MaxServerNameBytes);

        Assert.Empty(value);
    }

    [Fact]
    public void EmptyAddressRecords_UseResponseSourceAddress()
    {
        Guid id = Guid.NewGuid();
        ServiceProfile profile = BasisLanServerAnnouncer.CreateProfile(
            id,
            4296,
            BasisNetworkStackRegistry.LiteNetLibId,
            "Address fallback",
            string.Empty,
            false,
            Array.Empty<IPAddress>());
        IPAddress responseSource = IPAddress.Parse("192.168.1.25");

        BasisLanAdvertisement advertisement = Extract(profile, responseSource, out IPAddress address);

        Assert.Equal(id, advertisement.InstanceId);
        Assert.Equal(responseSource, address);
        Assert.DoesNotContain(profile.Resources, resource => resource is AddressRecord);
    }

    [Fact]
    public void AdvertisedIpv4_IsPreferredOverIpv6ResponseSource()
    {
        IPAddress advertisedIpv4 = IPAddress.Parse("192.168.1.25");
        IPAddress responseSource = new IPAddress(
            IPAddress.Parse("fe80::1234").GetAddressBytes(),
            7);
        ServiceProfile profile = BasisLanServerAnnouncer.CreateProfile(
            Guid.NewGuid(),
            4296,
            BasisNetworkStackRegistry.LiteNetLibId,
            "Android host",
            string.Empty,
            false,
            new[] { advertisedIpv4 });

        Extract(profile, responseSource, out IPAddress address);

        Assert.Equal(advertisedIpv4, address);
    }

    [Fact]
    public void Ipv4ResponseSource_IsPreferredOverOtherAdvertisedIpv4()
    {
        IPAddress advertisedIpv4 = IPAddress.Parse("10.0.0.5");
        IPAddress responseSource = IPAddress.Parse("192.168.1.25");
        ServiceProfile profile = BasisLanServerAnnouncer.CreateProfile(
            Guid.NewGuid(),
            4296,
            BasisNetworkStackRegistry.LiteNetLibId,
            "Wi-Fi host",
            string.Empty,
            false,
            new[] { advertisedIpv4 });

        Extract(profile, responseSource, out IPAddress address);

        Assert.Equal(responseSource, address);
    }

    [Fact]
    public void AddressPreference_PrioritizesRoutableAddressesOverLinkLocal()
    {
        IPAddress ipv4 = IPAddress.Parse("192.168.1.10");
        IPAddress ipv6 = IPAddress.Parse("2001:db8::10");
        IPAddress apipa = IPAddress.Parse("169.254.10.20");
        IPAddress ipv6LinkLocal = IPAddress.Parse("fe80::10");

        Assert.True(BasisLanAddressUtility.PreferenceRank(ipv4)
            < BasisLanAddressUtility.PreferenceRank(ipv6));
        Assert.True(BasisLanAddressUtility.PreferenceRank(ipv6)
            < BasisLanAddressUtility.PreferenceRank(apipa));
        Assert.True(BasisLanAddressUtility.PreferenceRank(apipa)
            < BasisLanAddressUtility.PreferenceRank(ipv6LinkLocal));
        Assert.False(BasisLanAddressUtility.IsUsable(IPAddress.Parse("239.255.42.99")));
        Assert.False(BasisLanAddressUtility.IsUsable(IPAddress.Loopback));
        Assert.True(BasisLanAddressUtility.IsUsable(IPAddress.Loopback, allowLoopback: true));
    }

    private static ServiceProfile CreateProfile(
        Guid id,
        string serverName,
        string motd,
        bool requiresPassword)
    {
        return BasisLanServerAnnouncer.CreateProfile(
            id,
            4296,
            BasisNetworkStackRegistry.LiteNetLibId,
            serverName,
            motd,
            requiresPassword,
            new[] { IPAddress.Parse("192.168.1.25") });
    }

    private static BasisLanAdvertisement Extract(ServiceProfile profile)
    {
        IPAddress expectedAddress = IPAddress.Parse("192.168.1.25");
        BasisLanAdvertisement advertisement = Extract(profile, expectedAddress, out IPAddress address);
        Assert.Equal(expectedAddress, address);
        return advertisement;
    }

    private static BasisLanAdvertisement Extract(
        ServiceProfile profile,
        IPAddress responseSource,
        out IPAddress address)
    {
        Message message = new Message();
        foreach (ResourceRecord resource in profile.Resources)
        {
            if (resource is PTRRecord)
            {
                message.Answers.Add(resource);
            }
            else
            {
                message.AdditionalRecords.Add(resource);
            }
        }

        Assert.True(BasisLanServerBrowser.TryExtractAdvertisement(
            message,
            profile.FullyQualifiedName,
            responseSource,
            out BasisLanAdvertisement advertisement,
            out IPAddress? selectedAddress));
        address = Assert.IsType<IPAddress>(selectedAddress);
        return advertisement;
    }

    private static Dictionary<string, string> ReadProperties(ServiceProfile profile)
    {
        TXTRecord text = Assert.Single(profile.Resources.OfType<TXTRecord>());
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in text.Strings)
        {
            int separator = entry.IndexOf('=');
            Assert.True(separator > 0);
            properties[entry[..separator]] = entry[(separator + 1)..];
        }
        return properties;
    }
}
