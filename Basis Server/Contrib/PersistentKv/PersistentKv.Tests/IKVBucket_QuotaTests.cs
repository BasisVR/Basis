using System;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace PersistentKv.Tests
{
    /// <summary>
    /// Tests for quota tracking and enforcement on IKVBucket interface.
    /// Verifies that quotas are accurately tracked and enforced consistently.
    /// </summary>
    public class IKVBucket_QuotaTests : BasisKvBucketTestBase
    {
        public IKVBucket_QuotaTests(ITestOutputHelper output) : base(output)
        {
        }

        #region Initial Quota State Tests

        [Fact]
        public async Task GetQuota_NewBucket_ReturnsZeroUsage()
        {
            var bucket = await CreateBucketAsync();

            var result = await bucket.GetQuota();

            Output.WriteLine($"Quota: Keys={result.Value.CurrentKeys}/{result.Value.MaxKeys}, Bytes={result.Value.CurrentBytes}/{result.Value.MaxBytes}");
            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Equal(0, result.Value.CurrentKeys);
            Assert.Equal(0, result.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_NewBucket_HasPositiveLimits()
        {
            var bucket = await CreateBucketAsync();

            var result = await bucket.GetQuota();

            Assert.True(result.Value.MaxKeys > 0, "MaxKeys should be positive");
            Assert.True(result.Value.MaxBytes > 0, "MaxBytes should be positive");
        }

        [Fact]
        public async Task GetQuota_NewBucket_CurrentLessThanMax()
        {
            var bucket = await CreateBucketAsync();

            var result = await bucket.GetQuota();

            Assert.True(result.Value.CurrentKeys <= result.Value.MaxKeys);
            Assert.True(result.Value.CurrentBytes <= result.Value.MaxBytes);
        }

        #endregion

        #region Key Count Tracking Tests

        [Fact]
        public async Task GetQuota_AfterSingleSet_IncrementsKeyCount()
        {
            var bucket = await CreateBucketAsync();
            var key = "test-key";
            var value = Encoding.UTF8.GetBytes("test");

            await bucket.Set(key, value);
            var result = await bucket.GetQuota();

            Output.WriteLine($"After set: CurrentKeys={result.Value.CurrentKeys}");
            Assert.Equal(1, result.Value.CurrentKeys);
        }

        [Fact]
        public async Task GetQuota_AfterMultipleSets_IncrementsCorrectly()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"value-{i}"));
            }

            var result = await bucket.GetQuota();

            Output.WriteLine($"After 10 sets: CurrentKeys={result.Value.CurrentKeys}");
            Assert.Equal(10, result.Value.CurrentKeys);
        }

        [Fact]
        public async Task GetQuota_AfterOverwrite_KeyCountUnchanged()
        {
            var bucket = await CreateBucketAsync();
            var key = "overwrite";

            await bucket.Set(key, Encoding.UTF8.GetBytes("first"));
            var quota1 = await bucket.GetQuota();

            await bucket.Set(key, Encoding.UTF8.GetBytes("second"));
            var quota2 = await bucket.GetQuota();

            Output.WriteLine($"Before overwrite: {quota1.Value.CurrentKeys}, After: {quota2.Value.CurrentKeys}");
            Assert.Equal(quota1.Value.CurrentKeys, quota2.Value.CurrentKeys);
            Assert.Equal(1, quota2.Value.CurrentKeys);
        }

        [Fact]
        public async Task GetQuota_AfterDelete_DecrementsKeyCount()
        {
            var bucket = await CreateBucketAsync();
            var key = "to-delete";

            await bucket.Set(key, Encoding.UTF8.GetBytes("data"));
            await bucket.Delete(key);
            var result = await bucket.GetQuota();

            Output.WriteLine($"After delete: CurrentKeys={result.Value.CurrentKeys}");
            Assert.Equal(0, result.Value.CurrentKeys);
        }

        [Fact]
        public async Task GetQuota_AfterPartialDeletes_TracksCorrectly()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"value-{i}"));
            }

            // Delete every other key
            for (int i = 0; i < 10; i += 2)
            {
                await bucket.Delete($"key-{i}");
            }

            var result = await bucket.GetQuota();

            Output.WriteLine($"After partial deletes: CurrentKeys={result.Value.CurrentKeys}");
            Assert.Equal(5, result.Value.CurrentKeys);
        }

        [Fact]
        public async Task GetQuota_DeleteNonExistent_KeyCountUnchanged()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("existing", Encoding.UTF8.GetBytes("data"));
            var quota1 = await bucket.GetQuota();

            await bucket.Delete("non-existent");
            var quota2 = await bucket.GetQuota();

            Assert.Equal(quota1.Value.CurrentKeys, quota2.Value.CurrentKeys);
        }

        #endregion

        #region Byte Count Tracking Tests

        [Fact]
        public async Task GetQuota_AfterSet_IncreasesByteCount()
        {
            var bucket = await CreateBucketAsync();
            var key = "byte-test";
            var value = Encoding.UTF8.GetBytes("test-value");

            var quotaBefore = await bucket.GetQuota();
            await bucket.Set(key, value);
            var quotaAfter = await bucket.GetQuota();

            Output.WriteLine($"Bytes before: {quotaBefore.Value.CurrentBytes}, after: {quotaAfter.Value.CurrentBytes}");
            Assert.True(quotaAfter.Value.CurrentBytes > quotaBefore.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_ByteCountIncludesKeySize()
        {
            var bucket = await CreateBucketAsync();
            var key = "key";
            var value = Encoding.UTF8.GetBytes("value");

            await bucket.Set(key, value);
            var result = await bucket.GetQuota();

            var keyBytes = Encoding.UTF8.GetByteCount(key);
            var valueBytes = value.Length;
            var expectedMinBytes = keyBytes + valueBytes;

            Output.WriteLine($"Expected min bytes: {expectedMinBytes}, Actual: {result.Value.CurrentBytes}");
            Assert.True(result.Value.CurrentBytes >= expectedMinBytes);
        }

        [Fact]
        public async Task GetQuota_ByteCountAccurate_WithMultipleKeys()
        {
            var bucket = await CreateBucketAsync();
            long expectedBytes = 0;

            for (int i = 0; i < 5; i++)
            {
                var key = $"key-{i}";
                var value = Encoding.UTF8.GetBytes($"value-{i}");

                await bucket.Set(key, value);

                expectedBytes += Encoding.UTF8.GetByteCount(key) + value.Length;
            }

            var result = await bucket.GetQuota();

            Output.WriteLine($"Expected: {expectedBytes}, Actual: {result.Value.CurrentBytes}");
            Assert.Equal(expectedBytes, result.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_AfterOverwriteWithLarger_ByteCountIncreases()
        {
            var bucket = await CreateBucketAsync();
            var key = "grow";
            var smallValue = new byte[10];
            var largeValue = new byte[100];

            await bucket.Set(key, smallValue);
            var quota1 = await bucket.GetQuota();

            await bucket.Set(key, largeValue);
            var quota2 = await bucket.GetQuota();

            Output.WriteLine($"Before: {quota1.Value.CurrentBytes}, After: {quota2.Value.CurrentBytes}");
            Assert.True(quota2.Value.CurrentBytes > quota1.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_AfterOverwriteWithSmaller_ByteCountDecreases()
        {
            var bucket = await CreateBucketAsync();
            var key = "shrink";
            var largeValue = new byte[100];
            var smallValue = new byte[10];

            await bucket.Set(key, largeValue);
            var quota1 = await bucket.GetQuota();

            await bucket.Set(key, smallValue);
            var quota2 = await bucket.GetQuota();

            Output.WriteLine($"Before: {quota1.Value.CurrentBytes}, After: {quota2.Value.CurrentBytes}");
            Assert.True(quota2.Value.CurrentBytes < quota1.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_AfterOverwriteWithSameSize_ByteCountUnchanged()
        {
            var bucket = await CreateBucketAsync();
            var key = "same-size";
            var value1 = new byte[50];
            var value2 = new byte[50];
            Array.Fill(value1, (byte)0x01);
            Array.Fill(value2, (byte)0x02);

            await bucket.Set(key, value1);
            var quota1 = await bucket.GetQuota();

            await bucket.Set(key, value2);
            var quota2 = await bucket.GetQuota();

            Output.WriteLine($"Before: {quota1.Value.CurrentBytes}, After: {quota2.Value.CurrentBytes}");
            Assert.Equal(quota1.Value.CurrentBytes, quota2.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_AfterDelete_DecreasesBytes()
        {
            var bucket = await CreateBucketAsync();
            var key = "delete-bytes";
            var value = new byte[100];

            await bucket.Set(key, value);
            var quotaBefore = await bucket.GetQuota();

            await bucket.Delete(key);
            var quotaAfter = await bucket.GetQuota();

            Output.WriteLine($"Before delete: {quotaBefore.Value.CurrentBytes}, After: {quotaAfter.Value.CurrentBytes}");
            Assert.True(quotaAfter.Value.CurrentBytes < quotaBefore.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_AfterAllDeletes_ByteCountReturnsToZero()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 5; i++)
            {
                await bucket.Set($"key-{i}", new byte[50]);
            }

            for (int i = 0; i < 5; i++)
            {
                await bucket.Delete($"key-{i}");
            }

            var result = await bucket.GetQuota();

            Output.WriteLine($"Final bytes: {result.Value.CurrentBytes}");
            Assert.Equal(0, result.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_EmptyValue_CountsKeyBytesOnly()
        {
            var bucket = await CreateBucketAsync();
            var key = "empty-value";
            var value = Array.Empty<byte>();

            await bucket.Set(key, value);
            var result = await bucket.GetQuota();

            var expectedBytes = Encoding.UTF8.GetByteCount(key);
            Output.WriteLine($"Expected: {expectedBytes}, Actual: {result.Value.CurrentBytes}");
            Assert.Equal(expectedBytes, result.Value.CurrentBytes);
        }

        #endregion

        #region Quota Invariants Tests

        [Fact]
        public async Task GetQuota_CurrentKeysNeverNegative()
        {
            var bucket = await CreateBucketAsync();

            // Try to delete non-existent keys
            for (int i = 0; i < 10; i++)
            {
                await bucket.Delete($"non-existent-{i}");
            }

            var result = await bucket.GetQuota();

            Output.WriteLine($"CurrentKeys: {result.Value.CurrentKeys}");
            Assert.True(result.Value.CurrentKeys >= 0);
        }

        [Fact]
        public async Task GetQuota_CurrentBytesNeverNegative()
        {
            var bucket = await CreateBucketAsync();

            // Try to delete non-existent keys
            for (int i = 0; i < 10; i++)
            {
                await bucket.Delete($"non-existent-{i}");
            }

            var result = await bucket.GetQuota();

            Output.WriteLine($"CurrentBytes: {result.Value.CurrentBytes}");
            Assert.True(result.Value.CurrentBytes >= 0);
        }

        [Fact]
        public async Task GetQuota_CurrentKeysNeverExceedsActualCount()
        {
            var bucket = await CreateBucketAsync();

            // Set 10 keys
            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var quota = await bucket.GetQuota();
            var list = await bucket.ListKeys(0, 1000);

            Output.WriteLine($"Quota says: {quota.Value.CurrentKeys}, List says: {list.Value.keys.Length}");
            Assert.Equal(list.Value.keys.Length, quota.Value.CurrentKeys);
        }

        [Fact]
        public async Task GetQuota_ConsistentAcrossMultipleCalls()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("key1", Encoding.UTF8.GetBytes("value1"));
            await bucket.Set("key2", Encoding.UTF8.GetBytes("value2"));

            var quota1 = await bucket.GetQuota();
            var quota2 = await bucket.GetQuota();
            var quota3 = await bucket.GetQuota();

            Assert.Equal(quota1.Value.CurrentKeys, quota2.Value.CurrentKeys);
            Assert.Equal(quota1.Value.CurrentKeys, quota3.Value.CurrentKeys);
            Assert.Equal(quota1.Value.CurrentBytes, quota2.Value.CurrentBytes);
            Assert.Equal(quota1.Value.CurrentBytes, quota3.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_InvariantChecks_AllOperations()
        {
            var bucket = await CreateBucketAsync();

            // Helper to verify invariants
            async Task VerifyQuotaInvariants()
            {
                var quota = await bucket.GetQuota();
                Assert.Equal(KvError.Success, quota.ErrorCode);

                // Invariant: Current values never negative
                Assert.True(quota.Value.CurrentKeys >= 0, "CurrentKeys must be non-negative");
                Assert.True(quota.Value.CurrentBytes >= 0, "CurrentBytes must be non-negative");

                // Invariant: Max values always positive
                Assert.True(quota.Value.MaxKeys > 0, "MaxKeys must be positive");
                Assert.True(quota.Value.MaxBytes > 0, "MaxBytes must be positive");

                // Invariant: Current never exceeds max (with small tolerance for edge cases)
                Assert.True(quota.Value.CurrentKeys <= quota.Value.MaxKeys * 1.1,
                    $"CurrentKeys ({quota.Value.CurrentKeys}) should not greatly exceed MaxKeys ({quota.Value.MaxKeys})");
                Assert.True(quota.Value.CurrentBytes <= quota.Value.MaxBytes * 1.1,
                    $"CurrentBytes ({quota.Value.CurrentBytes}) should not greatly exceed MaxBytes ({quota.Value.MaxBytes})");
            }

            // Verify invariants at various stages
            await VerifyQuotaInvariants(); // Empty bucket

            await bucket.Set("key1", new byte[100]);
            await VerifyQuotaInvariants(); // After set

            await bucket.Set("key1", new byte[200]);
            await VerifyQuotaInvariants(); // After overwrite

            await bucket.Set("key2", new byte[50]);
            await VerifyQuotaInvariants(); // After another set

            await bucket.Delete("key1");
            await VerifyQuotaInvariants(); // After delete

            await bucket.Delete("key2");
            await VerifyQuotaInvariants(); // After all deleted
        }

        #endregion

        #region Complex Quota Scenarios

        [Fact]
        public async Task GetQuota_MixedOperations_TracksAccurately()
        {
            var bucket = await CreateBucketAsync();

            // Set 5 keys
            for (int i = 0; i < 5; i++)
            {
                await bucket.Set($"key-{i}", new byte[10]);
            }

            // Overwrite 2 keys with different sizes
            await bucket.Set("key-0", new byte[20]);
            await bucket.Set("key-1", new byte[5]);

            // Delete 2 keys
            await bucket.Delete("key-3");
            await bucket.Delete("key-4");

            // Add 1 new key
            await bucket.Set("key-new", new byte[15]);

            var quota = await bucket.GetQuota();
            var list = await bucket.ListKeys(0, 1000);

            Output.WriteLine($"Keys: {quota.Value.CurrentKeys}, Bytes: {quota.Value.CurrentBytes}");
            Assert.Equal(4, quota.Value.CurrentKeys); // 0,1,2,new
            Assert.Equal(list.Value.keys.Length, quota.Value.CurrentKeys);
        }

        [Fact]
        public async Task GetQuota_RepeatedSetDelete_MaintainsAccuracy()
        {
            var bucket = await CreateBucketAsync();
            var key = "churn";

            for (int i = 0; i < 20; i++)
            {
                await bucket.Set(key, new byte[100]);
                await bucket.Delete(key);
            }

            var result = await bucket.GetQuota();

            Output.WriteLine($"After churn: Keys={result.Value.CurrentKeys}, Bytes={result.Value.CurrentBytes}");
            Assert.Equal(0, result.Value.CurrentKeys);
            Assert.Equal(0, result.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_VaryingSizes_TracksCorrectly()
        {
            var bucket = await CreateBucketAsync();
            long expectedBytes = 0;

            for (int i = 1; i <= 10; i++)
            {
                var key = $"key-{i}";
                var value = new byte[i * 10]; // 10, 20, 30, ..., 100 bytes

                await bucket.Set(key, value);

                expectedBytes += Encoding.UTF8.GetByteCount(key) + value.Length;
            }

            var result = await bucket.GetQuota();

            Output.WriteLine($"Expected bytes: {expectedBytes}, Actual: {result.Value.CurrentBytes}");
            Assert.Equal(10, result.Value.CurrentKeys);
            Assert.Equal(expectedBytes, result.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_WithUnicodeKeys_CountsBytesNotChars()
        {
            var bucket = await CreateBucketAsync();
            var unicodeKey = "键-🔑-key"; // Mix of CJK, emoji, ASCII
            var value = new byte[10];

            await bucket.Set(unicodeKey, value);
            var result = await bucket.GetQuota();

            var keyByteCount = Encoding.UTF8.GetByteCount(unicodeKey);
            var expectedBytes = keyByteCount + value.Length;

            Output.WriteLine($"Unicode key byte count: {keyByteCount}, Expected total: {expectedBytes}, Actual: {result.Value.CurrentBytes}");
            Assert.Equal(expectedBytes, result.Value.CurrentBytes);
            Assert.True(keyByteCount > unicodeKey.Length); // UTF-8 bytes > char count
        }

        #endregion

        #region Quota Precision Tests

        [Fact]
        public async Task GetQuota_SingleByte_TrackedPrecisely()
        {
            var bucket = await CreateBucketAsync();
            var key = "a";
            var value = new byte[] { 0x42 };

            await bucket.Set(key, value);
            var result = await bucket.GetQuota();

            var expectedBytes = Encoding.UTF8.GetByteCount(key) + 1;
            Assert.Equal(expectedBytes, result.Value.CurrentBytes);
        }

        [Fact]
        public async Task GetQuota_MaxSizeValue_TrackedCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "max-val";
            var value = new byte[8000]; // Max allowed size

            await bucket.Set(key, value);
            var result = await bucket.GetQuota();

            var expectedBytes = Encoding.UTF8.GetByteCount(key) + 8000;
            Output.WriteLine($"Expected: {expectedBytes}, Actual: {result.Value.CurrentBytes}");
            Assert.Equal(expectedBytes, result.Value.CurrentBytes);
        }

        #endregion
    }
}
