using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BasisPersistentKv;
using Xunit;
using Xunit.Abstractions;


public class BasisPersistentKvDatabaseTests
{
    private readonly ITestOutputHelper _output;

    public BasisPersistentKvDatabaseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<string> CreateUserAsync()
    {
        var userId = Guid.NewGuid().ToString("N");
        var addResult = await BasisPersistientKVDatabase.AddUserAsync(userId);

        _output.WriteLine($"AddUserAsync({userId}) => {addResult.ErrorCode}, {addResult.Message}");
        Assert.Equal(KvError.Success, addResult.ErrorCode);

        return userId;
    }

    [Fact]
    public async Task AddUser_InitialQuotaIsZero()
    {
        var userId = await CreateUserAsync();

        var quotaResult = await BasisPersistientKVDatabase.GetQuotaAsync(userId, userId);
        _output.WriteLine($"GetQuotaAsync => {quotaResult.ErrorCode}, {quotaResult.Message}");

        Assert.Equal(KvError.Success, quotaResult.ErrorCode);

        var q = quotaResult.Value!;
        _output.WriteLine($"Quota: CurrentKeys={q.CurrentKeys}, MaxKeys={q.MaxKeys}, CurrentBytes={q.CurrentBytes}, MaxBytes={q.MaxBytes}");

        Assert.Equal(0, q.CurrentKeys);
        Assert.Equal(0, q.CurrentBytes);
        Assert.True(q.MaxKeys > 0);
        Assert.True(q.MaxBytes > 0);
    }

    [Fact]
    public async Task SetGetExistsDelete_RoundTrip_WorksAndKeepsQuotaConsistent()
    {
        var userId = await CreateUserAsync();

        var key = "test-key";
        var value = Encoding.UTF8.GetBytes("hello world");

        // Before: List should be empty
        var listBefore = await BasisPersistientKVDatabase.ListKeysAsync(userId, userId);
        _output.WriteLine($"ListKeys before => {listBefore.ErrorCode}, keys={string.Join(",", listBefore.Value)}");
        Assert.Equal(KvError.Success, listBefore.ErrorCode);
        Assert.Empty(listBefore.Value.keys);
        Assert.False(listBefore.Value.more);

        // Set
        var setResult = await BasisPersistientKVDatabase.SetKeyAsync(userId, userId, key, value);
        _output.WriteLine($"SetAsync => {setResult.ErrorCode}, {setResult.Message}");
        Assert.Equal(KvError.Success, setResult.ErrorCode);

        // Exists
        var existsResult = await BasisPersistientKVDatabase.KeyExistsAsync(userId, userId, key);
        _output.WriteLine($"ExistsAsync => {existsResult.ErrorCode}, {existsResult.Message}, value={existsResult.Value}");
        Assert.Equal(KvError.Success, existsResult.ErrorCode);
        Assert.True(existsResult.Value);

        // Get
        var getResult = await BasisPersistientKVDatabase.GetKeyAsync(userId, userId, key);
        _output.WriteLine($"GetAsync => {getResult.ErrorCode}, {getResult.Message}");
        Assert.Equal(KvError.Success, getResult.ErrorCode);
        Assert.Equal(value, getResult.Value);

        // List after insert
        var listAfter = await BasisPersistientKVDatabase.ListKeysAsync(userId, userId);
        _output.WriteLine($"ListKeys after => {listAfter.ErrorCode}, keys={string.Join(",", listAfter.Value.keys)}");
        Assert.Equal(KvError.Success, listAfter.ErrorCode);
        Assert.Single(listAfter.Value.keys);
        Assert.Equal(key, listAfter.Value.keys[0]);

        // Quota invariants
        var quotaAfter = await BasisPersistientKVDatabase.GetQuotaAsync(userId, userId);
        _output.WriteLine($"Quota after insert => Keys={quotaAfter.Value.CurrentKeys}, Bytes={quotaAfter.Value.CurrentBytes}");
        Assert.Equal(KvError.Success, quotaAfter.ErrorCode);
        Assert.Equal(1, quotaAfter.Value.CurrentKeys);
        Assert.True(quotaAfter.Value.CurrentBytes >= value.Length + key.Length);

        // Delete
        var deleteResult = await BasisPersistientKVDatabase.DeleteKeyAsync(userId, userId, key);
        _output.WriteLine($"DeleteAsync => {deleteResult.ErrorCode}, {deleteResult.Message}");
        Assert.Equal(KvError.Success, deleteResult.ErrorCode);

        // Exists => false
        var existsAfterDelete = await BasisPersistientKVDatabase.KeyExistsAsync(userId, userId, key);
        _output.WriteLine($"ExistsAsync after delete => {existsAfterDelete.ErrorCode}, {existsAfterDelete.Message}, value={existsAfterDelete.Value}");
        Assert.Equal(KvError.Success, existsAfterDelete.ErrorCode);
        Assert.False(existsAfterDelete.Value);

        // Get => KeyNotFound
        var getAfterDelete = await BasisPersistientKVDatabase.GetKeyAsync(userId, userId, key);
        _output.WriteLine($"GetAsync after delete => {getAfterDelete.ErrorCode}, {getAfterDelete.Message}");
        Assert.Equal(KvError.KeyNotFound, getAfterDelete.ErrorCode);

        // Quota should have decremented key count
        var quotaFinal = await BasisPersistientKVDatabase.GetQuotaAsync(userId, userId);
        _output.WriteLine($"Quota after delete => Keys={quotaFinal.Value!.CurrentKeys}, Bytes={quotaFinal.Value!.CurrentBytes}");
        Assert.Equal(KvError.Success, quotaFinal.ErrorCode);
        Assert.Equal(0, quotaFinal.Value!.CurrentKeys);
        Assert.True(quotaFinal.Value!.CurrentBytes >= 0);
    }

    [Fact]
    public async Task Set_SameKeyTwice_DoesNotIncreaseKeyCount()
    {
        var userId = await CreateUserAsync();
        var key = "dup-key";
        var value1 = Encoding.UTF8.GetBytes("v1");
        var value2 = Encoding.UTF8.GetBytes("v2-longer-value");

        var quotaBefore = await BasisPersistientKVDatabase.GetQuotaAsync(userId, userId);
        Assert.Equal(KvError.Success, quotaBefore.ErrorCode);
        Assert.Equal(0, quotaBefore.Value!.CurrentKeys);

        var set1 = await BasisPersistientKVDatabase.SetKeyAsync(userId, userId, key, value1);
        _output.WriteLine($"Set1 => {set1.ErrorCode}, {set1.Message}");
        Assert.Equal(KvError.Success, set1.ErrorCode);

        var quotaAfter1 = await BasisPersistientKVDatabase.GetQuotaAsync(userId, userId);
        _output.WriteLine($"Quota after first set => Keys={quotaAfter1.Value!.CurrentKeys}, Bytes={quotaAfter1.Value!.CurrentBytes}");
        Assert.Equal(1, quotaAfter1.Value!.CurrentKeys);

        var set2 = await BasisPersistientKVDatabase.SetKeyAsync(userId, userId, key, value2);
        _output.WriteLine($"Set2 => {set2.ErrorCode}, {set2.Message}");
        Assert.Equal(KvError.Success, set2.ErrorCode);

        var quotaAfter2 = await BasisPersistientKVDatabase.GetQuotaAsync(userId, userId);
        _output.WriteLine($"Quota after second set => Keys={quotaAfter2.Value!.CurrentKeys}, Bytes={quotaAfter2.Value!.CurrentBytes}");
        Assert.Equal(1, quotaAfter2.Value!.CurrentKeys); // invariant: key count unchanged
        Assert.True(quotaAfter2.Value!.CurrentBytes > quotaAfter1.Value!.CurrentBytes); // bytes should reflect bigger value

        var get = await BasisPersistientKVDatabase.GetKeyAsync(userId, userId, key);
        Assert.Equal(KvError.Success, get.ErrorCode);
        Assert.Equal(value2, get.Value);
    }

    [Fact]
    public async Task ListKeys_PagingAndPrefix_Works()
    {
        var userId = await CreateUserAsync();

        var keys = new[]
        {
            "alpha",
            "beta",
            "beta-1",
            "gamma",
            "beta-2"
        };

        foreach (var k in keys)
        {
            var res = await BasisPersistientKVDatabase.SetKeyAsync(userId, userId, k, Encoding.UTF8.GetBytes(k));
            _output.WriteLine($"SetAsync({k}) => {res.ErrorCode}, {res.Message}");
            Assert.Equal(KvError.Success, res.ErrorCode);
        }

        // All keys ordered by key
        var allKeysResult = await BasisPersistientKVDatabase.ListKeysAsync(userId, userId, 0, 100);
        Assert.Equal(KvError.Success, allKeysResult.ErrorCode);

        var sortedKeys = allKeysResult.Value!;
        _output.WriteLine("All keys: " + string.Join(", ", sortedKeys));
        Assert.Equal(keys.Length, sortedKeys.keys.Length);
        Assert.True(sortedKeys.keys.SequenceEqual(sortedKeys.keys.OrderBy(k => k)));

        // Prefix: "beta"
        var betaKeysResult = await BasisPersistientKVDatabase.ListKeysAsync(userId, userId, 0, 100, prefix: "beta");
        Assert.Equal(KvError.Success, betaKeysResult.ErrorCode);

        var betaKeys = betaKeysResult.Value!;
        _output.WriteLine("Beta keys: " + string.Join(", ", betaKeys));
        Assert.True(betaKeys.keys.All(k => k.StartsWith("beta", StringComparison.Ordinal)));
        Assert.Equal(3, betaKeys.keys.Length);

        // Paging (limit 2)
        var page1 = await BasisPersistientKVDatabase.ListKeysAsync(userId, userId, offset: 0, limit: 2);
        var page2 = await BasisPersistientKVDatabase.ListKeysAsync(userId, userId, offset: 2, limit: 2);

        Assert.Equal(KvError.Success, page1.ErrorCode);
        Assert.Equal(KvError.Success, page2.ErrorCode);

        _output.WriteLine("Page1: " + string.Join(", ", page1.Value!));
        _output.WriteLine("Page2: " + string.Join(", ", page2.Value!));

        Assert.Equal(2, page1.Value!.keys.Length);
        Assert.Equal(2, page2.Value!.keys.Length);
        Assert.True(page1.Value!.keys.Concat(page2.Value!.keys).SequenceEqual(sortedKeys.keys.Take(4)));
    }

    [Fact]
    public async Task Get_NonExistingKey_ReturnsKeyNotFound()
    {
        var userId = await CreateUserAsync();
        var getResult = await BasisPersistientKVDatabase.GetKeyAsync(userId, userId, "does-not-exist");

        _output.WriteLine($"Get(non-existing) => {getResult.ErrorCode}, {getResult.Message}");
        Assert.Equal(KvError.KeyNotFound, getResult.ErrorCode);
        Assert.Empty(getResult.Value.ToArray());
        Assert.Contains("Key not found", getResult.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exists_NonExistingKey_ReturnsFalse()
    {
        var userId = await CreateUserAsync();
        var existsResult = await BasisPersistientKVDatabase.KeyExistsAsync(userId, userId, "does-not-exist");

        _output.WriteLine($"Exists(non-existing) => {existsResult.ErrorCode}, {existsResult.Message}, value={existsResult.Value}");
        Assert.Equal(KvError.Success, existsResult.ErrorCode);
        Assert.False(existsResult.Value);
    }


    [Fact]
    public async Task KeysAreIsolatedPerUser()
    {
        var user1 = await CreateUserAsync();
        var user2 = await CreateUserAsync();

        var key = "shared-key";

        var v1 = Encoding.UTF8.GetBytes("user1");
        var v2 = Encoding.UTF8.GetBytes("user2");

        var set1 = await BasisPersistientKVDatabase.SetKeyAsync(user1, user1, key, v1);
        var set2 = await BasisPersistientKVDatabase.SetKeyAsync(user2, user2, key, v2);

        _output.WriteLine($"set1 errCode {set1.ErrorCode}");

        Assert.Equal(KvError.Success, set1.ErrorCode);
        Assert.Equal(KvError.Success, set2.ErrorCode);

        var get1 = await BasisPersistientKVDatabase.GetKeyAsync(user1, user1, key);
        var get2 = await BasisPersistientKVDatabase.GetKeyAsync(user2, user2, key);

        _output.WriteLine($"User1 get => {Encoding.UTF8.GetString(get1.Value.ToArray())}");
        _output.WriteLine($"User2 get => {Encoding.UTF8.GetString(get2.Value.ToArray())}");

        Assert.Equal(v1, get1.Value);
        Assert.Equal(v2, get2.Value);
    }

    [Fact]
    public async Task TooLongKey_TriggersValidationKeyTooLong()
    {
        var userId = await CreateUserAsync();
        // 257 ASCII chars (limit is 256 bytes)
        var longKey = new string('k', 257);
        var value = Encoding.UTF8.GetBytes("value");

        var setResult = await BasisPersistientKVDatabase.SetKeyAsync(userId, userId, longKey, value);
        _output.WriteLine($"SetAsync with long key => err: {setResult.ErrorCode}, {setResult.Message}");

        Assert.Equal(KvError.ValidationKeySize, setResult.ErrorCode);
    }


    [Fact]
    public async Task TooLargeValue_TriggersValidationValueTooBig()
    {
        var userId = await CreateUserAsync();
        var key = "big-value";

        var largeValue = new byte[8001];
        new Random().NextBytes(largeValue);

        var setResult = await BasisPersistientKVDatabase.SetKeyAsync(userId, userId, key, largeValue);
        _output.WriteLine($"SetAsync with large value => err: {setResult.ErrorCode}, {setResult.Message}");

        Assert.Equal(KvError.ValidationValueSize, setResult.ErrorCode);
    }


    [Fact]
    public async Task UserKVStore_WrapsDatabaseCorrectly()
    {
        var userId = await CreateUserAsync();
        var store = new UserKVStore(userId);

        var key = "store-key";
        var value = Encoding.UTF8.GetBytes("store-value");

        // Set
        var setResult = await store.Set(key, value);
        _output.WriteLine($"UserKVStore.Set => {setResult.ErrorCode}, {setResult.Message}");
        Assert.Equal(KvError.Success, setResult.ErrorCode);

        // Exists
        var existsResult = await store.Exists(key);
        _output.WriteLine($"UserKVStore.Exists => {existsResult.ErrorCode}, {existsResult.Message}, value={existsResult.Value}");
        Assert.Equal(KvError.Success, existsResult.ErrorCode);
        Assert.True(existsResult.Value);

        // Get
        var getResult = await store.Get(key);
        _output.WriteLine($"UserKVStore.GetAsync => {getResult.ErrorCode}, {getResult.Message}");
        Assert.Equal(KvError.Success, getResult.ErrorCode);
        Assert.Equal(value, getResult.Value);

        // List
        var listResult = await store.ListKeys();
        _output.WriteLine($"UserKVStore.ListKeysAsync => {listResult.ErrorCode}, keys={string.Join(",", listResult.Value.keys)}");
        Assert.Equal(KvError.Success, listResult.ErrorCode);
        Assert.Contains(key, listResult.Value.keys);

        // Quota
        var quotaResult = await store.GetQuota();
        _output.WriteLine($"UserKVStore.GetQuotaAsync => Keys={quotaResult.Value!.CurrentKeys}, Bytes={quotaResult.Value!.CurrentBytes}");
        Assert.Equal(KvError.Success, quotaResult.ErrorCode);
        Assert.True(quotaResult.Value!.CurrentKeys >= 1);

        // Delete
        var deleteResult = await store.Delete(key);
        _output.WriteLine($"UserKVStore.DeleteAsync => {deleteResult.ErrorCode}, {deleteResult.Message}");
        Assert.Equal(KvError.Success, deleteResult.ErrorCode);

        var existsAfterDelete = await store.Exists(key);
        Assert.False(existsAfterDelete.Value);
    }

    [Fact]
    public async Task GetQuota_NonExistingUser_ReturnsUserNotFound()
    {
        var nonExistentUser = Guid.NewGuid().ToString("N");
        var quotaResult = await BasisPersistientKVDatabase.GetQuotaAsync(nonExistentUser, nonExistentUser);

        _output.WriteLine($"GetQuota(non-existing user) => {quotaResult.ErrorCode}, {quotaResult.Message}");
        Assert.Equal(KvError.BucketNotFound, quotaResult.ErrorCode);
        Assert.Contains("does not exist", quotaResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("U_NOT_FOUND", quotaResult.Message); // no internal trigger code
    }

    [Fact]
    public async Task ManyUsers_MixedOperations_QuotasAndIsolationHold()
    {
        const int userCount = 5;
        const int keysPerUser = 10;

        var users = new string[userCount];
        for (int i = 0; i < userCount; i++)
        {
            users[i] = await CreateUserAsync();
        }

        // Track expected state for each user
        var rng = new Random(1234);
        var expectedPerUser = new Dictionary<string, Dictionary<string, byte[]>>();

        foreach (var user in users)
        {
            expectedPerUser[user] = new Dictionary<string, byte[]>();

            // Insert keys
            for (int i = 0; i < keysPerUser; i++)
            {
                var key = $"user-{user.Substring(0, 8)}-k{i}";
                var valueString = $"val-{i}-{new string('x', i)}";
                var value = Encoding.UTF8.GetBytes(valueString);

                var set = await BasisPersistientKVDatabase.SetKeyAsync(user, user, key, value);
                _output.WriteLine($"SetAsync({user}, {key}) => {set.ErrorCode}, {set.Message}");
                Assert.Equal(KvError.Success, set.ErrorCode);

                expectedPerUser[user][key] = value;
            }

            // Delete some keys (even indices)
            foreach (var key in expectedPerUser[user].Keys.Where((k, idx) => idx % 2 == 0).ToList())
            {
                var del = await BasisPersistientKVDatabase.DeleteKeyAsync(user, user, key);
                _output.WriteLine($"DeleteAsync({user}, {key}) => {del.ErrorCode}, {del.Message}");
                Assert.Equal(KvError.Success, del.ErrorCode);

                expectedPerUser[user].Remove(key);
            }
        }

        // Now verify for each user:
        // - Keys list matches
        // - Exists/Get behave as expected
        // - Quota matches our expected counts/bytes
        foreach (var user in users)
        {
            var expected = expectedPerUser[user];

            // List keys
            var list = await BasisPersistientKVDatabase.ListKeysAsync(user, user, 0, 1000);
            Assert.Equal(KvError.Success, list.ErrorCode);
            var listed = list.Value;
            _output.WriteLine($"User {user} -> listed keys: {string.Join(", ", listed.keys)}");

            Assert.Equal(expected.Keys.OrderBy(k => k), listed.keys.OrderBy(k => k));

            // Exists/Get
            foreach (var key in expected.Keys)
            {
                var exists = await BasisPersistientKVDatabase.KeyExistsAsync(user, user, key);
                Assert.Equal(KvError.Success, exists.ErrorCode);
                Assert.True(exists.Value);

                var get = await BasisPersistientKVDatabase.GetKeyAsync(user, user, key);
                Assert.Equal(KvError.Success, get.ErrorCode);
                Assert.Equal(expected[key], get.Value);
            }

            // Check that random non-existing key is absent
            var missingKey = "missing-" + user.Substring(0, 6);
            var existsMissing = await BasisPersistientKVDatabase.KeyExistsAsync(user, user, missingKey);
            Assert.Equal(KvError.Success, existsMissing.ErrorCode);
            Assert.False(existsMissing.Value);

            var getMissing = await BasisPersistientKVDatabase.GetKeyAsync(user, user, missingKey);
            Assert.Equal(KvError.KeyNotFound, getMissing.ErrorCode);

            // Quota check
            var quota = await BasisPersistientKVDatabase.GetQuotaAsync(user, user);
            Assert.Equal(KvError.Success, quota.ErrorCode);

            var q = quota.Value!;
            Assert.Equal(expected.Count, q.CurrentKeys);

            // Compute expected bytes: sum(len(keyBytes) + len(valueBytes))
            long expectedBytes = 0;
            foreach (var kvp in expected)
            {
                var keyBytes = Encoding.UTF8.GetByteCount(kvp.Key);
                var valueBytes = kvp.Value.Length;
                expectedBytes += keyBytes + valueBytes;
            }

            _output.WriteLine($"User {user}: QuotaBytes={q.CurrentBytes}, ExpectedBytes={expectedBytes}");
            Assert.Equal(expectedBytes, q.CurrentBytes);
        }

        // Isolation check: no keys leak between users
        foreach (var user in users)
        {
            var userKeys = expectedPerUser[user].Keys.ToHashSet();
            foreach (var other in users.Where(u => u != user))
            {
                var otherKeys = expectedPerUser[other].Keys;
                Assert.True(userKeys.Intersect(otherKeys).Count() == 0,
                    "Key namespaces should be isolated per user even if key strings collide.");
            }
        }
    }
    [Fact]
    public async Task QuotaTracking_ManyKeys_UpdatesAndDeletes_Accurate()
    {
        var userId = await CreateUserAsync();

        var expected = new Dictionary<string, byte[]>();

        void AddOrUpdateLocal(string key, byte[] value)
            => expected[key] = value;

        void DeleteLocal(string key)
        {
            if (expected.ContainsKey(key))
                expected.Remove(key);
        }

        // Initial insert
        for (int i = 0; i < 20; i++)
        {
            var key = $"k{i:D2}";
            var valStr = new string((char)('a' + (i % 26)), i + 1); // varying length
            var val = Encoding.UTF8.GetBytes(valStr);

            var set = await BasisPersistientKVDatabase.SetKeyAsync(userId, userId, key, val);
            Assert.Equal(KvError.Success, set.ErrorCode);
            AddOrUpdateLocal(key, val);
        }

        // Update some keys with larger values
        for (int i = 0; i < 10; i += 2)
        {
            var key = $"k{i:D2}";
            var valStr = $"bigger-{new string('z', 10 + i)}";
            var val = Encoding.UTF8.GetBytes(valStr);

            var set = await BasisPersistientKVDatabase.SetKeyAsync(userId, userId, key, val);
            Assert.Equal(KvError.Success, set.ErrorCode);
            AddOrUpdateLocal(key, val);
        }

        // Update some keys with smaller values
        for (int i = 1; i < 10; i += 2)
        {
            var key = $"k{i:D2}";
            var valStr = "small";
            var val = Encoding.UTF8.GetBytes(valStr);

            var set = await BasisPersistientKVDatabase.SetKeyAsync(userId, userId, key, val);
            Assert.Equal(KvError.Success, set.ErrorCode);
            AddOrUpdateLocal(key, val);
        }

        // Delete a few keys
        for (int i = 0; i < 20; i += 5)
        {
            var key = $"k{i:D2}";
            var del = await BasisPersistientKVDatabase.DeleteKeyAsync(userId, userId, key);
            Assert.Equal(KvError.Success, del.ErrorCode);
            DeleteLocal(key);
        }

        // Verify keys via List
        var list = await BasisPersistientKVDatabase.ListKeysAsync(userId, userId, 0, 100);
        Assert.Equal(KvError.Success, list.ErrorCode);
        var dbKeys = list.Value.keys.OrderBy(k => k).ToArray();
        var expectedKeys = expected.Keys.OrderBy(k => k).ToArray();
        Assert.Equal(expectedKeys, dbKeys);

        // Verify quota
        var quota = await BasisPersistientKVDatabase.GetQuotaAsync(userId, userId);
        Assert.Equal(KvError.Success, quota.ErrorCode);
        var q = quota.Value!;
        Assert.Equal(expected.Count, q.CurrentKeys);

        long expectedBytes = 0;
        foreach (var kvp in expected)
        {
            var keyBytes = Encoding.UTF8.GetByteCount(kvp.Key);
            var valueBytes = kvp.Value.Length;
            expectedBytes += keyBytes + valueBytes;
        }

        _output.WriteLine($"Quota bytes: DB={q.CurrentBytes}, Expected={expectedBytes}");
        Assert.Equal(expectedBytes, q.CurrentBytes);
    }
    [Fact]
    public async Task BulkAddDelete_ExistsGetListRemainConsistent()
    {
        var userId = await CreateUserAsync();
        const int keyCount = 50;

        var rnd = new Random(42);
        var expected = new Dictionary<string, byte[]>();

        // Insert
        for (int i = 0; i < keyCount; i++)
        {
            var key = $"bulk-{i:D2}";
            var valStr = $"val-{i}-{new string('x', rnd.Next(1, 20))}";
            var val = Encoding.UTF8.GetBytes(valStr);

            var set = await BasisPersistientKVDatabase.SetKeyAsync(userId, userId, key, val);
            Assert.Equal(KvError.Success, set.ErrorCode);
            expected[key] = val;
        }

        // Delete about half of them at random
        foreach (var key in expected.Keys.ToList())
        {
            if (rnd.NextDouble() < 0.5)
            {
                var del = await BasisPersistientKVDatabase.DeleteKeyAsync(userId, userId, key);
                Assert.Equal(KvError.Success, del.ErrorCode);
                expected.Remove(key);
            }
        }

        // List -> should match remaining expected keys
        var list = await BasisPersistientKVDatabase.ListKeysAsync(userId, userId, 0, 1000);
        Assert.Equal(KvError.Success, list.ErrorCode);
        var listed = list.Value.keys.OrderBy(k => k).ToArray();
        var expectedKeys = expected.Keys.OrderBy(k => k).ToArray();
        Assert.Equal(expectedKeys, listed);

        // Exists & Get should line up with expected
        for (int i = 0; i < keyCount; i++)
        {
            var key = $"bulk-{i:D2}";
            var exists = await BasisPersistientKVDatabase.KeyExistsAsync(userId, userId, key);
            Assert.Equal(KvError.Success, exists.ErrorCode);

            if (expected.TryGetValue(key, out var val))
            {
                Assert.True(exists.Value);

                var get = await BasisPersistientKVDatabase.GetKeyAsync(userId, userId, key);
                Assert.Equal(KvError.Success, get.ErrorCode);
                Assert.Equal(val, get.Value);
            }
            else
            {
                Assert.False(exists.Value);

                var get = await BasisPersistientKVDatabase.GetKeyAsync(userId, userId, key);
                Assert.Equal(KvError.KeyNotFound, get.ErrorCode);
            }
        }

        // Quota key count matches remaining keys
        var quota = await BasisPersistientKVDatabase.GetQuotaAsync(userId, userId);
        Assert.Equal(KvError.Success, quota.ErrorCode);
        Assert.Equal(expected.Count, quota.Value!.CurrentKeys);
    }

}
