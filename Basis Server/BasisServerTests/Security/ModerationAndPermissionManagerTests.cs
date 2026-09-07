using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Basis.Network.Core;
using Basis.Network.Server.Auth;
using BasisNetworkServer.Security;
using BasisPermissions;
using Xunit;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisServerTests;

// ─────────────────────────────────────────────────────────────────────────────
// Test doubles. Basis.Network.Core.NetPeer and IAuthIdentity are interfaces, so
// the moderation paths (which resolve UUID -> NetPeer through NetworkServer
// statics) can be exercised without a live socket.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class FakeNetPeer : NetPeer
{
    // Stands in for the transport peer the real LNLNetPeer wraps. LNLNetPeer is allocated fresh on
    // every event — connect, disconnect, each received packet — and gets its identity from this,
    // not from the wrapper object, so anything comparing peers with ReferenceEquals is always false
    // in production while looking correct in a test that reuses one instance.
    private readonly object _connection;

    public FakeNetPeer(int id, string address)
    {
        Id = id;
        Address = IPAddress.Parse(address);
        _connection = new object();
    }

    private FakeNetPeer(int id, IPAddress address, object connection)
    {
        Id = id;
        Address = address;
        _connection = connection;
    }

    /// <summary>A distinct wrapper object over the same connection, as the transport hands out.</summary>
    public FakeNetPeer Wrap() => new(Id, Address, _connection);

    public override bool Equals(object obj) => obj is FakeNetPeer other && ReferenceEquals(_connection, other._connection);

    public override int GetHashCode() => _connection.GetHashCode();

    public List<(byte[] Data, byte Channel, DeliveryMethod Method)> Sent { get; } = new();
    public List<byte[]> DisconnectData { get; } = new();
    public int DisconnectCalls { get; private set; }

    public int Id { get; }
    public IPAddress Address { get; }
    public int RemoteId => Id;
    public int RoundTripTime => 0;
    public float TimeSinceLastPacket => 0f;
    public long RemoteTimeDelta => 0;
    public int Mtu => 1200;
    public object Tag { get; set; } = new object();

    public void Disconnect() => DisconnectCalls++;

    public void Disconnect(byte[] b)
    {
        DisconnectCalls++;
        DisconnectData.Add(b);
    }

    public void DisconnectForce() => DisconnectCalls++;

    public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod)
        => Sent.Add(((byte[])data.Clone(), channelNumber, deliveryMethod));

    // Copy immediately: the server returns writers to a pool (Reset) right after TrySend.
    public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod)
        => Sent.Add((data.AsReadOnlySpan().ToArray(), channelNumber, deliveryMethod));

    public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0)
    {
    }

    public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod) => 0;
}

internal sealed class MapAuthIdentity : IAuthIdentity
{
    private readonly ConcurrentDictionary<string, int> _uuidToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string> _idToUuid = new();
    private readonly ConcurrentDictionary<int, NetPeer> _owner = new();

    public readonly List<int> Released = new();

    public void Register(string uuid, int netId)
    {
        _uuidToId[uuid] = netId;
        _idToUuid[netId] = uuid;
    }

    public void Register(string uuid, int netId, NetPeer owner)
    {
        Register(uuid, netId);
        _owner[netId] = owner;
    }

    public void ProcessConnection(Configuration Configuration, ConnectionRequest ConnectionRequest, NetPeer NetPeer)
    {
    }

    public void DeInitialize()
    {
    }

    public void RemoveConnection(int NetPeer) => RemoveConnection(NetPeer, null);

    public bool RemoveConnection(int Id, NetPeer Expected)
    {
        if (Expected != null && _owner.TryGetValue(Id, out NetPeer? owner) && !Equals(owner, Expected))
        {
            return false;
        }
        if (!_idToUuid.TryRemove(Id, out _))
        {
            return false;
        }
        _owner.TryRemove(Id, out _);
        lock (Released) { Released.Add(Id); }
        return true;
    }

    public bool NetIDToUUID(NetPeer Peer, out string UUID)
    {
        if (_idToUuid.TryGetValue(Peer.Id, out string? found))
        {
            UUID = found;
            return true;
        }

        UUID = string.Empty;
        return false;
    }

    public bool UUIDToNetID(string UUID, out int Peer) => _uuidToId.TryGetValue(UUID, out Peer);
}

// ─────────────────────────────────────────────────────────────────────────────
// PermissionManager (BasisNetworkServer\Security\PermissionManager.cs).
// Every test builds its own PermissionManager instance, so this class is safe
// to run in parallel with everything else. The two tests that touch the
// PermissionIntegration singleton only use GUID-suffixed uuids/nodes.
// ─────────────────────────────────────────────────────────────────────────────

public class PermissionManagerTests
{
    private static readonly string IoRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "perm-test-io");

    /// <summary>Fresh manager whose debounced saves never fire and whose xml path is unique.</summary>
    private static PermissionManager CreateManager()
    {
        PermissionManager manager = new PermissionManager
        {
            SaveDebounceMs = int.MaxValue,
        };
        manager.SetXmlPath(UniqueXmlPath());
        return manager;
    }

    private static string UniqueXmlPath()
    {
        Directory.CreateDirectory(IoRoot);
        return Path.Combine(IoRoot, $"perms-{Guid.NewGuid():N}.xml");
    }

    private static string NewUuid() => $"user-{Guid.NewGuid():N}";

    // ---- stability contract: permission node wire/persistence names ----

    [Fact]
    public void PermNodes_StringValues_ArePinned()
    {
        // These strings are persisted in permissions.xml and sent over the wire;
        // renaming any of them is a breaking change.
        Assert.Equal("*", PermNodes.All);
        Assert.Equal("basis.command.help", PermNodes.help);
        Assert.Equal("basis.server.stats", PermNodes.ServerStats);
        Assert.Equal("basis.resource.load.world", PermNodes.ResourceLoadWorld);
        Assert.Equal("basis.resource.unload.world", PermNodes.ResourceUnloadWorld);
        Assert.Equal("basis.resource.load.prop", PermNodes.ResourceLoadProp);
        Assert.Equal("basis.resource.unload.prop", PermNodes.ResourceUnloadProp);
        Assert.Equal("basis.resource.load.avatar", PermNodes.ResourceLoadAvatar);
        Assert.Equal("basis.resource.unload.avatar", PermNodes.ResourceUnloadAvatar);
        Assert.Equal("basis.resource.lockbypass.avatar", PermNodes.ResourceLockBypassAvatar);
        Assert.Equal("basis.resource.lockbypass.prop", PermNodes.ResourceLockBypassProp);
        Assert.Equal("basis.resource.lockbypass.world", PermNodes.ResourceLockBypassWorld);
        Assert.Equal("basis.resource.lockbypass.server", PermNodes.ResourceLockBypassServer);
        Assert.Equal("basis.ownership.transfer", PermNodes.OwnershipTransfer);
        Assert.Equal("basis.ownership.remove", PermNodes.OwnershipRemove);
        Assert.Equal("basis.ownership.get", PermNodes.OwnershipGet);
        Assert.Equal("basis.contentshare.delete", PermNodes.ContentShareDelete);
        Assert.Equal("basis.contentshare.create", PermNodes.ContentShareCreate);
        Assert.Equal("basis.protection", PermNodes.protection);
        Assert.Equal("basis.configuration", PermNodes.ConfigurationEditor);
        Assert.Equal("basis.moderation", PermNodes.PlayerModeration);
        Assert.Equal("basis.moderation.ban", PermNodes.ModerationBan);
        Assert.Equal("basis.moderation.kick", PermNodes.ModerationKick);
        Assert.Equal("basis.moderation.ipban", PermNodes.ModerationIpBan);
        Assert.Equal("basis.moderation.unban", PermNodes.ModerationUnban);
        Assert.Equal("basis.moderation.unbanip", PermNodes.ModerationUnbanIp);
        Assert.Equal("basis.moderation.message", PermNodes.ModerationMessage);
        Assert.Equal("basis.moderation.messageall", PermNodes.ModerationMessageAll);
        Assert.Equal("basis.moderation.teleport", PermNodes.ModerationTeleport);
        Assert.Equal("basis.moderation.announce", PermNodes.ModerationAnnounce);
        Assert.Equal("basis.moderation.globallock", PermNodes.ModerationGlobalLock);
        Assert.Equal("basis.moderation.headlessaudio", PermNodes.ModerationHeadlessAudio);
        Assert.Equal("basis.moderation.opusbitrate", PermNodes.ModerationOpusBitrate);
        Assert.Equal("basis.moderation.fullqualitybroadcast", PermNodes.ModerationFullQualityBroadcast);
        // Gotcha worth pinning: the allowlist node's VALUE says "whitelist".
        Assert.Equal("basis.moderation.whitelist", PermNodes.ModerationAllowlist);
        Assert.Equal("basis.admin.logs", PermNodes.AdminLogs);
        Assert.Equal("basis.permissions.view", PermNodes.PermissionsView);
        Assert.Equal("basis.permissions.edit", PermNodes.PermissionsEdit);
    }

    // ---- stability contract: permission-name -> bit index wire mapping ----

    private static readonly string[] ExpectedBitsetOrder =
    {
        "*",
        "basis.server.stats",
        "basis.resource.load.world",
        "basis.resource.unload.world",
        "basis.resource.load.prop",
        "basis.resource.unload.prop",
        "basis.resource.load.avatar",
        "basis.resource.unload.avatar",
        "basis.ownership.transfer",
        "basis.ownership.remove",
        "basis.ownership.get",
        "basis.contentshare.delete",
        "basis.contentshare.create",
        "basis.protection",
        "basis.configuration",
        "basis.moderation",
        "basis.moderation.ban",
        "basis.moderation.kick",
        "basis.moderation.ipban",
        "basis.moderation.unban",
        "basis.moderation.unbanip",
        "basis.moderation.message",
        "basis.moderation.messageall",
        "basis.moderation.teleport",
        "basis.moderation.announce",
        "basis.permissions.view",
        "basis.permissions.edit",
        "basis.moderation.headlessaudio",
    };

    [Fact]
    public void PermissionBitsetMap_NodeToBitIndex_IsPinned()
    {
        // The bitset rides ServerMetaDataMessage; the map is append-only wire format.
        Assert.Equal(ExpectedBitsetOrder.Length, PermissionBitsetMap.KnownCount);
        Assert.Equal((ExpectedBitsetOrder.Length + 7) / 8, PermissionBitsetMap.ByteCount);

        // Index 0 is "*" and is special-cased (sets every bit), so start at 1.
        for (int i = 1; i < ExpectedBitsetOrder.Length; i++)
        {
            PermissionBitsetMap.Encode(new[] { ExpectedBitsetOrder[i] }, out byte[] bitset, out string[] extras);
            Assert.Empty(extras);
            for (int bit = 0; bit < PermissionBitsetMap.KnownCount; bit++)
            {
                bool set = (bitset[bit >> 3] & (1 << (bit & 7))) != 0;
                Assert.Equal(bit == i, set);
            }
        }
    }

    [Fact]
    public void PermissionBitsetMap_EncodeDecode_RoundTripsWildcardExtrasAndDenies()
    {
        // Wildcard expands to every known node.
        PermissionBitsetMap.Encode(new[] { "*" }, out byte[] bitset, out string[] extras);
        Assert.Empty(extras);
        HashSet<string> all = PermissionBitsetMap.Decode(bitset, extras);
        Assert.Equal(PermissionBitsetMap.KnownCount, all.Count);
        Assert.Contains(PermNodes.ModerationBan, all);

        // Unknown nodes travel through the extras side channel.
        string custom = $"custom.node.{Guid.NewGuid():N}";
        PermissionBitsetMap.Encode(new[] { PermNodes.help, custom }, out bitset, out extras);
        Assert.Contains(custom, extras);
        HashSet<string> decoded = PermissionBitsetMap.Decode(bitset, extras);
        Assert.Contains(custom, decoded);

        // Denied nodes clear their bit even when wildcard set them.
        PermissionBitsetMap.Encode(new[] { "*" }, out bitset, out extras, new[] { PermNodes.ModerationKick });
        decoded = PermissionBitsetMap.Decode(bitset, extras);
        Assert.DoesNotContain(PermNodes.ModerationKick, decoded);
        Assert.Contains(PermNodes.ModerationBan, decoded);
    }

    // ---- default role / unknown player ----

    [Fact]
    public void UnknownUser_OnFreshManager_HasNoPermissions()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();

        Assert.False(m.Has(uuid, PermNodes.help));
        Assert.False(m.Has(uuid, PermNodes.All));
        Assert.Empty(m.GetAllAllowedRules(uuid));
        Assert.Empty(m.GetAllDeniedRules(uuid));
        Assert.False(m.TryGetUser(uuid, out _));
    }

    [Fact]
    public void UnknownUser_InheritsImplicitDefaultGroup_AndCacheInvalidates()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();

        // Prime the effective-permission cache while nothing is granted.
        Assert.False(m.Has(uuid, "test.zone.enter"));

        // A group change bumps the version and must invalidate that cached result,
        // even for a user that was never explicitly created.
        m.AddGroupNode("default", "test.zone.enter");
        Assert.True(m.Has(uuid, "test.zone.enter"));

        m.RemoveGroupNode("default", "test.zone.enter");
        Assert.False(m.Has(uuid, "test.zone.enter"));
    }

    [Fact]
    public void EnsureDefaults_GrantsBaselineToUnknownUsers()
    {
        PermissionManager m = CreateManager();
        m.EnsureDefaults();
        string uuid = NewUuid();

        Assert.True(m.Has(uuid, PermNodes.help));
        Assert.True(m.Has(uuid, PermNodes.ResourceLoadAvatar));
        Assert.True(m.Has(uuid, PermNodes.OwnershipTransfer));
        Assert.True(m.Has(uuid, PermNodes.ContentShareCreate));
        Assert.False(m.Has(uuid, PermNodes.ModerationKick));
        Assert.False(m.Has(uuid, PermNodes.protection));
        Assert.False(m.Has(uuid, PermNodes.All));

        Assert.True(m.TryGetGroup("default", out var def));
        Assert.True(def.Nodes.SetEquals(new[]
        {
            PermNodes.help,
            PermNodes.ResourceLoadProp, PermNodes.ResourceUnloadProp,
            PermNodes.ResourceLoadAvatar, PermNodes.ResourceUnloadAvatar,
            PermNodes.ResourceLoadWorld, PermNodes.ResourceUnloadWorld,
            PermNodes.OwnershipTransfer, PermNodes.OwnershipRemove, PermNodes.OwnershipGet,
            PermNodes.ContentShareDelete, PermNodes.ContentShareCreate,
        }));
    }

    [Fact]
    public void EnsureDefaults_ModeratorInheritsDefault_AdminGetsWildcard()
    {
        PermissionManager m = CreateManager();
        m.EnsureDefaults();

        string mod = NewUuid();
        m.AddUserToGroup(mod, "moderator");
        Assert.True(m.Has(mod, PermNodes.ModerationKick));
        Assert.True(m.Has(mod, PermNodes.ModerationBan));
        Assert.True(m.Has(mod, PermNodes.PermissionsView));
        Assert.True(m.Has(mod, PermNodes.ResourceLockBypassAvatar));
        Assert.True(m.Has(mod, PermNodes.ChatLockBypass));
        Assert.True(m.Has(mod, PermNodes.VoiceLockBypass));
        Assert.True(m.Has(mod, PermNodes.ModerationForceAvatar));
        Assert.True(m.Has(mod, PermNodes.ModerationLocomotion));
        Assert.True(m.Has(mod, PermNodes.help)); // via "default" parent
        Assert.False(m.Has(mod, PermNodes.PermissionsEdit));
        Assert.False(m.Has(mod, PermNodes.ConfigurationEditor));
        Assert.False(m.Has(mod, PermNodes.AdminLogs));
        Assert.False(m.Has(mod, $"random.node.{Guid.NewGuid():N}"));

        string admin = NewUuid();
        m.AddUserToGroup(admin, "admin");
        Assert.True(m.Has(admin, PermNodes.PermissionsEdit));
        Assert.True(m.Has(admin, PermNodes.protection));
        Assert.True(m.Has(admin, $"random.node.{Guid.NewGuid():N}")); // "*" wildcard
        Assert.Contains("*", m.GetAllAllowedRules(admin));

        Assert.True(m.TryGetGroup("moderator", out var modGroup));
        Assert.Equal(23, modGroup.Nodes.Count);
        Assert.Contains("default", modGroup.Parents);

        Assert.True(m.TryGetGroup("admin", out var adminGroup));
        Assert.Contains("*", adminGroup.Nodes);
        Assert.Contains("moderator", adminGroup.Parents);
    }

    [Fact]
    public void IsInGroup_WalksParentChain_AndAgreesWithNodeInheritance()
    {
        PermissionManager m = CreateManager();
        m.EnsureDefaults();

        string admin = NewUuid();
        m.AddUserToGroup(admin, "admin");

        // admin -> moderator -> default, the chain EnsureDefaults builds. A role check has to see
        // the whole chain, or a world badging "moderator" would miss every admin.
        Assert.True(m.IsInGroup(admin, "admin"));
        Assert.True(m.IsInGroup(admin, "moderator"));
        Assert.True(m.IsInGroup(admin, "default"));

        // ...and not the other way round.
        string mod = NewUuid();
        m.AddUserToGroup(mod, "moderator");
        Assert.True(m.IsInGroup(mod, "moderator"));
        Assert.False(m.IsInGroup(mod, "admin"));

        Assert.True(m.IsInGroup(admin, "ADMIN"), "group names compare case-insensitively");
        Assert.False(m.IsInGroup(admin, $"absent-{Guid.NewGuid():N}"));
        Assert.False(m.IsInGroup(admin, ""));
        Assert.False(m.IsInGroup("", "admin"));
    }

    [Fact]
    public void IsInGroup_UnknownUserIsInDefault_AndSurvivesGroupCycles()
    {
        PermissionManager m = CreateManager();
        m.EnsureDefaults();

        // Same implicit membership BuildEffective_NoLock grants, so a role check and a node check
        // answer consistently for someone with no row of their own.
        string stranger = NewUuid();
        Assert.True(m.IsInGroup(stranger, "default"));
        Assert.False(m.IsInGroup(stranger, "moderator"));

        // A parent cycle is reachable through the admin UI; the walk has to terminate on it.
        m.GetOrCreateGroup("loop-a");
        m.GetOrCreateGroup("loop-b");
        m.AddGroupParent("loop-a", "loop-b");
        m.AddGroupParent("loop-b", "loop-a");

        string looped = NewUuid();
        m.AddUserToGroup(looped, "loop-a");
        Assert.True(m.IsInGroup(looped, "loop-a"));
        Assert.True(m.IsInGroup(looped, "loop-b"));
        Assert.False(m.IsInGroup(looped, "admin"));

        // A membership naming a group with no row still counts — AddUserToGroup does not create
        // the group, and hand-edited xml can name one that was never defined. The membership on
        // the user is the fact being asked about; inheritance simply has nothing to walk.
        string rowless = NewUuid();
        m.AddUserToGroup(rowless, "never-defined");
        Assert.True(m.IsInGroup(rowless, "never-defined"));
        Assert.False(m.IsInGroup(rowless, "loop-b"));

        // Deleting a group does scrub it off every user, so that path leaves nothing behind.
        m.DeleteGroup("loop-a");
        Assert.False(m.IsInGroup(looped, "loop-a"));
    }

    [Fact]
    public void EnsureDefaults_IsIdempotent_AndNeverOverwritesExistingGroups()
    {
        PermissionManager m = CreateManager();
        m.EnsureDefaults();
        m.EnsureDefaults();
        Assert.Equal(3, m.Snapshot().Groups.Count);

        // A pre-existing "default" group is left exactly as the operator configured it.
        PermissionManager custom = CreateManager();
        custom.AddGroupNode("default", "custom.only.node");
        custom.EnsureDefaults();

        string uuid = NewUuid();
        Assert.True(custom.Has(uuid, "custom.only.node"));
        Assert.False(custom.Has(uuid, PermNodes.help));
        Assert.True(custom.TryGetGroup("default", out var def));
        Assert.Single(def.Nodes);
    }

    // ---- precedence as implemented (deny-wins decision table) ----

    [Fact]
    public void UserDeny_OverridesGroupAllow()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();

        m.AddGroupNode("default", "world.build");
        m.AddUserNode(uuid, "-world.build");

        Assert.False(m.Has(uuid, "world.build"));
        Assert.Contains("world.build", m.GetAllDeniedRules(uuid));
        Assert.DoesNotContain("world.build", m.GetAllAllowedRules(uuid));
    }

    [Fact]
    public void GroupDeny_CannotBeReallowedByUserGrant()
    {
        // ApplyRawNodes never overwrites an existing deny: once "default" denies a
        // node, a direct user-level allow of the same node does NOT re-enable it.
        PermissionManager m = CreateManager();
        string uuid = NewUuid();

        m.AddGroupNode("default", "-world.destroy");
        m.AddUserNode(uuid, "world.destroy");

        Assert.False(m.Has(uuid, "world.destroy"));
    }

    [Fact]
    public void GroupInheritance_ParentsApplyFirst_DenyAlwaysSticks()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();

        m.AddGroupNode("parent-grp", "node.one");
        m.AddGroupNode("parent-grp", "-node.two");
        m.AddGroupNode("child-grp", "-node.one"); // child deny overrides parent allow
        m.AddGroupNode("child-grp", "node.two");  // child allow cannot undo parent deny
        m.AddGroupNode("child-grp", "node.three");
        m.AddGroupParent("child-grp", "parent-grp");
        m.AddUserToGroup(uuid, "child-grp");

        Assert.False(m.Has(uuid, "node.one"));
        Assert.False(m.Has(uuid, "node.two"));
        Assert.True(m.Has(uuid, "node.three"));
    }

    [Fact]
    public void WildcardNodes_ClimbByDotSegments()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();
        m.AddUserNode(uuid, "a.b.*");

        Assert.True(m.Has(uuid, "a.b.c"));
        Assert.True(m.Has(uuid, "a.b.c.d"));   // deeper nodes climb to a.b.*
        Assert.True(m.Has(uuid, "a.b.*"));     // the wildcard key itself
        Assert.False(m.Has(uuid, "a.b"));      // "a.b.*" does NOT grant the stem
        Assert.False(m.Has(uuid, "a.c"));
        Assert.False(m.Has(uuid, "other"));
    }

    [Fact]
    public void MoreSpecificRule_WinsAtQueryTime_EvenAgainstDeny()
    {
        // Query resolution is exact -> nearest wildcard -> "*". A specific wildcard
        // deny beats the global allow, and an exact allow beats a wildcard deny.
        PermissionManager m = CreateManager();
        string uuid = NewUuid();
        m.AddUserNode(uuid, "*");
        m.AddUserNode(uuid, "-a.b.*");
        m.AddUserNode(uuid, "a.b.special");

        Assert.True(m.Has(uuid, "x.y"));          // global allow
        Assert.False(m.Has(uuid, "a.b.c"));       // wildcard deny beats "*"
        Assert.True(m.Has(uuid, "a.b.special"));  // exact allow beats wildcard deny
        Assert.False(m.Has(uuid, "a.b.*"));       // the denied wildcard key itself
    }

    [Fact]
    public void UuidsAndNodes_AreCaseInsensitive_AndTrimmed()
    {
        PermissionManager m = CreateManager();
        string uuid = $"User-{Guid.NewGuid():N}";

        m.AddUserNode(uuid.ToUpperInvariant(), "  Spaced.Node  ");

        Assert.True(m.Has(uuid.ToLowerInvariant(), "spaced.node"));
        Assert.True(m.Has(uuid, " SPACED.NODE "));
        Assert.True(m.TryGetUser(uuid.ToUpperInvariant(), out var viaUpper));
        Assert.True(m.TryGetUser(uuid.ToLowerInvariant(), out var viaLower));
        Assert.Same(viaUpper, viaLower);
        Assert.Contains("spaced.node", viaUpper.Nodes); // HashSet comparer is OrdinalIgnoreCase
    }

    [Fact]
    public void GroupParentCycles_ResolveWithoutHanging()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();

        m.AddGroupParent("cyc-a", "cyc-b");
        m.AddGroupParent("cyc-b", "cyc-a");
        m.AddGroupParent("cyc-self", "cyc-self");
        m.AddGroupNode("cyc-a", "cycle.node");
        m.AddUserToGroup(uuid, "cyc-b");
        m.AddUserToGroup(uuid, "cyc-self");

        Assert.True(m.Has(uuid, "cycle.node"));
    }

    // ---- invalid input ----

    [Fact]
    public void InvalidInputs_AreSafeNoOps_OrThrowPinnedExceptions()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();

        // Whitespace/null uuid or node: mutators silently do nothing.
        m.AddUserNode(null!, "some.node");
        m.AddUserNode("", "some.node");
        m.AddUserNode("   ", "some.node");
        m.AddUserNode(uuid, null!);
        m.AddUserNode(uuid, "");
        m.AddUserToGroup(uuid, "  ");
        m.AddGroupNode("", "some.node");
        m.RemoveUserNode(uuid, "never.granted");
        m.RemoveUserFromGroup(NewUuid(), "nope");
        Assert.False(m.TryGetUser(uuid, out _)); // nothing above created the user
        Assert.Empty(m.Snapshot().Users);
        Assert.Empty(m.Snapshot().Groups);

        Assert.False(m.DeleteGroup(""));
        Assert.False(m.DeleteGroup($"missing-{Guid.NewGuid():N}"));

        // Unknown/blank permission names simply resolve to false.
        Assert.False(m.Has(uuid, ""));
        Assert.False(m.Has(uuid, "   "));
        Assert.False(m.Has(uuid, null!));

        // Sharp edges pinned as currently implemented.
        Assert.Throws<ArgumentNullException>(() => m.Has(null!, "some.node"));
        Assert.Throws<ArgumentException>(() => m.SetXmlPath(""));
        Assert.Throws<ArgumentException>(() => m.SetXmlPath(null!));
    }

    // ---- structure APIs ----

    [Fact]
    public void GetOrCreateUser_AddsDefaultMembership_AndIsIdempotent()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();

        PermissionUser first = m.GetOrCreateUser(uuid);
        PermissionUser second = m.GetOrCreateUser(uuid);

        Assert.Same(first, second);
        Assert.Equal(uuid, first.Uuid);
        Assert.Contains("default", first.Groups);
        Assert.True(m.TryGetUser(uuid, out _));

        PermissionGroup g1 = m.GetOrCreateGroup("builders");
        PermissionGroup g2 = m.GetOrCreateGroup("builders");
        Assert.Same(g1, g2);
        Assert.Equal("builders", g1.Name);
    }

    [Fact]
    public void Mutations_RaiseOnPermissionsChanged_UuidForUserOps_NullForGroupOps()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();
        List<string?> events = new();
        m.OnPermissionsChanged += u => events.Add(u);

        m.AddUserNode(uuid, "ev.node");        // -> uuid
        m.AddUserNode(uuid, "ev.node");        // duplicate -> no event
        m.AddUserToGroup(uuid, "ev-group");    // -> uuid
        m.AddGroupNode("ev-group", "g.node");  // -> null
        m.AddGroupParent("ev-group", "ev-parent"); // -> null
        m.RemoveUserNode(uuid, "ev.node");     // -> uuid
        m.RemoveUserNode(uuid, "ev.node");     // already gone -> no event
        m.RemoveGroupNode("ev-group", "g.node"); // -> null
        Assert.True(m.DeleteGroup("ev-group"));  // -> null
        Assert.False(m.DeleteGroup("ev-group")); // unknown -> no event

        Assert.Equal(new List<string?> { uuid, uuid, null, null, uuid, null, null }, events);
    }

    [Fact]
    public void Snapshot_IsADeepCopy()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();
        m.AddUserNode(uuid, "real.node");
        m.AddGroupNode("snap-group", "group.node");

        PermissionStore snap = m.Snapshot();
        snap.Users[uuid].Nodes.Add("evil.injected");
        snap.Groups["snap-group"].Nodes.Add("evil.group.injected");
        snap.Users.Clear();

        Assert.False(m.Has(uuid, "evil.injected"));
        Assert.True(m.TryGetUser(uuid, out var user));
        Assert.DoesNotContain("evil.injected", user.Nodes);
        Assert.True(m.TryGetGroup("snap-group", out var group));
        Assert.DoesNotContain("evil.group.injected", group.Nodes);
    }

    [Fact]
    public void DeleteGroup_ScrubsUserMembershipsAndGroupParents()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();

        m.AddGroupNode("doomed", "doomed.node");
        m.AddUserToGroup(uuid, "doomed");
        m.AddGroupParent("survivor", "doomed");
        Assert.True(m.Has(uuid, "doomed.node"));

        Assert.True(m.DeleteGroup("doomed"));

        Assert.False(m.Has(uuid, "doomed.node"));
        Assert.False(m.TryGetGroup("doomed", out _));
        Assert.True(m.TryGetUser(uuid, out var user));
        Assert.DoesNotContain("doomed", user.Groups);
        Assert.True(m.TryGetGroup("survivor", out var survivor));
        Assert.Empty(survivor.Parents);
    }

    [Fact]
    public void EnumerationApis_ReturnExactlyTheGrantedAndDeniedRules()
    {
        PermissionManager m = CreateManager();
        string uuid = NewUuid();
        m.AddUserNode(uuid, "alpha.one");
        m.AddUserNode(uuid, "-beta.two");

        IReadOnlyCollection<string> allowed = m.GetAllAllowedRules(uuid);
        IReadOnlyCollection<string> denied = m.GetAllDeniedRules(uuid);

        Assert.Single(allowed);
        Assert.Contains("alpha.one", allowed);
        Assert.Single(denied);
        Assert.Contains("beta.two", denied);
    }

    // ---- persistence ----

    [Fact]
    public void SaveToXml_LoadFromXml_RoundTripsGroupsUsersDeniesAndParents()
    {
        string path = UniqueXmlPath();
        try
        {
            PermissionManager a = CreateManager();
            string u1 = NewUuid();
            string u2 = NewUuid();
            a.AddGroupNode("staff", "perm.a");
            a.AddGroupNode("staff", "-perm.b");
            a.AddGroupParent("staff", "base");
            a.AddGroupNode("base", "perm.base");
            a.AddUserToGroup(u1, "staff");
            a.AddUserNode(u1, "user.only");
            a.AddUserNode(u2, "-blocked.node");

            a.SaveToXml(path);
            Assert.True(File.Exists(path));

            PermissionManager b = CreateManager();
            string configuredPath = b.GetXmlPath();
            b.LoadFromXml(path);
            Assert.Equal(configuredPath, b.GetXmlPath()); // pathOverride must not stick

            Assert.True(b.TryGetGroup("staff", out var staff));
            Assert.True(staff.Nodes.SetEquals(new[] { "perm.a", "-perm.b" }));
            Assert.True(staff.Parents.SetEquals(new[] { "base" }));

            Assert.True(b.TryGetUser(u1, out var user1));
            Assert.True(user1.Groups.SetEquals(new[] { "default", "staff" }));
            Assert.True(user1.Nodes.SetEquals(new[] { "user.only" }));

            Assert.True(b.Has(u1, "perm.a"));
            Assert.True(b.Has(u1, "perm.base")); // via staff -> base inheritance
            Assert.True(b.Has(u1, "user.only"));
            Assert.False(b.Has(u1, "perm.b"));   // deny survived the round trip
            Assert.Contains("blocked.node", b.GetAllDeniedRules(u2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromXml_MissingFile_ReplacesStoreWithEmpty()
    {
        string missing = UniqueXmlPath();
        Assert.False(File.Exists(missing));

        PermissionStore direct = PermissionManager.PermissionXml.Load(missing);
        Assert.Empty(direct.Users);
        Assert.Empty(direct.Groups);

        PermissionManager m = CreateManager();
        string uuid = NewUuid();
        m.AddUserNode(uuid, "pre.load.node");
        Assert.True(m.Has(uuid, "pre.load.node"));

        m.LoadFromXml(missing);
        Assert.False(m.Has(uuid, "pre.load.node"));
        Assert.Empty(m.Snapshot().Users);
    }

    [Fact]
    public void SaveToXmlDebounced_WritesAfterTheQuietPeriod()
    {
        string path = UniqueXmlPath();
        try
        {
            PermissionManager m = new PermissionManager { SaveDebounceMs = 20 };
            m.SetXmlPath(path);
            string uuid = NewUuid();
            m.AddUserNode(uuid, "debounced.node"); // schedules the save internally

            // Normally lands within ~20-60 ms; the loop is just flake armor.
            Stopwatch sw = Stopwatch.StartNew();
            while (!File.Exists(path) && sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(10);
            }

            Assert.True(File.Exists(path), "debounced save never hit the disk");
            PermissionManager reader = CreateManager();
            reader.LoadFromXml(path);
            Assert.True(reader.Has(uuid, "debounced.node"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- thread safety smoke ----

    [Fact]
    public void ParallelMutationsAndQueries_DoNotCorruptState()
    {
        PermissionManager m = CreateManager();
        const int Workers = 4;
        const int PerWorker = 100;

        Task[] tasks = new Task[Workers];
        for (int w = 0; w < Workers; w++)
        {
            int worker = w;
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < PerWorker; i++)
                {
                    string uuid = $"par-user-{worker}-{i}";
                    m.AddUserNode(uuid, $"par.node.{worker}.{i}");
                    m.AddGroupNode("par-shared", $"par.grp.{worker}.{i}");
                    m.Has(uuid, $"par.node.{worker}.{i}");
                    m.Has($"par-user-{worker}-{i - 1}", "par.never.granted");
                }
            });
        }
        Task.WaitAll(tasks);

        for (int w = 0; w < Workers; w++)
        {
            for (int i = 0; i < PerWorker; i++)
            {
                Assert.True(m.Has($"par-user-{w}-{i}", $"par.node.{w}.{i}"));
            }
        }

        Assert.True(m.TryGetGroup("par-shared", out var shared));
        Assert.Equal(Workers * PerWorker, shared.Nodes.Count);
    }

    // ---- PermissionIntegration singleton (GUID-isolated) ----

    [Fact]
    public void HasValidRequirement_PassesOnExactNodeOrWildcard()
    {
        PermissionManager manager = PermissionManager.PermissionIntegration.Manager;
        string user = $"itg-user-{Guid.NewGuid():N}";
        string admin = $"itg-admin-{Guid.NewGuid():N}";
        string node = $"itg.node.{Guid.NewGuid():N}";

        try
        {
            Assert.False(PermissionManager.PermissionIntegration.HasValidRequirement(user, node));

            manager.AddUserNode(user, node);
            Assert.True(PermissionManager.PermissionIntegration.HasValidRequirement(user, node));
            Assert.False(PermissionManager.PermissionIntegration.HasValidRequirement(user, $"{node}.deeper"));

            manager.AddUserNode(admin, PermNodes.All);
            Assert.True(PermissionManager.PermissionIntegration.HasValidRequirement(admin, node));
        }
        finally
        {
            manager.RemoveUserNode(user, node);
            manager.RemoveUserNode(admin, PermNodes.All);
        }
    }

    [Fact]
    public void PlayerMeta_StoreQueryRemove_RoundTrips()
    {
        string uuid = $"meta-user-{Guid.NewGuid():N}";
        var meta = new global::SerializableBasis.ClientMetaDataMessage
        {
            playerUUID = uuid,
            playerDisplayName = "Meta Test Name",
            playerPlatform = "test-platform",
        };

        try
        {
            PermissionManager.PermissionIntegration.StorePlayerMeta(uuid, meta);
            Assert.True(PermissionManager.PermissionIntegration.TryGetPlayerMeta(uuid, out var got));
            Assert.Equal(uuid, got.playerUUID);
            Assert.Equal("Meta Test Name", got.playerDisplayName);
            Assert.Equal("test-platform", got.playerPlatform);
        }
        finally
        {
            PermissionManager.PermissionIntegration.RemovePlayerMeta(uuid);
        }

        Assert.False(PermissionManager.PermissionIntegration.TryGetPlayerMeta(uuid, out _));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BasisPlayerModeration (BasisNetworkServer\Security\BasisPlayerModeration.cs).
// The class is static, so every test for it lives in this one class (xunit runs
// methods of a class sequentially) and uses GUID-suffixed uuids plus unique IPs
// so results never depend on ordering or on ban-file leftovers from prior runs.
// ─────────────────────────────────────────────────────────────────────────────

[Collection("BasisServer shared network statics")]
public class BasisPlayerModerationTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int _peerIdCounter = 50_000;

    private static string BanFilePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName, "banned_players.xml");

    private static void InstallIdentity() => NetworkServer.AuthIdentity = Identity;

    private static string UniqueIp()
    {
        byte[] b = Guid.NewGuid().ToByteArray();
        return $"10.{b[0]}.{b[1]}.{b[2]}";
    }

    private static (string Uuid, FakeNetPeer Peer) ConnectPlayer(string? ip = null)
    {
        InstallIdentity();
        int id = Interlocked.Increment(ref _peerIdCounter);
        string uuid = $"mod-user-{Guid.NewGuid():N}";
        FakeNetPeer peer = new FakeNetPeer(id, ip ?? UniqueIp());
        Identity.Register(uuid, id);
        NetworkServer.AuthenticatedPeers[id] = peer;
        return (uuid, peer);
    }

    private static void RemovePlayer(FakeNetPeer peer)
        => NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);

    private static NetPacketReader BuildAdminPayload(AdminRequestMode mode, Action<NetDataWriter>? write = null)
    {
        NetDataWriter w = new NetDataWriter();
        new AdminRequest().Serialize(w, mode);
        write?.Invoke(w);
        byte[] bytes = w.AsReadOnlySpan().ToArray();
        return NetPacketReader.Create(bytes, 0, bytes.Length, () => { });
    }

    /// <summary>Parses one captured admin-channel reply; asserts it is a Message and returns the text.</summary>
    private static string ReadAdminMessage(FakeNetPeer peer, int index = 0)
    {
        NetDataReader r = new NetDataReader(peer.Sent[index].Data);
        AdminRequest req = new AdminRequest();
        req.Deserialize(r);
        Assert.Equal(AdminRequestMode.Message, req.GetAdminRequestMode());
        return r.GetString();
    }

    // ---- direct moderation calls ----

    [Fact]
    public void Ban_Kick_IpBan_RejectInvalidArgumentsAndOfflinePlayers()
    {
        BasisPlayerModeration.UseFileOnDisc = false;
        InstallIdentity();

        Assert.Equal("UUID invalid", BasisPlayerModeration.Ban(null!, "reason"));
        Assert.Equal("UUID invalid", BasisPlayerModeration.Ban("", "reason"));
        Assert.Equal("Reason invalid", BasisPlayerModeration.Ban("someone", null!));
        Assert.Equal("Reason invalid", BasisPlayerModeration.Ban("someone", ""));
        Assert.Equal("UUID invalid", BasisPlayerModeration.Kick(null!, "reason"));
        Assert.Equal("Reason invalid", BasisPlayerModeration.IpBan("someone", ""));

        string offline = $"offline-{Guid.NewGuid():N}";
        Assert.Equal("Player not found", BasisPlayerModeration.Ban(offline, "reason"));
        Assert.Equal("Player not found", BasisPlayerModeration.Kick(offline, "reason"));
        Assert.Equal("Player not found", BasisPlayerModeration.IpBan(offline, "reason"));
        Assert.False(BasisPlayerModeration.IsBanned(offline));
    }

    [Fact]
    public void Ban_DisconnectsPeerRecordsReasonAndUnbanRoundTrips()
    {
        BasisPlayerModeration.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        try
        {
            string reason = $"griefing-{Guid.NewGuid():N}";
            string result = BasisPlayerModeration.Ban(uuid, reason);

            Assert.Equal($"Player {uuid} banned.", result);
            Assert.True(BasisPlayerModeration.IsBanned(uuid));
            Assert.True(BasisPlayerModeration.GetBannedReason(uuid, out string stored));
            Assert.Equal(reason, stored);
            Assert.Equal(1, peer.DisconnectCalls);
            Assert.Equal(Encoding.UTF8.GetBytes(reason), peer.DisconnectData[0]);

            // A plain ban must not create an IP ban.
            Assert.False(BasisPlayerModeration.IsIpBanned(peer.Address.ToString()));

            Assert.True(BasisPlayerModeration.Unban(uuid));
            Assert.False(BasisPlayerModeration.IsBanned(uuid));
            Assert.False(BasisPlayerModeration.GetBannedReason(uuid, out _));
            Assert.False(BasisPlayerModeration.Unban(uuid)); // second unban fails
        }
        finally
        {
            RemovePlayer(peer);
        }
    }

    [Fact]
    public void IpBan_RecordsAddress_AndUnbanIpClearsEveryMatchingEntry()
    {
        BasisPlayerModeration.UseFileOnDisc = false;
        string sharedIp = UniqueIp();
        var (uuidA, peerA) = ConnectPlayer(sharedIp);
        var (uuidB, peerB) = ConnectPlayer(sharedIp);
        var (uuidC, peerC) = ConnectPlayer();
        try
        {
            Assert.Equal($"Player {uuidA} and IP {sharedIp} banned.", BasisPlayerModeration.IpBan(uuidA, "spam"));
            Assert.Equal($"Player {uuidB} and IP {sharedIp} banned.", BasisPlayerModeration.IpBan(uuidB, "spam"));
            string otherIp = peerC.Address.ToString();
            BasisPlayerModeration.IpBan(uuidC, "other");

            Assert.True(BasisPlayerModeration.IsIpBanned(sharedIp));
            Assert.True(BasisPlayerModeration.IsIpBanned(otherIp));
            Assert.True(BasisPlayerModeration.IsBanned(uuidA));
            Assert.True(BasisPlayerModeration.IsBanned(uuidB));
            Assert.Equal(1, peerA.DisconnectCalls);
            Assert.Equal(1, peerB.DisconnectCalls);

            // One UnbanIp sweep removes every player banned under that address.
            Assert.True(BasisPlayerModeration.UnbanIp(sharedIp));
            Assert.False(BasisPlayerModeration.IsIpBanned(sharedIp));
            Assert.False(BasisPlayerModeration.IsBanned(uuidA));
            Assert.False(BasisPlayerModeration.IsBanned(uuidB));
            Assert.False(BasisPlayerModeration.UnbanIp(sharedIp)); // nothing left
            Assert.True(BasisPlayerModeration.IsBanned(uuidC));    // unrelated ip untouched

            Assert.True(BasisPlayerModeration.UnbanIp(otherIp));
            Assert.False(BasisPlayerModeration.IsBanned(uuidC));
        }
        finally
        {
            RemovePlayer(peerA);
            RemovePlayer(peerB);
            RemovePlayer(peerC);
        }
    }

    [Fact]
    public void Kick_DisconnectsWithoutRecordingABan()
    {
        BasisPlayerModeration.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        try
        {
            string result = BasisPlayerModeration.Kick(uuid, "be nicer");

            Assert.Equal($"Player {uuid} kicked.", result);
            Assert.Equal(1, peer.DisconnectCalls);
            Assert.Equal(Encoding.UTF8.GetBytes("be nicer"), peer.DisconnectData[0]);
            Assert.False(BasisPlayerModeration.IsBanned(uuid));
        }
        finally
        {
            RemovePlayer(peer);
        }
    }

    [Fact]
    public void ProtectedPlayers_CannotBeBannedKickedOrIpBanned()
    {
        BasisPlayerModeration.UseFileOnDisc = false;
        var (uuid, peer) = ConnectPlayer();
        PermissionManager perms = PermissionManager.PermissionIntegration.Manager;
        perms.AddUserNode(uuid, PermNodes.protection);
        try
        {
            Assert.Equal("Target is protected", BasisPlayerModeration.Ban(uuid, "nope"));
            Assert.Equal("Target is protected", BasisPlayerModeration.Kick(uuid, "nope"));
            Assert.Equal("Target is protected", BasisPlayerModeration.IpBan(uuid, "nope"));

            Assert.False(BasisPlayerModeration.IsBanned(uuid));
            Assert.Equal(0, peer.DisconnectCalls);
        }
        finally
        {
            perms.RemoveUserNode(uuid, PermNodes.protection);
            RemovePlayer(peer);
        }
    }

    [Fact]
    public void UnknownPlayers_QueryAsSafeDefaults()
    {
        BasisPlayerModeration.UseFileOnDisc = false;
        string unknown = $"unknown-{Guid.NewGuid():N}";

        Assert.False(BasisPlayerModeration.IsBanned(unknown));
        Assert.False(BasisPlayerModeration.GetBannedReason(unknown, out string reason));
        Assert.Equal(string.Empty, reason);
        Assert.False(BasisPlayerModeration.Unban(unknown));
        Assert.False(BasisPlayerModeration.IsIpBanned(null!));
        Assert.False(BasisPlayerModeration.IsIpBanned(""));
        Assert.False(BasisPlayerModeration.IsIpBanned("   "));
        Assert.False(BasisPlayerModeration.UnbanIp(UniqueIp()));
    }

    // ---- persistence ----

    [Fact]
    public void BanState_SurvivesSaveAndLoad_AndUnbanPersists()
    {
        Directory.CreateDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName));
        BasisPlayerModeration.UseFileOnDisc = true;
        var (uuidA, peerA) = ConnectPlayer();
        var (uuidB, peerB) = ConnectPlayer();
        string ipB = peerB.Address.ToString();
        try
        {
            string reasonA = $"persist-{Guid.NewGuid():N}";
            BasisPlayerModeration.Ban(uuidA, reasonA);
            BasisPlayerModeration.IpBan(uuidB, "ip persist");

            // Simulated restart: reload state purely from banned_players.xml.
            BasisPlayerModeration.LoadBannedPlayers();

            Assert.True(BasisPlayerModeration.IsBanned(uuidA));
            Assert.True(BasisPlayerModeration.GetBannedReason(uuidA, out string reason));
            Assert.Equal(reasonA, reason);
            Assert.True(BasisPlayerModeration.IsBanned(uuidB));
            Assert.True(BasisPlayerModeration.IsIpBanned(ipB));

            // Unban also persists (writes the file), so a second reload stays clean.
            Assert.True(BasisPlayerModeration.Unban(uuidA));
            Assert.True(BasisPlayerModeration.Unban(uuidB));
            BasisPlayerModeration.LoadBannedPlayers();
            Assert.False(BasisPlayerModeration.IsBanned(uuidA));
            Assert.False(BasisPlayerModeration.IsBanned(uuidB));
            Assert.False(BasisPlayerModeration.IsIpBanned(ipB));
        }
        finally
        {
            BasisPlayerModeration.Unban(uuidA);
            BasisPlayerModeration.Unban(uuidB);
            BasisPlayerModeration.UseFileOnDisc = false;
            RemovePlayer(peerA);
            RemovePlayer(peerB);
        }
    }

    [Fact]
    public void LoadBannedPlayers_MissingFile_KeepsInMemoryStateAndRecreatesTheFile()
    {
        Directory.CreateDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName));
        BasisPlayerModeration.UseFileOnDisc = true;
        var (uuid, peer) = ConnectPlayer();
        try
        {
            BasisPlayerModeration.Ban(uuid, "keep me");
            File.Delete(BanFilePath);

            BasisPlayerModeration.LoadBannedPlayers();

            Assert.True(BasisPlayerModeration.IsBanned(uuid));
            Assert.True(File.Exists(BanFilePath));

            // The recreated file really contains the in-memory state.
            BasisPlayerModeration.LoadBannedPlayers();
            Assert.True(BasisPlayerModeration.IsBanned(uuid));
        }
        finally
        {
            BasisPlayerModeration.Unban(uuid);
            BasisPlayerModeration.UseFileOnDisc = false;
            RemovePlayer(peer);
        }
    }

    // ---- OnAdminMessage permission gating (no live sockets needed) ----

    [Fact]
    public void OnAdminMessage_UnauthenticatedPeer_GetsUuidNotFound()
    {
        BasisPlayerModeration.UseFileOnDisc = false;
        InstallIdentity();
        // Peer intentionally NOT registered with the auth identity.
        FakeNetPeer stranger = new FakeNetPeer(Interlocked.Increment(ref _peerIdCounter), UniqueIp());

        BasisPlayerModeration.OnAdminMessage(stranger, BuildAdminPayload(AdminRequestMode.Ban, w =>
        {
            w.Put("victim");
            w.Put("reason");
        }));

        Assert.Single(stranger.Sent);
        Assert.Equal("UUID not found", ReadAdminMessage(stranger));
    }

    [Fact]
    public void OnAdminMessage_Ban_WithoutPermission_IsRefusedAndTargetUnaffected()
    {
        BasisPlayerModeration.UseFileOnDisc = false;
        var (adminUuid, adminPeer) = ConnectPlayer();
        var (targetUuid, targetPeer) = ConnectPlayer();
        try
        {
            BasisPlayerModeration.OnAdminMessage(adminPeer, BuildAdminPayload(AdminRequestMode.Ban, w =>
            {
                w.Put(targetUuid);
                w.Put("no rights");
            }));

            Assert.Single(adminPeer.Sent);
            Assert.Equal($"No permission: {PermNodes.ModerationBan}", ReadAdminMessage(adminPeer));
            Assert.Equal(BasisNetworkCommons.AdminChannel, adminPeer.Sent[0].Channel);
            Assert.False(BasisPlayerModeration.IsBanned(targetUuid));
            Assert.Equal(0, targetPeer.DisconnectCalls);
            _ = adminUuid;
        }
        finally
        {
            RemovePlayer(adminPeer);
            RemovePlayer(targetPeer);
        }
    }

    [Fact]
    public void OnAdminMessage_Ban_WithModerationBanNode_BansTheTarget()
    {
        BasisPlayerModeration.UseFileOnDisc = false;
        var (adminUuid, adminPeer) = ConnectPlayer();
        var (targetUuid, targetPeer) = ConnectPlayer();
        PermissionManager perms = PermissionManager.PermissionIntegration.Manager;
        perms.AddUserNode(adminUuid, PermNodes.ModerationBan);
        try
        {
            BasisPlayerModeration.OnAdminMessage(adminPeer, BuildAdminPayload(AdminRequestMode.Ban, w =>
            {
                w.Put(targetUuid);
                w.Put("admin banhammer");
            }));

            Assert.True(BasisPlayerModeration.IsBanned(targetUuid));
            Assert.Equal(1, targetPeer.DisconnectCalls);
            Assert.Single(adminPeer.Sent);
            Assert.Equal($"Player {targetUuid} banned.", ReadAdminMessage(adminPeer));
        }
        finally
        {
            BasisPlayerModeration.Unban(targetUuid);
            perms.RemoveUserNode(adminUuid, PermNodes.ModerationBan);
            RemovePlayer(adminPeer);
            RemovePlayer(targetPeer);
        }
    }

    [Fact]
    public void OnAdminMessage_GetPermissions_RequiresView_ThenSerializesTheSnapshot()
    {
        BasisPlayerModeration.UseFileOnDisc = false;
        var (adminUuid, adminPeer) = ConnectPlayer();
        PermissionManager perms = PermissionManager.PermissionIntegration.Manager;

        string markerGroup = $"grp-{Guid.NewGuid():N}";
        string markerParent = $"par-{Guid.NewGuid():N}";
        string markerGroupNode = $"marker.group.{Guid.NewGuid():N}";
        string markerUser = $"marker-user-{Guid.NewGuid():N}";
        string markerUserNode = $"marker.user.{Guid.NewGuid():N}";
        try
        {
            // Without the view node the request is refused outright.
            BasisPlayerModeration.OnAdminMessage(adminPeer, BuildAdminPayload(AdminRequestMode.GetPermissions));
            Assert.Equal("No permission: view", ReadAdminMessage(adminPeer));
            adminPeer.Sent.Clear();

            perms.AddGroupNode(markerGroup, markerGroupNode);
            perms.AddGroupParent(markerGroup, markerParent);
            perms.AddUserToGroup(markerUser, markerGroup);
            perms.AddUserNode(markerUser, markerUserNode);
            perms.AddUserNode(adminUuid, PermNodes.PermissionsView);

            BasisPlayerModeration.OnAdminMessage(adminPeer, BuildAdminPayload(AdminRequestMode.GetPermissions));

            Assert.Single(adminPeer.Sent);
            NetDataReader r = new NetDataReader(adminPeer.Sent[0].Data);
            AdminRequest reply = new AdminRequest();
            reply.Deserialize(r);
            Assert.Equal(AdminRequestMode.GetPermissions, reply.GetAdminRequestMode());

            // [int groupCount] { name, [int nodes] n*, [int parents] p* } then
            // [int userCount]  { uuid, [int groups] g*, [int nodes] n* }.
            bool sawGroup = false;
            int groupCount = r.GetInt();
            for (int i = 0; i < groupCount; i++)
            {
                string name = r.GetString();
                int nodeCount = r.GetInt();
                List<string> nodes = new List<string>(nodeCount);
                for (int n = 0; n < nodeCount; n++) nodes.Add(r.GetString());
                int parentCount = r.GetInt();
                List<string> parents = new List<string>(parentCount);
                for (int p = 0; p < parentCount; p++) parents.Add(r.GetString());

                if (name == markerGroup)
                {
                    sawGroup = true;
                    Assert.Contains(markerGroupNode, nodes);
                    Assert.Contains(markerParent, parents);
                }
            }

            bool sawUser = false;
            int userCount = r.GetInt();
            for (int i = 0; i < userCount; i++)
            {
                string uuid = r.GetString();
                int groupMemberships = r.GetInt();
                List<string> groups = new List<string>(groupMemberships);
                for (int g = 0; g < groupMemberships; g++) groups.Add(r.GetString());
                int nodeCount = r.GetInt();
                List<string> nodes = new List<string>(nodeCount);
                for (int n = 0; n < nodeCount; n++) nodes.Add(r.GetString());

                if (uuid == markerUser)
                {
                    sawUser = true;
                    Assert.Contains(markerGroup, groups);
                    Assert.Contains(markerUserNode, nodes);
                }
            }

            Assert.True(sawGroup, "marker group missing from GetPermissions payload");
            Assert.True(sawUser, "marker user missing from GetPermissions payload");
            Assert.Equal(0, r.AvailableBytes);
        }
        finally
        {
            perms.DeleteGroup(markerGroup);
            perms.RemoveUserNode(markerUser, markerUserNode);
            perms.RemoveUserNode(adminUuid, PermNodes.PermissionsView);
            RemovePlayer(adminPeer);
        }
    }
}
