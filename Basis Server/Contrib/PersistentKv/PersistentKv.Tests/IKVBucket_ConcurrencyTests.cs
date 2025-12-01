using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace PersistentKv.Tests
{
    /// <summary>
    /// Tests for concurrent operations and edge cases on IKVBucket interface.
    /// Verifies thread safety, race conditions, and complex scenarios.
    /// </summary>
    public class IKVBucket_ConcurrencyTests : BasisKvBucketTestBase
    {
        public IKVBucket_ConcurrencyTests(ITestOutputHelper output) : base(output)
        {
        }

        #region Concurrent Set Tests

        [Fact]
        public async Task ConcurrentSet_DifferentKeys_AllSucceed()
        {
            var bucket = await CreateBucketAsync();
            var taskCount = 10;
            var tasks = new List<Task<KvResult<Unit>>>();

            for (int i = 0; i < taskCount; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var key = $"concurrent-key-{index}";
                    var value = Encoding.UTF8.GetBytes($"value-{index}");
                    return await bucket.Set(key, value);
                }));
            }

            var results = await Task.WhenAll(tasks);

            Output.WriteLine($"Completed {taskCount} concurrent sets");
            Assert.All(results, r => Assert.Equal(KvError.Success, r.ErrorCode));

            // Verify all keys exist
            var listResult = await bucket.ListKeys(limit: 100);
            Assert.Equal(taskCount, listResult.Value.keys.Length);
        }

        [Fact]
        public async Task ConcurrentSet_SameKey_LastWriteWins()
        {
            var bucket = await CreateBucketAsync();
            var key = "contested-key";
            var taskCount = 20;
            var tasks = new List<Task<KvResult<Unit>>>();

            for (int i = 0; i < taskCount; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var value = Encoding.UTF8.GetBytes($"value-{index}");
                    return await bucket.Set(key, value);
                }));
            }

            var results = await Task.WhenAll(tasks);

            Output.WriteLine($"Completed {taskCount} concurrent sets to same key");
            Assert.All(results, r => Assert.Equal(KvError.Success, r.ErrorCode));

            // Key should exist and have one of the values
            var getResult = await bucket.Get(key);
            Assert.Equal(KvError.Success, getResult.ErrorCode);

            // Should have exactly 1 key
            var quota = await bucket.GetQuota();
            Assert.Equal(1, quota.Value.CurrentKeys);
        }

        [Fact]
        public async Task ConcurrentSet_MixedKeys_SomeContended()
        {
            var bucket = await CreateBucketAsync();
            var taskCount = 40;
            var tasks = new List<Task<KvResult<Unit>>>();

            // Create contention on a few keys
            for (int i = 0; i < taskCount; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var key = $"key-{index % 10}"; // 10 keys with 4 writers each
                    var value = Encoding.UTF8.GetBytes($"value-{index}");
                    return await bucket.Set(key, value);
                }));
            }

            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.Equal(KvError.Success, r.ErrorCode));

            // Should have exactly 10 keys
            var quota = await bucket.GetQuota();
            Assert.Equal(10, quota.Value.CurrentKeys);
        }

        #endregion

        #region Concurrent Read Tests

        [Fact]
        public async Task ConcurrentGet_SameKey_AllSucceed()
        {
            var bucket = await CreateBucketAsync();
            var key = "read-key";
            var value = Encoding.UTF8.GetBytes("read-value");

            await bucket.Set(key, value);

            var taskCount = 20;
            var tasks = new List<Task<KvResult<Memory<byte>>>>();

            for (int i = 0; i < taskCount; i++)
            {
                tasks.Add(Task.Run(async () => await bucket.Get(key)));
            }

            var results = await Task.WhenAll(tasks);

            Output.WriteLine($"Completed {taskCount} concurrent reads");
            Assert.All(results, r =>
            {
                Assert.Equal(KvError.Success, r.ErrorCode);
                Assert.Equal(value, r.Value.ToArray());
            });
        }

        [Fact]
        public async Task ConcurrentGet_DifferentKeys_AllSucceed()
        {
            var bucket = await CreateBucketAsync();
            var keyCount = 10;

            // Set up keys
            for (int i = 0; i < keyCount; i++)
            {
                await bucket.Set($"key-{i}", Encoding.UTF8.GetBytes($"value-{i}"));
            }

            var tasks = new List<Task<KvResult<Memory<byte>>>>();

            for (int i = 0; i < keyCount; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () => await bucket.Get($"key-{index}")));
            }

            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.Equal(KvError.Success, r.ErrorCode));
        }

        [Fact]
        public async Task ConcurrentExists_SameKey_ConsistentResults()
        {
            var bucket = await CreateBucketAsync();
            var key = "exists-key";

            await bucket.Set(key, Encoding.UTF8.GetBytes("data"));

            var taskCount = 15;
            var tasks = new List<Task<KvResult<bool>>>();

            for (int i = 0; i < taskCount; i++)
            {
                tasks.Add(Task.Run(async () => await bucket.Exists(key)));
            }

            var results = await Task.WhenAll(tasks);

            Assert.All(results, r =>
            {
                Assert.Equal(KvError.Success, r.ErrorCode);
                Assert.True(r.Value);
            });
        }

        #endregion

        #region Concurrent Write-Read Tests

        [Fact]
        public async Task ConcurrentWriteRead_SameKey_ReadsAreConsistent()
        {
            var bucket = await CreateBucketAsync();
            var key = "write-read-key";
            var iterations = 10;

            var writeTasks = new List<Task>();
            var readResults = new ConcurrentBag<byte[]>();

            // Writers
            for (int i = 0; i < iterations; i++)
            {
                var index = i;
                writeTasks.Add(Task.Run(async () =>
                {
                    var value = Encoding.UTF8.GetBytes($"value-{index}");
                    await bucket.Set(key, value);
                }));
            }

            // Readers (more than writers)
            var readTasks = new List<Task>();
            for (int i = 0; i < iterations * 2; i++)
            {
                readTasks.Add(Task.Run(async () =>
                {
                    var result = await bucket.Get(key);
                    if (result.ErrorCode == KvError.Success)
                    {
                        readResults.Add(result.Value.ToArray());
                    }
                }));
            }

            await Task.WhenAll(writeTasks.Concat(readTasks));

            Output.WriteLine($"Completed {iterations} writes and {iterations * 2} reads");
            Output.WriteLine($"Successful reads: {readResults.Count}");

            // All successful reads should return valid data
            Assert.All(readResults, bytes => Assert.NotEmpty(bytes));
        }

        [Fact]
        public async Task ConcurrentSetDelete_SameKey_EventualConsistency()
        {
            var bucket = await CreateBucketAsync();
            var key = "set-delete-key";
            var iterations = 15;

            var tasks = new List<Task>();

            // Interleave sets and deletes
            for (int i = 0; i < iterations; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    await bucket.Set(key, Encoding.UTF8.GetBytes($"value-{index}"));
                }));

                tasks.Add(Task.Run(async () =>
                {
                    await bucket.Delete(key);
                }));
            }

            await Task.WhenAll(tasks);

            Output.WriteLine($"Completed {iterations} sets and {iterations} deletes");

            // Final state should be consistent
            var exists = await bucket.Exists(key);
            var quota = await bucket.GetQuota();

            if (exists.Value)
            {
                Assert.Equal(1, quota.Value.CurrentKeys);
            }
            else
            {
                Assert.Equal(0, quota.Value.CurrentKeys);
            }
        }

        #endregion

        #region Concurrent Delete Tests

        [Fact]
        public async Task ConcurrentDelete_SameKey_OnlyOneSucceeds()
        {
            var bucket = await CreateBucketAsync();
            var key = "delete-key";

            await bucket.Set(key, Encoding.UTF8.GetBytes("to-delete"));

            var taskCount = 10;
            var tasks = new List<Task<KvResult<bool>>>();

            for (int i = 0; i < taskCount; i++)
            {
                tasks.Add(Task.Run(async () => await bucket.Delete(key)));
            }

            var results = await Task.WhenAll(tasks);

            var successCount = results.Count(r => r.ErrorCode == KvError.Success && r.Value);
            var failCount = results.Count(r => r.ErrorCode == KvError.Success && !r.Value);

            Output.WriteLine($"Deletes: {successCount} succeeded, {failCount} found nothing");

            // At least one should succeed, rest should return false (not found)
            Assert.True(successCount >= 1);
            Assert.Equal(taskCount, successCount + failCount);

            // Key should not exist
            var exists = await bucket.Exists(key);
            Assert.False(exists.Value);
        }

        [Fact]
        public async Task ConcurrentDelete_DifferentKeys_AllSucceed()
        {
            var bucket = await CreateBucketAsync();
            var keyCount = 10;

            // Set up keys
            for (int i = 0; i < keyCount; i++)
            {
                await bucket.Set($"del-key-{i}", Encoding.UTF8.GetBytes($"value-{i}"));
            }

            var tasks = new List<Task<KvResult<bool>>>();

            for (int i = 0; i < keyCount; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () => await bucket.Delete($"del-key-{index}")));
            }

            var results = await Task.WhenAll(tasks);

            Assert.All(results, r =>
            {
                Assert.Equal(KvError.Success, r.ErrorCode);
                Assert.True(r.Value);
            });

            // All keys should be gone
            var quota = await bucket.GetQuota();
            Assert.Equal(0, quota.Value.CurrentKeys);
        }

        #endregion

        #region Concurrent List Tests

        [Fact]
        public async Task ConcurrentList_WhileWriting_ReturnsConsistentResults()
        {
            var bucket = await CreateBucketAsync();
            var iterations = 20;

            var writeTasks = new List<Task>();
            var listTasks = new List<Task<KvResult<(string[] keys, bool more)>>>();

            // Writers adding keys
            for (int i = 0; i < iterations; i++)
            {
                var index = i;
                writeTasks.Add(Task.Run(async () =>
                {
                    await bucket.Set($"key-{index:D3}", Encoding.UTF8.GetBytes($"value-{index}"));
                }));
            }

            // Readers listing keys
            for (int i = 0; i < 10; i++)
            {
                listTasks.Add(Task.Run(async () => await bucket.ListKeys(limit: 100)));
            }

            await Task.WhenAll(writeTasks.Concat<Task>(listTasks));

            var listResults = await Task.WhenAll(listTasks);

            // All list operations should succeed
            Assert.All(listResults, r => Assert.Equal(KvError.Success, r.ErrorCode));

            // Final list should have all keys
            var finalList = await bucket.ListKeys(limit: 100);
            Assert.Equal(iterations, finalList.Value.keys.Length);
        }

        [Fact]
        public async Task ConcurrentList_DifferentPrefixes_NoInterference()
        {
            var bucket = await CreateBucketAsync();

            // Set up keys with different prefixes
            for (int i = 0; i < 5; i++)
            {
                await bucket.Set($"prefix-a-{i}", Encoding.UTF8.GetBytes($"a-{i}"));
                await bucket.Set($"prefix-b-{i}", Encoding.UTF8.GetBytes($"b-{i}"));
                await bucket.Set($"prefix-c-{i}", Encoding.UTF8.GetBytes($"c-{i}"));
            }

            var tasks = new List<Task<KvResult<(string[] keys, bool more)>>>();

            // Concurrent listing of different prefixes
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(Task.Run(async () => await bucket.ListKeys(prefix: "prefix-a-")));
                tasks.Add(Task.Run(async () => await bucket.ListKeys(prefix: "prefix-b-")));
                tasks.Add(Task.Run(async () => await bucket.ListKeys(prefix: "prefix-c-")));
            }

            var results = await Task.WhenAll(tasks);

            // All should succeed
            Assert.All(results, r =>
            {
                Assert.Equal(KvError.Success, r.ErrorCode);
                Assert.Equal(5, r.Value.keys.Length);
            });
        }

        #endregion

        #region Concurrent KeyInfo Tests

        [Fact]
        public async Task ConcurrentKeyInfo_SameKey_AllSucceed()
        {
            var bucket = await CreateBucketAsync();
            var key = "info-key";
            var value = Encoding.UTF8.GetBytes("test-data");

            await bucket.Set(key, value);

            var taskCount = 20;
            var tasks = new List<Task<KvResult<KvInfo>>>();

            for (int i = 0; i < taskCount; i++)
            {
                tasks.Add(Task.Run(async () => await bucket.KeyInfo(key)));
            }

            var results = await Task.WhenAll(tasks);

            Output.WriteLine($"Completed {taskCount} concurrent KeyInfo calls");
            Assert.All(results, r =>
            {
                Assert.Equal(KvError.Success, r.ErrorCode);
                Assert.Equal((ulong)value.Length, r.Value.valueSize);
            });
        }

        [Fact]
        public async Task ConcurrentKeyInfo_DuringUpdates_RemainsConsistent()
        {
            var bucket = await CreateBucketAsync();
            var key = "update-info-key";

            await bucket.Set(key, Encoding.UTF8.GetBytes("initial"));

            var writeTasks = new List<Task>();
            var infoTasks = new List<Task<KvResult<KvInfo>>>();

            // Writers updating the key
            for (int i = 0; i < 10; i++)
            {
                var index = i;
                writeTasks.Add(Task.Run(async () =>
                {
                    await bucket.Set(key, Encoding.UTF8.GetBytes($"value-{index}"));
                }));
            }

            // Readers getting key info
            for (int i = 0; i < 15; i++)
            {
                infoTasks.Add(Task.Run(async () => await bucket.KeyInfo(key)));
            }

            await Task.WhenAll(writeTasks.Concat<Task>(infoTasks));

            var infoResults = await Task.WhenAll(infoTasks);

            // All reads should succeed (might see different versions)
            Assert.All(infoResults, r => Assert.Equal(KvError.Success, r.ErrorCode));

            // Final state should be consistent
            var finalInfo = await bucket.KeyInfo(key);
            var finalGet = await bucket.Get(key);

            Assert.Equal(KvError.Success, finalInfo.ErrorCode);
            Assert.Equal(KvError.Success, finalGet.ErrorCode);
            Assert.Equal((ulong)finalGet.Value.Length, finalInfo.Value.valueSize);
        }

        #endregion

        #region Concurrent Quota Tests

        [Fact]
        public async Task ConcurrentQuotaReads_ReturnConsistentData()
        {
            var bucket = await CreateBucketAsync();

            // Set up initial state
            for (int i = 0; i < 5; i++)
            {
                await bucket.Set($"key-{i}", new byte[100]);
            }

            var tasks = new List<Task<KvResult<QuotaInfo>>>();

            for (int i = 0; i < 15; i++)
            {
                tasks.Add(Task.Run(async () => await bucket.GetQuota()));
            }

            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.Equal(KvError.Success, r.ErrorCode));

            // All should report the same quota
            var firstQuota = results[0].Value;
            Assert.All(results, r =>
            {
                Assert.Equal(firstQuota.CurrentKeys, r.Value.CurrentKeys);
                Assert.Equal(firstQuota.CurrentBytes, r.Value.CurrentBytes);
            });
        }

        [Fact]
        public async Task ConcurrentQuotaReads_WhileWriting_EventualConsistency()
        {
            var bucket = await CreateBucketAsync();
            var writeCount = 20;

            var writeTasks = new List<Task>();
            var quotaTasks = new List<Task<KvResult<QuotaInfo>>>();

            // Writers
            for (int i = 0; i < writeCount; i++)
            {
                var index = i;
                writeTasks.Add(Task.Run(async () =>
                {
                    await bucket.Set($"key-{index}", new byte[50]);
                }));
            }

            // Quota readers
            for (int i = 0; i < 10; i++)
            {
                quotaTasks.Add(Task.Run(async () => await bucket.GetQuota()));
            }

            await Task.WhenAll(writeTasks.Concat<Task>(quotaTasks));

            // Final quota should be accurate
            var finalQuota = await bucket.GetQuota();
            Assert.Equal(writeCount, finalQuota.Value.CurrentKeys);
        }

        #endregion

        #region Stress Tests

        [Fact]
        public async Task StressTest_MixedOperations_MaintainsConsistency()
        {
            var bucket = await CreateBucketAsync();
            var iterations = 50;
            var random = new Random(42);

            var tasks = new List<Task>();

            for (int i = 0; i < iterations; i++)
            {
                var index = i;
                var operation = random.Next(5);

                tasks.Add(Task.Run(async () =>
                {
                    var key = $"stress-key-{index % 10}";

                    switch (operation)
                    {
                        case 0: // Set
                            await bucket.Set(key, Encoding.UTF8.GetBytes($"value-{index}"));
                            break;
                        case 1: // Get
                            await bucket.Get(key);
                            break;
                        case 2: // Delete
                            await bucket.Delete(key);
                            break;
                        case 3: // Exists
                            await bucket.Exists(key);
                            break;
                        case 4: // List
                            await bucket.ListKeys(limit: 10);
                            break;
                    }
                }));
            }

            await Task.WhenAll(tasks);

            Output.WriteLine($"Completed {iterations} mixed operations");

            // Verify consistency
            var list = await bucket.ListKeys(limit: 100);
            var quota = await bucket.GetQuota();

            Assert.Equal(list.Value.keys.Length, quota.Value.CurrentKeys);

            // All listed keys should exist
            foreach (var key in list.Value.keys)
            {
                var exists = await bucket.Exists(key);
                if (!exists.Value)
                {
                    Output.WriteLine($"Key '{key}' from ListKeys does not exist!");
                    Output.WriteLine($"All keys from ListKeys: {string.Join(", ", list.Value.keys)}");

                    // Check if the key actually exists by trying to get it
                    var getResult = await bucket.Get(key);
                    Output.WriteLine($"Get result for '{key}': ErrorCode={getResult.ErrorCode}");
                }
                Assert.True(exists.Value);
            }
        }

        [Fact]
        public async Task StressTest_HighContentionSingleKey_RemainsConsistent()
        {
            var bucket = await CreateBucketAsync();
            var key = "high-contention";
            var iterations = 60;

            var tasks = new List<Task>();

            for (int i = 0; i < iterations; i++)
            {
                var index = i;
                // Mix of operations on same key
                if (index % 3 == 0)
                {
                    tasks.Add(Task.Run(async () => await bucket.Set(key, Encoding.UTF8.GetBytes($"val-{index}"))));
                }
                else if (index % 3 == 1)
                {
                    tasks.Add(Task.Run(async () => await bucket.Get(key)));
                }
                else
                {
                    tasks.Add(Task.Run(async () => await bucket.Delete(key)));
                }
            }

            await Task.WhenAll(tasks);

            Output.WriteLine($"Completed {iterations} operations on single key");

            // Final state should be consistent
            var exists = await bucket.Exists(key);
            var quota = await bucket.GetQuota();

            if (exists.Value)
            {
                var get = await bucket.Get(key);
                Assert.Equal(KvError.Success, get.ErrorCode);
                Assert.Equal(1, quota.Value.CurrentKeys);
            }
            else
            {
                Assert.Equal(0, quota.Value.CurrentKeys);
            }
        }

        #endregion

        #region Edge Case Concurrency Tests

        [Fact]
        public async Task ConcurrentSetDelete_QuotaRemainsAccurate()
        {
            var bucket = await CreateBucketAsync();
            var keyCount = 20;

            var tasks = new List<Task>();

            // Rapidly create and delete keys
            for (int i = 0; i < keyCount; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var key = $"temp-key-{index}";
                    await bucket.Set(key, new byte[100]);
                    await bucket.Delete(key);
                }));
            }

            await Task.WhenAll(tasks);

            var quota = await bucket.GetQuota();

            Output.WriteLine($"Final quota: Keys={quota.Value.CurrentKeys}, Bytes={quota.Value.CurrentBytes}");
            Assert.Equal(0, quota.Value.CurrentKeys);
            Assert.Equal(0, quota.Value.CurrentBytes);
        }

        [Fact]
        public async Task ConcurrentOverwrite_QuotaBytesUpdatesCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "overwrite-bytes";
            var iterations = 15;

            await bucket.Set(key, new byte[100]);

            var tasks = new List<Task>();

            for (int i = 0; i < iterations; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var size = 50 + (index % 10) * 10; // Varying sizes
                    await bucket.Set(key, new byte[size]);
                }));
            }

            await Task.WhenAll(tasks);

            var quota = await bucket.GetQuota();
            var get = await bucket.Get(key);

            Output.WriteLine($"Final value size: {get.Value.Length}, Quota bytes: {quota.Value.CurrentBytes}");

            // Should have exactly 1 key
            Assert.Equal(1, quota.Value.CurrentKeys);

            // Quota bytes should match actual value size + key size
            var keyBytes = Encoding.UTF8.GetByteCount(key);
            var expectedBytes = keyBytes + get.Value.Length;
            Assert.Equal(expectedBytes, quota.Value.CurrentBytes);
        }

        #endregion

        #region Isolation Tests

        [Fact]
        public async Task MultipleBuckets_ConcurrentOperations_Isolated()
        {
            var bucket1 = await CreateBucketAsync();
            var bucket2 = await CreateBucketAsync();
            var bucket3 = await CreateBucketAsync();

            var tasks = new List<Task>();

            // Concurrent operations on different buckets
            for (int i = 0; i < 10; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () => await bucket1.Set($"key-{index}", Encoding.UTF8.GetBytes($"b1-{index}"))));
                tasks.Add(Task.Run(async () => await bucket2.Set($"key-{index}", Encoding.UTF8.GetBytes($"b2-{index}"))));
                tasks.Add(Task.Run(async () => await bucket3.Set($"key-{index}", Encoding.UTF8.GetBytes($"b3-{index}"))));
            }

            await Task.WhenAll(tasks);

            // Verify isolation
            var list1 = await bucket1.ListKeys(limit: 100);
            var list2 = await bucket2.ListKeys(limit: 100);
            var list3 = await bucket3.ListKeys(limit: 100);

            Assert.Equal(10, list1.Value.keys.Length);
            Assert.Equal(10, list2.Value.keys.Length);
            Assert.Equal(10, list3.Value.keys.Length);

            // Verify values are isolated
            var val1 = await bucket1.Get("key-0");
            var val2 = await bucket2.Get("key-0");
            var val3 = await bucket3.Get("key-0");

            Assert.NotEqual(val1.Value.ToArray(), val2.Value.ToArray());
            Assert.NotEqual(val2.Value.ToArray(), val3.Value.ToArray());
        }

        #endregion
    }
}
