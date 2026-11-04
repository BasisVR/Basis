using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

public class BasisSrvResolverTests
{
    private static readonly byte[] QueryNameBytes = { 6, (byte)'_', (byte)'b', (byte)'a', (byte)'s', (byte)'i', (byte)'s', 4, (byte)'_', (byte)'u', (byte)'d', (byte)'p', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0 };

    [Fact]
    public void BuildQuery_EncodesHeaderLabelsTypeAndClass()
    {
        byte[] query = BasisSrvResolver.BuildQuery("_basis._udp.example.com", 0xBEEF);
        Assert.Equal(new byte[] { 0xBE, 0xEF, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, query.Take(12).ToArray());
        Assert.Equal(QueryNameBytes, query.Skip(12).Take(QueryNameBytes.Length).ToArray());
        Assert.Equal(new byte[] { 0, 33, 0, 1 }, query.Skip(12 + QueryNameBytes.Length).ToArray());
    }

    [Theory]
    [InlineData("a..b")]
    [InlineData("café.example.com")]
    public void BuildQuery_RejectsInvalidNames(string name)
    {
        Assert.Throws<ArgumentException>(() => BasisSrvResolver.BuildQuery(name, 1));
    }

    [Fact]
    public void BuildQuery_RejectsOverlongLabel()
    {
        Assert.Throws<ArgumentException>(() => BasisSrvResolver.BuildQuery(new string('a', 64) + ".com", 1));
    }

    [Fact]
    public void TryParseResponse_ReadsSrvAnswersWithCompressedNames()
    {
        byte[] response = BuildResponse(0x1234, 0, Srv(10, 60, 4296, "game", 300), Srv(5, 0, 5000, null, 120));
        Assert.True(BasisSrvResolver.TryParseResponse(response, 0x1234, out BasisSrvRecord[] records, out uint ttl));
        Assert.Equal(2, records.Length);
        Assert.Equal(10, records[0].Priority);
        Assert.Equal(60, records[0].Weight);
        Assert.Equal(4296, records[0].Port);
        Assert.Equal("game.example.com", records[0].Target);
        Assert.Equal("example.com", records[1].Target);
        Assert.Equal(5000, records[1].Port);
        Assert.Equal(120u, ttl);
    }

    [Fact]
    public void TryParseResponse_NxDomainIsAnAnswerWithNoRecords()
    {
        Assert.True(BasisSrvResolver.TryParseResponse(BuildResponse(7, 3), 7, out BasisSrvRecord[] records, out uint ttl));
        Assert.Empty(records);
        Assert.Equal((uint)BasisSrvResolver.NegativeCacheSeconds, ttl);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void TryParseResponse_ServerFailuresAreRejected(int rcode)
    {
        Assert.False(BasisSrvResolver.TryParseResponse(BuildResponse(7, rcode), 7, out _, out _));
    }

    [Fact]
    public void TryParseResponse_RejectsMismatchedIdAndTruncatedData()
    {
        byte[] response = BuildResponse(9, 0, Srv(1, 1, 4296, "game", 60));
        Assert.False(BasisSrvResolver.TryParseResponse(response, 10, out _, out _));
        for (int length = 0; length < response.Length; length++)
            Assert.False(BasisSrvResolver.TryParseResponse(response.Take(length).ToArray(), 9, out BasisSrvRecord[] partial, out _) && partial.Length > 0);
    }

    [Fact]
    public void TryReadName_StopsOnPointerLoops()
    {
        byte[] data = { 0xC0, 0x02, 0xC0, 0x00 };
        Assert.False(BasisSrvResolver.TryReadName(data, 0, out _));
    }

    [Fact]
    public void Order_SortsByPriorityAndDropsUnavailableTargets()
    {
        BasisSrvRecord[] records =
        {
            new BasisSrvRecord { Priority = 20, Weight = 0, Port = 3, Target = "c.example.com" },
            new BasisSrvRecord { Priority = 10, Weight = 5, Port = 1, Target = "a.example.com" },
            new BasisSrvRecord { Priority = 10, Weight = 0, Port = 4296, Target = "" },
            new BasisSrvRecord { Priority = 15, Weight = 1, Port = 2, Target = "b.example.com" },
        };
        BasisSrvRecord[] ordered = BasisSrvResolver.Order(records, new Random(1));
        Assert.Equal(new ushort[] { 1, 2, 3 }, ordered.Select(r => r.Port).ToArray());
    }

    [Fact]
    public void Order_SingleDotTargetMeansServiceUnavailable()
    {
        Assert.Empty(BasisSrvResolver.Order(new[] { new BasisSrvRecord { Priority = 0, Weight = 0, Port = 0, Target = "" } }, new Random(1)));
    }

    [Fact]
    public void Order_PicksWithinAPriorityByWeight()
    {
        BasisSrvRecord[] records =
        {
            new BasisSrvRecord { Priority = 1, Weight = 90, Port = 1, Target = "heavy.example.com" },
            new BasisSrvRecord { Priority = 1, Weight = 10, Port = 2, Target = "light.example.com" },
        };
        Random rng = new Random(42);
        int heavyFirst = 0;
        for (int i = 0; i < 4000; i++)
        {
            BasisSrvRecord[] ordered = BasisSrvResolver.Order(records, rng);
            Assert.Equal(2, ordered.Length);
            if (ordered[0].Port == 1) heavyFirst++;
        }
        Assert.InRange(heavyFirst, 3400, 3800);
    }

    [Theory]
    [InlineData("example.com", "_basis._udp.example.com")]
    [InlineData("Example.COM.", "_basis._udp.Example.COM")]
    [InlineData("bücher.example", "_basis._udp.xn--bcher-kva.example")]
    public void TryBuildServiceName_BuildsRfc2782Names(string host, string expected)
    {
        Assert.True(BasisSrvResolver.TryBuildServiceName(host, BasisSrvResolver.UdpProtocol, out string name));
        Assert.Equal(expected, name, ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("wss://example.com")]
    public void TryBuildServiceName_SkipsLiteralsAndSingleLabels(string host)
    {
        Assert.False(BasisSrvResolver.TryBuildServiceName(host, BasisSrvResolver.UdpProtocol, out _));
    }

    [Theory]
    [InlineData("example.com", "4296", true)]
    [InlineData("example.com", "", true)]
    [InlineData("example.com", "5000", false)]
    [InlineData("10.0.0.2", "4296", false)]
    public void TryGetServiceName_OnlyLooksUpWhenThePortIsTheDefault(string host, string port, bool expected)
    {
        ConnectionTarget target = new ConnectionTarget(BasisNetworkStackRegistry.LiteNetLibId, host);
        target.Set(ConnectionTarget.Keys.Address, host);
        if (port.Length > 0) target.Set(ConnectionTarget.Keys.Port, port);
        Assert.Equal(expected, BasisSrvResolver.TryGetServiceName(target, out _));
    }

    [Fact]
    public void TryGetServiceName_IgnoresStacksWithoutAProtocol()
    {
        ConnectionTarget target = new ConnectionTarget("not-a-registered-stack", "example.com");
        target.Set(ConnectionTarget.Keys.Address, "example.com");
        Assert.False(BasisSrvResolver.TryGetServiceName(target, out _));
    }

    [Fact]
    public void WithEndpoint_KeepsPasswordAndReplacesAddressAndPort()
    {
        ConnectionTarget source = new ConnectionTarget(BasisNetworkStackRegistry.LiteNetLibId, "example.com");
        source.Set(ConnectionTarget.Keys.Address, "example.com");
        source.Set(ConnectionTarget.Keys.Port, "4296");
        source.Set(ConnectionTarget.Keys.Password, "secret");
        ConnectionTarget target = BasisSrvResolver.WithEndpoint(source, "game.example.com", 5000);
        Assert.Equal("game.example.com", target.Get(ConnectionTarget.Keys.Address));
        Assert.Equal("5000", target.Get(ConnectionTarget.Keys.Port));
        Assert.Equal("secret", target.Get(ConnectionTarget.Keys.Password));
        Assert.Equal("game.example.com:5000", target.Raw);
        Assert.Equal("4296", source.Get(ConnectionTarget.Keys.Port));
    }

    private static (ushort Priority, ushort Weight, ushort Port, string Label, uint Ttl) Srv(ushort priority, ushort weight, ushort port, string label, uint ttl) => (priority, weight, port, label, ttl);

    private static byte[] BuildResponse(ushort id, int rcode, params (ushort Priority, ushort Weight, ushort Port, string Label, uint Ttl)[] answers)
    {
        List<byte> bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x81, (byte)(0x80 | rcode), 0, 1, 0, (byte)answers.Length, 0, 0, 0, 0 };
        bytes.AddRange(QueryNameBytes);
        bytes.AddRange(new byte[] { 0, 33, 0, 1 });
        const int exampleComOffset = 12 + 12;
        foreach (var answer in answers)
        {
            bytes.AddRange(new byte[] { 0xC0, 12, 0, 33, 0, 1, (byte)(answer.Ttl >> 24), (byte)(answer.Ttl >> 16), (byte)(answer.Ttl >> 8), (byte)answer.Ttl });
            List<byte> rdata = new List<byte> { (byte)(answer.Priority >> 8), (byte)answer.Priority, (byte)(answer.Weight >> 8), (byte)answer.Weight, (byte)(answer.Port >> 8), (byte)answer.Port };
            if (answer.Label != null)
            {
                rdata.Add((byte)answer.Label.Length);
                rdata.AddRange(answer.Label.Select(c => (byte)c));
            }
            rdata.AddRange(new byte[] { 0xC0, exampleComOffset });
            bytes.Add((byte)(rdata.Count >> 8));
            bytes.Add((byte)rdata.Count);
            bytes.AddRange(rdata);
        }
        return bytes.ToArray();
    }
}
