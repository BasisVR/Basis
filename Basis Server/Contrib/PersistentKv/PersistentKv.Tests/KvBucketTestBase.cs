using System;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace PersistentKv.Tests
{

    /// <summary>
    /// Shared helpers for IKV bucket integration tests so each test fixture
    /// does not have to re-implement boilerplate setup code.
    /// </summary>
    public abstract class BasisKvBucketTestBase
    {
        protected BasisKvBucketTestBase(ITestOutputHelper output)
        {
            Output = output;
        }

        protected ITestOutputHelper Output { get; }

        protected static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        protected async Task<IKVBucket> CreateBucketAsync()
        {
            var userId = Guid.NewGuid().ToString("N");
            var addResult = await BasisPersistentKv.AddBucketAsync(userId);

            Output.WriteLine($"Created bucket: {userId}");
            Assert.Equal(KvError.Success, addResult.ErrorCode);

            return new BucketKVStore(userId);
        }
    }
}