using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace PersistentKv.Tests
{
    /// <summary>
    /// Tests for basic CRUD (Create, Read, Update, Delete) operations on IKVBucket interface.
    /// Tests the contract that any IKVBucket implementation must satisfy.
    /// </summary>
    public class IKVBucket_CrudTests : BasisKvBucketTestBase
    {
        public IKVBucket_CrudTests(ITestOutputHelper output) : base(output)
        {
        }

        #region Set Operation Tests

        public static IEnumerable<object[]> SetPayloadCases()
        {
            var ones = new byte[100];
            Array.Fill(ones, (byte)0xFF);

            yield return new object[] { "test-key", Bytes("test-value") };
            yield return new object[] { "empty-key", Array.Empty<byte>() };
            yield return new object[] { "single-byte", new byte[] { 0x42 } };
            yield return new object[] { "binary-key", new byte[] { 0x00, 0xFF, 0x01, 0xFE, 0x7F, 0x80 } };
            yield return new object[] { "zeros", new byte[100] };
            yield return new object[] { "ones", ones };
        }

        [Theory]
        [MemberData(nameof(SetPayloadCases))]
        public async Task Set_SupportsVariousPayloads(string key, byte[] payload)
        {
            var bucket = await CreateBucketAsync();

            var result = await bucket.Set(key, payload);

            Assert.Equal(KvError.Success, result.ErrorCode);

            var getResult = await bucket.Get(key);
            Assert.Equal(KvError.Success, getResult.ErrorCode);
            Assert.Equal(payload, getResult.Value.ToArray());
        }

        public static IEnumerable<object[]> OverwriteCases()
        {
            yield return new object[] { Bytes("first"), Bytes("second") };
            yield return new object[] { new byte[1000], new byte[10] };
            yield return new object[] { new byte[10], new byte[1000] };
        }

        [Theory]
        [MemberData(nameof(OverwriteCases))]
        public async Task Set_OverwritesExistingValue(byte[] initial, byte[] updated)
        {
            var bucket = await CreateBucketAsync();
            var key = "overwrite-key";

            Assert.Equal(KvError.Success, (await bucket.Set(key, initial)).ErrorCode);
            Assert.Equal(KvError.Success, (await bucket.Set(key, updated)).ErrorCode);

            var getResult = await bucket.Get(key);
            Assert.Equal(KvError.Success, getResult.ErrorCode);
            Assert.Equal(updated, getResult.Value.ToArray());
        }

        [Fact]
        public async Task Set_CreateOnly_NewKey_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            var key = "new-create-only";
            var value = Bytes("test-value");

            var result = await bucket.Set(key, value, create_only: true);

            Assert.Equal(KvError.Success, result.ErrorCode);

            var getResult = await bucket.Get(key);
            Assert.Equal(KvError.Success, getResult.ErrorCode);
            Assert.Equal(value, getResult.Value.ToArray());
        }

        [Fact]
        public async Task Set_CreateOnly_ExistingKey_Fails()
        {
            var bucket = await CreateBucketAsync();
            var key = "existing-create-only";
            var originalValue = Bytes("original");
            var newValue = Bytes("new-value");

            // First set succeeds
            await bucket.Set(key, originalValue);

            // Second set with create_only should fail
            var result = await bucket.Set(key, newValue, create_only: true);

            Assert.NotEqual(KvError.Success, result.ErrorCode);

            // Original value should remain unchanged
            var getResult = await bucket.Get(key);
            Assert.Equal(KvError.Success, getResult.ErrorCode);
            Assert.Equal(originalValue, getResult.Value.ToArray());
        }

        [Fact]
        public async Task Set_CreateOnly_AfterDelete_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            var key = "delete-then-create";
            var value1 = Bytes("first");
            var value2 = Bytes("second");

            await bucket.Set(key, value1);
            await bucket.Delete(key);

            var result = await bucket.Set(key, value2, create_only: true);

            Assert.Equal(KvError.Success, result.ErrorCode);

            var getResult = await bucket.Get(key);
            Assert.Equal(KvError.Success, getResult.ErrorCode);
            Assert.Equal(value2, getResult.Value.ToArray());
        }

        [Fact]
        public async Task Set_CreateOnlyFalse_AllowsOverwrite()
        {
            var bucket = await CreateBucketAsync();
            var key = "overwrite-allowed";
            var value1 = Bytes("first");
            var value2 = Bytes("second");

            await bucket.Set(key, value1, create_only: false);
            var result = await bucket.Set(key, value2, create_only: false);

            Assert.Equal(KvError.Success, result.ErrorCode);

            var getResult = await bucket.Get(key);
            Assert.Equal(KvError.Success, getResult.ErrorCode);
            Assert.Equal(value2, getResult.Value.ToArray());
        }

        #endregion

        #region Get Operation Tests

        public static IEnumerable<object[]> GetValueCases()
        {
            var fullRange = new byte[256];
            for (var i = 0; i < 256; i++)
            {
                fullRange[i] = (byte)i;
            }

            yield return new object[] { "get-test", Bytes("hello world") };
            yield return new object[] { "empty", Array.Empty<byte>() };
            yield return new object[] { "binary", fullRange };
        }

        [Theory]
        [MemberData(nameof(GetValueCases))]
        public async Task Get_ReturnsStoredBytes(string key, byte[] payload)
        {
            var bucket = await CreateBucketAsync();

            Assert.Equal(KvError.Success, (await bucket.Set(key, payload)).ErrorCode);

            var result = await bucket.Get(key);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Equal(payload, result.Value.ToArray());
        }

        [Fact]
        public async Task Get_NonExistentKey_ReturnsKeyNotFound()
        {
            var bucket = await CreateBucketAsync();
            var result = await bucket.Get("does-not-exist");

            Assert.Equal(KvError.KeyNotFound, result.ErrorCode);
        }

        [Fact]
        public async Task Get_AfterMultipleOverwrites_ReturnsLatestValue()
        {
            var bucket = await CreateBucketAsync();
            var key = "multi-write";

            for (var i = 0; i < 10; i++)
            {
                Assert.Equal(KvError.Success, (await bucket.Set(key, Bytes($"value-{i}"))).ErrorCode);
            }

            var result = await bucket.Get(key);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Equal(Bytes("value-9"), result.Value.ToArray());
        }

        #endregion

        #region Exists Operation Tests

        [Fact]
        public async Task Exists_TracksLifecycleTransitions()
        {
            var bucket = await CreateBucketAsync();
            var key = "exists-test";

            var initial = await bucket.Exists(key);
            Assert.Equal(KvError.Success, initial.ErrorCode);
            Assert.False(initial.Value);

            await bucket.Set(key, Bytes("data"));
            var afterSet = await bucket.Exists(key);
            Assert.Equal(KvError.Success, afterSet.ErrorCode);
            Assert.True(afterSet.Value);

            await bucket.Delete(key);
            var afterDelete = await bucket.Exists(key);
            Assert.Equal(KvError.Success, afterDelete.ErrorCode);
            Assert.False(afterDelete.Value);
        }

        [Theory]
        [InlineData("empty-exists", "")]
        [InlineData("exists-nonempty", "value")]
        public async Task Exists_ReturnsTrueForStoredValues(string key, string text)
        {
            var bucket = await CreateBucketAsync();
            await bucket.Set(key, Bytes(text));

            var result = await bucket.Exists(key);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.True(result.Value);
        }

        #endregion

        #region KeyInfo Operation Tests

        [Fact]
        public async Task KeyInfo_ExistingKey_ReturnsMetadata()
        {
            var bucket = await CreateBucketAsync();
            var key = "info-test";
            var value = Bytes("test-data");

            await bucket.Set(key, value);
            var result = await bucket.KeyInfo(key);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.True(result.Value.creation > 0, "Creation timestamp should be set");
            Assert.True(result.Value.lastUpdate > 0, "LastUpdate timestamp should be set");
            Assert.Equal(1UL, result.Value.version);
            Assert.Equal((ulong)value.Length, result.Value.valueSize);
        }

        [Fact]
        public async Task KeyInfo_NonExistentKey_ReturnsKeyNotFound()
        {
            var bucket = await CreateBucketAsync();
            var result = await bucket.KeyInfo("non-existent-key");

            Assert.Equal(KvError.KeyNotFound, result.ErrorCode);
        }

        [Fact]
        public async Task KeyInfo_AfterOverwrite_UpdatesVersionAndTimestamp()
        {
            var bucket = await CreateBucketAsync();
            var key = "version-test";
            var value1 = Bytes("first-value");
            var value2 = Bytes("second-value-longer");

            await bucket.Set(key, value1);
            var info1 = await bucket.KeyInfo(key);

            // Wait a small amount to ensure timestamps differ
            await Task.Delay(10);

            await bucket.Set(key, value2);
            var info2 = await bucket.KeyInfo(key);

            Assert.Equal(KvError.Success, info1.ErrorCode);
            Assert.Equal(KvError.Success, info2.ErrorCode);

            // Creation timestamp should remain the same
            Assert.Equal(info1.Value.creation, info2.Value.creation);

            // Last update should be newer
            Assert.True(info2.Value.lastUpdate >= info1.Value.lastUpdate, "LastUpdate should be greater or equal after update");

            // Version should increment
            Assert.True(info2.Value.version > info1.Value.version, "Version should increment after update");

            // Value size should reflect new value
            Assert.Equal((ulong)value2.Length, info2.Value.valueSize);
        }

        [Fact]
        public async Task KeyInfo_EmptyValue_ReturnsZeroSize()
        {
            var bucket = await CreateBucketAsync();
            var key = "empty-value-key";
            var emptyValue = Array.Empty<byte>();

            await bucket.Set(key, emptyValue);
            var result = await bucket.KeyInfo(key);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Equal(0UL, result.Value.valueSize);
        }

        [Fact]
        public async Task KeyInfo_LargeValue_ReturnsCorrectSize()
        {
            var bucket = await CreateBucketAsync();
            var key = "large-value-key";
            var largeValue = new byte[8000]; // Max size

            await bucket.Set(key, largeValue);
            var result = await bucket.KeyInfo(key);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Equal(8000UL, result.Value.valueSize);
        }

        [Fact]
        public async Task KeyInfo_AfterDelete_ReturnsKeyNotFound()
        {
            var bucket = await CreateBucketAsync();
            var key = "delete-info-test";

            await bucket.Set(key, Bytes("data"));
            await bucket.Delete(key);

            var result = await bucket.KeyInfo(key);

            Assert.Equal(KvError.KeyNotFound, result.ErrorCode);
        }

        [Fact]
        public async Task KeyInfo_AfterDeleteAndRecreate_ResetsMetadata()
        {
            var bucket = await CreateBucketAsync();
            var key = "recreate-info-test";
            var value1 = Bytes("first");
            var value2 = Bytes("second");

            await bucket.Set(key, value1);
            var info1 = await bucket.KeyInfo(key);

            await Task.Delay(10);

            await bucket.Delete(key);
            await bucket.Set(key, value2);
            var info2 = await bucket.KeyInfo(key);

            Assert.Equal(KvError.Success, info1.ErrorCode);
            Assert.Equal(KvError.Success, info2.ErrorCode);

            // Creation timestamp should be different (new key)
            Assert.True(info2.Value.creation >= info1.Value.creation, "New creation should be after or equal to old");

            // Version should reset to 1
            Assert.Equal(1UL, info2.Value.version);
        }

        [Fact]
        public async Task KeyInfo_MultipleUpdates_VersionIncrementsCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "multi-update";

            for (int i = 0; i < 5; i++)
            {
                await bucket.Set(key, Bytes($"value-{i}"));
            }

            var result = await bucket.KeyInfo(key);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.True(result.Value.version >= 1, "Version should be at least 1");
        }

        [Fact]
        public async Task KeyInfo_TimestampsAreMonotonic()
        {
            var bucket = await CreateBucketAsync();
            var key = "timestamp-test";

            await bucket.Set(key, Bytes("v1"));
            var info1 = await bucket.KeyInfo(key);

            await Task.Delay(10);

            await bucket.Set(key, Bytes("v2"));
            var info2 = await bucket.KeyInfo(key);

            await Task.Delay(10);

            await bucket.Set(key, Bytes("v3"));
            var info3 = await bucket.KeyInfo(key);

            Assert.True(info1.Value.creation <= info1.Value.lastUpdate);
            Assert.True(info2.Value.creation <= info2.Value.lastUpdate);
            Assert.True(info3.Value.creation <= info3.Value.lastUpdate);
            Assert.True(info1.Value.lastUpdate <= info2.Value.lastUpdate);
            Assert.True(info2.Value.lastUpdate <= info3.Value.lastUpdate);
        }

        #endregion

        #region Delete Operation Tests

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Delete_ReturnsExpectedResult(bool insertFirst)
        {
            var bucket = await CreateBucketAsync();
            var key = insertFirst ? "delete-existing" : "delete-missing";

            if (insertFirst)
            {
                await bucket.Set(key, Bytes("value"));
            }

            var result = await bucket.Delete(key);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Equal(insertFirst, result.Value);

            var exists = await bucket.Exists(key);
            Assert.Equal(KvError.Success, exists.ErrorCode);
            Assert.False(exists.Value);

            var get = await bucket.Get(key);
            Assert.Equal(KvError.KeyNotFound, get.ErrorCode);
        }

        [Fact]
        public async Task Delete_AlreadyDeleted_ReturnsFalse()
        {
            var bucket = await CreateBucketAsync();
            var key = "double-delete";

            await bucket.Set(key, Bytes("data"));
            Assert.True((await bucket.Delete(key)).Value);

            var second = await bucket.Delete(key);

            Assert.Equal(KvError.Success, second.ErrorCode);
            Assert.False(second.Value);
        }

        [Fact]
        public async Task Delete_ThenRecreate_WorksCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "recreate";

            await bucket.Set(key, Bytes("first"));
            await bucket.Delete(key);
            await bucket.Set(key, Bytes("second"));
            var getResult = await bucket.Get(key);

            Assert.Equal(KvError.Success, getResult.ErrorCode);
            Assert.Equal(Bytes("second"), getResult.Value.ToArray());
        }

        #endregion

        #region Round-trip Integrity Tests

        [Fact]
        public async Task RoundTrip_SetGetDelete_MaintainsDataIntegrity()
        {
            var bucket = await CreateBucketAsync();
            var key = "round-trip";
            var value = Bytes("integrity test");

            // Set
            var setResult = await bucket.Set(key, value);
            Assert.Equal(KvError.Success, setResult.ErrorCode);

            // Get
            var getResult = await bucket.Get(key);
            Assert.Equal(KvError.Success, getResult.ErrorCode);
            Assert.Equal(value, getResult.Value.ToArray());

            // Exists
            var existsResult = await bucket.Exists(key);
            Assert.True(existsResult.Value);

            // Delete
            var deleteResult = await bucket.Delete(key);
            Assert.True(deleteResult.Value);

            // Verify deleted
            var finalExists = await bucket.Exists(key);
            Assert.False(finalExists.Value);
        }

        [Fact]
        public async Task RoundTrip_MultipleKeys_DontInterfere()
        {
            var bucket = await CreateBucketAsync();
            var keys = new[] { "key1", "key2", "key3" };
            var values = new[]
            {
                Bytes("value1"),
                Bytes("value2"),
                Bytes("value3")
            };

            // Set all
            for (int i = 0; i < keys.Length; i++)
            {
                await bucket.Set(keys[i], values[i]);
            }

            // Verify all
            for (int i = 0; i < keys.Length; i++)
            {
                var result = await bucket.Get(keys[i]);
                Assert.Equal(KvError.Success, result.ErrorCode);
                Assert.Equal(values[i], result.Value.ToArray());
            }

            // Delete one
            await bucket.Delete(keys[1]);

            // Verify others still exist
            var get0 = await bucket.Get(keys[0]);
            var get2 = await bucket.Get(keys[2]);
            Assert.Equal(KvError.Success, get0.ErrorCode);
            Assert.Equal(KvError.Success, get2.ErrorCode);

            // Verify deleted one is gone
            var get1 = await bucket.Get(keys[1]);
            Assert.Equal(KvError.KeyNotFound, get1.ErrorCode);
        }

        #endregion

        #region Invariant Tests

        [Fact]
        public async Task Operations_SuccessfulResults_HaveCorrectErrorCode()
        {
            var bucket = await CreateBucketAsync();
            var key = "invariant-test";
            var value = Bytes("test-data");

            // All successful operations should return Success
            var setResult = await bucket.Set(key, value);
            Assert.Equal(KvError.Success, setResult.ErrorCode);

            var getResult = await bucket.Get(key);
            Assert.Equal(KvError.Success, getResult.ErrorCode);

            var existsResult = await bucket.Exists(key);
            Assert.Equal(KvError.Success, existsResult.ErrorCode);

            var keyInfoResult = await bucket.KeyInfo(key);
            Assert.Equal(KvError.Success, keyInfoResult.ErrorCode);

            var listResult = await bucket.ListKeys();
            Assert.Equal(KvError.Success, listResult.ErrorCode);

            var quotaResult = await bucket.GetQuota();
            Assert.Equal(KvError.Success, quotaResult.ErrorCode);

            var deleteResult = await bucket.Delete(key);
            Assert.Equal(KvError.Success, deleteResult.ErrorCode);
        }

        [Fact]
        public async Task Operations_NonExistentKeys_ReturnCorrectErrorCode()
        {
            var bucket = await CreateBucketAsync();
            var nonExistentKey = "does-not-exist";

            // Get should return KeyNotFound
            var getResult = await bucket.Get(nonExistentKey);
            Assert.Equal(KvError.KeyNotFound, getResult.ErrorCode);

            // KeyInfo should return KeyNotFound
            var infoResult = await bucket.KeyInfo(nonExistentKey);
            Assert.Equal(KvError.KeyNotFound, infoResult.ErrorCode);

            // Exists should return Success with false value
            var existsResult = await bucket.Exists(nonExistentKey);
            Assert.Equal(KvError.Success, existsResult.ErrorCode);
            Assert.False(existsResult.Value);

            // Delete should return Success with false value
            var deleteResult = await bucket.Delete(nonExistentKey);
            Assert.Equal(KvError.Success, deleteResult.ErrorCode);
            Assert.False(deleteResult.Value);
        }

        #endregion

        #region Key Name Edge Cases

        [Theory]
        [InlineData("key-with-special!@#$%^&*()")]
        [InlineData("key with spaces")]
        [InlineData("key-🔑-emoji")]
        [InlineData("config.setting.value")]
        [InlineData("path/to/key")]
        [InlineData("x")]
        public async Task Set_SupportsKeyEdgeCases(string key)
        {
            var bucket = await CreateBucketAsync();

            var result = await bucket.Set(key, Bytes("value"));

            Assert.Equal(KvError.Success, result.ErrorCode);

            var listResult = await bucket.ListKeys(limit: 10);
            Assert.Equal(KvError.Success, listResult.ErrorCode);
            Assert.Contains(key, listResult.Value.keys);
        }

        #endregion
    }
}
