using Basis.Network.Core;
using LiteNetDataReader = LiteNetLib.Utils.NetDataReader;
using LiteNetDataWriter = LiteNetLib.Utils.NetDataWriter;
using Xunit;

namespace BasisServerTests;

public sealed class BasisConnectionRejectionTests
{
    [Fact]
    public void AuthenticationRejectedReason_UsesBareStringPayload()
    {
        LiteNetDataWriter writer = new LiteNetDataWriter();
        writer.Put(BasisNetworkCommons.AuthenticationRejectedReason);
        LiteNetDataReader reader = new LiteNetDataReader(writer);

        Assert.Equal("Authentication failed, Auth rejected", reader.PeekString());
    }
}
