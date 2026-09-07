using Basis.Network.Core;
using BasisNetworkServer;
using Xunit;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class CustomServerDataTests
{
    private class TestDataPublisher : IBasisCustomServerDataPublisher
    {
        public byte[] InitialState;
        public List<byte[]> GetInitialState() => InitialState == null ? new List<byte[]>() : new List<byte[]> { InitialState };
    }

    [Fact]
    public void RegisterChannel_SucceedsAndCanReRegister()
    {
        TestDataPublisher publisher = new TestDataPublisher();
        string channelName = "test.channel.reg";
        
        // We can't easily check existence anymore via API, but we can verify registration doesn't throw
        BasisNetworkHandleCustomServerData.RegisterChannel(channelName, publisher);
        
        // Duplicate should still throw
        Assert.Throws<InvalidOperationException>(() => BasisNetworkHandleCustomServerData.RegisterChannel(channelName, publisher));
        
        BasisNetworkHandleCustomServerData.UnregisterChannel(channelName);
        
        // Should be able to register again after unregister
        BasisNetworkHandleCustomServerData.RegisterChannel(channelName, publisher);
        BasisNetworkHandleCustomServerData.UnregisterChannel(channelName);
    }

    [Fact]
    public void RegisterChannel_DuplicateName_ThrowsInvalidOperationException()
    {
        TestDataPublisher publisher = new TestDataPublisher();
        string channelName = "test.channel.dup";
        BasisNetworkHandleCustomServerData.RegisterChannel(channelName, publisher);
        
        Assert.Throws<InvalidOperationException>(() => BasisNetworkHandleCustomServerData.RegisterChannel(channelName, publisher));
        
        BasisNetworkHandleCustomServerData.UnregisterChannel(channelName);
    }

    [Fact]
    public void HandleSubscribeRequest_SendsInitialStateAndAddsSubscriber()
    {
        using var scope = new ServerStaticsScope();
        string channelName = "test.subscribe";
        byte[] initialState = new byte[] { 1, 2, 3 };
        var provider = new TestDataPublisher { InitialState = initialState };
        BasisNetworkHandleCustomServerData.RegisterChannel(channelName, provider);

        var peer = new FakeNetPeer(1, "127.0.0.1");
        var requestId = Guid.NewGuid();
        var request = new CustomServerDataSubscribeRequest
        {
            ChannelName = channelName,
            RequestID = requestId
        };

        BasisNetworkHandleCustomServerData.HandleSubscribeRequest(peer, request);

        // Verify initial state sent
        var sentMessage = peer.Sent.FirstOrDefault(s => s.Channel == BasisNetworkCommons.CustomServerDataChannel);
        Assert.NotNull(sentMessage);

        var reader = new NetDataReader(sentMessage.Data);
        Assert.Equal(BasisNetworkCommons.CustomServerData_InitialState, reader.GetByte());
        
        var response = new CustomServerDataInitialState();
        Assert.True(response.Deserialize(reader));
        Assert.Equal(channelName, response.ChannelName);
        Assert.Equal(initialState, response.Data);
        Assert.Equal(requestId, response.RequestID);

        BasisNetworkHandleCustomServerData.UnregisterChannel(channelName);
    }

    [Fact]
    public void HandleUnsubscribeRequest_RemovesSpecificSubscription()
    {
        using var scope = new ServerStaticsScope();
        string channelName = "test.unsubscribe";
        var provider = new TestDataPublisher();
        BasisNetworkHandleCustomServerData.RegisterChannel(channelName, provider);

        var peer = new FakeNetPeer(1, "127.0.0.1");
        NetworkServer.AuthenticatedPeers[1] = peer;
        NetworkServer.RebuildPeerSnapshot();

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        BasisNetworkHandleCustomServerData.HandleSubscribeRequest(peer, new CustomServerDataSubscribeRequest { ChannelName = channelName, RequestID = id1 });
        BasisNetworkHandleCustomServerData.HandleSubscribeRequest(peer, new CustomServerDataSubscribeRequest { ChannelName = channelName, RequestID = id2 });

        // Both subscribed, verify publish reaches peer
        byte[] updateData = new byte[] { 42 };
        BasisNetworkHandleCustomServerData.Publish(channelName, updateData);
        
        // One update should be sent (peer is unique subscriber)
        Assert.Single(peer.Sent.Where(s => s.Data[0] == BasisNetworkCommons.CustomServerData_Message));
        peer.Sent.Clear();

        // Unsubscribe one
        BasisNetworkHandleCustomServerData.HandleUnsubscribeRequest(peer, new CustomServerDataUnsubscribeRequest { ChannelName = channelName, RequestID = id1 });

        // Still one subscription left, should still receive updates
        BasisNetworkHandleCustomServerData.Publish(channelName, updateData);
        Assert.Single(peer.Sent.Where(s => s.Data[0] == BasisNetworkCommons.CustomServerData_Message));
        peer.Sent.Clear();

        // Unsubscribe second
        BasisNetworkHandleCustomServerData.HandleUnsubscribeRequest(peer, new CustomServerDataUnsubscribeRequest { ChannelName = channelName, RequestID = id2 });

        // No more subscriptions, should not receive updates
        BasisNetworkHandleCustomServerData.Publish(channelName, updateData);
        Assert.Empty(peer.Sent.Where(s => s.Data[0] == BasisNetworkCommons.CustomServerData_Message));

        BasisNetworkHandleCustomServerData.UnregisterChannel(channelName);
    }

    [Fact]
    public void RemovePlayerSubscriptions_CleansUpAllChannels()
    {
        using var scope = new ServerStaticsScope();
        string chan1 = "test.cleanup.1";
        string chan2 = "test.cleanup.2";
        BasisNetworkHandleCustomServerData.RegisterChannel(chan1, new TestDataPublisher());
        BasisNetworkHandleCustomServerData.RegisterChannel(chan2, new TestDataPublisher());

        var peer = new FakeNetPeer(1, "127.0.0.1");
        NetworkServer.AuthenticatedPeers[1] = peer;
        NetworkServer.RebuildPeerSnapshot();

        BasisNetworkHandleCustomServerData.HandleSubscribeRequest(peer, new CustomServerDataSubscribeRequest { ChannelName = chan1, RequestID = Guid.NewGuid() });
        BasisNetworkHandleCustomServerData.HandleSubscribeRequest(peer, new CustomServerDataSubscribeRequest { ChannelName = chan2, RequestID = Guid.NewGuid() });

        BasisNetworkHandleCustomServerData.RemovePlayerSubscriptions(peer.Id);

        byte[] updateData = new byte[] { 0 };
        BasisNetworkHandleCustomServerData.Publish(chan1, updateData);
        BasisNetworkHandleCustomServerData.Publish(chan2, updateData);

        Assert.Empty(peer.Sent.Where(s => s.Data[0] == BasisNetworkCommons.CustomServerData_Message));

        BasisNetworkHandleCustomServerData.UnregisterChannel(chan1);
        BasisNetworkHandleCustomServerData.UnregisterChannel(chan2);
    }
}
