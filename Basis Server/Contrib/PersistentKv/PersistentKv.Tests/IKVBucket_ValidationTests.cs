using System;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace PersistentKv.Tests
{
    /// <summary>
    /// Tests for validation and error handling on IKVBucket interface.
    /// Tests parameter validation, size limits, and error conditions.
    /// </summary>
    public class IKVBucket_ValidationTests : BasisKvBucketTestBase
    {
        public IKVBucket_ValidationTests(ITestOutputHelper output) : base(output)
        {
        }

        #region Key Validation Tests

        [Fact]
        public async Task Set_KeyTooLong_ReturnsValidationError()
        {
            var bucket = await CreateBucketAsync();
            var longKey = new string('k', 257); // 257 bytes exceeds 256 limit
            var value = Encoding.UTF8.GetBytes("value");

            var result = await bucket.Set(longKey, value);

            Output.WriteLine($"Set with long key => {result.ErrorCode}, {result.Message}");
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        [Fact]
        public async Task Set_KeyExactly256Bytes_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            var key = new string('k', 256); // Exactly 256 bytes
            var value = Encoding.UTF8.GetBytes("value");

            var result = await bucket.Set(key, value);

            Output.WriteLine($"Set with 256-byte key => {result.ErrorCode}");
            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        [Fact]
        public async Task Set_KeyExactly255Bytes_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            var key = new string('k', 255);
            var value = Encoding.UTF8.GetBytes("value");

            var result = await bucket.Set(key, value);

            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        [Fact]
        public async Task Set_UnicodeKeyExceeds256Bytes_ReturnsValidationError()
        {
            var bucket = await CreateBucketAsync();
            // Each emoji is 4 bytes in UTF-8, so 65 emojis = 260 bytes
            var longKey = string.Concat(System.Linq.Enumerable.Repeat("🔑", 65));
            var value = Encoding.UTF8.GetBytes("value");

            var byteCount = Encoding.UTF8.GetByteCount(longKey);
            Output.WriteLine($"Unicode key byte count: {byteCount}");

            var result = await bucket.Set(longKey, value);

            Output.WriteLine($"Set with unicode key => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        [Fact]
        public async Task Set_UnicodeKeyWithin256Bytes_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            // 64 emojis = 256 bytes
            var key = string.Concat(System.Linq.Enumerable.Repeat("🔑", 64));
            var value = Encoding.UTF8.GetBytes("value");

            var byteCount = Encoding.UTF8.GetByteCount(key);
            Output.WriteLine($"Unicode key byte count: {byteCount}");

            var result = await bucket.Set(key, value);

            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        [Fact]
        public async Task Get_KeyTooLong_ReturnsValidationError()
        {
            var bucket = await CreateBucketAsync();
            var longKey = new string('k', 257);

            var result = await bucket.Get(longKey);

            Output.WriteLine($"Get with long key => {result.ErrorCode}");
            // Should return validation error, not KeyNotFound
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        [Fact]
        public async Task Exists_KeyTooLong_ReturnsValidationError()
        {
            var bucket = await CreateBucketAsync();
            var longKey = new string('k', 257);

            var result = await bucket.Exists(longKey);

            Output.WriteLine($"Exists with long key => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        [Fact]
        public async Task Delete_KeyTooLong_ReturnsValidationError()
        {
            var bucket = await CreateBucketAsync();
            var longKey = new string('k', 257);

            var result = await bucket.Delete(longKey);

            Output.WriteLine($"Delete with long key => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        [Fact]
        public async Task KeyInfo_KeyTooLong_ReturnsValidationError()
        {
            var bucket = await CreateBucketAsync();
            var longKey = new string('k', 257);

            var result = await bucket.KeyInfo(longKey);

            Output.WriteLine($"KeyInfo with long key => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        [Fact]
        public async Task KeyInfo_EmptyKey_ReturnsValidationError()
        {
            var bucket = await CreateBucketAsync();

            var result = await bucket.KeyInfo("");

            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        #endregion

        #region Value Validation Tests

        [Fact]
        public async Task Set_ValueTooLarge_ReturnsValidationError()
        {
            var bucket = await CreateBucketAsync();
            var key = "large-value";
            var largeValue = new byte[8001]; // Exceeds 8000 byte limit

            var result = await bucket.Set(key, largeValue);

            Output.WriteLine($"Set with large value => {result.ErrorCode}, {result.Message}");
            Assert.Equal(KvError.ValidationValueSize, result.ErrorCode);
        }

        [Fact]
        public async Task Set_ValueExactly8000Bytes_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            var key = "max-value";
            var maxValue = new byte[8000]; // Exactly at limit

            var result = await bucket.Set(key, maxValue);

            Output.WriteLine($"Set with 8000-byte value => {result.ErrorCode}");
            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        [Fact]
        public async Task Set_Value7999Bytes_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            var key = "near-max";
            var value = new byte[7999];

            var result = await bucket.Set(key, value);

            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        [Fact]
        public async Task Set_ValueMuchTooLarge_ReturnsValidationError()
        {
            var bucket = await CreateBucketAsync();
            var key = "huge-value";
            var hugeValue = new byte[100000]; // Way over limit

            var result = await bucket.Set(key, hugeValue);

            Output.WriteLine($"Set with 100KB value => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationValueSize, result.ErrorCode);
        }

        [Fact]
        public async Task Set_EmptyValue_DoesNotReturnValidationError()
        {
            var bucket = await CreateBucketAsync();
            var key = "empty";
            var value = Array.Empty<byte>();

            var result = await bucket.Set(key, value);

            // Empty values should be allowed
            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        #endregion

        #region Combined Validation Tests

        [Fact]
        public async Task Set_BothKeyAndValueTooLarge_ReturnsKeyValidationError()
        {
            var bucket = await CreateBucketAsync();
            var longKey = new string('k', 257);
            var largeValue = new byte[8001];

            var result = await bucket.Set(longKey, largeValue);

            // Key validation should happen first
            Output.WriteLine($"Set with both invalid => {result.ErrorCode}");
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        [Fact]
        public async Task Set_MaxKeyAndMaxValue_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            var maxKey = new string('k', 256);
            var maxValue = new byte[8000];

            var result = await bucket.Set(maxKey, maxValue);

            Output.WriteLine($"Set with max key and value => {result.ErrorCode}");
            Assert.Equal(KvError.Success, result.ErrorCode);

            // Verify can retrieve it
            var getResult = await bucket.Get(maxKey);
            Assert.Equal(KvError.Success, getResult.ErrorCode);
            Assert.Equal(maxValue.Length, getResult.Value.Length);
        }

        [Fact]
        public async Task Set_SingleCharKeyMaxValue_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            var key = "x";
            var maxValue = new byte[8000];

            var result = await bucket.Set(key, maxValue);

            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        [Fact]
        public async Task Set_MaxKeySingleByteValue_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            var maxKey = new string('k', 256);
            var value = new byte[] { 0x42 };

            var result = await bucket.Set(maxKey, value);

            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        #endregion

        #region Error Message Quality Tests

        [Fact]
        public async Task Set_KeyTooLong_ErrorMessageIsDescriptive()
        {
            var bucket = await CreateBucketAsync();
            var longKey = new string('k', 300);
            var value = Encoding.UTF8.GetBytes("value");

            var result = await bucket.Set(longKey, value);

            Output.WriteLine($"Error message: {result.Message}");
            Assert.NotNull(result.Message);
            Assert.NotEmpty(result.Message);
        }

        [Fact]
        public async Task Set_ValueTooLarge_ErrorMessageIsDescriptive()
        {
            var bucket = await CreateBucketAsync();
            var key = "key";
            var largeValue = new byte[10000];

            var result = await bucket.Set(key, largeValue);

            Output.WriteLine($"Error message: {result.Message}");
            Assert.NotNull(result.Message);
            Assert.NotEmpty(result.Message);
        }

        [Fact]
        public async Task Get_KeyNotFound_ErrorMessageIsDescriptive()
        {
            var bucket = await CreateBucketAsync();

            var result = await bucket.Get("non-existent");

            Output.WriteLine($"Error message: {result.Message}");
            Assert.NotNull(result.Message);
            Assert.NotEmpty(result.Message);
        }

        #endregion

        #region Boundary Testing

        [Fact]
        public async Task Set_KeySize_BoundaryTesting()
        {
            var bucket = await CreateBucketAsync();
            var value = Encoding.UTF8.GetBytes("test");

            // Test various key sizes around the boundary
            var sizes = new[] { 1, 2, 100, 200, 254, 255, 256, 257, 300, 500 };

            foreach (var size in sizes)
            {
                var key = new string('k', size);
                var result = await bucket.Set(key, value);

                if (size <= 256)
                {
                    Assert.Equal(KvError.Success, result.ErrorCode);
                    Output.WriteLine($"Key size {size}: Success");
                }
                else
                {
                    Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
                    Output.WriteLine($"Key size {size}: ValidationKeySize");
                }
            }
        }

        [Fact]
        public async Task Set_ValueSize_BoundaryTesting()
        {
            var bucket = await CreateBucketAsync();

            // Disable quota guards to test individual value size limits without hitting quota
            var bucketStore = (BucketKVStore)bucket;
            await BasisPersistentKv.SetByteSizeGaurd(bucketStore.BucketId, false);
            await BasisPersistentKv.SetKeyCountGuard(bucketStore.BucketId, false);

            // Test various value sizes around the boundary
            var sizes = new[] { 0, 1, 100, 1000, 7998, 7999, 8000, 8001, 8002, 10000 };

            foreach (var size in sizes)
            {
                var key = $"value-size-{size}";
                var value = new byte[size];
                var result = await bucket.Set(key, value);

                if (size <= 8000)
                {
                    Assert.Equal(KvError.Success, result.ErrorCode);
                    Output.WriteLine($"Value size {size}: Success");
                }
                else
                {
                    Assert.Equal(KvError.ValidationValueSize, result.ErrorCode);
                    Output.WriteLine($"Value size {size}: ValidationValueSize");
                }
            }
        }

        #endregion

        #region Special Character Validation

        [Fact]
        public async Task Set_KeyWithNullCharacter_HandlesCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "key\0with\0nulls";
            var value = Encoding.UTF8.GetBytes("value");

            var result = await bucket.Set(key, value);

            // Should either succeed or return a specific error
            Output.WriteLine($"Key with null chars => {result.ErrorCode}");
            // This tests the implementation's handling of null chars
        }

        [Fact]
        public async Task Set_ValueWithAllNulls_Succeeds()
        {
            var bucket = await CreateBucketAsync();
            var key = "null-value";
            var value = new byte[100]; // All zeros/nulls

            var result = await bucket.Set(key, value);

            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        [Fact]
        public async Task Set_KeyWithControlCharacters_HandlesCorrectly()
        {
            var bucket = await CreateBucketAsync();
            var key = "key\twith\ncontrol\rchars";
            var value = Encoding.UTF8.GetBytes("value");

            var result = await bucket.Set(key, value);

            Output.WriteLine($"Key with control chars => {result.ErrorCode}");
            // Should handle gracefully
        }

        #endregion

        #region Multi-Byte Character Edge Cases

        [Fact]
        public async Task Set_KeyWithMixedMultiByteChars_ValidatesOnBytes()
        {
            var bucket = await CreateBucketAsync();

            // Create a key that's under 256 chars but might be close to byte limit
            // Mixing ASCII (1 byte), 2-byte, 3-byte, and 4-byte UTF-8 characters
            var key = "a" + // 1 byte
                      "é" + // 2 bytes
                      "中" + // 3 bytes
                      "🔑"; // 4 bytes
            // Total: 10 bytes for 4 characters

            // Repeat to approach limit
            var repeatedKey = string.Concat(System.Linq.Enumerable.Repeat(key, 25)); // 250 bytes
            var value = Encoding.UTF8.GetBytes("test");

            var byteCount = Encoding.UTF8.GetByteCount(repeatedKey);
            Output.WriteLine($"Key has {repeatedKey.Length} chars, {byteCount} bytes");

            var result = await bucket.Set(repeatedKey, value);

            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        [Fact]
        public async Task Set_KeyEndsWithPartialMultiByte_ValidatesCorrectly()
        {
            var bucket = await CreateBucketAsync();

            // Create a key that would be exactly 256 bytes
            // Fill with ASCII, then add multi-byte chars to exactly hit limit
            var asciiPart = new string('a', 252); // 252 bytes
            var emoji = "🔑"; // 4 bytes in UTF-8
            var key = asciiPart + emoji; // Exactly 256 bytes

            var byteCount = Encoding.UTF8.GetByteCount(key);
            Output.WriteLine($"Key byte count: {byteCount}");

            var result = await bucket.Set(key, Encoding.UTF8.GetBytes("value"));

            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        #endregion

        #region Operation-Specific Validation

        [Fact]
        public async Task ListKeys_InvalidOffset_HandlesGracefully()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("key1", Encoding.UTF8.GetBytes("value"));

            // Very large offset
            var result = await bucket.ListKeys(offset: uint.MaxValue, limit: 10);

            // Should return empty, not error
            Assert.Equal(KvError.Success, result.ErrorCode);
            Assert.Empty(result.Value.keys);
        }

        [Fact]
        public async Task ListKeys_InvalidLimit_HandlesGracefully()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("key1", Encoding.UTF8.GetBytes("value"));

            // Zero limit
            var result = await bucket.ListKeys(limit: 0);

            // Should handle gracefully
            Assert.Equal(KvError.Success, result.ErrorCode);
        }

        [Fact]
        public async Task ListKeys_VeryLongPrefix_HandlesCorrectly()
        {
            var bucket = await CreateBucketAsync();

            await bucket.Set("short-key", Encoding.UTF8.GetBytes("value"));

            var longPrefix = new string('p', 300);
            var result = await bucket.ListKeys(prefix: longPrefix);

            // Should return validation error
            Assert.Equal(KvError.ValidationKeySize, result.ErrorCode);
        }

        #endregion

        #region Validation Consistency Tests

        [Fact]
        public async Task Validation_ConsistentAcrossOperations()
        {
            var bucket = await CreateBucketAsync();
            var longKey = new string('k', 257);
            var value = Encoding.UTF8.GetBytes("value");

            var setResult = await bucket.Set(longKey, value);
            var getResult = await bucket.Get(longKey);
            var existsResult = await bucket.Exists(longKey);
            var deleteResult = await bucket.Delete(longKey);
            var keyInfoResult = await bucket.KeyInfo(longKey);

            // All should return the same validation error
            Output.WriteLine($"Set: {setResult.ErrorCode}, Get: {getResult.ErrorCode}, " +
                            $"Exists: {existsResult.ErrorCode}, Delete: {deleteResult.ErrorCode}, " +
                            $"KeyInfo: {keyInfoResult.ErrorCode}");

            Assert.Equal(KvError.ValidationKeySize, setResult.ErrorCode);
            Assert.Equal(KvError.ValidationKeySize, getResult.ErrorCode);
            Assert.Equal(KvError.ValidationKeySize, existsResult.ErrorCode);
            Assert.Equal(KvError.ValidationKeySize, deleteResult.ErrorCode);
            Assert.Equal(KvError.ValidationKeySize, keyInfoResult.ErrorCode);
        }

        [Fact]
        public async Task Validation_DoesNotAffectValidKeys()
        {
            var bucket = await CreateBucketAsync();

            // Try invalid operation
            var invalidResult = await bucket.Set(new string('k', 300), Encoding.UTF8.GetBytes("value"));
            Assert.Equal(KvError.ValidationKeySize, invalidResult.ErrorCode);

            // Valid operation should still work
            var validResult = await bucket.Set("valid-key", Encoding.UTF8.GetBytes("value"));
            Assert.Equal(KvError.Success, validResult.ErrorCode);
        }

        #endregion
    }
}
