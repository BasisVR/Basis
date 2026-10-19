using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Network.Core;
using BasisDidLink;
using Xunit;
using static BasisDidLink.BasisDIDAuthIdentity;

namespace BasisServerTests;

public class AuthIdentityRecycledIdTests
{
    private static OnAuth Entry(NetPeer owner, string uuid) => new()
    {
        Did = new Did(uuid),
        Peer = owner,
    };

    private static BasisDIDAuthIdentity NewIdentity() => new();

    [Fact]
    public void RemoveConnection_ReleasesTheEntryItsOwnPeerCreated()
    {
        BasisDIDAuthIdentity identity = NewIdentity();
        try
        {
            int id = LifecycleSupport.NextPeerId();
            FakeNetPeer owner = LifecycleSupport.Peer(id);
            identity.AuthIdentity[id] = Entry(owner, LifecycleSupport.NewUuid());

            Assert.True(identity.RemoveConnection(id, owner));
            Assert.False(identity.AuthIdentity.ContainsKey(id));
            Assert.False(identity.RemoveConnection(id, owner));
        }
        finally
        {
            identity.DeInitialize();
        }
    }

    [Fact]
    public void RemoveConnection_LeavesAnEntryThatBelongsToAnotherConnection()
    {
        BasisDIDAuthIdentity identity = NewIdentity();
        try
        {
            int id = LifecycleSupport.NextPeerId();
            FakeNetPeer stale = LifecycleSupport.Peer(id);
            FakeNetPeer live = LifecycleSupport.Peer(id);
            identity.AuthIdentity[id] = Entry(live, LifecycleSupport.NewUuid());

            Assert.False(identity.RemoveConnection(id, stale));
            Assert.True(identity.AuthIdentity.ContainsKey(id));
            Assert.Same(live, identity.AuthIdentity[id].Peer);
        }
        finally
        {
            identity.DeInitialize();
        }
    }

    [Fact]
    public void RemoveConnection_WithoutAPeer_RemovesWhicheverEntryHoldsTheId()
    {
        BasisDIDAuthIdentity identity = NewIdentity();
        try
        {
            int id = LifecycleSupport.NextPeerId();
            identity.AuthIdentity[id] = Entry(LifecycleSupport.Peer(id), LifecycleSupport.NewUuid());

            identity.RemoveConnection(id);
            Assert.False(identity.AuthIdentity.ContainsKey(id));
        }
        finally
        {
            identity.DeInitialize();
        }
    }

    [Fact]
    public void NetIDToUUID_AnswersOnlyForTheConnectionThatOwnsTheEntry()
    {
        BasisDIDAuthIdentity identity = NewIdentity();
        try
        {
            int id = LifecycleSupport.NextPeerId();
            FakeNetPeer owner = LifecycleSupport.Peer(id);
            FakeNetPeer recycled = LifecycleSupport.Peer(id);
            string uuid = LifecycleSupport.NewUuid();
            identity.AuthIdentity[id] = Entry(owner, uuid);
            Assert.False(identity.NetIDToUUID(owner, out _));
            Assert.True(identity.TryAdmit(id, new Did(uuid), int.MaxValue));

            Assert.True(identity.NetIDToUUID(owner, out string found));
            Assert.Equal(uuid, found);

            Assert.False(identity.NetIDToUUID(recycled, out string leaked));
            Assert.Equal(string.Empty, leaked);
        }
        finally
        {
            identity.DeInitialize();
        }
    }

    [Fact]
    public void UUIDToNetID_IgnoresALoginThatHasNotBeenVerified()
    {
        BasisDIDAuthIdentity identity = NewIdentity();
        try
        {
            string uuid = LifecycleSupport.NewUuid();
            int admittedId = LifecycleSupport.NextPeerId();
            int pendingId = LifecycleSupport.NextPeerId();
            identity.AuthIdentity[pendingId] = Entry(LifecycleSupport.Peer(pendingId), uuid);
            Assert.False(identity.UUIDToNetID(uuid, out _));

            identity.AuthIdentity[admittedId] = Entry(LifecycleSupport.Peer(admittedId), uuid);
            Assert.True(identity.TryAdmit(admittedId, new Did(uuid), int.MaxValue));
            Assert.True(identity.UUIDToNetID(uuid, out int found));
            Assert.Equal(admittedId, found);
        }
        finally
        {
            identity.DeInitialize();
        }
    }

    [Fact]
    public void DuplicateCount_RisesOnAdmissionAndFallsWhenTheAdmittedConnectionLeaves()
    {
        BasisDIDAuthIdentity identity = NewIdentity();
        try
        {
            Did did = new Did(LifecycleSupport.NewUuid());
            int first = LifecycleSupport.NextPeerId();
            int second = LifecycleSupport.NextPeerId();
            identity.AuthIdentity[first] = Entry(LifecycleSupport.Peer(first), did.V);
            identity.AuthIdentity[second] = Entry(LifecycleSupport.Peer(second), did.V);
            Assert.Equal(0, identity.CheckForDuplicates(did));

            Assert.True(identity.TryAdmit(first, did, 1));
            Assert.Equal(1, identity.CheckForDuplicates(did));
            Assert.False(identity.TryAdmit(second, did, 1));
            Assert.False(identity.TryAdmit(first, did, int.MaxValue));

            identity.RemoveConnection(second);
            Assert.Equal(1, identity.CheckForDuplicates(did));
            identity.RemoveConnection(first);
            Assert.Equal(0, identity.CheckForDuplicates(did));
        }
        finally
        {
            identity.DeInitialize();
        }
    }

    [Fact]
    public void AStaleTimeout_CannotEvictTheConnectionThatInheritedTheId()
    {
        BasisDIDAuthIdentity identity = NewIdentity();
        try
        {
            int id = LifecycleSupport.NextPeerId();
            FakeNetPeer timedOut = LifecycleSupport.Peer(id);
            FakeNetPeer inherited = LifecycleSupport.Peer(id);
            identity.AuthIdentity[id] = Entry(inherited, LifecycleSupport.NewUuid());

            Assert.False(identity.RemoveConnection(id, timedOut));
            Assert.True(identity.AuthIdentity.ContainsKey(id));
            Assert.Same(inherited, identity.AuthIdentity[id].Peer);
        }
        finally
        {
            identity.DeInitialize();
        }
    }
}
