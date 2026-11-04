using Basis.Network.Core;
using BasisNetworkServer.Storage;
using BasisPermissions;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Text;
using Xunit;
using static BasisPermissions.PermissionManager;

namespace BasisServerTests;

internal static class StorageTestSupport
{
    public static string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "basis-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void TryDelete(string folder)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static BasisStorageRequest Set(string scope, string key, byte[] value, ushort id = 1) => new() { Op = BasisStorageOp.Set, RequestId = id, Scope = scope, Key = key, Value = value };
    public static BasisStorageRequest Get(string scope, string key, ushort id = 1) => new() { Op = BasisStorageOp.Get, RequestId = id, Scope = scope, Key = key };
    public static BasisStorageRequest Delete(string scope, string key, ushort id = 1) => new() { Op = BasisStorageOp.Delete, RequestId = id, Scope = scope, Key = key };
    public static BasisStorageRequest Op(BasisStorageOp op, string scope, ushort id = 1) => new() { Op = op, RequestId = id, Scope = scope };

    public static BasisStorageReply Run(BasisStorageService service, string owner, BasisStorageRequest request)
    {
        TaskCompletionSource<BasisStorageReply> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BasisStorageStatus queued = service.Submit(owner, request, reply => done.TrySetResult(reply));
        if (queued != BasisStorageStatus.Ok) return new BasisStorageReply { Op = request.Op, RequestId = request.RequestId, Status = queued };
        Assert.True(done.Task.Wait(TimeSpan.FromSeconds(10)), "storage reply timed out");
        return done.Task.Result;
    }

    public static string SqliteConnectionString(string folder) => new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "storage.db"), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();

    public static byte[] Bytes(int length, byte seed = 7)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(seed + i * 31);
        return data;
    }
}

public class BasisStorageProtocolTests
{
    [Theory]
    [InlineData("score")]
    [InlineData("high score")]
    [InlineData("日本語のキー")]
    [InlineData("save \U0001F3AE")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")]
    public void ValidNames_AreAccepted(string name)
    {
        Assert.True(BasisStorageProtocol.IsValidName(name));
        Assert.True(BasisStorageProtocol.TryEncodeName(name, out byte[] bytes));
        Assert.True(BasisStorageProtocol.TryDecodeName(bytes, out string decoded));
        Assert.Equal(name, decoded);
    }

    [Fact]
    public void InvalidNames_AreRejected()
    {
        string[] names =
        {
            "",
            " leading",
            "trailing ",
            "line" + (char)10 + "break",
            "tab" + (char)9 + "here",
            "nul" + (char)0 + "byte",
            "zero" + (char)0x200B + "width",
            "bidi" + (char)0x202E + "override",
            "line" + (char)0x2028 + "separator",
            "lone" + (char)0xD800 + "high",
            "lone" + (char)0xDC00 + "low",
            "ends" + (char)0xD83C,
            new string('a', 65),
            string.Concat(Enumerable.Repeat("日本語", 8)),
        };
        foreach (string name in names)
        {
            Assert.False(BasisStorageProtocol.IsValidName(name), $"accepted {name.Length} chars");
            Assert.False(BasisStorageProtocol.TryEncodeName(name, out _));
        }
    }

    [Fact]
    public void Null_IsNotAName()
    {
        Assert.False(BasisStorageProtocol.IsValidName(null!));
        Assert.False(BasisStorageProtocol.TryDecodeName(null!, out _));
    }

    [Fact]
    public void InvalidUtf8_IsRejected()
    {
        Assert.False(BasisStorageProtocol.TryDecodeName(new byte[] { 0x61, 0xC0, 0xAF }, out _));
        Assert.False(BasisStorageProtocol.TryDecodeName(new byte[] { 0xED, 0xA0, 0x80 }, out _));
        Assert.False(BasisStorageProtocol.TryDecodeName(new byte[] { 0xFF }, out _));
    }

    [Theory]
    [InlineData(BasisStorageOp.Get)]
    [InlineData(BasisStorageOp.Set)]
    [InlineData(BasisStorageOp.Delete)]
    [InlineData(BasisStorageOp.Keys)]
    [InlineData(BasisStorageOp.Usage)]
    [InlineData(BasisStorageOp.Clear)]
    public void Request_RoundTripsEveryOp(BasisStorageOp op)
    {
        BasisStorageRequest request = new() { Op = op, RequestId = 4321, Scope = "world-1", Key = "k", Value = StorageTestSupport.Bytes(300) };
        NetDataWriter writer = new NetDataWriter();
        BasisStorageProtocol.WriteRequest(writer, request);
        Assert.True(BasisStorageProtocol.TryReadRequest(new NetDataReader(writer.CopyData()), out BasisStorageRequest read));
        Assert.Equal(op, read.Op);
        Assert.Equal(4321, read.RequestId);
        Assert.Equal("world-1", read.Scope);
        Assert.Equal(BasisStorageProtocol.HasKey(op) ? "k" : null, read.Key);
        if (op == BasisStorageOp.Set) Assert.Equal(request.Value, read.Value);
        else Assert.Null(read.Value);
    }

    [Fact]
    public void Request_RejectsTrailingBytesUnknownOpsAndBadNames()
    {
        NetDataWriter writer = new NetDataWriter();
        BasisStorageProtocol.WriteRequest(writer, StorageTestSupport.Get("scope", "key"));
        writer.Put((byte)1);
        Assert.False(BasisStorageProtocol.TryReadRequest(new NetDataReader(writer.CopyData()), out _));

        writer.Reset();
        writer.Put((byte)200);
        writer.Put((ushort)1);
        Assert.False(BasisStorageProtocol.TryReadRequest(new NetDataReader(writer.CopyData()), out BasisStorageRequest unknown));
        Assert.Equal(0, unknown.RequestId);

        writer.Reset();
        writer.Put((byte)BasisStorageOp.Keys);
        writer.Put((ushort)9);
        writer.Put((ushort)3);
        writer.Put(new byte[] { 0x61, 0x0A, 0x62 });
        Assert.False(BasisStorageProtocol.TryReadRequest(new NetDataReader(writer.CopyData()), out BasisStorageRequest badName));
        Assert.Equal(9, badName.RequestId);

        writer.Reset();
        writer.Put((byte)BasisStorageOp.Keys);
        writer.Put((ushort)9);
        writer.Put((ushort)200);
        writer.Put(new byte[200]);
        Assert.False(BasisStorageProtocol.TryReadRequest(new NetDataReader(writer.CopyData()), out _));

        writer.Reset();
        writer.Put((byte)BasisStorageOp.Set);
        writer.Put((ushort)9);
        writer.Put((ushort)1);
        writer.Put((byte)'s');
        writer.Put((ushort)1);
        writer.Put((byte)'k');
        writer.Put((ushort)500);
        writer.Put(new byte[10]);
        Assert.False(BasisStorageProtocol.TryReadRequest(new NetDataReader(writer.CopyData()), out _));
    }

    [Fact]
    public void Reply_RoundTripsValuesAndKeys()
    {
        NetDataWriter writer = new NetDataWriter();
        BasisStorageProtocol.WriteReply(writer, new BasisStorageReply { Op = BasisStorageOp.Get, RequestId = 7, Status = BasisStorageStatus.Ok, Used = 120, Limit = 16384, Value = StorageTestSupport.Bytes(64) });
        Assert.True(BasisStorageProtocol.TryReadReply(new NetDataReader(writer.CopyData()), out BasisStorageReply get));
        Assert.Equal(StorageTestSupport.Bytes(64), get.Value);
        Assert.Equal(120, get.Used);
        Assert.Equal(16384, get.Limit);

        writer.Reset();
        BasisStorageProtocol.WriteReply(writer, new BasisStorageReply { Op = BasisStorageOp.Keys, RequestId = 8, Status = BasisStorageStatus.Ok, Keys = new[] { "a", "b \U0001F3AE" } });
        Assert.True(BasisStorageProtocol.TryReadReply(new NetDataReader(writer.CopyData()), out BasisStorageReply keys));
        Assert.Equal(new[] { "a", "b \U0001F3AE" }, keys.Keys);

        writer.Reset();
        BasisStorageProtocol.WriteReply(writer, new BasisStorageReply { Op = BasisStorageOp.Get, RequestId = 9, Status = BasisStorageStatus.NotFound });
        Assert.True(BasisStorageProtocol.TryReadReply(new NetDataReader(writer.CopyData()), out BasisStorageReply missing));
        Assert.Equal(BasisStorageStatus.NotFound, missing.Status);
        Assert.Null(missing.Value);
    }

    [Fact]
    public void RandomBytes_NeverThrow()
    {
        Random random = new Random(1234);
        for (int i = 0; i < 20000; i++)
        {
            byte[] data = new byte[random.Next(0, 96)];
            random.NextBytes(data);
            if (data.Length > 0) data[0] = (byte)random.Next(0, 7);
            BasisStorageProtocol.TryReadRequest(new NetDataReader(data), out _);
            BasisStorageProtocol.TryReadReply(new NetDataReader(data), out _);
        }
    }
}

public class BasisStorageRecordTests
{
    private const string Owner = "did:key:z6MkStorageRecordTestOwner";

    [Fact]
    public void Used_MatchesTheSerializedPayload()
    {
        BasisStorageRecord record = new BasisStorageRecord(Owner);
        Assert.Equal(BasisStorageStatus.Ok, record.Set("world", "a", StorageTestSupport.Bytes(10), 16384));
        Assert.Equal(BasisStorageStatus.Ok, record.Set("world", "bb", StorageTestSupport.Bytes(20), 16384));
        Assert.Equal(BasisStorageStatus.Ok, record.Set("prop \U0001F3AE", "c", Array.Empty<byte>(), 16384));
        int expected = BasisStorageProtocol.ScopeCost(5) + BasisStorageProtocol.EntryCost(1, 10) + BasisStorageProtocol.EntryCost(2, 20) + BasisStorageProtocol.ScopeCost(Encoding.UTF8.GetByteCount("prop \U0001F3AE")) + BasisStorageProtocol.EntryCost(1, 0);
        Assert.Equal(expected, record.Used);
        Assert.Equal(record.BlobLength, record.Serialize().Length);

        Assert.True(record.Remove("world", "a"));
        Assert.False(record.Remove("world", "a"));
        Assert.Equal(1, record.Clear("prop \U0001F3AE"));
        Assert.Equal(BasisStorageProtocol.ScopeCost(5) + BasisStorageProtocol.EntryCost(2, 20), record.Used);
        Assert.True(record.Remove("world", "bb"));
        Assert.True(record.IsEmpty);
        Assert.Equal(0, record.Used);
        Assert.Equal(0, record.Entries);
    }

    [Fact]
    public void Serialize_RoundTripsEverything()
    {
        BasisStorageRecord record = new BasisStorageRecord(Owner) { Revision = 41 };
        record.Set("world", "z", StorageTestSupport.Bytes(50, 1), 16384);
        record.Set("world", "a", StorageTestSupport.Bytes(5, 2), 16384);
        record.Set("other", "key", StorageTestSupport.Bytes(0), 16384);
        Assert.True(BasisStorageRecord.TryParse(record.Serialize(), Owner, out BasisStorageRecord parsed));
        Assert.Equal(41, parsed.Revision);
        Assert.Equal(record.Used, parsed.Used);
        Assert.Equal(record.Entries, parsed.Entries);
        Assert.Equal(new[] { "other", "world" }, parsed.ScopeNames());
        Assert.Equal(new[] { "a", "z" }, parsed.KeysOf("world"));
        Assert.True(parsed.TryGet("world", "z", out byte[] value));
        Assert.Equal(StorageTestSupport.Bytes(50, 1), value);
        Assert.Equal(record.Serialize(), parsed.Serialize());
    }

    [Fact]
    public void AnyFlippedByte_FailsTheIntegrityCheck()
    {
        BasisStorageRecord record = new BasisStorageRecord(Owner) { Revision = 3 };
        record.Set("world", "key", StorageTestSupport.Bytes(24), 16384);
        byte[] blob = record.Serialize();
        for (int i = 0; i < blob.Length; i++)
        {
            byte[] copy = (byte[])blob.Clone();
            copy[i] ^= 0x01;
            Assert.False(BasisStorageRecord.TryParse(copy, Owner, out _), $"byte {i} flipped and still parsed");
        }
        for (int length = 0; length < blob.Length; length++)
        {
            Assert.False(BasisStorageRecord.TryParse(blob.AsSpan(0, length).ToArray(), Owner, out _));
        }
    }

    [Fact]
    public void ARecord_OnlyParsesForItsOwner()
    {
        BasisStorageRecord record = new BasisStorageRecord(Owner);
        record.Set("world", "key", StorageTestSupport.Bytes(4), 16384);
        byte[] blob = record.Serialize();
        Assert.True(BasisStorageRecord.TryParse(blob, Owner, out _));
        Assert.False(BasisStorageRecord.TryParse(blob, Owner + "x", out _));
        Assert.False(BasisStorageRecord.TryParse(blob, "steam:76561198000000000", out _));
    }

    [Fact]
    public void Quota_RefusesGrowthButAllowsShrinkingWhenOver()
    {
        BasisStorageRecord record = new BasisStorageRecord(Owner);
        Assert.Equal(BasisStorageStatus.Ok, record.Set("w", "k", StorageTestSupport.Bytes(900), 1000));
        Assert.Equal(BasisStorageStatus.Full, record.Set("w", "k2", StorageTestSupport.Bytes(100), 1000));
        Assert.Equal(BasisStorageStatus.Full, record.Set("w", "k", StorageTestSupport.Bytes(995), 1000));
        int used = record.Used;
        Assert.Equal(BasisStorageStatus.Ok, record.Set("w", "k", StorageTestSupport.Bytes(800), 500));
        Assert.True(record.Used < used);
        Assert.Equal(BasisStorageStatus.Full, record.Set("w", "k", StorageTestSupport.Bytes(801), 500));
    }

    [Fact]
    public void EntryAndScopeCaps_AreEnforced()
    {
        BasisStorageRecord record = new BasisStorageRecord(Owner);
        for (int i = 0; i < BasisStorageProtocol.MaxEntriesPerPlayer; i++)
        {
            Assert.Equal(BasisStorageStatus.Ok, record.Set("s" + (i % BasisStorageProtocol.MaxScopesPerPlayer), "k" + i, Array.Empty<byte>(), BasisStorageProtocol.MaxBytesPerPlayer));
        }
        Assert.Equal(BasisStorageStatus.Full, record.Set("s0", "one-more", Array.Empty<byte>(), BasisStorageProtocol.MaxBytesPerPlayer));
        Assert.Equal(BasisStorageProtocol.MaxScopesPerPlayer, record.Scopes);
        Assert.True(BasisStorageRecord.TryParse(record.Serialize(), Owner, out BasisStorageRecord parsed));
        Assert.Equal(BasisStorageProtocol.MaxEntriesPerPlayer, parsed.Entries);

        BasisStorageRecord scopes = new BasisStorageRecord(Owner);
        for (int i = 0; i < BasisStorageProtocol.MaxScopesPerPlayer; i++) Assert.Equal(BasisStorageStatus.Ok, scopes.Set("scope" + i, "k", Array.Empty<byte>(), BasisStorageProtocol.MaxBytesPerPlayer));
        Assert.Equal(BasisStorageStatus.Full, scopes.Set("scope-extra", "k", Array.Empty<byte>(), BasisStorageProtocol.MaxBytesPerPlayer));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        BasisStorageRecord record = new BasisStorageRecord(Owner);
        record.Set("w", "k", StorageTestSupport.Bytes(3), 16384);
        BasisStorageRecord clone = record.Clone();
        clone.Set("w", "k2", StorageTestSupport.Bytes(3), 16384);
        clone.Remove("w", "k");
        Assert.True(record.TryGet("w", "k", out _));
        Assert.False(record.TryGet("w", "k2", out _));
    }

    [Fact]
    public void OwnerKeys_AreFixedLengthHex()
    {
        string key = BasisStorageRecord.OwnerKeyOf(Owner);
        Assert.Equal(64, key.Length);
        Assert.True(BasisStorageRecord.IsOwnerKey(key));
        Assert.Equal(key, BasisStorageRecord.OwnerKeyOf(Owner));
        Assert.NotEqual(key, BasisStorageRecord.OwnerKeyOf(Owner + "2"));
        Assert.False(BasisStorageRecord.IsOwnerKey("../" + key.Substring(3)));
        Assert.False(BasisStorageRecord.IsOwnerKey(key.ToUpperInvariant()));
        Assert.False(BasisStorageRecord.IsOwnerKey(key + "0"));
    }
}

public class BasisFileStorageBackendTests
{
    [Fact]
    public void SaveLoadDelete_StayInsideTheStorageFolder()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using BasisFileStorageBackend backend = new BasisFileStorageBackend(folder);
            backend.Open();
            string key = BasisStorageRecord.OwnerKeyOf("did:key:file-owner");
            Assert.False(backend.TryLoad(key, out _));
            byte[] blob = StorageTestSupport.Bytes(100);
            Assert.True(backend.TrySave(key, blob, 0, 1));
            string path = Path.Combine(folder, "players", key.Substring(0, 2), key + ".bsr");
            Assert.True(File.Exists(path));
            Assert.True(backend.TryLoad(key, out byte[] loaded));
            Assert.Equal(blob, loaded);
            backend.Measure(out long records, out long bytes);
            Assert.Equal(1, records);
            Assert.Equal(100, bytes);
            Assert.True(backend.TryDelete(key, 1));
            Assert.False(File.Exists(path));
            Assert.True(backend.TryDelete(key, 1));
            Assert.Throws<ArgumentException>(() => backend.TryLoad("../../../../etc/passwd", out _));
            Assert.Throws<ArgumentException>(() => backend.TrySave(key.ToUpperInvariant(), blob, 0, 1));
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void ASecondServer_CannotOpenTheSameFolder()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using BasisFileStorageBackend first = new BasisFileStorageBackend(folder);
            first.Open();
            using BasisFileStorageBackend second = new BasisFileStorageBackend(folder);
            Assert.ThrowsAny<IOException>(() => second.Open());
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void Open_ClearsInterruptedWrites_AndQuarantineSetsRecordsAside()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            string key = BasisStorageRecord.OwnerKeyOf("did:key:interrupted");
            string shard = Path.Combine(folder, "players", key.Substring(0, 2));
            Directory.CreateDirectory(shard);
            File.WriteAllBytes(Path.Combine(shard, key + ".bsr.tmp"), new byte[] { 1, 2, 3 });
            using BasisFileStorageBackend backend = new BasisFileStorageBackend(folder);
            backend.Open();
            Assert.False(File.Exists(Path.Combine(shard, key + ".bsr.tmp")));
            Assert.True(backend.TrySave(key, StorageTestSupport.Bytes(10), 0, 1));
            backend.Quarantine(key);
            Assert.False(backend.TryLoad(key, out _));
            Assert.Single(Directory.GetFiles(shard, key + ".bsr.corrupt-*"));
            backend.Measure(out long records, out _);
            Assert.Equal(0, records);
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void OversizedFiles_AreNotRead()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using BasisFileStorageBackend backend = new BasisFileStorageBackend(folder);
            backend.Open();
            string key = BasisStorageRecord.OwnerKeyOf("did:key:huge");
            string shard = Path.Combine(folder, "players", key.Substring(0, 2));
            Directory.CreateDirectory(shard);
            using (FileStream stream = new FileStream(Path.Combine(shard, key + ".bsr"), FileMode.Create)) stream.SetLength(BasisStorageRecord.MaxBlobBytes + 1);
            Assert.Throws<InvalidDataException>(() => backend.TryLoad(key, out _));
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }
}

public class BasisSqlStorageBackendTests
{
    private static BasisSqlStorageBackend Open(string folder, string table = "basis_storage")
    {
        BasisSqlStorageBackend backend = new BasisSqlStorageBackend("sqlite", SqliteFactory.Instance, BasisStorageSqlDialect.Sqlite, StorageTestSupport.SqliteConnectionString(folder), table);
        backend.Open();
        return backend;
    }

    [Fact]
    public void Revisions_StopLostUpdates()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using BasisSqlStorageBackend backend = Open(folder);
            string key = BasisStorageRecord.OwnerKeyOf("did:key:sql-owner");
            Assert.False(backend.TryLoad(key, out _));
            Assert.True(backend.TrySave(key, StorageTestSupport.Bytes(10, 1), 0, 1));
            Assert.False(backend.TrySave(key, StorageTestSupport.Bytes(10, 2), 0, 1));
            Assert.True(backend.TrySave(key, StorageTestSupport.Bytes(12, 3), 1, 2));
            Assert.False(backend.TrySave(key, StorageTestSupport.Bytes(12, 4), 1, 2));
            Assert.True(backend.TryLoad(key, out byte[] loaded));
            Assert.Equal(StorageTestSupport.Bytes(12, 3), loaded);
            backend.Measure(out long records, out long bytes);
            Assert.Equal(1, records);
            Assert.Equal(12, bytes);
            Assert.False(backend.TryDelete(key, 1));
            Assert.True(backend.TryDelete(key, 2));
            Assert.True(backend.TryDelete(key, 2));
            Assert.False(backend.TryLoad(key, out _));
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void Quarantine_MovesTheRowOutOfTheWay()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using BasisSqlStorageBackend backend = Open(folder);
            string key = BasisStorageRecord.OwnerKeyOf("did:key:sql-corrupt");
            Assert.True(backend.TrySave(key, StorageTestSupport.Bytes(10), 0, 1));
            backend.Quarantine(key);
            Assert.False(backend.TryLoad(key, out _));
            Assert.True(backend.TrySave(key, StorageTestSupport.Bytes(4), 0, 1));
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("1table")]
    [InlineData("basis_storage; DROP TABLE x")]
    [InlineData("basis-storage")]
    [InlineData("a\"b")]
    [InlineData("tablenamethatiswaytoolongtobeallowedbecauseitgoesonandonandonandon")]
    public void TableNames_CannotInjectSql(string table)
    {
        Assert.False(BasisSqlStorageBackend.IsValidTableName(table));
        Assert.Throws<ArgumentException>(() => new BasisSqlStorageBackend("sqlite", SqliteFactory.Instance, BasisStorageSqlDialect.Sqlite, "Data Source=:memory:", table));
    }

    [Fact]
    public void OwnerKeys_AreCheckedBeforeQuerying()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using BasisSqlStorageBackend backend = Open(folder);
            Assert.Throws<ArgumentException>(() => backend.TryLoad("' OR 1=1 --", out _));
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }
}

public class BasisStorageServiceTests
{
    private static BasisStorageService Memory(int bytesPerPlayer = 16384, long maxTotal = 1 << 24) => new BasisStorageService(new BasisMemoryStorageBackend(), bytesPerPlayer, maxTotal);

    [Fact]
    public void SetGetKeysUsageDeleteClear_Work()
    {
        using BasisStorageService service = Memory();
        const string owner = "did:key:service-owner";
        Assert.Equal(BasisStorageStatus.NotFound, StorageTestSupport.Run(service, owner, StorageTestSupport.Get("world", "a")).Status);
        BasisStorageReply set = StorageTestSupport.Run(service, owner, StorageTestSupport.Set("world", "b", StorageTestSupport.Bytes(30)));
        Assert.Equal(BasisStorageStatus.Ok, set.Status);
        Assert.Equal(16384, set.Limit);
        Assert.Equal(BasisStorageProtocol.ScopeCost(5) + BasisStorageProtocol.EntryCost(1, 30), set.Used);
        StorageTestSupport.Run(service, owner, StorageTestSupport.Set("world", "a", StorageTestSupport.Bytes(5)));
        BasisStorageReply get = StorageTestSupport.Run(service, owner, StorageTestSupport.Get("world", "b"));
        Assert.Equal(StorageTestSupport.Bytes(30), get.Value);
        Assert.Equal(new[] { "a", "b" }, StorageTestSupport.Run(service, owner, StorageTestSupport.Op(BasisStorageOp.Keys, "world")).Keys);
        Assert.Equal(get.Used, StorageTestSupport.Run(service, owner, StorageTestSupport.Op(BasisStorageOp.Usage, "world")).Used);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, owner, StorageTestSupport.Delete("world", "a")).Status);
        Assert.Equal(BasisStorageStatus.NotFound, StorageTestSupport.Run(service, owner, StorageTestSupport.Delete("world", "a")).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, owner, StorageTestSupport.Op(BasisStorageOp.Clear, "world")).Status);
        Assert.Equal(0, StorageTestSupport.Run(service, owner, StorageTestSupport.Op(BasisStorageOp.Usage, "world")).Used);
        Assert.Equal(0, service.TotalRecords);
        Assert.Equal(0, service.TotalBytes);
    }

    [Fact]
    public void Players_OnlyEverSeeTheirOwnData()
    {
        using BasisStorageService service = Memory();
        StorageTestSupport.Run(service, "did:key:alice", StorageTestSupport.Set("world", "secret", Encoding.UTF8.GetBytes("alice")));
        Assert.Equal(BasisStorageStatus.NotFound, StorageTestSupport.Run(service, "did:key:bob", StorageTestSupport.Get("world", "secret")).Status);
        Assert.Empty(StorageTestSupport.Run(service, "did:key:bob", StorageTestSupport.Op(BasisStorageOp.Keys, "world")).Keys);
        StorageTestSupport.Run(service, "did:key:bob", StorageTestSupport.Op(BasisStorageOp.Clear, "world"));
        Assert.Equal("alice", Encoding.UTF8.GetString(StorageTestSupport.Run(service, "did:key:alice", StorageTestSupport.Get("world", "secret")).Value));
    }

    [Fact]
    public void Quota_IsEnforcedPerPlayer()
    {
        using BasisStorageService service = Memory(bytesPerPlayer: 1024);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:q", StorageTestSupport.Set("w", "a", StorageTestSupport.Bytes(900))).Status);
        BasisStorageReply full = StorageTestSupport.Run(service, "did:key:q", StorageTestSupport.Set("w", "b", StorageTestSupport.Bytes(200)));
        Assert.Equal(BasisStorageStatus.Full, full.Status);
        Assert.Equal(1024, full.Limit);
        Assert.Equal(BasisStorageStatus.NotFound, StorageTestSupport.Run(service, "did:key:q", StorageTestSupport.Get("w", "b")).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:other", StorageTestSupport.Set("w", "b", StorageTestSupport.Bytes(200))).Status);
    }

    [Fact]
    public void ServerCap_StopsGrowthForEveryone()
    {
        using BasisStorageService service = Memory(maxTotal: 2 * BasisStorageService.RecordOverheadBytes + 300);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:first", StorageTestSupport.Set("w", "a", StorageTestSupport.Bytes(100))).Status);
        Assert.Equal(BasisStorageStatus.Full, StorageTestSupport.Run(service, "did:key:second", StorageTestSupport.Set("w", "a", StorageTestSupport.Bytes(200))).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:first", StorageTestSupport.Set("w", "a", StorageTestSupport.Bytes(10))).Status);
    }

    [Fact]
    public void Writes_AreRateLimitedPerPlayer()
    {
        using BasisStorageService service = Memory();
        int limited = 0;
        for (int i = 0; i < 30; i++)
        {
            if (StorageTestSupport.Run(service, "did:key:spammer", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(8))).Status == BasisStorageStatus.RateLimited) limited++;
        }
        Assert.InRange(limited, 15, 20);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:polite", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(8))).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:spammer", StorageTestSupport.Get("w", "k")).Status);
    }

    [Fact]
    public void MalformedRequests_AreRefusedWithoutTouchingStorage()
    {
        using BasisStorageService service = Memory();
        Assert.Equal(BasisStorageStatus.Invalid, service.Submit("did:key:m", new BasisStorageRequest { Op = BasisStorageOp.Set, RequestId = 1, Scope = "w\n", Key = "k", Value = new byte[1] }, _ => { }));
        Assert.Equal(BasisStorageStatus.Invalid, service.Submit("did:key:m", new BasisStorageRequest { Op = BasisStorageOp.Set, RequestId = 1, Scope = "w", Key = "k" }, _ => { }));
        Assert.Equal(BasisStorageStatus.Invalid, service.Submit("did:key:m", new BasisStorageRequest { Op = (BasisStorageOp)99, RequestId = 1, Scope = "w" }, _ => { }));
        Assert.Equal(BasisStorageStatus.Denied, service.Submit("", StorageTestSupport.Get("w", "k"), _ => { }));
        Assert.Equal(0, service.TotalRecords);
    }

    [Fact]
    public void FileStorage_SurvivesARestart_AndIsMeasuredOnBoot()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            using (BasisStorageService first = new BasisStorageService(Opened(new BasisFileStorageBackend(folder)), 16384, 1 << 24))
            {
                Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(first, "did:key:persist", StorageTestSupport.Set("world", "level", Encoding.UTF8.GetBytes("7"))).Status);
                Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(first, "did:key:persist2", StorageTestSupport.Set("world", "level", Encoding.UTF8.GetBytes("9"))).Status);
            }
            using BasisStorageService second = new BasisStorageService(Opened(new BasisFileStorageBackend(folder)), 16384, 1 << 24);
            Assert.Equal(2, second.TotalRecords);
            Assert.True(second.TotalBytes > 0);
            Assert.Equal("7", Encoding.UTF8.GetString(StorageTestSupport.Run(second, "did:key:persist", StorageTestSupport.Get("world", "level")).Value));
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void ACorruptFile_IsSetAsideAndThePlayerStartsEmpty()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            const string owner = "did:key:corrupted";
            string key = BasisStorageRecord.OwnerKeyOf(owner);
            using (BasisStorageService first = new BasisStorageService(Opened(new BasisFileStorageBackend(folder)), 16384, 1 << 24))
            {
                StorageTestSupport.Run(first, owner, StorageTestSupport.Set("world", "k", StorageTestSupport.Bytes(40)));
            }
            string path = Path.Combine(folder, "players", key.Substring(0, 2), key + ".bsr");
            byte[] blob = File.ReadAllBytes(path);
            blob[blob.Length / 2] ^= 0xFF;
            File.WriteAllBytes(path, blob);
            using BasisStorageService second = new BasisStorageService(Opened(new BasisFileStorageBackend(folder)), 16384, 1 << 24);
            Assert.Equal(BasisStorageStatus.NotFound, StorageTestSupport.Run(second, owner, StorageTestSupport.Get("world", "k")).Status);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, key + ".bsr.corrupt-*"));
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(second, owner, StorageTestSupport.Set("world", "k", StorageTestSupport.Bytes(4))).Status);
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void TwoServersOnOneDatabase_NeverLoseEachOthersWrites()
    {
        string folder = StorageTestSupport.TempFolder();
        try
        {
            const string owner = "did:key:roaming";
            using BasisStorageService a = new BasisStorageService(Opened(new BasisSqlStorageBackend("sqlite", SqliteFactory.Instance, BasisStorageSqlDialect.Sqlite, StorageTestSupport.SqliteConnectionString(folder), "shared_storage")), 16384, 1 << 24);
            using BasisStorageService b = new BasisStorageService(Opened(new BasisSqlStorageBackend("sqlite", SqliteFactory.Instance, BasisStorageSqlDialect.Sqlite, StorageTestSupport.SqliteConnectionString(folder), "shared_storage")), 16384, 1 << 24);
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(a, owner, StorageTestSupport.Set("world", "from-a", StorageTestSupport.Bytes(3))).Status);
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(b, owner, StorageTestSupport.Set("world", "from-b", StorageTestSupport.Bytes(3))).Status);
            Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(a, owner, StorageTestSupport.Set("world", "from-a-again", StorageTestSupport.Bytes(3))).Status);
            a.Forget(owner);
            Assert.Equal(new[] { "from-a", "from-a-again", "from-b" }, StorageTestSupport.Run(a, owner, StorageTestSupport.Op(BasisStorageOp.Keys, "world")).Keys);
        }
        finally
        {
            StorageTestSupport.TryDelete(folder);
        }
    }

    [Fact]
    public void BackendFailures_StillAnswerEveryRequest()
    {
        using BasisStorageService service = new BasisStorageService(new FailingBackend(), 16384, 1 << 24);
        Assert.Equal(BasisStorageStatus.Unavailable, StorageTestSupport.Run(service, "did:key:unlucky", StorageTestSupport.Get("w", "k")).Status);
        Assert.Equal(BasisStorageStatus.Unavailable, StorageTestSupport.Run(service, "did:key:unlucky", StorageTestSupport.Set("w", "k", new byte[1])).Status);
        Assert.Equal(2, service.Failures);
    }

    [Fact]
    public void InspectAndWipe_ReachOnePlayer()
    {
        using BasisStorageService service = Memory();
        StorageTestSupport.Run(service, "did:key:wipe-me", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(8)));
        StorageTestSupport.Run(service, "did:key:keep-me", StorageTestSupport.Set("w", "k", StorageTestSupport.Bytes(8)));
        Assert.True(service.TryInspect("did:key:wipe-me", TimeSpan.FromSeconds(5), out BasisStorageRecord snapshot));
        Assert.Equal(new[] { "k" }, snapshot.KeysOf("w"));
        Assert.True(service.TryWipe("did:key:wipe-me", TimeSpan.FromSeconds(5)));
        Assert.Equal(BasisStorageStatus.NotFound, StorageTestSupport.Run(service, "did:key:wipe-me", StorageTestSupport.Get("w", "k")).Status);
        Assert.Equal(BasisStorageStatus.Ok, StorageTestSupport.Run(service, "did:key:keep-me", StorageTestSupport.Get("w", "k")).Status);
        Assert.Equal(1, service.TotalRecords);
    }

    [Fact]
    public void RequestsFromOnePlayer_RunInOrder()
    {
        using BasisStorageService service = Memory();
        ConcurrentQueue<BasisStorageReply> replies = new();
        using CountdownEvent done = new CountdownEvent(8);
        for (ushort i = 1; i <= 8; i++)
        {
            Assert.Equal(BasisStorageStatus.Ok, service.Submit("did:key:ordered", StorageTestSupport.Set("w", "k", new[] { (byte)i }, i), reply => { replies.Enqueue(reply); done.Signal(); }));
        }
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (ushort)i), replies.Select(r => r.RequestId));
        Assert.Equal(new byte[] { 8 }, StorageTestSupport.Run(service, "did:key:ordered", StorageTestSupport.Get("w", "k")).Value);
    }

    private static IBasisStorageBackend Opened(IBasisStorageBackend backend)
    {
        backend.Open();
        return backend;
    }

    private sealed class FailingBackend : IBasisStorageBackend
    {
        public string Name => "failing";
        public void Open() { }
        public bool TryLoad(string ownerKey, out byte[] blob) => throw new IOException("disk on fire");
        public bool TrySave(string ownerKey, byte[] blob, long expectedRevision, long revision) => throw new IOException("disk on fire");
        public bool TryDelete(string ownerKey, long expectedRevision) => throw new IOException("disk on fire");
        public void Quarantine(string ownerKey) { }
        public void Measure(out long records, out long bytes)
        {
            records = 0;
            bytes = 0;
        }
        public void Dispose() { }
    }
}

[Collection("BasisServer shared network statics")]
public class BasisServerStorageHandlerTests
{
    private static int peerIds = 41_000;

    private static NetPacketReader Packet(BasisStorageRequest request)
    {
        NetDataWriter writer = new NetDataWriter();
        BasisStorageProtocol.WriteRequest(writer, request);
        byte[] data = writer.CopyData();
        return NetPacketReader.Create(data, 0, data.Length, static () => { });
    }

    private static BasisStorageReply[] Replies(FakeNetPeer peer)
    {
        List<BasisStorageReply> replies = new();
        lock (peer.Sent)
        {
            foreach ((byte[] data, byte channel, DeliveryMethod method) in peer.Sent)
            {
                if (channel != BasisNetworkCommons.StorageChannel) continue;
                Assert.Equal(DeliveryMethod.ReliableOrdered, method);
                Assert.True(BasisStorageProtocol.TryReadReply(new NetDataReader(data), out BasisStorageReply reply));
                replies.Add(reply);
            }
        }
        return replies.ToArray();
    }

    private static async Task<BasisStorageReply> Await(FakeNetPeer peer, ushort id)
    {
        await TransportTestSupport.Until(() => Replies(peer).Any(r => r.RequestId == id));
        return Replies(peer).Single(r => r.RequestId == id);
    }

    private static (FakeNetPeer Peer, string Uuid, MapAuthIdentity Identity) Install(bool verified)
    {
        MapAuthIdentity identity = new MapAuthIdentity();
        NetworkServer.AuthIdentity = identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIds);
        FakeNetPeer peer = new FakeNetPeer(id, "10.1.2.3") { Tag = NetworkServer.AuthenticatedPeerTag };
        string uuid = $"storage-user-{Guid.NewGuid():N}";
        if (verified) identity.Register(uuid, id, peer);
        return (peer, uuid, identity);
    }

    [Fact]
    public async Task VerifiedPlayers_CanSaveAndLoad_AndUnverifiedOnesCannot()
    {
        using ServerStaticsScope scope = new();
        Assert.True(BasisServerStorage.Start(new Configuration { HasFileSupport = false, UseAuthIdentity = true }));
        try
        {
            (FakeNetPeer peer, _, _) = Install(verified: true);
            BasisServerStorage.HandleRequest(Packet(StorageTestSupport.Set("world", "coins", Encoding.UTF8.GetBytes("12"), 10)), peer);
            Assert.Equal(BasisStorageStatus.Ok, (await Await(peer, 10)).Status);
            BasisServerStorage.HandleRequest(Packet(StorageTestSupport.Get("world", "coins", 11)), peer);
            BasisStorageReply get = await Await(peer, 11);
            Assert.Equal("12", Encoding.UTF8.GetString(get.Value));
            Assert.Equal(BasisStorageProtocol.DefaultBytesPerPlayer, get.Limit);

            (FakeNetPeer stranger, _, _) = Install(verified: false);
            BasisServerStorage.HandleRequest(Packet(StorageTestSupport.Get("world", "coins", 12)), stranger);
            Assert.Equal(BasisStorageStatus.Denied, (await Await(stranger, 12)).Status);
        }
        finally
        {
            BasisServerStorage.Stop();
        }
    }

    [Fact]
    public async Task ADeniedPermission_ShutsAPlayerOut()
    {
        using ServerStaticsScope scope = new();
        Assert.True(BasisServerStorage.Start(new Configuration { HasFileSupport = false }));
        (FakeNetPeer peer, string uuid, _) = Install(verified: true);
        PermissionIntegration.Manager.AddUserNode(uuid, "-" + PermNodes.StorageUse);
        try
        {
            BasisServerStorage.HandleRequest(Packet(StorageTestSupport.Set("world", "k", new byte[1], 20)), peer);
            Assert.Equal(BasisStorageStatus.Denied, (await Await(peer, 20)).Status);
        }
        finally
        {
            PermissionIntegration.Manager.RemoveUserNode(uuid, "-" + PermNodes.StorageUse);
            BasisServerStorage.Stop();
        }
    }

    [Fact]
    public async Task StorageOff_AndMalformedPackets_AreAnswered()
    {
        using ServerStaticsScope scope = new();
        BasisServerStorage.Stop();
        (FakeNetPeer peer, _, _) = Install(verified: true);
        BasisServerStorage.HandleRequest(Packet(StorageTestSupport.Get("world", "k", 30)), peer);
        Assert.Equal(BasisStorageStatus.Unavailable, (await Await(peer, 30)).Status);
        Assert.False(BasisServerStorage.Start(new Configuration { HasFileSupport = false, StorageEnabled = false }));

        Assert.True(BasisServerStorage.Start(new Configuration { HasFileSupport = false }));
        try
        {
            NetDataWriter writer = new NetDataWriter();
            writer.Put((byte)BasisStorageOp.Get);
            writer.Put((ushort)31);
            writer.Put((ushort)2);
            writer.Put(new byte[] { 0x0A, 0x0B });
            byte[] bad = writer.CopyData();
            BasisServerStorage.HandleRequest(NetPacketReader.Create(bad, 0, bad.Length, static () => { }), peer);
            Assert.Equal(BasisStorageStatus.Invalid, (await Await(peer, 31)).Status);
            byte[] garbage = { 0xFF, 0x01 };
            int before = Replies(peer).Length;
            BasisServerStorage.HandleRequest(NetPacketReader.Create(garbage, 0, garbage.Length, static () => { }), peer);
            await Task.Delay(100);
            Assert.Equal(before, Replies(peer).Length);
        }
        finally
        {
            BasisServerStorage.Stop();
        }
    }

    [Fact]
    public void FileBackend_FallsBackToMemoryWithoutFileSupport()
    {
        Assert.True(BasisServerStorage.Start(new Configuration { HasFileSupport = false, StorageBackend = "file" }));
        try
        {
            Assert.Equal("memory", BasisServerStorage.Service.BackendName);
        }
        finally
        {
            BasisServerStorage.Stop();
        }
        Assert.False(BasisServerStorage.Start(new Configuration { HasFileSupport = false, StorageBackend = "no-such-backend" }));
        Assert.False(BasisServerStorage.IsRunning);
    }
}
