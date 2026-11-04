using Basis.Network.Core;
using BasisNetworking.InitialData;
using BasisNetworkServer;
using BasisPermissions;
using Xunit;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class ResourceSaveToDiskTests : IDisposable
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 29_000;
    private readonly string folder = Path.Combine(Path.GetTempPath(), "basis-savetodisk-" + Guid.NewGuid().ToString("N"));
    private readonly string previousFolder = BasisNetworkResourceDisk.Folder;
    private readonly List<FakeNetPeer> peers = new();
    private readonly List<string> spawned = new();

    public ResourceSaveToDiskTests()
    {
        Directory.CreateDirectory(folder);
        BasisNetworkResourceDisk.Folder = folder;
        NetworkServer.Configuration ??= new Configuration();
    }

    public void Dispose()
    {
        foreach (string id in spawned)
        {
            BasisNetworkResourceDisk.OnUnloaded(id);
            if (BasisNetworkResourceManagement.UshortNetworkDatabase.TryRemove(id, out LocalLoadResource r)) BasisNetworkResourceManagement.NoteResourceRemoved(r.UUIDOfCreator);
        }
        foreach (FakeNetPeer peer in peers) NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
        NetworkServer.RebuildPeerSnapshot();
        BasisNetworkResourceDisk.Folder = previousFolder;
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private (FakeNetPeer Peer, string Uuid) NewPeer(bool canSave)
    {
        NetworkServer.AuthIdentity = Identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.8.8") { Tag = NetworkServer.AuthenticatedPeerTag };
        string uuid = $"savetodisk-user-{Guid.NewGuid():N}";
        Identity.Register(uuid, id, peer);
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        if (canSave) PermissionIntegration.Manager.AddUserNode(uuid, PermNodes.ResourceSaveToDisk);
        peers.Add(peer);
        return (peer, uuid);
    }

    private string Spawn(string creatorUuid)
    {
        string id = $"resource-{Guid.NewGuid():N}";
        BasisNetworkResourceManagement.UshortNetworkDatabase[id] = new LocalLoadResource
        {
            Mode = 0,
            LoadedNetID = id,
            CombinedURL = "https://example.invalid/prop.BEE",
            UnlockPassword = "pw",
            UUIDOfCreator = creatorUuid,
            QuaternionW = 1f,
            ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f,
            Static = true,
        };
        BasisNetworkResourceManagement.NoteResourceAdded(creatorUuid);
        spawned.Add(id);
        return id;
    }

    private static void Request(FakeNetPeer peer, string id, byte op, float x = 0f)
    {
        ModifyResource request = new ModifyResource { LoadedNetID = id, Mode = 0, Op = op, HasTransform = op == ModifyResource.OpSaveToDisk, PositionX = x, QuaternionW = 1f, ScaleX = 2f, ScaleY = 2f, ScaleZ = 2f };
        NetDataWriter writer = new NetDataWriter();
        request.Serialize(writer);
        NetDataReader reader = new NetDataReader(writer.CopyData());
        ModifyResource back = new ModifyResource();
        back.Deserialize(reader);
        BasisNetworkResourceDisk.HandleRequest(back, peer);
    }

    [Fact]
    public void Save_WritesLoadableXml_AtCurrentTransform_AndMarksPersistent()
    {
        (FakeNetPeer admin, string adminUuid) = NewPeer(true);
        string id = Spawn(adminUuid);

        Request(admin, id, ModifyResource.OpSaveToDisk, 5f);

        Assert.True(BasisNetworkResourceDisk.IsSaved(id));
        BasisLoadableConfiguration[] configs = BasisLoadableConfiguration.LoadAllFromFolder(folder, out string[] files);
        BasisLoadableConfiguration saved = Assert.Single(configs);
        Assert.Equal(id, saved.LoadedNetID);
        Assert.Equal("https://example.invalid/prop.BEE", saved.CombinedURL);
        Assert.Equal(5f, saved.PositionX);
        Assert.Equal(2f, saved.ScaleY);
        Assert.True(saved.Persist);
        Assert.True(saved.Static);
        Assert.Equal(Path.Combine(folder, BasisNetworkResourceDisk.FileNameFor(id)), Assert.Single(files));
        LocalLoadResource live = BasisNetworkResourceManagement.UshortNetworkDatabase[id];
        Assert.True(live.Persist);
        Assert.True(live.IsAdminLocked);
        Assert.Contains(admin.Sent, s => s.Channel == BasisNetworkCommons.ModifyResourceChannel);
    }

    [Fact]
    public void Save_WithoutPermission_IsRefused()
    {
        (FakeNetPeer player, string uuid) = NewPeer(false);
        string id = Spawn(uuid);

        Request(player, id, ModifyResource.OpSaveToDisk);

        Assert.False(BasisNetworkResourceDisk.IsSaved(id));
        Assert.Empty(Directory.GetFiles(folder, "*.xml"));
        Assert.False(BasisNetworkResourceManagement.UshortNetworkDatabase[id].Persist);
    }

    [Fact]
    public void Remove_DeletesFile_ButKeepsTheLiveItem()
    {
        (FakeNetPeer admin, string adminUuid) = NewPeer(true);
        string id = Spawn(adminUuid);
        Request(admin, id, ModifyResource.OpSaveToDisk);

        Request(admin, id, ModifyResource.OpRemoveFromDisk);

        Assert.False(BasisNetworkResourceDisk.IsSaved(id));
        Assert.Empty(Directory.GetFiles(folder, "*.xml"));
        Assert.True(BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey(id));
    }

    [Fact]
    public void UnloadingSavedContent_DeletesItsFile()
    {
        (FakeNetPeer admin, string adminUuid) = NewPeer(true);
        PermissionIntegration.Manager.AddUserNode(adminUuid, PermNodes.protection);
        string id = Spawn(adminUuid);
        Request(admin, id, ModifyResource.OpSaveToDisk);

        BasisNetworkResourceManagement.UnloadResource(new UnLoadResource { Mode = 0, LoadedNetID = id }, admin);

        Assert.False(BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey(id));
        Assert.False(BasisNetworkResourceDisk.IsSaved(id));
        Assert.Empty(Directory.GetFiles(folder, "*.xml"));
    }

    [Fact]
    public void LateJoiner_IsToldWhatIsSaved()
    {
        (FakeNetPeer admin, string adminUuid) = NewPeer(true);
        string id = Spawn(adminUuid);
        Request(admin, id, ModifyResource.OpSaveToDisk);
        (FakeNetPeer joiner, string _) = NewPeer(false);
        joiner.Sent.Clear();

        BasisNetworkResourceDisk.SendStateToPeer(joiner, BasisNetworkResourceManagement.UshortNetworkDatabase[id]);

        Assert.Contains(joiner.Sent, s => s.Channel == BasisNetworkCommons.ModifyResourceChannel);
    }

    [Theory]
    [InlineData("abc-DEF_123", "saved-abc-DEF_123.xml")]
    public void FileName_KeepsSafeIds(string id, string expected) => Assert.Equal(expected, BasisNetworkResourceDisk.FileNameFor(id));

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a b")]
    [InlineData("ボール")]
    public void FileName_HashesUnsafeIds(string id)
    {
        string name = BasisNetworkResourceDisk.FileNameFor(id);
        Assert.Matches("^saved-[0-9a-f]{32}\\.xml$", name);
    }

    [Fact]
    public void ModifyResource_LegacyPayload_ReadsAsStaticRequest()
    {
        NetDataWriter writer = new NetDataWriter();
        writer.Put("legacy");
        writer.Put((byte)0);
        writer.Put(true);
        writer.Put(false);
        ModifyResource back = new ModifyResource();
        back.Deserialize(new NetDataReader(writer.CopyData()));
        Assert.False(back.HasExtension);
        Assert.Equal(ModifyResource.OpStatic, back.Op);
        Assert.True(back.Static);
    }

    [Fact]
    public void ModifyResource_Extension_RoundTripsTransform()
    {
        ModifyResource msg = new ModifyResource { LoadedNetID = "x", Op = ModifyResource.OpSaveToDisk, SavedToDisk = true, HasTransform = true, PositionY = 3f, QuaternionW = 1f, ScaleZ = 4f };
        NetDataWriter writer = new NetDataWriter();
        msg.Serialize(writer);
        ModifyResource back = new ModifyResource();
        back.Deserialize(new NetDataReader(writer.CopyData()));
        Assert.True(back.HasExtension);
        Assert.True(back.HasTransform);
        Assert.True(back.SavedToDisk);
        Assert.Equal(ModifyResource.OpSaveToDisk, back.Op);
        Assert.Equal(3f, back.PositionY);
        Assert.Equal(4f, back.ScaleZ);
    }
}
