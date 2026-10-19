using Basis.Network.Core;
using BasisNetworkServer;
using BasisNetworkServer.Security;
using BasisPermissions;
using Xunit;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class ResourceUnloadPolicyTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 28_000;

    private static (FakeNetPeer Peer, string Uuid) NewAuthenticatedPeer()
    {
        NetworkServer.AuthIdentity = Identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.7.7") { Tag = NetworkServer.AuthenticatedPeerTag };
        string uuid = $"unload-user-{Guid.NewGuid():N}";
        Identity.Register(uuid, id, peer);
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        return (peer, uuid);
    }

    private static void Remove(params FakeNetPeer[] peers)
    {
        foreach (FakeNetPeer peer in peers)
        {
            NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
        }
        NetworkServer.RebuildPeerSnapshot();
    }

    private static void SetRemovalLock(bool locked)
    {
        if (BasisGlobalLockManager.ContentRemovalLocked != locked) BasisGlobalLockManager.ToggleContentRemoval();
    }

    private static string Spawn(string creatorUuid, bool spawnedByProtectedPlayer)
    {
        string id = $"resource-{Guid.NewGuid():N}";
        BasisNetworkResourceManagement.UshortNetworkDatabase[id] = new LocalLoadResource
        {
            Mode = 0,
            LoadedNetID = id,
            UUIDOfCreator = creatorUuid,
            IsAdminLocked = spawnedByProtectedPlayer,
        };
        BasisNetworkResourceManagement.NoteResourceAdded(creatorUuid);
        return id;
    }

    private static bool Unload(FakeNetPeer from, string id)
    {
        BasisNetworkResourceManagement.UnloadResource(new UnLoadResource { Mode = 0, LoadedNetID = id }, from);
        return !BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey(id);
    }

    private static void Forget(string id)
    {
        if (BasisNetworkResourceManagement.UshortNetworkDatabase.TryRemove(id, out LocalLoadResource resource))
        {
            BasisNetworkResourceManagement.NoteResourceRemoved(resource.UUIDOfCreator);
        }
    }

    [Fact]
    public void AnyPlayer_CanUnloadAnotherPlayersProp_AndEveryoneIsTold()
    {
        (FakeNetPeer creator, string creatorUuid) = NewAuthenticatedPeer();
        (FakeNetPeer other, string _) = NewAuthenticatedPeer();
        SetRemovalLock(false);
        string id = Spawn(creatorUuid, false);
        try
        {
            Assert.True(Unload(other, id), "another player could not unload the prop");
            Assert.Contains(creator.Sent, s => s.Channel == BasisNetworkCommons.UnloadResourceChannel);
            Assert.Contains(other.Sent, s => s.Channel == BasisNetworkCommons.UnloadResourceChannel);
        }
        finally
        {
            Forget(id);
            Remove(creator, other);
        }
    }

    [Fact]
    public void PropSpawnedByAProtectedPlayer_OnlyModeratorsCanUnload()
    {
        (FakeNetPeer creator, string creatorUuid) = NewAuthenticatedPeer();
        (FakeNetPeer other, string _) = NewAuthenticatedPeer();
        (FakeNetPeer moderator, string moderatorUuid) = NewAuthenticatedPeer();
        SetRemovalLock(false);
        string id = Spawn(creatorUuid, true);
        try
        {
            Assert.False(Unload(other, id), "a player unloaded a protected player's prop");
            PermissionIntegration.Manager.AddUserNode(moderatorUuid, PermNodes.protection);
            try
            {
                Assert.True(Unload(moderator, id), "a moderator could not unload a protected player's prop");
            }
            finally
            {
                PermissionIntegration.Manager.RemoveUserNode(moderatorUuid, PermNodes.protection);
            }
        }
        finally
        {
            Forget(id);
            Remove(creator, other, moderator);
        }
    }

    [Fact]
    public void RemovalLock_LeavesOnlyTheCreatorAndModerators()
    {
        (FakeNetPeer creator, string creatorUuid) = NewAuthenticatedPeer();
        (FakeNetPeer other, string otherUuid) = NewAuthenticatedPeer();
        string first = Spawn(creatorUuid, false);
        string second = Spawn(creatorUuid, false);
        SetRemovalLock(true);
        try
        {
            Assert.False(Unload(other, first), "another player unloaded a prop while removal was locked");
            Assert.True(Unload(creator, first), "the creator could not unload their own prop while removal was locked");
            PermissionIntegration.Manager.AddUserNode(otherUuid, PermNodes.protection);
            try
            {
                Assert.True(Unload(other, second), "a moderator could not unload a prop while removal was locked");
            }
            finally
            {
                PermissionIntegration.Manager.RemoveUserNode(otherUuid, PermNodes.protection);
            }
        }
        finally
        {
            SetRemovalLock(false);
            Forget(first);
            Forget(second);
            Remove(creator, other);
        }
    }
}
