using Basis.Network.Core;
using Basis.Network.Server;
using System.Net;
using System.Xml.Serialization;
using Xunit;

namespace BasisServerTests;

public sealed class DocumentedTransportConfig
{
    public int ConfigVersion = 0;
    public int Alpha = 1;
    public string Beta = "b";
}

internal sealed class SchemeTargetParser : IConnectionTargetParser
{
    public void Parse(ConnectionTarget target)
    {
        string raw = target.Raw ?? string.Empty;
        int hash = raw.IndexOf('#');
        target.Set(ConnectionTarget.Keys.Address, hash >= 0 ? raw.Substring(0, hash) : raw);
        target.Set(ConnectionTarget.Keys.Password, hash >= 0 ? raw.Substring(hash + 1) : string.Empty);
    }

    public string Format(ConnectionTarget target) => target.Get(ConnectionTarget.Keys.Address, string.Empty);
}

internal sealed class ReportingTransport : NetManager, IBasisTransportHealth
{
    private readonly string _id;

    public ReportingTransport(string id)
    {
        _id = id;
    }

    public void Start(IPAddress iPv4Address, IPAddress iPv6Address, int setPort) { }
    public void Stop() { }
    public NetPeer Connect(string sIP, int port, NetDataWriter writer) => null!;
    public bool SendUnconnectedMessage(NetDataWriter writer, IPEndPoint remoteEndPoint) => false;
    public NetStatistics Statistics { get; } = new NetStatistics();
    public int ConnectedPeersCount => 2;
    public string StackId => _id;

    public void WriteHealth(IBasisHealthWriter writer)
    {
        writer.Number("queued", 7);
        writer.Flag("secure", true);
        writer.Text("note", "say \"hi\"");
    }
}

public class TransportHookTests
{
    [Fact]
    public void AnAddressMatcherRoutesItsAddressesToItsStack()
    {
        string id = ConfigTestSupport.NewStackId();
        string scheme = id + "://";
        BasisNetworkStackRegistry.Register(id, "Scheme", (listener, configuration) => new ScriptedTransport(id, listener));
        BasisNetworkStackRegistry.RegisterParser(id, new SchemeTargetParser());
        BasisNetworkStackRegistry.RegisterAddressMatcher(id, address => address.StartsWith(scheme, StringComparison.Ordinal));

        Assert.True(BasisNetworkStackRegistry.TryMatchAddress(scheme + "host/room#secret", out string matched));
        Assert.Equal(id, matched);
        ConnectionTarget target = BasisNetworkStackRegistry.ParseAddress(scheme + "host/room#secret");
        Assert.Equal(id, target.StackId);
        Assert.Equal(scheme + "host/room", target.Get(ConnectionTarget.Keys.Address));
        Assert.Equal("secret", target.Get(ConnectionTarget.Keys.Password));
    }

    [Fact]
    public void AnAddressNoStackClaimsGoesToTheDefaultStack()
    {
        Assert.False(BasisNetworkStackRegistry.TryMatchAddress("example.com:5000#secret", out string matched));
        Assert.Null(matched);
        Assert.False(BasisNetworkStackRegistry.TryMatchAddress("  ", out _));
        ConnectionTarget target = BasisNetworkStackRegistry.ParseAddress("example.com:5000#secret");
        Assert.Equal(BasisNetworkStackRegistry.DefaultId, target.StackId);
        Assert.Equal("example.com", target.Get(ConnectionTarget.Keys.Address));
        Assert.Equal("5000", target.Get(ConnectionTarget.Keys.Port));
        Assert.Equal("secret", target.Get(ConnectionTarget.Keys.Password));
    }

    [Fact]
    public void AMatcherThatThrowsIsSkipped()
    {
        string broken = ConfigTestSupport.NewStackId();
        string working = ConfigTestSupport.NewStackId();
        string address = "throwtest://" + working;
        BasisNetworkStackRegistry.Register(broken, "Broken", (listener, configuration) => null!);
        BasisNetworkStackRegistry.Register(working, "Working", (listener, configuration) => null!);
        BasisNetworkStackRegistry.RegisterAddressMatcher(broken, value => value == address ? throw new InvalidOperationException("matcher failed") : false);
        BasisNetworkStackRegistry.RegisterAddressMatcher(working, value => value == address);

        Assert.True(BasisNetworkStackRegistry.TryMatchAddress(address, out string matched));
        Assert.Equal(working, matched);
    }

    [Fact]
    public void APackageCanDocumentItsConfigFile()
    {
        BasisConfigXmlDocs.Register(typeof(DocumentedTransportConfig), " Documented header ",
            new BasisConfigXmlDocs.FieldDoc("Alpha", " Alpha doc ", " Section one "),
            new BasisConfigXmlDocs.FieldDoc("Beta", " Beta doc "));
        StringWriter writer = new StringWriter();
        BasisConfigXmlDocs.Serialize(new XmlSerializer(typeof(DocumentedTransportConfig)), typeof(DocumentedTransportConfig), new DocumentedTransportConfig(), writer);
        string xml = writer.ToString();

        Assert.Contains("<!-- Documented header -->", xml);
        int section = xml.IndexOf("<!-- Section one -->", StringComparison.Ordinal);
        int alphaDoc = xml.IndexOf("<!-- Alpha doc -->", StringComparison.Ordinal);
        int alpha = xml.IndexOf("<Alpha>", StringComparison.Ordinal);
        Assert.True(section >= 0 && section < alphaDoc && alphaDoc < alpha);
        Assert.Contains("<!-- Beta doc -->", xml);
    }
}

[Collection("BasisServer shared network statics")]
public class TransportHealthTests
{
    [Fact]
    public void HealthListsEveryTransportWithTheFieldsItReports()
    {
        NetManager previous = NetworkServer.Server;
        try
        {
            NetworkServer.Server = new BasisMultiTransportNetManager(new[] { "plain", "reporting" }, new EventBasedNetListener(), new Configuration(),
                (id, listener, configuration) => id == "reporting" ? new ReportingTransport(id) : new ScriptedTransport(id, listener));

            Assert.Equal("[{\"id\":\"plain\",\"running\":true,\"peers\":0},{\"id\":\"reporting\",\"running\":true,\"peers\":2,\"queued\":7,\"secure\":true,\"note\":\"say \\\"hi\\\"\"}]",
                BasisNetworkHealthCheck.BuildTransportsJson());
        }
        finally
        {
            NetworkServer.Server = previous;
        }
    }
}
