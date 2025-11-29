using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace PersistentKv.Tests
{
    /// <summary>
    /// Tests for the static BasisPersistientKVDatabase API.
    /// Focuses on quota guard enforcement, invariants, and error conditions.
    /// Generic CRUD/validation tests are covered in IKVBucket_* test files.
    /// </summary>
    public class BasisPersistentKvTests
    {
        private readonly ITestOutputHelper _output;

        public BasisPersistentKvTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private async Task<string> CreateBucketAsync()
        {
            var bucketId = Guid.NewGuid().ToString("N");
            var addResult = await BasisPersistentKv.AddBucketAsync(bucketId);

            _output.WriteLine($"Created bucket: {bucketId}");
            Assert.Equal(KvError.Success, addResult.ErrorCode);

            return bucketId;
        }

        #region Bucket Lifecycle Tests

        [Fact]
        public async Task AddUserAsync_NewBucket_Succeeds()
        {
            var bucketId = Guid.NewGuid().ToString("N");
            var result = await BasisPersistentKv.AddBucketAsync(bucketId);

            _output.WriteLine($"AddBucketAsync({bucketId}) => {result.ErrorCode}");
            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        [Fact]
        public async Task AddUserAsync_DuplicateBucket_Succeeds()
        {
            var bucketId = Guid.NewGuid().ToString("N");
            var result1 = await BasisPersistentKv.AddBucketAsync(bucketId);
            var result2 = await BasisPersistentKv.AddBucketAsync(bucketId);

            _output.WriteLine($"First: {result1.ErrorCode}, Second: {result2.ErrorCode}");
            Assert.Equal(KvError.Success, result1.ErrorCode);
            Assert.Equal(KvError.Success, result2.ErrorCode);
        }

        [Fact]
        public async Task DeleteBucketAsync_ExistingBucket_RemovesKeys()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetKeyAsync(bucketId, "key1", Encoding.UTF8.GetBytes("value"));

            var deleteResult = await BasisPersistentKv.DeleteBucketAsync(bucketId, removeKeys: true);

            _output.WriteLine($"DeleteBucket => {deleteResult.ErrorCode}");
            Assert.Equal(KvError.Success, deleteResult.ErrorCode);
            Assert.True(deleteResult.Value);
        }

        [Fact]
        public async Task DeleteBucketAsync_WithoutRemovingKeys_Fails()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetKeyAsync(bucketId, "key1", Encoding.UTF8.GetBytes("value"));

            var deleteResult = await BasisPersistentKv.DeleteBucketAsync(bucketId, removeKeys: false);

            _output.WriteLine($"DeleteBucket => {deleteResult.ErrorCode}");
            Assert.Equal(KvError.ServerInvalidParameter, deleteResult.ErrorCode);
        }

        [Fact]
        public async Task DeleteBucketAsync_EmptyBucket_SucceedsWithoutRemoveKeys()
        {
            var bucketId = await CreateBucketAsync();

            var deleteResult = await BasisPersistentKv.DeleteBucketAsync(bucketId, removeKeys: false);

            _output.WriteLine($"DeleteBucket => {deleteResult.ErrorCode}");
            Assert.Equal(KvError.Success, deleteResult.ErrorCode);
        }

        [Fact]
        public async Task DeleteBucketAsync_NonExistentBucket_Fails()
        {
            var nonExistentBucket = Guid.NewGuid().ToString("N");
            var deleteResult = await BasisPersistentKv.DeleteBucketAsync(nonExistentBucket, removeKeys: true);

            _output.WriteLine($"DeleteBucket(non-existent) => {deleteResult.ErrorCode}");
            Assert.Equal(KvError.BucketNotFound, deleteResult.ErrorCode);
        }

        #endregion

        #region Initial Quota State Tests

        [Fact]
        public async Task GetQuotaAsync_NewBucket_ReturnsZeroUsage()
        {
            var bucketId = await CreateBucketAsync();

            var quotaResult = await BasisPersistentKv.GetQuotaAsync(bucketId);

            _output.WriteLine($"Quota: Keys={quotaResult.Value.CurrentKeys}/{quotaResult.Value.MaxKeys}, Bytes={quotaResult.Value.CurrentBytes}/{quotaResult.Value.MaxBytes}");
            Assert.Equal(KvError.Success, quotaResult.ErrorCode);
            Assert.Equal(0, quotaResult.Value.CurrentKeys);
            Assert.Equal(0, quotaResult.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuotaAsync_NewBucket_HasPositiveLimits()
        {
            var bucketId = await CreateBucketAsync();

            var quotaResult = await BasisPersistentKv.GetQuotaAsync(bucketId);

            Assert.Equal(KvError.Success, quotaResult.ErrorCode);
            Assert.True(quotaResult.Value.MaxKeys > 0);
            Assert.True(quotaResult.Value.MaxBytes > 0);
        }

        [Fact]
        public async Task GetQuotaAsync_NonExistentBucket_Fails()
        {
            var nonExistentBucket = Guid.NewGuid().ToString("N");
            var quotaResult = await BasisPersistentKv.GetQuotaAsync(nonExistentBucket);

            _output.WriteLine($"GetQuota(non-existent) => {quotaResult.ErrorCode}");
            Assert.Equal(KvError.BucketNotFound, quotaResult.ErrorCode);
        }

        #endregion

        #region Quota Guard Tests - Key Limit

        [Fact]
        public async Task SetKeyAsync_KeyGuardDisabled_AllowsExceedingMaxKeys()
        {
            var bucketId = await CreateBucketAsync();

            // Guard is disabled by default
            var quota = BasisPersistentKv.GetQuotaSync(bucketId);
            var maxKeys = quota.Value.MaxKeys;
            await BasisPersistentKv.SetKeyCountGuard(bucketId, false);
            // Try to exceed max keys
            for (int i = 0; i < maxKeys + 5; i++)
            {
                var result = await BasisPersistentKv.SetKeyAsync(bucketId, $"key-{i}", Encoding.UTF8.GetBytes("value"));
                Assert.Equal(KvError.Success, result.ErrorCode);
            }

            var finalQuota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            _output.WriteLine($"Final keys: {finalQuota.Value.CurrentKeys}, Max: {maxKeys}");
            Assert.True(finalQuota.Value.CurrentKeys > maxKeys);
        }

        [Fact]
        public async Task SetKeyAsync_KeyGuardEnabled_BlocksWhenAtMax()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetKeyCountGuard(bucketId, true);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxKeys = quota.Value.MaxKeys;

            // Fill to max
            for (int i = 0; i < maxKeys; i++)
            {
                var result = await BasisPersistentKv.SetKeyAsync(bucketId, $"key-{i}", Encoding.UTF8.GetBytes("value"));
                Assert.Equal(KvError.Success, result.ErrorCode);
            }

            // Attempt to exceed
            var blockedResult = await BasisPersistentKv.SetKeyAsync(bucketId, "overflow-key", Encoding.UTF8.GetBytes("value"));
            _output.WriteLine($"Overflow attempt => {blockedResult.ErrorCode}");
            Assert.Equal(KvError.QuotaKeys, blockedResult.ErrorCode);

            var finalQuota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            Assert.Equal(maxKeys, finalQuota.Value.CurrentKeys);
        }

        [Fact]
        public async Task SetKeyAsync_KeyGuardEnabled_AllowsOverwriteAtMax()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetKeyCountGuard(bucketId, true);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxKeys = quota.Value.MaxKeys;

            // Fill to max
            for (int i = 0; i < maxKeys; i++)
            {
                await BasisPersistentKv.SetKeyAsync(bucketId, $"key-{i}", Encoding.UTF8.GetBytes("value1"));
            }

            // Overwrite existing key should succeed
            var overwriteResult = await BasisPersistentKv.SetKeyAsync(bucketId, "key-0", Encoding.UTF8.GetBytes("value2"));
            _output.WriteLine($"Overwrite at max => {overwriteResult.ErrorCode}");
            Assert.Equal(KvError.Success, overwriteResult.ErrorCode);

            var finalQuota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            Assert.Equal(maxKeys, finalQuota.Value.CurrentKeys);
        }

        [Fact]
        public async Task SetKeyAsync_KeyGuardEnabled_AllowsInsertAfterDelete()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetKeyCountGuard(bucketId, true);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxKeys = quota.Value.MaxKeys;

            // Fill to max
            for (int i = 0; i < maxKeys; i++)
            {
                await BasisPersistentKv.SetKeyAsync(bucketId, $"key-{i}", Encoding.UTF8.GetBytes("value"));
            }

            // Delete one key
            await BasisPersistentKv.DeleteKeyAsync(bucketId, "key-0");

            // Now should be able to add a new key
            var insertResult = await BasisPersistentKv.SetKeyAsync(bucketId, "new-key", Encoding.UTF8.GetBytes("value"));
            _output.WriteLine($"Insert after delete => {insertResult.ErrorCode}");
            Assert.Equal(KvError.Success, insertResult.ErrorCode);

            var finalQuota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            Assert.Equal(maxKeys, finalQuota.Value.CurrentKeys);
        }

        [Fact]
        public async Task SetKeyAsync_KeyGuardToggle_CanBeEnabledAndDisabled()
        {
            var bucketId = await CreateBucketAsync();
            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxKeys = quota.Value.MaxKeys;

            // Enable guard
            await BasisPersistentKv.SetKeyCountGuard(bucketId, true);

            // Fill to max
            for (int i = 0; i < maxKeys; i++)
            {
                await BasisPersistentKv.SetKeyAsync(bucketId, $"key-{i}", Encoding.UTF8.GetBytes("value"));
            }

            // Should be blocked
            var blockedResult = await BasisPersistentKv.SetKeyAsync(bucketId, "blocked", Encoding.UTF8.GetBytes("value"));
            Assert.Equal(KvError.QuotaKeys, blockedResult.ErrorCode);

            // Disable guard
            await BasisPersistentKv.SetKeyCountGuard(bucketId, false);

            // Now should succeed
            var allowedResult = await BasisPersistentKv.SetKeyAsync(bucketId, "allowed", Encoding.UTF8.GetBytes("value"));
            _output.WriteLine($"After disabling guard => {allowedResult.ErrorCode}");
            Assert.Equal(KvError.Success, allowedResult.ErrorCode);
        }

        #endregion

        #region Quota Guard Tests - Byte Limit

        [Fact]
        public async Task SetKeyAsync_ByteGuardDisabled_AllowsExceedingMaxBytes()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetByteSizeGaurd(bucketId, false);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxBytes = quota.Value.MaxBytes;

            // Fill with large values to exceed max bytes
            int valueSizePerKey = 8000;
            int keysNeeded = (int)(maxBytes / valueSizePerKey) + 10;

            for (int i = 0; i < keysNeeded; i++)
            {
                var result = await BasisPersistentKv.SetKeyAsync(bucketId, $"k{i}", new byte[valueSizePerKey]);
                Assert.Equal(KvError.Success, result.ErrorCode);
            }

            var finalQuota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            _output.WriteLine($"Final bytes: {finalQuota.Value.CurrentBytes}, Max: {maxBytes}");
            Assert.True(finalQuota.Value.CurrentBytes > maxBytes);
        }

        [Fact]
        public async Task SetKeyAsync_ByteGuardEnabled_BlocksWhenExceedingMaxBytes()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetByteSizeGaurd(bucketId, true);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxBytes = quota.Value.MaxBytes;

            // Fill close to limit (accounting for key size)
            int valueSize = 1000;
            int keysToAdd = (int)(maxBytes / (valueSize + 10)) - 1; // -1 for safety margin

            for (int i = 0; i < keysToAdd; i++)
            {
                await BasisPersistentKv.SetKeyAsync(bucketId, $"k{i}", new byte[valueSize]);
            }

            var beforeOverflow = await BasisPersistentKv.GetQuotaAsync(bucketId);
            _output.WriteLine($"Before overflow: {beforeOverflow.Value.CurrentBytes}/{maxBytes}");

            // Attempt to add value that would exceed limit
            // Use a value size within the 8000 byte limit (to avoid ValidationValueSize error)
            var blockedResult = await BasisPersistentKv.SetKeyAsync(bucketId, "overflow", new byte[7000]);
            _output.WriteLine($"Overflow attempt => {blockedResult.ErrorCode}");
            Assert.Equal(KvError.QuotaBytes, blockedResult.ErrorCode);
        }

        [Fact]
        public async Task SetKeyAsync_ByteGuardEnabled_AllowsOverwriteWithSmallerValue()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetByteSizeGaurd(bucketId, true);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxBytes = quota.Value.MaxBytes;

            // Fill close to limit
            int largeValueSize = (int)(maxBytes * 0.9);
            await BasisPersistentKv.SetKeyAsync(bucketId, "large", new byte[largeValueSize]);

            // Overwrite with smaller value should succeed
            var overwriteResult = await BasisPersistentKv.SetKeyAsync(bucketId, "large", new byte[100]);
            _output.WriteLine($"Overwrite with smaller => {overwriteResult.ErrorCode}");
            Assert.Equal(KvError.Success, overwriteResult.ErrorCode);
        }

        [Fact]
        public async Task SetKeyAsync_ByteGuardEnabled_BlocksOverwriteWithLargerValue()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetByteSizeGaurd(bucketId, true);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxBytes = quota.Value.MaxBytes;

            // Set initial key with a smaller value (e.g., 6000 bytes)
            int initialSize = 6000;
            await BasisPersistentKv.SetKeyAsync(bucketId, "key", new byte[initialSize]);
            // Current usage: 3 (key) + 6000 (value) = 6003 bytes

            // Fill closer to limit with another key
            // We want remaining space to be less than (8000 - 6000) = 2000 bytes
            // So we need to add: maxBytes - 6003 - 1500 = 16000 - 6003 - 1500 = 8497 bytes
            // But max value is 8000, so we add 8000
            int fillSize = 8000;
            await BasisPersistentKv.SetKeyAsync(bucketId, "k1", new byte[fillSize]);
            // Current usage: 6003 + 2 + 8000 = 14005 bytes

            var beforeOverwrite = await BasisPersistentKv.GetQuotaAsync(bucketId);
            _output.WriteLine($"Before overwrite: {beforeOverwrite.Value.CurrentBytes}/{maxBytes}");

            // Try to overwrite "key" with 8000 bytes instead of 6000
            // This would change usage from 14005 to: 14005 - 6003 + 3 + 8000 = 16005 bytes (exceeds 16000)
            var overwriteResult = await BasisPersistentKv.SetKeyAsync(bucketId, "key", new byte[8000]);
            _output.WriteLine($"Overwrite with larger => {overwriteResult.ErrorCode}, Message: {overwriteResult.Message}");

            var finalQuota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            _output.WriteLine($"Final quota: {finalQuota.Value.CurrentBytes}/{finalQuota.Value.MaxBytes}");

            Assert.Equal(KvError.QuotaBytes, overwriteResult.ErrorCode);
        }

        [Fact]
        public async Task SetKeyAsync_ByteGuardEnabled_AccountsForKeySize()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetByteSizeGaurd(bucketId, true);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxBytes = quota.Value.MaxBytes;

            // Use a long key (200 bytes)
            string longKey = new string('k', 200);
            int keyByteCount = Encoding.UTF8.GetByteCount(longKey);
            // Use max allowed value size (8000 bytes) - key size should still fit in quota
            int valueSize = 7700; // 200 + 7700 = 7900, well under 16000

            // Should succeed with value that fits
            var result1 = await BasisPersistentKv.SetKeyAsync(bucketId, longKey, new byte[valueSize]);
            Assert.Equal(KvError.Success, result1.ErrorCode);

            // Add more data to get close to limit
            // Current: 200 + 7700 = 7900 bytes
            // Add another: ~8000 bytes to get to ~15900 total
            var result2 = await BasisPersistentKv.SetKeyAsync(bucketId, "k1", new byte[8000]);
            Assert.Equal(KvError.Success, result2.ErrorCode);

            // Now we're at approximately 7900 + 2 + 8000 = 15902 bytes
            // Should fail with another key+value that would exceed limit
            var result3 = await BasisPersistentKv.SetKeyAsync(bucketId, "another-key", new byte[500]);
            _output.WriteLine($"Third insert => {result3.ErrorCode}");
            Assert.Equal(KvError.QuotaBytes, result3.ErrorCode);
        }

        [Fact]
        public async Task SetKeyAsync_ByteGuardToggle_CanBeEnabledAndDisabled()
        {
            var bucketId = await CreateBucketAsync();
            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxBytes = quota.Value.MaxBytes;

            // Enable guard
            await BasisPersistentKv.SetByteSizeGaurd(bucketId, true);

            // Fill to near limit with two keys (can't exceed 8000 per value)
            await BasisPersistentKv.SetKeyAsync(bucketId, "fill1", new byte[8000]);
            await BasisPersistentKv.SetKeyAsync(bucketId, "fill2", new byte[7400]);
            // Total: ~15410 bytes (close to 16000 limit)

            // Should be blocked
            var blockedResult = await BasisPersistentKv.SetKeyAsync(bucketId, "blocked", new byte[1000]);
            Assert.Equal(KvError.QuotaBytes, blockedResult.ErrorCode);

            // Disable guard
            await BasisPersistentKv.SetByteSizeGaurd(bucketId, false);

            // Now should succeed
            var allowedResult = await BasisPersistentKv.SetKeyAsync(bucketId, "allowed", new byte[1000]);
            _output.WriteLine($"After disabling guard => {allowedResult.ErrorCode}");
            Assert.Equal(KvError.Success, allowedResult.ErrorCode);
        }

        [Fact]
        public async Task SetKeyAsync_ByteGuardEnabled_AllowsInsertAfterDelete()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetByteSizeGaurd(bucketId, true);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxBytes = quota.Value.MaxBytes;

            // Fill to near max with two keys (can't use single value > 8000)
            // First key: 8000 bytes value
            await BasisPersistentKv.SetKeyAsync(bucketId, "large1", new byte[8000]);
            // Second key: 7500 bytes value
            // Total: 6 (keys) + 8000 + 7500 = 15506 bytes (close to 16000 limit)
            await BasisPersistentKv.SetKeyAsync(bucketId, "large2", new byte[7500]);

            // Should be blocked (would add ~1000 + key size, exceeding 16000)
            var blockedResult = await BasisPersistentKv.SetKeyAsync(bucketId, "blocked", new byte[1000]);
            Assert.Equal(KvError.QuotaBytes, blockedResult.ErrorCode);

            // Delete to free space
            await BasisPersistentKv.DeleteKeyAsync(bucketId, "large1");

            // Now should succeed (after deleting 8000+ bytes, we have room)
            var allowedResult = await BasisPersistentKv.SetKeyAsync(bucketId, "allowed", new byte[1000]);
            _output.WriteLine($"After delete => {allowedResult.ErrorCode}");
            Assert.Equal(KvError.Success, allowedResult.ErrorCode);
        }

        #endregion

        #region Quota Guard Tests - Combined Limits

        [Fact]
        public async Task SetKeyAsync_BothGuardsEnabled_EnforcesBothLimits()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetKeyCountGuard(bucketId, true);
            await BasisPersistentKv.SetByteSizeGaurd(bucketId, true);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxKeys = quota.Value.MaxKeys;
            var maxBytes = quota.Value.MaxBytes;

            // Fill with small values to hit key limit first
            int smallValueSize = 10;
            for (int i = 0; i < maxKeys; i++)
            {
                var result = await BasisPersistentKv.SetKeyAsync(bucketId, $"k{i}", new byte[smallValueSize]);
                Assert.Equal(KvError.Success, result.ErrorCode);
            }

            // Should be blocked by key quota
            var keyBlockedResult = await BasisPersistentKv.SetKeyAsync(bucketId, "overflow", new byte[smallValueSize]);
            _output.WriteLine($"Key limit hit => {keyBlockedResult.ErrorCode}");
            Assert.Equal(KvError.QuotaKeys, keyBlockedResult.ErrorCode);

            var finalQuota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            _output.WriteLine($"Final: Keys={finalQuota.Value.CurrentKeys}/{maxKeys}, Bytes={finalQuota.Value.CurrentBytes}/{maxBytes}");
            Assert.True(finalQuota.Value.CurrentBytes < maxBytes); // Byte limit not hit
        }

        [Fact]
        public async Task SetKeyAsync_BothGuardsEnabled_ByteLimitCanBlockFirst()
        {
            var bucketId = await CreateBucketAsync();
            await BasisPersistentKv.SetKeyCountGuard(bucketId, true);
            await BasisPersistentKv.SetByteSizeGaurd(bucketId, true);

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var maxKeys = quota.Value.MaxKeys;
            var maxBytes = quota.Value.MaxBytes;

            // Fill with large values to hit byte limit before key limit
            int largeValueSize = (int)(maxBytes / 10);
            int keysAdded = 0;

            for (int i = 0; i < maxKeys; i++)
            {
                var result = await BasisPersistentKv.SetKeyAsync(bucketId, $"k{i}", new byte[largeValueSize]);
                if (result.ErrorCode == KvError.QuotaBytes)
                {
                    _output.WriteLine($"Byte limit hit at key {i}");
                    break;
                }
                Assert.Equal(KvError.Success, result.ErrorCode);
                keysAdded++;
            }

            var finalQuota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            _output.WriteLine($"Final: Keys={finalQuota.Value.CurrentKeys}/{maxKeys}, Bytes={finalQuota.Value.CurrentBytes}/{maxBytes}");
            Assert.True(keysAdded < maxKeys); // Key limit not hit
            Assert.True(finalQuota.Value.CurrentBytes >= maxBytes * 0.9); // Close to byte limit
        }

        #endregion

        #region Error Condition Tests

        [Fact]
        public async Task SetKeyAsync_NonExistentBucket_Fails()
        {
            var nonExistentBucket = Guid.NewGuid().ToString("N");
            var result = await BasisPersistentKv.SetKeyAsync(nonExistentBucket, "key", Encoding.UTF8.GetBytes("value"));

            _output.WriteLine($"Set on non-existent bucket => {result.ErrorCode}");
            Assert.Equal(KvError.BucketNotFound, result.ErrorCode);
        }

        [Fact]
        public async Task GetKeyAsync_NonExistentBucket_Fails()
        {
            var nonExistentBucket = Guid.NewGuid().ToString("N");
            var result = await BasisPersistentKv.GetKeyAsync(nonExistentBucket, "key");

            _output.WriteLine($"Get on non-existent bucket => {result.ErrorCode}");
            Assert.Equal(KvError.BucketNotFound, result.ErrorCode);
        }

        [Fact]
        public async Task DeleteKeyAsync_NonExistentBucket_Fails()
        {
            var nonExistentBucket = Guid.NewGuid().ToString("N");
            var result = await BasisPersistentKv.DeleteKeyAsync(nonExistentBucket, "key");

            _output.WriteLine($"Delete on non-existent bucket => {result.ErrorCode}");
            Assert.Equal(KvError.BucketNotFound, result.ErrorCode);
        }

        [Fact]
        public async Task KeyExistsAsync_NonExistentBucket_Fails()
        {
            var nonExistentBucket = Guid.NewGuid().ToString("N");
            var result = await BasisPersistentKv.KeyExistsAsync(nonExistentBucket, "key");

            _output.WriteLine($"Exists on non-existent bucket => {result.ErrorCode}");
            Assert.Equal(KvError.BucketNotFound, result.ErrorCode);
        }

        [Fact]
        public async Task ListKeysAsync_NonExistentBucket_Fails()
        {
            var nonExistentBucket = Guid.NewGuid().ToString("N");
            var result = await BasisPersistentKv.ListKeysAsync(nonExistentBucket);

            _output.WriteLine($"List on non-existent bucket => {result.ErrorCode}");
            Assert.Equal(KvError.BucketNotFound, result.ErrorCode);
        }

        [Fact]
        public async Task GetKeyInfoAsync_NonExistentBucket_Fails()
        {
            var nonExistentBucket = Guid.NewGuid().ToString("N");
            var result = await BasisPersistentKv.GetKeyInfoAsync(nonExistentBucket, "key");

            _output.WriteLine($"GetKeyInfo on non-existent bucket => {result.ErrorCode}");
            Assert.Equal(KvError.BucketNotFound, result.ErrorCode);
        }

        [Fact]
        public async Task SetKeyAsync_EmptyKey_Fails()
        {
            var bucketId = await CreateBucketAsync();
            var result = await BasisPersistentKv.SetKeyAsync(bucketId, "", Encoding.UTF8.GetBytes("value"));

            _output.WriteLine($"Set with empty key => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        [Fact]
        public async Task SetKeyAsync_NullKey_Fails()
        {
            var bucketId = await CreateBucketAsync();
            var result = await BasisPersistentKv.SetKeyAsync(bucketId, null!, Encoding.UTF8.GetBytes("value"));

            _output.WriteLine($"Set with null key => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        [Fact]
        public async Task SetKeyAsync_NullValue_Fails()
        {
            var bucketId = await CreateBucketAsync();
            var result = await BasisPersistentKv.SetKeyAsync(bucketId, "key", null!);

            _output.WriteLine($"Set with null value => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationValueNull, result.ErrorCode);
        }

        [Fact]
        public async Task SetKeyAsync_OversizedKey_Fails()
        {
            var bucketId = await CreateBucketAsync();
            var longKey = new string('k', 257);
            var result = await BasisPersistentKv.SetKeyAsync(bucketId, longKey, Encoding.UTF8.GetBytes("value"));

            _output.WriteLine($"Set with oversized key => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        [Fact]
        public async Task SetKeyAsync_OversizedValue_Fails()
        {
            var bucketId = await CreateBucketAsync();
            var largeValue = new byte[8001];
            var result = await BasisPersistentKv.SetKeyAsync(bucketId, "key", largeValue);

            _output.WriteLine($"Set with oversized value => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationValueSize, result.ErrorCode);
        }

        [Fact]
        public async Task GetKeyAsync_NonExistentKey_Fails()
        {
            var bucketId = await CreateBucketAsync();
            var result = await BasisPersistentKv.GetKeyAsync(bucketId, "non-existent");

            _output.WriteLine($"Get non-existent key => {result.ErrorCode}");
            Assert.Equal(KvError.KeyNotFound, result.ErrorCode);
        }

        [Fact]
        public async Task DeleteKeyAsync_NonExistentKey_ReturnsSuccess()
        {
            var bucketId = await CreateBucketAsync();
            var result = await BasisPersistentKv.DeleteKeyAsync(bucketId, "non-existent");

            _output.WriteLine($"Delete non-existent key => {result.ErrorCode}, deleted={result.Value}");
            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.False(result.Value); // Not deleted because didn't exist
        }

        #endregion

        #region Invariant Tests

        [Fact]
        public async Task QuotaInvariant_CurrentKeysNeverNegative()
        {
            var bucketId = await CreateBucketAsync();

            // Try to delete non-existent keys
            for (int i = 0; i < 20; i++)
            {
                await BasisPersistentKv.DeleteKeyAsync(bucketId, $"non-existent-{i}");
            }

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            _output.WriteLine($"After deletes: CurrentKeys={quota.Value.CurrentKeys}");
            Assert.True(quota.Value.CurrentKeys >= 0);
        }

        [Fact]
        public async Task QuotaInvariant_CurrentBytesNeverNegative()
        {
            var bucketId = await CreateBucketAsync();

            // Try to delete non-existent keys
            for (int i = 0; i < 20; i++)
            {
                await BasisPersistentKv.DeleteKeyAsync(bucketId, $"non-existent-{i}");
            }

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            _output.WriteLine($"After deletes: CurrentBytes={quota.Value.CurrentBytes}");
            Assert.True(quota.Value.CurrentBytes >= 0);
        }

        [Fact]
        public async Task QuotaInvariant_KeyCountMatchesListCount()
        {
            var bucketId = await CreateBucketAsync();

            // Add keys
            for (int i = 0; i < 10; i++)
            {
                await BasisPersistentKv.SetKeyAsync(bucketId, $"key-{i}", Encoding.UTF8.GetBytes($"value-{i}"));
            }

            // Delete some
            for (int i = 0; i < 5; i += 2)
            {
                await BasisPersistentKv.DeleteKeyAsync(bucketId, $"key-{i}");
            }

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var list = await BasisPersistentKv.ListKeysAsync(bucketId, 0, 1000);

            _output.WriteLine($"Quota keys: {quota.Value.CurrentKeys}, List keys: {list.Value.keys.Length}");
            Assert.Equal(list.Value.keys.Length, quota.Value.CurrentKeys);
        }

        [Fact]
        public async Task QuotaInvariant_ByteCountMatchesCalculated()
        {
            var bucketId = await CreateBucketAsync();
            var expected = new Dictionary<string, byte[]>();

            // Add various keys
            for (int i = 0; i < 15; i++)
            {
                var key = $"k{i}";
                var value = new byte[i * 10 + 5];
                await BasisPersistentKv.SetKeyAsync(bucketId, key, value);
                expected[key] = value;
            }

            // Overwrite some
            for (int i = 0; i < 5; i++)
            {
                var key = $"k{i}";
                var value = new byte[100 + i];
                await BasisPersistentKv.SetKeyAsync(bucketId, key, value);
                expected[key] = value;
            }

            // Delete some
            for (int i = 5; i < 10; i++)
            {
                var key = $"k{i}";
                await BasisPersistentKv.DeleteKeyAsync(bucketId, key);
                expected.Remove(key);
            }

            // Calculate expected bytes
            long expectedBytes = 0;
            foreach (var kvp in expected)
            {
                expectedBytes += Encoding.UTF8.GetByteCount(kvp.Key) + kvp.Value.Length;
            }

            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            _output.WriteLine($"Expected bytes: {expectedBytes}, Actual: {quota.Value.CurrentBytes}");
            Assert.Equal(expectedBytes, quota.Value.CurrentBytes);
        }

        [Fact]
        public async Task QuotaInvariant_OverwriteSameKey_KeyCountUnchanged()
        {
            var bucketId = await CreateBucketAsync();
            var key = "stable-key";

            await BasisPersistentKv.SetKeyAsync(bucketId, key, Encoding.UTF8.GetBytes("value1"));
            var quota1 = await BasisPersistentKv.GetQuotaAsync(bucketId);

            // Overwrite multiple times
            for (int i = 2; i <= 10; i++)
            {
                await BasisPersistentKv.SetKeyAsync(bucketId, key, Encoding.UTF8.GetBytes($"value{i}"));
            }

            var quota2 = await BasisPersistentKv.GetQuotaAsync(bucketId);

            _output.WriteLine($"Before overwrites: {quota1.Value.CurrentKeys}, After: {quota2.Value.CurrentKeys}");
            Assert.Equal(quota1.Value.CurrentKeys, quota2.Value.CurrentKeys);
            Assert.Equal(1, quota2.Value.CurrentKeys);
        }

        [Fact]
        public async Task QuotaInvariant_ConsistentAfterMixedOperations()
        {
            var bucketId = await CreateBucketAsync();
            var tracker = new Dictionary<string, byte[]>();

            var random = new Random(42);

            // Perform 100 random operations
            for (int i = 0; i < 100; i++)
            {
                var op = random.Next(3);
                var key = $"key-{random.Next(20)}";

                switch (op)
                {
                    case 0: // Set
                        var value = new byte[random.Next(100, 500)];
                        await BasisPersistentKv.SetKeyAsync(bucketId, key, value);
                        tracker[key] = value;
                        break;
                    case 1: // Delete
                        await BasisPersistentKv.DeleteKeyAsync(bucketId, key);
                        tracker.Remove(key);
                        break;
                    case 2: // Overwrite existing
                        if (tracker.Count > 0)
                        {
                            var existingKey = tracker.Keys.ElementAt(random.Next(tracker.Count));
                            var newValue = new byte[random.Next(100, 500)];
                            await BasisPersistentKv.SetKeyAsync(bucketId, existingKey, newValue);
                            tracker[existingKey] = newValue;
                        }
                        break;
                }
            }

            // Verify invariants
            var quota = await BasisPersistentKv.GetQuotaAsync(bucketId);
            var list = await BasisPersistentKv.ListKeysAsync(bucketId, 0, 1000);

            long expectedBytes = 0;
            foreach (var kvp in tracker)
            {
                expectedBytes += Encoding.UTF8.GetByteCount(kvp.Key) + kvp.Value.Length;
            }

            _output.WriteLine($"Expected: Keys={tracker.Count}, Bytes={expectedBytes}");
            _output.WriteLine($"Actual: Keys={quota.Value.CurrentKeys}, Bytes={quota.Value.CurrentBytes}");

            Assert.Equal(tracker.Count, quota.Value.CurrentKeys);
            Assert.Equal(tracker.Count, list.Value.keys.Length);
            Assert.Equal(expectedBytes, quota.Value.CurrentBytes);
        }

        #endregion

        #region Isolation Tests

        [Fact]
        public async Task MultipleBuckets_QuotasAreIsolated()
        {
            var bucket1 = await CreateBucketAsync();
            var bucket2 = await CreateBucketAsync();

            // Fill bucket1
            for (int i = 0; i < 10; i++)
            {
                await BasisPersistentKv.SetKeyAsync(bucket1, $"key-{i}", new byte[100]);
            }

            // Check bucket2 is still empty
            var quota2 = await BasisPersistentKv.GetQuotaAsync(bucket2);
            Assert.Equal(0, quota2.Value.CurrentKeys);
            Assert.Equal(0, quota2.Value.CurrentBytes);

            // Fill bucket2
            for (int i = 0; i < 5; i++)
            {
                await BasisPersistentKv.SetKeyAsync(bucket2, $"key-{i}", new byte[200]);
            }

            // Check bucket1 unchanged
            var quota1 = await BasisPersistentKv.GetQuotaAsync(bucket1);
            Assert.Equal(10, quota1.Value.CurrentKeys);

            _output.WriteLine($"Bucket1: {quota1.Value.CurrentKeys} keys, Bucket2: {quota2.Value.CurrentKeys} keys");
        }

        [Fact]
        public async Task MultipleBuckets_GuardsAreIndependent()
        {
            var bucket1 = await CreateBucketAsync();
            var bucket2 = await CreateBucketAsync();

            // Enable guard on bucket1, disable on bucket2
            await BasisPersistentKv.SetKeyCountGuard(bucket1, true);
            await BasisPersistentKv.SetKeyCountGuard(bucket2, false);

            var quota1 = await BasisPersistentKv.GetQuotaAsync(bucket1);
            var quota2 = await BasisPersistentKv.GetQuotaAsync(bucket2);

            // Fill bucket1 to max
            for (int i = 0; i < quota1.Value.MaxKeys; i++)
            {
                await BasisPersistentKv.SetKeyAsync(bucket1, $"k{i}", Encoding.UTF8.GetBytes("v"));
            }

            // Bucket1 should be blocked
            var blocked1 = await BasisPersistentKv.SetKeyAsync(bucket1, "overflow", Encoding.UTF8.GetBytes("v"));
            Assert.Equal(KvError.QuotaKeys, blocked1.ErrorCode);

            // Bucket2 should not be blocked (guard not enabled)
            for (int i = 0; i < quota2.Value.MaxKeys + 5; i++)
            {
                var result = await BasisPersistentKv.SetKeyAsync(bucket2, $"k{i}", Encoding.UTF8.GetBytes("v"));
                Assert.Equal(KvError.Success, result.ErrorCode);
            }

            _output.WriteLine($"Bucket1 blocked, Bucket2 allowed to exceed");
        }

        #endregion
    }
}
