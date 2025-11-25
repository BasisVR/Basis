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
    /// Tests for data integrity and correctness on IKVBucket interface.
    /// Verifies data is stored and retrieved correctly without corruption.
    /// </summary>
    public class IKVBucket_IntegrityTests : BasisKvBucketTestBase
    {
        public IKVBucket_IntegrityTests(ITestOutputHelper output) : base(output)
        {
        }

        #region Binary Data Integrity Tests

        [Fact]
        public async Task BinaryData_AllByteValues_PreservedCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "all-bytes";
            var value = new byte[256];

            // Test all possible byte values
            for (int i = 0; i < 256; i++)
            {
                value[i] = (byte)i;
            }

            await bucket.Set(key, value);
            var result = await bucket.Get(key);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Equal(value, result.Value.ToArray());
            Assert.Equal(256, result.Value.Length);

            // Verify each byte
            var retrieved = result.Value.ToArray();
            for (int i = 0; i < 256; i++)
            {
                Assert.Equal((byte)i, retrieved[i]);
            }
        }

        [Fact]
        public async Task BinaryData_RandomBytes_PreservedCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var random = new Random(42);

            for (int i = 0; i < 10; i++)
            {
                var key = $"random-{i}";
                var value = new byte[1000];
                random.NextBytes(value);

                await bucket.Set(key, value);
                var result = await bucket.Get(key);

                Assert.Equal(KvError.Success, result.ErrorCode);
                Assert.Equal(value, result.Value.ToArray());
            }
        }

        [Fact]
        public async Task BinaryData_LargeValue_NoCorruption()
        {
            var bucket = await CreateBucketAsync();
            var key = "large-binary";
            var value = new byte[8000]; // Max size

            // Fill with pattern
            for (int i = 0; i < value.Length; i++)
            {
                value[i] = (byte)(i % 256);
            }

            await bucket.Set(key, value);
            var result = await bucket.Get(key);

            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Equal(value.Length, result.Value.Length);

            // Verify pattern is intact
            var retrieved = result.Value.ToArray();
            for (int i = 0; i < value.Length; i++)
            {
                Assert.Equal((byte)(i % 256), retrieved[i]);
            }
        }

        [Fact]
        public async Task BinaryData_NullBytes_PreservedCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "null-bytes";
            var value = new byte[] { 0x00, 0xFF, 0x00, 0xFF, 0x00 };

            await bucket.Set(key, value);
            var result = await bucket.Get(key);

            Assert.Equal(value, result.Value.ToArray());
        }

        [Fact]
        public async Task BinaryData_AlternatingPattern_PreservedCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "alternating";
            var value = new byte[1000];

            for (int i = 0; i < value.Length; i++)
            {
                value[i] = (byte)(i % 2 == 0 ? 0xFF : 0x00);
            }

            await bucket.Set(key, value);
            var result = await bucket.Get(key);

            Assert.Equal(value, result.Value.ToArray());
        }

        #endregion

        #region Text Data Integrity Tests

        [Fact]
        public async Task TextData_UTF8_PreservedCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var texts = new[]
            {
                "Simple ASCII text",
                "Accented: café, naïve, résumé",
                "Chinese: 你好世界",
                "Japanese: こんにちは世界",
                "Arabic: مرحبا بالعالم",
                "Emoji: 🔑🌍🎉💻🚀",
                "Mixed: Hello 世界 🌍!"
            };

            for (int i = 0; i < texts.Length; i++)
            {
                var key = $"text-{i}";
                var value = Encoding.UTF8.GetBytes(texts[i]);

                await bucket.Set(key, value);
                var result = await bucket.Get(key);

                var retrievedText = Encoding.UTF8.GetString(result.Value.ToArray());
                Output.WriteLine($"Original: {texts[i]}");
                Output.WriteLine($"Retrieved: {retrievedText}");

                Assert.Equal(texts[i], retrievedText);
            }
        }

        [Fact]
        public async Task TextData_MultilineStrings_PreservedCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "multiline";
            var text = "Line 1\nLine 2\r\nLine 3\rLine 4\n\nLine 6";
            var value = Encoding.UTF8.GetBytes(text);

            await bucket.Set(key, value);
            var result = await bucket.Get(key);

            var retrieved = Encoding.UTF8.GetString(result.Value.ToArray());
            Assert.Equal(text, retrieved);
        }

        [Fact]
        public async Task TextData_SpecialCharacters_PreservedCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "special";
            var text = "Special chars: \t\n\r\"'`~!@#$%^&*()_+-=[]{}|;:,.<>?/\\";
            var value = Encoding.UTF8.GetBytes(text);

            await bucket.Set(key, value);
            var result = await bucket.Get(key);

            var retrieved = Encoding.UTF8.GetString(result.Value.ToArray());
            Assert.Equal(text, retrieved);
        }

        [Fact]
        public async Task TextData_LongString_PreservedCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "long-text";
            var text = new string('A', 7000);
            var value = Encoding.UTF8.GetBytes(text);

            await bucket.Set(key, value);
            var result = await bucket.Get(key);

            var retrieved = Encoding.UTF8.GetString(result.Value.ToArray());
            Assert.Equal(text.Length, retrieved.Length);
            Assert.Equal(text, retrieved);
        }

        #endregion

        #region Overwrite Integrity Tests

        [Fact]
        public async Task Overwrite_DifferentSizes_NoDataMixing()
        {
            var bucket = await CreateBucketAsync();
            var key = "overwrite-test";

            // Write progressively different values
            var values = new[]
            {
                Encoding.UTF8.GetBytes("short"),
                Encoding.UTF8.GetBytes("medium length value"),
                Encoding.UTF8.GetBytes("very long value with lots of text"),
                Encoding.UTF8.GetBytes("x"),
                new byte[1000]
            };

            foreach (var value in values)
            {
                await bucket.Set(key, value);
                var result = await bucket.Get(key);

                Assert.Equal(value, result.Value.ToArray());
                Assert.Equal(value.Length, result.Value.Length);
            }
        }

        [Fact]
        public async Task Overwrite_WithDifferentContent_CompletelyReplaced()
        {
            var bucket = await CreateBucketAsync();
            var key = "replace";

            var value1 = new byte[500];
            Array.Fill(value1, (byte)0xFF);

            var value2 = new byte[500];
            Array.Fill(value2, (byte)0x00);

            await bucket.Set(key, value1);
            await bucket.Set(key, value2);

            var result = await bucket.Get(key);
            var retrieved = result.Value.ToArray();

            // Should be all zeros, no 0xFF bytes
            Assert.All(retrieved, b => Assert.Equal(0x00, b));
        }

        [Fact]
        public async Task Overwrite_MultipleIterations_NoResidue()
        {
            var bucket = await CreateBucketAsync();
            var key = "iterations";

            for (int i = 0; i < 20; i++)
            {
                var value = Encoding.UTF8.GetBytes($"iteration-{i:D3}");
                await bucket.Set(key, value);

                var result = await bucket.Get(key);
                var retrieved = Encoding.UTF8.GetString(result.Value.ToArray());

                Assert.Equal($"iteration-{i:D3}", retrieved);
            }
        }

        #endregion

        #region Multiple Keys Integrity Tests

        [Fact]
        public async Task MultipleKeys_NoDataLeakage()
        {
            var bucket = await CreateBucketAsync();
            var keyCount = 50;

            // Set unique values for each key
            for (int i = 0; i < keyCount; i++)
            {
                var key = $"isolated-{i}";
                var value = Encoding.UTF8.GetBytes($"unique-value-{i}");
                await bucket.Set(key, value);
            }

            // Verify each key has its own distinct value
            for (int i = 0; i < keyCount; i++)
            {
                var key = $"isolated-{i}";
                var result = await bucket.Get(key);
                var retrieved = Encoding.UTF8.GetString(result.Value.ToArray());

                Assert.Equal($"unique-value-{i}", retrieved);
            }
        }

        [Fact]
        public async Task MultipleKeys_SimilarNames_DistinctValues()
        {
            var bucket = await CreateBucketAsync();

            var keys = new[]
            {
                "key",
                "key1",
                "key-1",
                "key_1",
                "key.1",
                "key/1"
            };

            for (int i = 0; i < keys.Length; i++)
            {
                await bucket.Set(keys[i], Encoding.UTF8.GetBytes($"value-{i}"));
            }

            // Verify each key is distinct
            for (int i = 0; i < keys.Length; i++)
            {
                var result = await bucket.Get(keys[i]);
                var retrieved = Encoding.UTF8.GetString(result.Value.ToArray());
                Assert.Equal($"value-{i}", retrieved);
            }
        }

        [Fact]
        public async Task MultipleKeys_DifferentSizes_NoInterference()
        {
            var bucket = await CreateBucketAsync();

            var sizes = new[] { 1, 10, 100, 1000, 5000, 8000 };

            foreach (var size in sizes)
            {
                var key = $"size-{size}";
                var value = new byte[size];
                Array.Fill(value, (byte)(size % 256));

                await bucket.Set(key, value);
            }

            // Verify all sizes are correct
            foreach (var size in sizes)
            {
                var key = $"size-{size}";
                var result = await bucket.Get(key);

                Assert.Equal(size, result.Value.Length);
                Assert.All(result.Value.ToArray(), b => Assert.Equal((byte)(size % 256), b));
            }
        }

        #endregion

        #region Delete Integrity Tests

        [Fact]
        public async Task Delete_DoesNotAffectOtherKeys()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("key1", Encoding.UTF8.GetBytes("value1"));
            await bucket.Set("key2", Encoding.UTF8.GetBytes("value2"));
            await bucket.Set("key3", Encoding.UTF8.GetBytes("value3"));

            await bucket.Delete("key2");

            var result1 = await bucket.Get("key1");
            var result3 = await bucket.Get("key3");

            Assert.Equal("value1", Encoding.UTF8.GetString(result1.Value.ToArray()));
            Assert.Equal("value3", Encoding.UTF8.GetString(result3.Value.ToArray()));
        }

        [Fact]
        public async Task Delete_ThenRecreate_NoPreviousData()
        {
            var bucket = await CreateBucketAsync();
            var key = "recreate";

            var originalValue = Encoding.UTF8.GetBytes("original data with specific content");
            await bucket.Set(key, originalValue);
            await bucket.Delete(key);

            var newValue = Encoding.UTF8.GetBytes("new");
            await bucket.Set(key, newValue);

            var result = await bucket.Get(key);
            var retrieved = result.Value.ToArray();

            Assert.Equal(newValue, retrieved);
            Assert.Equal(3, retrieved.Length); // Should be just "new"
            Assert.DoesNotContain((byte)'o', retrieved); // From "original"
        }

        [Fact]
        public async Task Delete_MultipleKeys_EachDeleteIndependent()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"del-{i}", Encoding.UTF8.GetBytes($"value-{i}"));
            }

            // Delete even keys
            for (int i = 0; i < 10; i += 2)
            {
                await bucket.Delete($"del-{i}");
            }

            // Verify odd keys still exist with correct data
            for (int i = 1; i < 10; i += 2)
            {
                var result = await bucket.Get($"del-{i}");
                var retrieved = Encoding.UTF8.GetString(result.Value.ToArray());
                Assert.Equal($"value-{i}", retrieved);
            }
        }

        #endregion

        #region State Consistency Tests

        [Fact]
        public async Task StateConsistency_ListMatchesIndividualGets()
        {
            var bucket = await CreateBucketAsync();

            var expectedData = new Dictionary<string, byte[]>();

            for (int i = 0; i < 20; i++)
            {
                var key = $"consistent-{i}";
                var value = Encoding.UTF8.GetBytes($"data-{i}");
                await bucket.Set(key, value);
                expectedData[key] = value;
            }

            var listResult = await bucket.ListKeys(limit: 100);

            foreach (var key in listResult.Value.keys)
            {
                var getResult = await bucket.Get(key);
                Assert.Equal(expectedData[key], getResult.Value.ToArray());
            }
        }

        [Fact]
        public async Task StateConsistency_ExistsMatchesGet()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"value-{i}"));
            }

            // Delete some
            await bucket.Delete("key-3");
            await bucket.Delete("key-7");

            for (int i = 0; i < 10; i++)
            {
                var key = $"key-{i}";
                var existsResult = await bucket.Exists(key);
                var getResult = await bucket.Get(key);

                if (existsResult.Value)
                {
                    Assert.Equal(KvError.Success, getResult.ErrorCode);
                }
                else
                {
                    Assert.Equal(KvError.KeyNotFound, getResult.ErrorCode);
                }
            }
        }

        [Fact]
        public async Task StateConsistency_QuotaMatchesActualData()
        {
            var bucket = await CreateBucketAsync();

            var totalKeys = 0;
            long totalBytes = 0;

            for (int i = 0; i < 15; i++)
            {
                var key = $"quota-{i}";
                var value = new byte[50 + (i * 10)];

                await bucket.Set(key, value);

                totalKeys++;
                totalBytes += Encoding.UTF8.GetByteCount(key) + value.Length;
            }

            var quota = await bucket.GetQuota();

            Output.WriteLine($"Expected: {totalKeys} keys, {totalBytes} bytes");
            Output.WriteLine($"Quota: {quota.Value.CurrentKeys} keys, {quota.Value.CurrentBytes} bytes");

            Assert.Equal(totalKeys, quota.Value.CurrentKeys);
            Assert.Equal(totalBytes, quota.Value.CurrentBytes);
        }

        [Fact]
        public async Task StateConsistency_KeyInfoMatchesGetSize()
        {
            var bucket = await CreateBucketAsync();

            for (int i = 0; i < 10; i++)
            {
                var key = $"size-check-{i}";
                var value = new byte[50 * (i + 1)];

                await bucket.Set(key, value);

                var getResult = await bucket.Get(key);
                var infoResult = await bucket.KeyInfo(key);

                Assert.Equal(KvError.Success, getResult.ErrorCode);
                Assert.Equal(KvError.Success, infoResult.ErrorCode);
                Assert.Equal((ulong)getResult.Value.Length, infoResult.Value.valueSize);
            }
        }

        [Fact]
        public async Task StateConsistency_KeyInfoVersionTracksUpdates()
        {
            var bucket = await CreateBucketAsync();
            var key = "version-tracking";

            var previousVersion = 0UL;

            for (int i = 0; i < 10; i++)
            {
                await bucket.Set(key, Encoding.UTF8.GetBytes($"value-{i}"));

                var info = await bucket.KeyInfo(key);
                Assert.Equal(KvError.Success, info.ErrorCode);

                if (i > 0)
                {
                    Assert.True(info.Value.version > previousVersion,
                        $"Version should increment: previous={previousVersion}, current={info.Value.version}");
                }

                previousVersion = info.Value.version;
            }
        }

        #endregion

        #region Complex Scenario Tests

        [Fact]
        public async Task ComplexScenario_MixedOperations_DataRemainsCorrect()
        {
            var bucket = await CreateBucketAsync();

            // Build a complex state
            await bucket.Set("user:123:name", Encoding.UTF8.GetBytes("Alice"));
            await bucket.Set("user:123:email", Encoding.UTF8.GetBytes("alice@example.com"));
            await bucket.Set("user:456:name", Encoding.UTF8.GetBytes("Bob"));
            await bucket.Set("user:456:email", Encoding.UTF8.GetBytes("bob@example.com"));

            // Update one field
            await bucket.Set("user:123:email", Encoding.UTF8.GetBytes("alice.new@example.com"));

            // Delete a user
            await bucket.Delete("user:456:name");
            await bucket.Delete("user:456:email");

            // Verify final state
            var name123 = await bucket.Get("user:123:name");
            var email123 = await bucket.Get("user:123:email");
            var name456 = await bucket.Get("user:456:name");
            var email456 = await bucket.Get("user:456:email");

            Assert.Equal("Alice", Encoding.UTF8.GetString(name123.Value.ToArray()));
            Assert.Equal("alice.new@example.com", Encoding.UTF8.GetString(email123.Value.ToArray()));
            Assert.Equal(KvError.KeyNotFound, name456.ErrorCode);
            Assert.Equal(KvError.KeyNotFound, email456.ErrorCode);

            var quota = await bucket.GetQuota();
            Assert.Equal(2, quota.Value.CurrentKeys);
        }

        [Fact]
        public async Task ComplexScenario_SequentialUpdates_FinalStateCorrect()
        {
            var bucket = await CreateBucketAsync();
            var key = "counter";

            // Simulate a counter being updated
            for (int i = 0; i < 100; i++)
            {
                var value = Encoding.UTF8.GetBytes($"{i}");
                await bucket.Set(key, value);
            }

            var result = await bucket.Get(key);
            var finalValue = Encoding.UTF8.GetString(result.Value.ToArray());

            Assert.Equal("99", finalValue);

            var quota = await bucket.GetQuota();
            Assert.Equal(1, quota.Value.CurrentKeys);
        }

        [Fact]
        public async Task ComplexScenario_PrefixBasedOperations_IsolationMaintained()
        {
            var bucket = await CreateBucketAsync();

            // Simulate different namespaces
            var configs = new[] { "debug:true", "timeout:30", "retries:3" };
            var sessions = new[] { "session1", "session2", "session3" };
            var caches = new[] { "item1", "item2" };

            foreach (var cfg in configs)
            {
                await bucket.Set($"config:{cfg}", Encoding.UTF8.GetBytes(cfg));
            }

            foreach (var sess in sessions)
            {
                await bucket.Set($"session:{sess}", Encoding.UTF8.GetBytes($"data-{sess}"));
            }

            foreach (var cache in caches)
            {
                await bucket.Set($"cache:{cache}", Encoding.UTF8.GetBytes($"cached-{cache}"));
            }

            // Verify each namespace is isolated
            var configList = await bucket.ListKeys(prefix: "config:");
            var sessionList = await bucket.ListKeys(prefix: "session:");
            var cacheList = await bucket.ListKeys(prefix: "cache:");

            Assert.Equal(configs.Length, configList.Value.keys.Length);
            Assert.Equal(sessions.Length, sessionList.Value.keys.Length);
            Assert.Equal(caches.Length, cacheList.Value.keys.Length);

            // Clear one namespace
            foreach (var key in cacheList.Value.keys)
            {
                await bucket.Delete(key);
            }

            // Others should be unaffected
            var configListAfter = await bucket.ListKeys(prefix: "config:");
            var sessionListAfter = await bucket.ListKeys(prefix: "session:");
            var cacheListAfter = await bucket.ListKeys(prefix: "cache:");

            Assert.Equal(configs.Length, configListAfter.Value.keys.Length);
            Assert.Equal(sessions.Length, sessionListAfter.Value.keys.Length);
            Assert.Empty(cacheListAfter.Value.keys);
        }

        #endregion

        #region Edge Data Pattern Tests

        [Fact]
        public async Task EdgePattern_RepeatingBytes_DetectsCorrectBoundaries()
        {
            var bucket = await CreateBucketAsync();

            var patterns = new[]
            {
                new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA },
                new byte[] { 0x55, 0x55, 0x55, 0x55 },
                new byte[] { 0xFF, 0xFF, 0xFF }
            };

            for (int i = 0; i < patterns.Length; i++)
            {
                var key = $"pattern-{i}";
                await bucket.Set(key, patterns[i]);
            }

            for (int i = 0; i < patterns.Length; i++)
            {
                var key = $"pattern-{i}";
                var result = await bucket.Get(key);

                Assert.Equal(patterns[i], result.Value.ToArray());
                Assert.Equal(patterns[i].Length, result.Value.Length);
            }
        }

        [Fact]
        public async Task EdgePattern_SequentialKeys_CorrectOrdering()
        {
            var bucket = await CreateBucketAsync();

            // Keys that sort differently as strings vs numbers
            var keys = new[] { "1", "2", "10", "20", "100", "11", "21", "3" };

            foreach (var key in keys)
            {
                await bucket.Set(key, Encoding.UTF8.GetBytes($"value-{key}"));
            }

            var listResult = await bucket.ListKeys(limit: 100);
            var sortedKeys = keys.OrderBy(k => k).ToArray();

            Output.WriteLine($"Expected order: {string.Join(", ", sortedKeys)}");
            Output.WriteLine($"Actual order: {string.Join(", ", listResult.Value.keys)}");

            Assert.Equal(sortedKeys, listResult.Value.keys);
        }

        #endregion
    }
}
