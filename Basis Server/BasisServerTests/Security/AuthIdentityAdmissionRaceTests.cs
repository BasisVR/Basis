using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Network.Core;
using BasisDidLink;
using Xunit;
using static BasisDidLink.BasisDIDAuthIdentity;

namespace BasisServerTests;

public class AuthIdentityAdmissionRaceTests
{
    private static OnAuth Entry(NetPeer owner, Did did) => new()
    {
        Did = did,
        Peer = owner,
    };

    [Fact]
    public void ALoginWhoseEntryWasRemovedWhileItsSignatureWasChecked_IsNotAdmitted()
    {
        BasisDIDAuthIdentity identity = new BasisDIDAuthIdentity();
        try
        {
            Did did = new Did(LifecycleSupport.NewUuid());
            int id = LifecycleSupport.NextPeerId();
            FakeNetPeer peer = LifecycleSupport.Peer(id);
            identity.AuthIdentity[id] = Entry(peer, did);

            Assert.True(identity.RemoveConnection(id, peer));
            Assert.False(identity.TryAdmit(id, did, 2, peer));
            Assert.False(identity.TryAdmit(id, did, 2));
            Assert.Equal(0, identity.CheckForDuplicates(did));
            Assert.False(identity.NetIDToUUID(peer, out _));
            Assert.False(identity.UUIDToNetID(did.V, out _));
        }
        finally
        {
            identity.DeInitialize();
        }
    }

    [Fact]
    public void ALoginCannotBeAdmittedIntoTheSlotOfTheConnectionThatInheritedItsId()
    {
        BasisDIDAuthIdentity identity = new BasisDIDAuthIdentity();
        try
        {
            Did gone = new Did(LifecycleSupport.NewUuid());
            Did successor = new Did(LifecycleSupport.NewUuid());
            int id = LifecycleSupport.NextPeerId();
            FakeNetPeer departed = LifecycleSupport.Peer(id);
            FakeNetPeer inheritor = LifecycleSupport.Peer(id);
            identity.AuthIdentity[id] = Entry(departed, gone);
            Assert.True(identity.RemoveConnection(id, departed));
            identity.AuthIdentity[id] = Entry(inheritor, successor);

            Assert.False(identity.TryAdmit(id, gone, 2, departed));
            Assert.Equal(0, identity.CheckForDuplicates(gone));
            Assert.True(identity.TryAdmit(id, successor, 2, inheritor));
            Assert.True(identity.NetIDToUUID(inheritor, out string uuid));
            Assert.Equal(successor.V, uuid);
        }
        finally
        {
            identity.DeInitialize();
        }
    }

    [Fact]
    public void AbandonedLogins_NeverLockTheirDidOut()
    {
        BasisDIDAuthIdentity identity = new BasisDIDAuthIdentity();
        try
        {
            Did did = new Did(LifecycleSupport.NewUuid());
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int id = LifecycleSupport.NextPeerId();
                FakeNetPeer abandoned = LifecycleSupport.Peer(id);
                identity.AuthIdentity[id] = Entry(abandoned, did);
                identity.RemoveConnection(id);
                Assert.False(identity.TryAdmit(id, did, 2, abandoned));
            }
            Assert.Equal(0, identity.CheckForDuplicates(did));

            int liveId = LifecycleSupport.NextPeerId();
            FakeNetPeer live = LifecycleSupport.Peer(liveId);
            identity.AuthIdentity[liveId] = Entry(live, did);
            Assert.True(identity.TryAdmit(liveId, did, 2, live));
            Assert.Equal(1, identity.CheckForDuplicates(did));
        }
        finally
        {
            identity.DeInitialize();
        }
    }

    [Fact]
    public async Task AdmissionRacingRemoval_NeverLeavesTheDidCounted()
    {
        BasisDIDAuthIdentity identity = new BasisDIDAuthIdentity();
        try
        {
            Did did = new Did(LifecycleSupport.NewUuid());
            int admittedThenRemoved = 0, refused = 0;
            for (int round = 0; round < 2000; round++)
            {
                int id = LifecycleSupport.NextPeerId();
                FakeNetPeer peer = LifecycleSupport.Peer(id);
                identity.AuthIdentity[id] = Entry(peer, did);
                using Barrier start = new Barrier(2);
                bool admitted = false;
                Task admit = Task.Run(() =>
                {
                    start.SignalAndWait();
                    admitted = identity.TryAdmit(id, did, int.MaxValue, peer);
                });
                Task remove = Task.Run(() =>
                {
                    start.SignalAndWait();
                    identity.RemoveConnection(id, peer);
                });
                await Task.WhenAll(admit, remove);
                if (admitted) admittedThenRemoved++;
                else refused++;
                Assert.Equal(0, identity.CheckForDuplicates(did));
                Assert.False(identity.AuthIdentity.ContainsKey(id));
                Assert.False(identity.NetIDToUUID(peer, out _));
            }
            Assert.Equal(2000, admittedThenRemoved + refused);
        }
        finally
        {
            identity.DeInitialize();
        }
    }
}
