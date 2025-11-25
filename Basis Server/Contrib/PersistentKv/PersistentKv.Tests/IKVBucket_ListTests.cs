using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace PersistentKv.Tests
{
    /// <summary>
    /// Tests for ListKeys operation on IKVBucket interface.
    /// Covers pagination, prefix filtering, ordering, and edge cases.
    /// </summary>
    public class IKVBucket_ListTests : BasisKvBucketTestBase
    {
        public IKVBucket_ListTests(ITestOutputHelper output) : base(output)
        {
        }

        #region Empty Bucket Tests

        [Fact]
        public async Task ListKeys_EmptyBucket_ReturnsEmptyArray()
        {
            var bucket = await CreateBucketAsync();

            var result = await bucket.ListKeys();

            Output.WriteLine($"ListKeys => {result.ErrorCode}, count={result.Value.keys.Length}");
            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.NotNull(result.Value.keys);
            Assert.Empty(result.Value.keys);
            Assert.False(result.Value.more);
        }

        [Fact]
        public async Task ListKeys_EmptyBucket_WithOffset_ReturnsEmpty()
        {
            var bucket = await CreateBucketAsync();

            var result = await bucket.ListKeys(offset: 10);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Empty(result.Value.keys);
            Assert.False(result.Value.more);
        }

        [Fact]
        public async Task ListKeys_EmptyBucket_WithPrefix_ReturnsEmpty()
        {
            var bucket = await CreateBucketAsync();

            var result = await bucket.ListKeys(prefix: "nonexistent");

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Empty(result.Value.keys);
            Assert.False(result.Value.more);
        }

        #endregion

        #region Basic Listing Tests

        [Fact]
        public async Task ListKeys_SingleKey_ReturnsKey()
        {
            var bucket = await CreateBucketAsync();
            var key = "only-key";

            await bucket.Set(key, Encoding.UTF8.GetBytes("value"));
            var result = await bucket.ListKeys();

            Output.WriteLine($"Keys: {string.Join(", ", result.Value.keys)}");
            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Single(result.Value.keys);
            Assert.Equal(key, result.Value.keys[0]);
            Assert.False(result.Value.more);
        }

        [Fact]
        public async Task ListKeys_MultipleKeys_ReturnsAll()
        {
            var bucket = await CreateBucketAsync();
            var keys = new[] { "key1", "key2", "key3", "key4", "key5" };

            foreach (var key in keys)
            {
                await bucket.Set(key, Encoding.UTF8.GetBytes($"value-{key}"));
            }

            var result = await bucket.ListKeys(limit: 100);

            Output.WriteLine($"Keys: {string.Join(", ", result.Value.keys)}");
            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Equal(keys.Length, result.Value.keys.Length);
        }

        [Fact]
        public async Task ListKeys_ReturnsKeysInAlphabeticalOrder()
        {
            var bucket = await CreateBucketAsync();
            var keys = new[] { "zebra", "apple", "monkey", "banana", "dog" };

            foreach (var key in keys)
            {
                await bucket.Set(key, Encoding.UTF8.GetBytes("data"));
            }

            var result = await bucket.ListKeys(limit: 100);

            Output.WriteLine($"Keys: {string.Join(", ", result.Value.keys)}");
            Assert.Equal(KvError.Success, result.ErrorCode);

            var sortedKeys = result.Value.keys.OrderBy(k => k).ToArray();
            Assert.Equal(sortedKeys, result.Value.keys);
        }

        [Fact]
        public async Task ListKeys_AfterDelete_ExcludesDeletedKey()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("key1", Encoding.UTF8.GetBytes("v1"));
            await bucket.Set("key2", Encoding.UTF8.GetBytes("v2"));
            await bucket.Set("key3", Encoding.UTF8.GetBytes("v3"));

            await bucket.Delete("key2");

            var result = await bucket.ListKeys();

            Output.WriteLine($"Keys after delete: {string.Join(", ", result.Value.keys)}");
            Assert.Equal(2, result.Value.keys.Length);
            Assert.Contains("key1", result.Value.keys);
            Assert.Contains("key3", result.Value.keys);
            Assert.DoesNotContain("key2", result.Value.keys);
        }

        [Fact]
        public async Task ListKeys_IncludesKeysWithEmptyValues()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("empty-key", Array.Empty<byte>());
            await bucket.Set("normal-key", Encoding.UTF8.GetBytes("data"));

            var result = await bucket.ListKeys();

            Assert.Equal(2, result.Value.keys.Length);
            Assert.Contains("empty-key", result.Value.keys);
            Assert.Contains("normal-key", result.Value.keys);
        }

        #endregion

        #region Pagination Tests

        [Fact]
        public async Task ListKeys_WithLimit_ReturnsCorrectCount()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 20; i++)
            {
                await bucket.Set($"key-{i:D2}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var result = await bucket.ListKeys(limit: 5);

            Output.WriteLine($"Keys (limit 5): {string.Join(", ", result.Value.keys)}");
            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Equal(5, result.Value.keys.Length);
        }

        [Fact]
        public async Task ListKeys_WithLimit_SetsMoreFlagCorrectly()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 20; i++)
            {
                await bucket.Set($"key-{i:D2}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var result = await bucket.ListKeys(limit: 5);

            Output.WriteLine($"More flag: {result.Value.more}");
            Assert.True(result.Value.more, "Should indicate more results available");
        }

        [Fact]
        public async Task ListKeys_LastPage_MoreFlagIsFalse()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var result = await bucket.ListKeys(limit: 100);

            Output.WriteLine($"More flag: {result.Value.more}");
            Assert.False(result.Value.more, "Should not indicate more results");
        }

        [Fact]
        public async Task ListKeys_WithOffset_SkipsCorrectly()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i:D2}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var allKeys = await bucket.ListKeys(limit: 100);
            var offsetKeys = await bucket.ListKeys(offset: 5, limit: 100);

            Output.WriteLine($"All keys: {string.Join(", ", allKeys.Value.keys)}");
            Output.WriteLine($"Offset keys: {string.Join(", ", offsetKeys.Value.keys)}");

            Assert.Equal(10, allKeys.Value.keys.Length);
            Assert.Equal(5, offsetKeys.Value.keys.Length);
            Assert.Equal(allKeys.Value.keys.Skip(5).ToArray(), offsetKeys.Value.keys);
        }

        [Fact]
        public async Task ListKeys_PaginationThroughAllKeys_ReturnsComplete()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 25; i++)
            {
                await bucket.Set($"key-{i:D2}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var page1 = await bucket.ListKeys(offset: 0, limit: 10);
            var page2 = await bucket.ListKeys(offset: 10, limit: 10);
            var page3 = await bucket.ListKeys(offset: 20, limit: 10);

            Output.WriteLine($"Page 1: {page1.Value.keys.Length} keys, more={page1.Value.more}");
            Output.WriteLine($"Page 2: {page2.Value.keys.Length} keys, more={page2.Value.more}");
            Output.WriteLine($"Page 3: {page3.Value.keys.Length} keys, more={page3.Value.more}");

            Assert.Equal(10, page1.Value.keys.Length);
            Assert.True(page1.Value.more);

            Assert.Equal(10, page2.Value.keys.Length);
            Assert.True(page2.Value.more);

            Assert.Equal(5, page3.Value.keys.Length);
            Assert.False(page3.Value.more);

            var allKeys = page1.Value.keys.Concat(page2.Value.keys).Concat(page3.Value.keys).ToArray();
            Assert.Equal(25, allKeys.Length);
            Assert.Equal(allKeys.Distinct().Count(), allKeys.Length); // No duplicates
        }

        [Fact]
        public async Task ListKeys_OffsetBeyondEnd_ReturnsEmpty()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 5; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var result = await bucket.ListKeys(offset: 100, limit: 10);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Empty(result.Value.keys);
            Assert.False(result.Value.more);
        }

        [Fact]
        public async Task ListKeys_LimitZero_ReturnsEmpty()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("key1", Encoding.UTF8.GetBytes("value"));

            var result = await bucket.ListKeys(limit: 0);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Empty(result.Value.keys);
        }

        [Fact]
        public async Task ListKeys_LimitOne_ReturnsSingleKey()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var result = await bucket.ListKeys(limit: 1);

            Assert.Single(result.Value.keys);
            Assert.True(result.Value.more);
        }

        #endregion

        #region Prefix Filtering Tests

        [Fact]
        public async Task ListKeys_WithPrefix_ReturnsMatchingKeysOnly()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("user:123", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("user:456", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("admin:789", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("guest:000", Encoding.UTF8.GetBytes("data"));

            var result = await bucket.ListKeys(prefix: "user:");

            Output.WriteLine($"Keys with prefix 'user:': {string.Join(", ", result.Value.keys)}");
            Assert.Equal(2, result.Value.keys.Length);
            Assert.All(result.Value.keys, key => Assert.StartsWith("user:", key));
        }

        [Fact]
        public async Task ListKeys_PrefixNoMatches_ReturnsEmpty()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("apple", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("banana", Encoding.UTF8.GetBytes("data"));

            var result = await bucket.ListKeys(prefix: "orange");

            Assert.Empty(result.Value.keys);
            Assert.False(result.Value.more);
        }

        [Fact]
        public async Task ListKeys_EmptyPrefix_ReturnsAll()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("key1", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("key2", Encoding.UTF8.GetBytes("data"));

            var result = await bucket.ListKeys(prefix: "");

            Assert.Equal(2, result.Value.keys.Length);
        }

        [Fact]
        public async Task ListKeys_PrefixWithPagination_WorksCorrectly()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 20; i++)
            {
                await bucket.Set($"prefix-{i:D2}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            await bucket.Set("other-key", Encoding.UTF8.GetBytes("data"));

            var page1 = await bucket.ListKeys(offset: 0, limit: 5, prefix: "prefix-");
            var page2 = await bucket.ListKeys(offset: 5, limit: 5, prefix: "prefix-");

            Output.WriteLine($"Page 1: {string.Join(", ", page1.Value.keys)}");
            Output.WriteLine($"Page 2: {string.Join(", ", page2.Value.keys)}");

            Assert.Equal(5, page1.Value.keys.Length);
            Assert.True(page1.Value.more);

            Assert.Equal(5, page2.Value.keys.Length);
            Assert.True(page2.Value.more);

            Assert.All(page1.Value.keys.Concat(page2.Value.keys), key => Assert.StartsWith("prefix-", key));
        }

        [Fact]
        public async Task ListKeys_PrefixIsCaseSensitive()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("User:123", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("user:456", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("USER:789", Encoding.UTF8.GetBytes("data"));

            var lowerResult = await bucket.ListKeys(prefix: "user:");
            var upperResult = await bucket.ListKeys(prefix: "User:");

            Output.WriteLine($"Lowercase prefix: {string.Join(", ", lowerResult.Value.keys)}");
            Output.WriteLine($"Uppercase prefix: {string.Join(", ", upperResult.Value.keys)}");

            Assert.Single(lowerResult.Value.keys);
            Assert.Single(upperResult.Value.keys);
            Assert.NotEqual(lowerResult.Value.keys[0], upperResult.Value.keys[0]);
        }

        [Fact]
        public async Task ListKeys_PrefixMatchesFullKey_ReturnsKey()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("exactmatch", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("exactmatch-suffix", Encoding.UTF8.GetBytes("data"));

            var result = await bucket.ListKeys(prefix: "exactmatch");

            Assert.Equal(2, result.Value.keys.Length);
            Assert.Contains("exactmatch", result.Value.keys);
            Assert.Contains("exactmatch-suffix", result.Value.keys);
        }

        [Fact]
        public async Task ListKeys_PrefixWithSpecialChars_WorksCorrectly()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("config.app.setting", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("config.db.setting", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("data.value", Encoding.UTF8.GetBytes("data"));

            var result = await bucket.ListKeys(prefix: "config.");

            Output.WriteLine($"Keys with 'config.': {string.Join(", ", result.Value.keys)}");
            Assert.Equal(2, result.Value.keys.Length);
        }

        #endregion

        #region Key Ordering Tests

        [Fact]
        public async Task ListKeys_NumericStrings_OrdersLexicographically()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("10", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("2", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("1", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("20", Encoding.UTF8.GetBytes("data"));

            var result = await bucket.ListKeys();

            Output.WriteLine($"Order: {string.Join(", ", result.Value.keys)}");
            // Lexicographic order: "1", "10", "2", "20"
            Assert.Equal(new[] { "1", "10", "2", "20" }, result.Value.keys);
        }

        [Fact]
        public async Task ListKeys_MixedCase_OrdersCorrectly()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("Zebra", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("apple", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("Banana", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("cherry", Encoding.UTF8.GetBytes("data"));

            var result = await bucket.ListKeys();

            Output.WriteLine($"Order: {string.Join(", ", result.Value.keys)}");
            // Should be sorted lexicographically using ordinal (byte-by-byte) comparison
            // SQLite BINARY collation matches StringComparer.Ordinal
            Assert.True(result.Value.keys.SequenceEqual(result.Value.keys.OrderBy(k => k, StringComparer.Ordinal)));
        }

        [Fact]
        public async Task ListKeys_SpecialCharacters_OrdersCorrectly()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("key-1", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("key_1", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("key.1", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("key/1", Encoding.UTF8.GetBytes("data"));

            var result = await bucket.ListKeys();

            Output.WriteLine($"Order: {string.Join(", ", result.Value.keys)}");
            Assert.True(result.Value.keys.SequenceEqual(result.Value.keys.OrderBy(k => k, StringComparer.Ordinal)));
        }

        [Fact]
        public async Task ListKeys_UnicodeCharacters_OrdersCorrectly()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("café", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("apple", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("über", Encoding.UTF8.GetBytes("data"));
            await bucket.Set("banana", Encoding.UTF8.GetBytes("data"));

            var result = await bucket.ListKeys();

            Output.WriteLine($"Order: {string.Join(", ", result.Value.keys)}");
            Assert.True(result.Value.keys.SequenceEqual(result.Value.keys.OrderBy(k => k, StringComparer.Ordinal)));
        }

        #endregion

        #region List Consistency Tests

        [Fact]
        public async Task ListKeys_MatchesExistsForAllKeys()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var listResult = await bucket.ListKeys(limit: 100);

            foreach (var key in listResult.Value.keys)
            {
                var existsResult = await bucket.Exists(key);
                Assert.True(existsResult.Value, $"Key {key} from list should exist");
            }
        }

        [Fact]
        public async Task ListKeys_MatchesGetForAllKeys()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var listResult = await bucket.ListKeys(limit: 100);

            foreach (var key in listResult.Value.keys)
            {
                var getResult = await bucket.Get(key);
                Assert.Equal(KvError.Success, getResult.ErrorCode);
                Assert.NotEmpty(getResult.Value.ToArray());
            }
        }

        [Fact]
        public async Task ListKeys_CountMatchesQuota()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 15; i++)
            {
                await bucket.Set($"key-{i:D2}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var listResult = await bucket.ListKeys(limit: 100);
            var quotaResult = await bucket.GetQuota();

            Output.WriteLine($"List count: {listResult.Value.keys.Length}, Quota keys: {quotaResult.Value.CurrentKeys}");
            Assert.Equal(quotaResult.Value.CurrentKeys, listResult.Value.keys.Length);
        }

        [Fact]
        public async Task ListKeys_ConsistentAcrossMultipleCalls()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var result1 = await bucket.ListKeys(limit: 100);
            var result2 = await bucket.ListKeys(limit: 100);
            var result3 = await bucket.ListKeys(limit: 100);

            Assert.Equal(result1.Value.keys, result2.Value.keys);
            Assert.Equal(result1.Value.keys, result3.Value.keys);
        }

        [Fact]
        public async Task ListKeys_InvariantChecks_AllScenarios()
        {
            var bucket = await CreateBucketAsync();

            // Helper to verify invariants
            async Task VerifyListInvariants(uint offset = 0, uint limit = 100, string? prefix = null)
            {
                var result = await bucket.ListKeys(offset, limit, prefix);

                // Invariant: Always succeeds
                Assert.Equal(KvError.Success, result.ErrorCode);

                // Invariant: Keys array is never null
                Assert.NotNull(result.Value.keys);

                // Invariant: No duplicate keys
                var uniqueKeys = result.Value.keys.Distinct().ToArray();
                Assert.Equal(uniqueKeys.Length, result.Value.keys.Length);

                // Invariant: Keys are sorted
                var sortedKeys = result.Value.keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
                Assert.Equal(sortedKeys, result.Value.keys);

                // Invariant: All keys match prefix if provided
                if (!string.IsNullOrEmpty(prefix))
                {
                    Assert.All(result.Value.keys, key => Assert.StartsWith(prefix, key));
                }

                // Invariant: Returned keys count respects limit
                Assert.True(result.Value.keys.Length <= (int)limit,
                    $"Returned {result.Value.keys.Length} keys, limit was {limit}");

                // Invariant: more flag is true only if there are more results
                if (!result.Value.more)
                {
                    // If more is false, we should have returned fewer keys than the limit
                    // OR we're at the exact end
                    Assert.True(result.Value.keys.Length <= (int)limit);
                }

                // Invariant: All returned keys should exist
                foreach (var key in result.Value.keys)
                {
                    var exists = await bucket.Exists(key);
                    Assert.True(exists.Value, $"Key '{key}' from ListKeys should exist");
                }
            }

            // Test with empty bucket
            await VerifyListInvariants();

            // Add some keys
            for (int i = 0; i < 15; i++)
            {
                await bucket.Set($"key-{i:D2}", Encoding.UTF8.GetBytes($"value-{i}"));
            }

            // Test various scenarios
            await VerifyListInvariants(); // All keys
            await VerifyListInvariants(offset: 5); // With offset
            await VerifyListInvariants(limit: 5); // With limit
            await VerifyListInvariants(offset: 5, limit: 5); // Both
            await VerifyListInvariants(prefix: "key-"); // With prefix
            await VerifyListInvariants(prefix: "key-1"); // More specific prefix

            // Delete some and verify again
            await bucket.Delete("key-05");
            await bucket.Delete("key-10");
            await VerifyListInvariants();
        }

        #endregion

        #region Edge Cases

        [Fact]
        public async Task ListKeys_VeryLargeLimit_ReturnsAllKeys()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"val-{i}"));
            }

            var result = await bucket.ListKeys(limit: 10000);

            Assert.Equal(10, result.Value.keys.Length);
            Assert.False(result.Value.more);
        }

        // [Fact]
        // public async Task ListKeys_AfterSetAndDelete_Reflects CurrentState()
        // {
        //     var bucket = await CreateBucketAsync();

        //     await bucket.Set("key1", Encoding.UTF8.GetBytes("v1"));
        //     await bucket.Set("key2", Encoding.UTF8.GetBytes("v2"));
        //     var list1 = await bucket.ListKeys();

        //     await bucket.Set("key3", Encoding.UTF8.GetBytes("v3"));
        //     await bucket.Delete("key1");
        //     var list2 = await bucket.ListKeys();

        //     Output.WriteLine($"First list: {string.Join(", ", list1.Value.keys)}");
        //     Output.WriteLine($"Second list: {string.Join(", ", list2.Value.keys)}");

        //     Assert.Equal(2, list1.Value.keys.Length);
        //     Assert.Equal(2, list2.Value.keys.Length);
        //     Assert.Contains("key2", list2.Value.keys);
        //     Assert.Contains("key3", list2.Value.keys);
        //     Assert.DoesNotContain("key1", list2.Value.keys);
        // }

        [Fact]
        public async Task ListKeys_NoDuplicates_EvenWithOverwrites()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set("key1", Encoding.UTF8.GetBytes($"value-{i}"));
                await bucket.Set("key2", Encoding.UTF8.GetBytes($"value-{i}"));
            }

            var result = await bucket.ListKeys();

            Assert.Equal(2, result.Value.keys.Length);
            Assert.Equal(result.Value.keys.Distinct().Count(), result.Value.keys.Length);
        }

        #endregion
    }
}
