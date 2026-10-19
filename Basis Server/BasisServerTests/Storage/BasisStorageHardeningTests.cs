using Basis.Network.Core;
using BasisNetworkServer.Storage;
using BasisPermissions;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
using Xunit;
using static BasisPermissions.PermissionManager;

namespace BasisServerTests;

internal static class StorageHardeningSupport
{
    public static BasisStorageReply Run(BasisStorageService service, string owner, BasisStorageRequest request, string origin)
    {
        TaskCompletionSource<BasisStorageReply> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BasisStorageStatus queued = service.Submit(owner, request, reply => done.TrySetResult(reply), origin);
        if (queued != BasisStorageStatus.Ok) return new BasisStorageReply { Op = request.Op, RequestId = request.RequestId, Status = queued };
        Assert.True(done.Task.Wait(TimeSpan.FromSeconds(10)), "storage reply timed out");
        return done.Task.Result;
    }

    public static BasisSqlStorageBackend Sql(string folder, string table) => Opened(new BasisSqlStorageBackend("sqlite", SqliteFactory.Instance, BasisStorageSqlDialect.Sqlite, StorageTestSupport.SqliteConnectionString(folder), table));

    public static T Opened<T>(T backend) where T : IBasisStorageBackend
    {
        backend.Open();
        return backend;
    }

    public static string RecordPath(string folder, string owner)
    {
        string key = BasisStorageRecord.OwnerKeyOf(owner);
        return Path.Combine(folder, "players", key.Substring(0, 2), key + ".bsr");
    }

    public static void Execute(string folder, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = new SqliteConnection(StorageTestSupport.SqliteConnectionString(folder));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    public static long Scalar(string folder, string sql)
    {
        using SqliteConnection connection = new SqliteConnection(StorageTestSupport.SqliteConnectionString(folder));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public static long DaysAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();

    public static void Settle(BasisStorageService service, string owner) => Assert.True(service.TryInspect(owner, TimeSpan.FromSeconds(5), out _));
}

public class BasisStorageCapacityTests
{
    [Fact]
    public void EveryStoredPlayer_CountsItsRecordOverheadAgainstTheServerCap()
    {
        using BasisStorageService service = new BasisStorageService(new BasisMemoryStorageBackend(), 16384, 2L * BasisStorageService.RecordOverheadBytes + 1024);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:one", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(1))).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:two", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(1))).Status);
        Assert.Equal(BasisStorageStatus.Full, StorageTestSupport.Run(service, "did:key:three", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(1))).Status);
        Assert.True(service.TotalBytes < 1024, "the payload alone is far under the cap");
        Assert.Equal(2, service.TotalRecords);
        Assert.Equal(service.TotalBytes + 2L * BasisStorageService.RecordOverheadBytes, service.UsedBytes);

        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:one", StorageTestSupport.Op(BasisStorageOp.Clear, "w")).Status);
        Assert.Equal(1, service.TotalRecords);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:three", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(1))).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:three", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(200))).Status);
    }

    [Fact]
    public void ThousandsOfTinyRecords_CannotHideUnderTheCap()
    {
        long cap = 64L * 1024;
        using BasisStorageService service = new BasisStorageService(new BasisMemoryStorageBackend(), 16384, cap);
        service.ConfigureNewOwners(100000, TimeSpan.FromHours(1), 100000);
        int stored = 0;
        for (int i = 0; i < 200; i++)
        {
            if (StorageTestSupport.Run(service, $"did:key:tiny-{i}", StorageTestSupport.Set("w", "k", Array.Empty<byte>())).Status == BasisStorageStatus.Ok) stored++;
        }
        Assert.InRange(stored, 1, (int)(cap / BasisStorageService.RecordOverheadBytes));
        Assert.True(service.UsedBytes <= cap);
        Assert.Equal(stored, service.TotalRecords);
    }

    [Fact]
    public void TheServerCapRestartsFromTheStoredRecords()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using (BasisStorageService first = new BasisStorageService(StorageHardeningSupport.Opened(new BasisFileStorageBackend(folder)), 16384, 1 << 24))
            {
                StorageTestSupport.Run(first, "did:key:a", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(10)));
                StorageTestSupport.Run(first, "did:key:b", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(10)));
            }
            using BasisStorageService second = new BasisStorageService(StorageHardeningSupport.Opened(new BasisFileStorageBackend(folder)), 16384, 1 << 24);
            Assert.Equal(2, second.TotalRecords);
            Assert.Equal(second.TotalBytes + 2L * BasisStorageService.RecordOverheadBytes, second.UsedBytes);
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }
}

public class BasisStorageNewOwnerTests
{
    private static BasisStorageService Memory() => new BasisStorageService(new BasisMemoryStorageBackend(), 16384, 1 << 24);

    [Fact]
    public void FirstSaves_AreLimitedPerAddress_AndPlayersWithDataAreNot()
    {
        using BasisStorageService service = Memory();
        service.ConfigureNewOwners(2, TimeSpan.FromHours(1), 1000);
        const string home = "198.51.100.7";
        Assert.Equal(BasisStorageStatus.Ok, StorageHardeningSupport.Run(service, "did:key:first", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4)), home).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageHardeningSupport.Run(service, "did:key:second", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4)), home).Status);
        Assert.Equal(BasisStorageStatus.RateLimited, StorageHardeningSupport.Run(service, "did:key:third", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4)), home).Status);
        Assert.Equal(BasisStorageStatus.NotFound, StorageHardeningSupport.Run(service, "did:key:third", StorageTestSupport.Get("w", "k"), home).Status);
        Assert.Equal(2, service.TotalRecords);

        Assert.Equal(BasisStorageStatus.Ok, StorageHardeningSupport.Run(service, "did:key:first", StorageTestSupport.Set("w", "k2", StorageTestSupport.Bytes(4)), home).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageHardeningSupport.Run(service, "did:key:third", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4)), "198.51.100.8").Status);
        Assert.Equal(3, service.TotalRecords);
        Assert.Equal(1, service.NewOwners.Refused);
        Assert.Equal(3, service.NewOwners.Admitted);
    }

    [Fact]
    public void FirstSaves_AreLimitedServerWide_WhateverTheirAddress()
    {
        using BasisStorageService service = Memory();
        service.ConfigureNewOwners(100, TimeSpan.FromHours(1), 3);
        for (int i = 0; i < 3; i++) Assert.Equal(BasisStorageStatus.Ok, StorageHardeningSupport.Run(service, $"did:key:spread-{i}", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4)), $"203.0.113.{i + 1}").Status);
        Assert.Equal(BasisStorageStatus.RateLimited, StorageHardeningSupport.Run(service, "did:key:spread-3", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4)), "203.0.113.200").Status);
        Assert.Equal(BasisStorageStatus.RateLimited, StorageHardeningSupport.Run(service, "did:key:spread-4", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4)), null!).Status);
        Assert.Equal(3, service.TotalRecords);
    }

    [Fact]
    public void ReadsAndRefusedWrites_NeverUseUpAFirstSave()
    {
        using BasisStorageService service = new BasisStorageService(new BasisMemoryStorageBackend(), 64, 1 << 24);
        service.ConfigureNewOwners(1, TimeSpan.FromHours(1), 1000);
        const string address = "192.0.2.50";
        Assert.Equal(BasisStorageStatus.NotFound, StorageHardeningSupport.Run(service, "did:key:reader", StorageTestSupport.Get("w", "k"), address).Status);
        Assert.Empty(StorageHardeningSupport.Run(service, "did:key:reader", StorageTestSupport.Op(BasisStorageOp.Keys, "w"), address).Keys);
        Assert.Equal(BasisStorageStatus.NotFound, StorageHardeningSupport.Run(service, "did:key:reader", StorageTestSupport.Delete("w", "k"), address).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageHardeningSupport.Run(service, "did:key:reader", StorageTestSupport.Op(BasisStorageOp.Clear, "w"), address).Status);
        Assert.Equal(BasisStorageStatus.Full, StorageHardeningSupport.Run(service, "did:key:greedy", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(200)), address).Status);
        Assert.Equal(0, service.NewOwners.Admitted);
        Assert.Equal(BasisStorageStatus.Ok, StorageHardeningSupport.Run(service, "did:key:saver", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4)), address).Status);
        Assert.Equal(BasisStorageStatus.RateLimited, StorageHardeningSupport.Run(service, "did:key:reader", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4)), address).Status);
    }
}

public class BasisStorageNewOwnerLimiterTests
{
    [Fact]
    public void AnAddress_GetsItsFirstSavesBackAsTheWindowPasses()
    {
        long now = 1_000_000;
        BasisStorageNewOwnerLimiter limiter = new BasisStorageNewOwnerLimiter(() => now);
        limiter.Configure(2, TimeSpan.FromMinutes(60), 1000);
        Assert.True(limiter.TryAdmit("a"));
        Assert.True(limiter.TryAdmit("a"));
        Assert.False(limiter.TryAdmit("a"));
        Assert.True(limiter.TryAdmit("b"));
        now += 30 * 60000L;
        Assert.True(limiter.TryAdmit("a"));
        Assert.False(limiter.TryAdmit("a"));
        now += 60 * 60000L;
        Assert.True(limiter.TryAdmit("a"));
        Assert.True(limiter.TryAdmit("a"));
        Assert.False(limiter.TryAdmit("a"));
        Assert.Equal(3, limiter.Refused);
    }

    [Fact]
    public void TheServerWideRate_RefillsOverAnHour()
    {
        long now = 0;
        BasisStorageNewOwnerLimiter limiter = new BasisStorageNewOwnerLimiter(() => now);
        limiter.Configure(1000, TimeSpan.FromMinutes(60), 4);
        for (int i = 0; i < 4; i++) Assert.True(limiter.TryAdmit("address-" + i));
        Assert.False(limiter.TryAdmit("address-9"));
        Assert.False(limiter.TryAdmit(null!));
        now += 15 * 60000L;
        Assert.True(limiter.TryAdmit(null!));
        Assert.False(limiter.TryAdmit("address-10"));
    }

    [Fact]
    public void ZeroOrNegativeSettings_RestoreTheDefaults()
    {
        BasisStorageNewOwnerLimiter limiter = new BasisStorageNewOwnerLimiter();
        limiter.Configure(0, TimeSpan.Zero, -5);
        Assert.Equal(BasisStorageNewOwnerLimiter.DefaultPerAddress, limiter.PerAddress);
        Assert.Equal(BasisStorageNewOwnerLimiter.DefaultPerHour, limiter.PerHour);
        Assert.Equal(TimeSpan.FromMinutes(BasisStorageNewOwnerLimiter.DefaultWindowMinutes), limiter.Window);
    }

    [Fact]
    public void RaisingTheServerWideRate_TakesEffectAtOnce()
    {
        long now = 0;
        BasisStorageNewOwnerLimiter limiter = new BasisStorageNewOwnerLimiter(() => now);
        limiter.Configure(1000, TimeSpan.FromMinutes(60), 200);
        int admitted = 0;
        for (int i = 0; i < 250; i++) if (limiter.TryAdmit("a" + i)) admitted++;
        Assert.Equal(200, admitted);
        limiter.Configure(1000, TimeSpan.FromMinutes(60), 2);
        Assert.False(limiter.TryAdmit("late"));
    }

    [Fact]
    public void AddressesThatRefilled_AreForgotten()
    {
        long now = 0;
        BasisStorageNewOwnerLimiter limiter = new BasisStorageNewOwnerLimiter(() => now);
        limiter.Configure(3, TimeSpan.FromMinutes(10), 1000);
        for (int i = 0; i < 50; i++) Assert.True(limiter.TryAdmit("busy-" + i));
        Assert.Equal(50, limiter.TrackedAddresses);
        now += 11 * 60000L;
        Assert.True(limiter.TryAdmit("fresh"));
        Assert.Equal(1, limiter.TrackedAddresses);
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:eeee:dddd:9", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    public void Origins_GroupIpv6ByItsSlash64(string address, string origin)
    {
        Assert.Equal(origin, BasisStorageNewOwnerLimiter.OriginOf(IPAddress.Parse(address)));
    }

    [Fact]
    public void AnUnknownAddress_HasNoOrigin()
    {
        Assert.Null(BasisStorageNewOwnerLimiter.OriginOf(null!));
    }
}

public class BasisStorageExpiryTests
{
    [Fact]
    public void IdleRecords_AreRemoved_AndRecentOnesKept()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using BasisStorageService service = new BasisStorageService(StorageHardeningSupport.Opened(new BasisFileStorageBackend(folder)), 16384, 1 << 24);
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:idle", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(30))).Status);
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:active", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(30))).Status);
            string idle = StorageHardeningSupport.RecordPath(folder, "did:key:idle"), active = StorageHardeningSupport.RecordPath(folder, "did:key:active");
            service.Forget("did:key:idle");
            File.SetLastWriteTimeUtc(idle, DateTime.UtcNow.AddDays(-400));
            long before = service.TotalBytes;

            Assert.True(service.TryPreviewExpiry(TimeSpan.FromDays(365), out long found, out long size));
            Assert.Equal(1, found);
            Assert.Equal(new FileInfo(idle).Length, size);
            Assert.True(service.TryExpire(TimeSpan.FromDays(365), out long removed, out long freed));
            Assert.Equal(1, removed);
            Assert.Equal(size, freed);
            Assert.False(File.Exists(idle));
            Assert.True(File.Exists(active));
            Assert.Equal(1, service.TotalRecords);
            Assert.Equal(before - freed, service.TotalBytes);
            Assert.Equal(1, service.Expired);
            Assert.Equal(BasisStorageStatus.NotFound, StorageTestSupport.Run(service, "did:key:idle", StorageTestSupport.Get("w", "k")).Status);
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:active", StorageTestSupport.Get("w", "k")).Status);
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void ARecordInUse_IsKept_AndItsUseIsRecordedForLater()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using BasisStorageService service = new BasisStorageService(StorageHardeningSupport.Opened(new BasisFileStorageBackend(folder)), 16384, 1 << 24);
            const string owner = "did:key:regular";
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, owner, StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(8))).Status);
            string path = StorageHardeningSupport.RecordPath(folder, owner);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-400));

            Assert.True(service.TryExpire(TimeSpan.FromDays(365), out long removed, out _));
            Assert.Equal(0, removed);
            Assert.True(File.Exists(path));

            service.Forget(owner);
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, owner, StorageTestSupport.Get("w", "k")).Status);
            Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-5), "reading it did not record the use");
            service.Forget(owner);
            StorageHardeningSupport.Settle(service, owner);
            service.Forget(owner);
            Assert.True(service.TryExpire(TimeSpan.FromDays(1), out removed, out _));
            Assert.Equal(0, removed);
            Assert.True(File.Exists(path));
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void ARecordUsedJustInsideTheWindow_IsNeverRemoved()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using BasisStorageService service = new BasisStorageService(StorageHardeningSupport.Opened(new BasisFileStorageBackend(folder)), 16384, 1 << 24);
            const string owner = "did:key:edge";
            StorageTestSupport.Run(service, owner, StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(8)));
            service.Forget(owner);
            File.SetLastWriteTimeUtc(StorageHardeningSupport.RecordPath(folder, owner), DateTime.UtcNow.AddDays(-30).AddMinutes(10));
            Assert.True(service.TryPreviewExpiry(TimeSpan.FromDays(30), out long found, out _));
            Assert.Equal(0, found);
            Assert.True(service.TryExpire(TimeSpan.FromDays(30), out long removed, out _));
            Assert.Equal(0, removed);
            Assert.True(File.Exists(StorageHardeningSupport.RecordPath(folder, owner)));
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void TheHourlySweep_UsesTheConfiguredDays_AndZeroMeansNever()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using BasisStorageService service = new BasisStorageService(StorageHardeningSupport.Opened(new BasisFileStorageBackend(folder)), 16384, 1 << 24);
            System.Reflection.MethodInfo sweep = typeof(BasisStorageService).GetMethod("ExpireIdle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            StorageTestSupport.Run(service, "did:key:forgotten", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(8)));
            service.Forget("did:key:forgotten");
            string path = StorageHardeningSupport.RecordPath(folder, "did:key:forgotten");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-400));

            service.ConfigureExpiry(0);
            sweep.Invoke(service, null);
            Assert.True(File.Exists(path));
            service.ConfigureExpiry(500);
            sweep.Invoke(service, null);
            Assert.True(File.Exists(path));
            service.ConfigureExpiry(365);
            sweep.Invoke(service, null);
            Assert.False(File.Exists(path));
            Assert.Equal(0, service.TotalRecords);
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void ExpiryDays_AreClamped()
    {
        using BasisStorageService service = new BasisStorageService(new BasisMemoryStorageBackend(), 16384, 1 << 24);
        Assert.True(service.CanExpire);
        Assert.Equal(0, service.ExpiryDays);
        service.ConfigureExpiry(365);
        Assert.Equal(365, service.ExpiryDays);
        service.ConfigureExpiry(-1);
        Assert.Equal(0, service.ExpiryDays);
        service.ConfigureExpiry(int.MaxValue);
        Assert.Equal(BasisStorageService.MaxExpiryDays, service.ExpiryDays);
        Assert.True(service.TryExpire(TimeSpan.FromDays(service.ExpiryDays), out _, out _));
    }

    [Fact]
    public void ABackendThatCannotTellWhenDataWasUsed_IsNeverPruned()
    {
        using BasisStorageService service = new BasisStorageService(new PlainBackend(), 16384, 1 << 24);
        Assert.False(service.CanExpire);
        Assert.False(service.TryPreviewExpiry(TimeSpan.FromDays(1), out _, out _));
        Assert.False(service.TryExpire(TimeSpan.FromDays(1), out _, out _));
        Assert.False(service.TryList(long.MaxValue, _ => true));
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:plain", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(3))).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:plain", StorageTestSupport.Get("w", "k")).Status);
    }

    [Fact]
    public void SharedDatabase_KeepsWhatAnotherServerIsUsing_AndForgetsWhatIsGone()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            const string owner = "did:key:shared-expiry", table = "expiry_storage";
            using BasisStorageService a = new BasisStorageService(StorageHardeningSupport.Sql(folder, table), 16384, 1 << 24);
            using BasisStorageService b = new BasisStorageService(StorageHardeningSupport.Sql(folder, table), 16384, 1 << 24);
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(a, owner, StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(8))).Status);
            a.Forget(owner);
            StorageHardeningSupport.Execute(folder, $"UPDATE {table} SET updated_at = @old", ("@old", StorageHardeningSupport.DaysAgo(400)));

            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(b, owner, StorageTestSupport.Get("w", "k")).Status);
            Assert.True(StorageHardeningSupport.Scalar(folder, $"SELECT updated_at FROM {table}") > StorageHardeningSupport.DaysAgo(1));
            Assert.True(a.TryExpire(TimeSpan.FromDays(365), out long removed, out _));
            Assert.Equal(0, removed);

            StorageHardeningSupport.Execute(folder, $"UPDATE {table} SET updated_at = @old", ("@old", StorageHardeningSupport.DaysAgo(400)));
            Assert.True(a.TryExpire(TimeSpan.FromDays(365), out removed, out _));
            Assert.Equal(1, removed);
            Assert.Equal(0, StorageHardeningSupport.Scalar(folder, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(BasisStorageStatus.NotFound, StorageTestSupport.Run(b, owner, StorageTestSupport.Get("w", "k")).Status);
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void Backends_ListAndExpireByLastUse()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            IBasisStorageBackend[] backends =
            {
                StorageHardeningSupport.Opened(new BasisMemoryStorageBackend()),
                StorageHardeningSupport.Opened(new BasisFileStorageBackend(Path.Combine(folder, "files"))),
                StorageHardeningSupport.Sql(folder, "catalog_storage"),
            };
            foreach (IBasisStorageBackend backend in backends)
            {
                using (backend)
                {
                    IBasisStorageCatalog catalog = (IBasisStorageCatalog)backend;
                    string kept = BasisStorageRecord.OwnerKeyOf("did:key:kept"), gone = BasisStorageRecord.OwnerKeyOf("did:key:gone");
                    Assert.True(backend.TrySave(kept, StorageTestSupport.Bytes(10), 0, 1));
                    Assert.True(backend.TrySave(gone, StorageTestSupport.Bytes(20), 0, 1));
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    Assert.True(catalog.TryLoad(kept, out byte[] blob, out long lastUsed));
                    Assert.Equal(10, blob.Length);
                    Assert.InRange(lastUsed, now - 600000, now + 600000);

                    List<BasisStorageRecordInfo> all = new List<BasisStorageRecordInfo>();
                    catalog.List(long.MaxValue, info =>
                    {
                        all.Add(info);
                        return true;
                    });
                    Assert.Equal(2, all.Count);
                    Assert.Contains(all, info => info.OwnerKey == gone && info.Bytes == 20);
                    int visited = 0;
                    catalog.List(long.MaxValue, _ => ++visited < 1);
                    Assert.Equal(1, visited);

                    Assert.False(catalog.TryExpire(kept, now - 600000, out _));
                    Assert.True(catalog.TryExpire(gone, now + 600000, out long freed));
                    Assert.Equal(20, freed);
                    Assert.False(backend.TryLoad(gone, out _));
                    Assert.False(catalog.TryExpire(gone, now + 600000, out _));

                    catalog.Touch(kept, now + 3600000);
                    Assert.True(catalog.TryLoad(kept, out _, out lastUsed));
                    Assert.InRange(lastUsed, now + 3600000 - 2000, now + 3600000 + 2000);
                    catalog.Touch(kept, now - 3600000);
                    Assert.True(catalog.TryLoad(kept, out _, out long unchanged));
                    Assert.Equal(lastUsed, unchanged);
                    Assert.False(catalog.TryExpire(kept, now + 600000, out _));
                }
            }
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    private sealed class PlainBackend : IBasisStorageBackend
    {
        private readonly Dictionary<string, byte[]> store = new Dictionary<string, byte[]>();
        public string Name => "plain";
        public void Open() { }
        public bool TryLoad(string ownerKey, out byte[] blob)
        {
            lock (store)
            {
                if (store.TryGetValue(ownerKey, out byte[]? found))
                {
                    blob = found;
                    return true;
                }
            }
            blob = null!;
            return false;
        }
        public bool TrySave(string ownerKey, byte[] blob, long expectedRevision, long revision)
        {
            lock (store) store[ownerKey] = blob;
            return true;
        }
        public bool TryDelete(string ownerKey, long expectedRevision)
        {
            lock (store) store.Remove(ownerKey);
            return true;
        }
        public void Quarantine(string ownerKey) { }
        public void Measure(out long records, out long bytes)
        {
            records = 0;
            bytes = 0;
        }
        public void Dispose() { }
    }
}

public class BasisStorageSharedDatabaseTests
{
    [Fact]
    public void ARecordDeletedAndRecreated_IsNotOverwrittenByAnotherServersOldCopy()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            const string owner = "did:key:aba", table = "aba_storage";
            using BasisStorageService a = new BasisStorageService(StorageHardeningSupport.Sql(folder, table), 16384, 1 << 24);
            using BasisStorageService b = new BasisStorageService(StorageHardeningSupport.Sql(folder, table), 16384, 1 << 24);
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(a, owner, StorageTestSupport.Set("w", "k", Encoding.UTF8.GetBytes("1"))).Status);
            Assert.Equal("1", Encoding.UTF8.GetString(StorageTestSupport.Run(b, owner, StorageTestSupport.Get("w", "k")).Value));
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(a, owner, StorageTestSupport.Delete("w", "k")).Status);
            Assert.Equal(0, StorageHardeningSupport.Scalar(folder, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(a, owner, StorageTestSupport.Set("w", "k", Encoding.UTF8.GetBytes("2"))).Status);

            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(b, owner, StorageTestSupport.Set("w", "j", Encoding.UTF8.GetBytes("b"))).Status);

            using BasisStorageService fresh = new BasisStorageService(StorageHardeningSupport.Sql(folder, table), 16384, 1 << 24);
            Assert.Equal("2", Encoding.UTF8.GetString(StorageTestSupport.Run(fresh, owner, StorageTestSupport.Get("w", "k")).Value));
            Assert.Equal("b", Encoding.UTF8.GetString(StorageTestSupport.Run(fresh, owner, StorageTestSupport.Get("w", "j")).Value));
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void AnOldRevisionNumber_NeverMatchesARecreatedRow()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            const string owner = "did:key:revisions", table = "revision_storage";
            using BasisStorageService service = new BasisStorageService(StorageHardeningSupport.Sql(folder, table), 16384, 1 << 24);
            HashSet<long> starts = new HashSet<long>();
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, owner, StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4))).Status);
                long revision = StorageHardeningSupport.Scalar(folder, $"SELECT revision FROM {table}");
                Assert.True(revision > 1, "a new record started at a guessable revision");
                Assert.True(starts.Add(revision), "two records started at the same revision");
                Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, owner, StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(5))).Status);
                Assert.Equal(revision + 1, StorageHardeningSupport.Scalar(folder, $"SELECT revision FROM {table}"));
                Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, owner, StorageTestSupport.Op(BasisStorageOp.Clear, "w")).Status);
            }
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void ReadsFromACachedCopy_FollowAnotherServersWrites()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            const string owner = "did:key:reader", table = "stale_storage";
            using BasisStorageService a = new BasisStorageService(StorageHardeningSupport.Sql(folder, table), 16384, 1 << 24);
            using BasisStorageService b = new BasisStorageService(StorageHardeningSupport.Sql(folder, table), 16384, 1 << 24);
            StorageTestSupport.Run(a, owner, StorageTestSupport.Set("w", "k", Encoding.UTF8.GetBytes("old")));
            Assert.Equal("old", Encoding.UTF8.GetString(StorageTestSupport.Run(b, owner, StorageTestSupport.Get("w", "k")).Value));
            StorageTestSupport.Run(a, owner, StorageTestSupport.Set("w", "k", Encoding.UTF8.GetBytes("new")));
            Assert.Equal("new", Encoding.UTF8.GetString(StorageTestSupport.Run(b, owner, StorageTestSupport.Get("w", "k")).Value));
            Assert.Equal(new[] { "k" }, StorageTestSupport.Run(b, owner, StorageTestSupport.Op(BasisStorageOp.Keys, "w")).Keys);
            StorageTestSupport.Run(a, owner, StorageTestSupport.Op(BasisStorageOp.Clear, "w"));
            Assert.Equal(BasisStorageStatus.NotFound, StorageTestSupport.Run(b, owner, StorageTestSupport.Get("w", "k")).Status);
            Assert.Equal(0, StorageTestSupport.Run(b, owner, StorageTestSupport.Op(BasisStorageOp.Usage, "w")).Used);
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void QuarantinedRows_AreLeftOutOfTheServerCapAfterARestart()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            const string owner = "did:key:corrupt-row", table = "quarantine_storage";
            using (BasisStorageService first = new BasisStorageService(StorageHardeningSupport.Sql(folder, table), 16384, 1 << 24))
            {
                Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(first, owner, StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(40))).Status);
                Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(first, "did:key:healthy", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(40))).Status);
                first.Forget(owner);
                StorageHardeningSupport.Execute(folder, $"UPDATE {table} SET record = @garbage WHERE owner_key = @owner", ("@garbage", StorageTestSupport.Bytes(120)), ("@owner", BasisStorageRecord.OwnerKeyOf(owner)));
                Assert.Equal(BasisStorageStatus.NotFound, StorageTestSupport.Run(first, owner, StorageTestSupport.Get("w", "k")).Status);
                Assert.Equal(1, first.TotalRecords);
            }
            Assert.Equal(1, StorageHardeningSupport.Scalar(folder, $"SELECT COUNT(*) FROM {table} WHERE owner_key LIKE '%-corrupt-%'"));

            using BasisSqlStorageBackend backend = StorageHardeningSupport.Sql(folder, table);
            backend.Measure(out long records, out long bytes);
            Assert.Equal(1, records);
            int listed = 0;
            backend.List(long.MaxValue, info =>
            {
                Assert.Equal(BasisStorageRecord.OwnerKeyOf("did:key:healthy"), info.OwnerKey);
                listed++;
                return true;
            });
            Assert.Equal(1, listed);

            using BasisStorageService second = new BasisStorageService(StorageHardeningSupport.Sql(folder, table), 16384, 1 << 24);
            Assert.Equal(1, second.TotalRecords);
            Assert.Equal(bytes, second.TotalBytes);
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }
}

public class BasisStorageListingTests
{
    [Fact]
    public void Listing_NamesEachStoredPlayerAndWhatTheyKeep()
    {
        using BasisStorageService service = new BasisStorageService(new BasisMemoryStorageBackend(), 16384, 1 << 24);
        StorageTestSupport.Run(service, "did:key:lister-a", StorageTestSupport.Set("world one", "k", StorageTestSupport.Bytes(5)));
        StorageTestSupport.Run(service, "did:key:lister-b", StorageTestSupport.Set("prop", "k", StorageTestSupport.Bytes(5)));
        StorageTestSupport.Run(service, "did:key:lister-b", StorageTestSupport.Set("world one", "x", StorageTestSupport.Bytes(5)));
        List<string> keys = new List<string>();
        Assert.True(service.TryList(long.MaxValue, info =>
        {
            keys.Add(info.OwnerKey);
            return true;
        }));
        Assert.Equal(2, keys.Count);

        Assert.True(service.TryDescribe(BasisStorageRecord.OwnerKeyOf("did:key:lister-b"), TimeSpan.FromSeconds(5), out BasisStorageRecord record));
        Assert.Equal("did:key:lister-b", record.Owner);
        Assert.Equal(new[] { "prop", "world one" }, record.ScopeNames());
        Assert.Equal(2, record.Entries);

        service.Forget("did:key:lister-a");
        Assert.True(service.TryDescribe(BasisStorageRecord.OwnerKeyOf("did:key:lister-a"), TimeSpan.FromSeconds(5), out BasisStorageRecord loaded));
        Assert.Equal("did:key:lister-a", loaded.Owner);
        Assert.False(service.TryDescribe(BasisStorageRecord.OwnerKeyOf("did:key:nobody"), TimeSpan.FromSeconds(5), out _));
        Assert.False(service.TryDescribe("not-a-key", TimeSpan.FromSeconds(5), out _));
    }

    [Fact]
    public void TheOwnerCanBeReadFromARecordWithoutKnowingItFirst()
    {
        BasisStorageRecord record = new BasisStorageRecord("steam:76561198000000001");
        record.Set("w", "k", StorageTestSupport.Bytes(3), 16384);
        Assert.True(BasisStorageRecord.TryReadOwner(record.Serialize(), out string owner));
        Assert.Equal("steam:76561198000000001", owner);
        Assert.False(BasisStorageRecord.TryReadOwner(StorageTestSupport.Bytes(80), out _));
        Assert.False(BasisStorageRecord.TryReadOwner(null!, out _));
    }
}

[Collection("BasisServer shared network statics")]
public class BasisServerStorageLimitsTests
{
    private static int peerIds = 43_000;

    private static FakeNetPeer Install(MapAuthIdentity identity, string address)
    {
        int id = Interlocked.Increment(ref peerIds);
        FakeNetPeer peer = new FakeNetPeer(id, address) { Tag = NetworkServer.AuthenticatedPeerTag };
        identity.Register($"storage-limit-user-{Guid.NewGuid():N}", id, peer);
        return peer;
    }

    private static void Send(FakeNetPeer peer, BasisStorageRequest request)
    {
        NetDataWriter writer = new NetDataWriter();
        BasisStorageProtocol.WriteRequest(writer, request);
        byte[] data = writer.CopyData();
        BasisServerStorage.HandleRequest(NetPacketReader.Create(data, 0, data.Length, static () => { }), peer);
    }

    private static List<BasisStorageReply> Replies(FakeNetPeer peer)
    {
        List<BasisStorageReply> replies = new List<BasisStorageReply>();
        lock (peer.Sent)
        {
            foreach ((byte[] data, byte channel, DeliveryMethod _) in peer.Sent)
            {
                if (channel == BasisNetworkCommons.StorageChannel && BasisStorageProtocol.TryReadReply(new NetDataReader(data), out BasisStorageReply reply)) replies.Add(reply);
            }
        }
        return replies;
    }

    private static async Task<BasisStorageReply> Await(FakeNetPeer peer, ushort id)
    {
        await TransportTestSupport.Until(() => Replies(peer).Any(r => r.RequestId == id));
        return Replies(peer).Single(r => r.RequestId == id);
    }

    [Fact]
    public async Task FirstSaves_FromOneAddress_FollowTheConfiguredLimit()
    {
        using ServerStaticsScope scope = new();
        MapAuthIdentity identity = new MapAuthIdentity();
        NetworkServer.AuthIdentity = identity;
        PermissionIntegration.Manager.EnsureDefaults();
        Assert.True(BasisServerStorage.Start(new Configuration { HasFileSupport = false, StorageNewOwnersPerAddress = 1 }));
        try
        {
            FakeNetPeer first = Install(identity, "192.0.2.10"), second = Install(identity, "192.0.2.10"), elsewhere = Install(identity, "192.0.2.11");
            Send(first, StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4), 1));
            Assert.Equal(BasisStorageStatus.Ok, (await Await(first, 1)).Status);
            Send(second, StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4), 2));
            Assert.Equal(BasisStorageStatus.RateLimited, (await Await(second, 2)).Status);
            Send(elsewhere, StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(4), 3));
            Assert.Equal(BasisStorageStatus.Ok, (await Await(elsewhere, 3)).Status);
            Send(first, StorageTestSupport.Set("w", "k2", StorageTestSupport.Bytes(4), 4));
            Assert.Equal(BasisStorageStatus.Ok, (await Await(first, 4)).Status);

            BasisServerStorage.ApplyConfiguration(new Configuration { HasFileSupport = false, StorageNewOwnersPerAddress = 9, StorageNewOwnerWindowMinutes = 5, StorageNewOwnersPerHour = 7, StorageExpiryDays = 30 });
            BasisStorageService service = BasisServerStorage.Service;
            Assert.Equal(9, service.NewOwners.PerAddress);
            Assert.Equal(TimeSpan.FromMinutes(5), service.NewOwners.Window);
            Assert.Equal(7, service.NewOwners.PerHour);
            Assert.Equal(30, service.ExpiryDays);
        }
        finally
        {
            BasisServerStorage.Stop();
        }
    }

    [Fact]
    public async Task AConnection_MayHaveTheProtocolsInFlightLimitOfRequestsWaiting()
    {
        using ServerStaticsScope scope = new();
        MapAuthIdentity identity = new MapAuthIdentity();
        NetworkServer.AuthIdentity = identity;
        PermissionIntegration.Manager.EnsureDefaults();
        using ManualResetEventSlim gate = new ManualResetEventSlim(false);
        string name = "held-" + Guid.NewGuid().ToString("N");
        BasisStorageBackends.Register(name, (configuration, storageFolder) => new HeldBackend(gate));
        Assert.True(BasisServerStorage.Start(new Configuration { HasFileSupport = false, StorageBackend = name }));
        try
        {
            Assert.Equal(BasisStorageProtocol.MaxInFlightPerConnection, BasisServerStorage.MaxInFlightPerPeer);
            FakeNetPeer peer = Install(identity, "192.0.2.20");
            int limit = BasisStorageProtocol.MaxInFlightPerConnection;
            for (int i = 1; i <= limit + 1; i++) Send(peer, StorageTestSupport.Get("w", "k", (ushort)i));
            Assert.Equal(BasisStorageStatus.Busy, (await Await(peer, (ushort)(limit + 1))).Status);
            Assert.Single(Replies(peer));
            gate.Set();
            for (int i = 1; i <= limit; i++) Assert.Equal(BasisStorageStatus.NotFound, (await Await(peer, (ushort)i)).Status);
        }
        finally
        {
            gate.Set();
            BasisServerStorage.Stop();
            BasisStorageBackends.Unregister(name);
        }
    }

    private sealed class HeldBackend : IBasisStorageBackend
    {
        private readonly ManualResetEventSlim gate;
        public HeldBackend(ManualResetEventSlim gate) => this.gate = gate;
        public string Name => "held";
        public void Open() { }
        public bool TryLoad(string ownerKey, out byte[] blob)
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            blob = null!;
            return false;
        }
        public bool TrySave(string ownerKey, byte[] blob, long expectedRevision, long revision) => true;
        public bool TryDelete(string ownerKey, long expectedRevision) => true;
        public void Quarantine(string ownerKey) { }
        public void Measure(out long records, out long bytes)
        {
            records = 0;
            bytes = 0;
        }
        public void Dispose() { }
    }
}
