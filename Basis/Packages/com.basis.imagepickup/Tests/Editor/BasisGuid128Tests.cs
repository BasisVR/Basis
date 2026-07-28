using System;
using System.IO;
using Basis.Scripts.Common;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Basis.ImagePickup.Tests
{
    public sealed class BasisGuid128Tests
    {
        private static readonly Guid TestGuid = new("00112233-4455-6677-8899-aabbccddeeff");

        [Test]
        public void IsA16ByteBlittableValue()
        {
            Assert.That(UnsafeUtility.SizeOf<BasisGuid128>(), Is.EqualTo(BasisGuid128.SerializedSize));
        }

        [Test]
        public void RoundTripsSystemGuid()
        {
            BasisGuid128 native = BasisGuid128.FromGuid(TestGuid);

            Assert.That(native.ToGuid(), Is.EqualTo(TestGuid));
            Assert.That(native, Is.EqualTo(BasisGuid128.FromGuid(TestGuid)));
        }

        [Test]
        public void ManagedSpanReadWriteMatchesGuidByteLayout()
        {
            byte[] expected = TestGuid.ToByteArray();
            BasisGuid128 native = BasisGuid128.ReadFrom(expected);
            byte[] actual = new byte[BasisGuid128.SerializedSize];

            native.WriteTo(actual);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void NativeWriteMatchesExistingGuidPacketLayout()
        {
            byte[] expected = TestGuid.ToByteArray();
            using var actual = new NativeArray<byte>(
                BasisGuid128.SerializedSize,
                Allocator.Temp,
                NativeArrayOptions.UninitializedMemory
            );

            BasisAnimatedImageNetworkCodec.WriteGuid(actual, 0, BasisGuid128.FromGuid(TestGuid));

            int expectedLength = expected.Length;
            for (int i = 0; i < expectedLength; i++)
                Assert.That(actual[i], Is.EqualTo(expected[i]), $"Byte {i} differs.");
        }

        [Test]
        public void ManagedPacketWriterMatchesExistingGuidPacketLayout()
        {
            byte[] expected = TestGuid.ToByteArray();
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            BasisAnimatedImageNetworkCodec.WriteGuid(writer, TestGuid);
            writer.Flush();

            Assert.That(stream.ToArray(), Is.EqualTo(expected));
        }
    }
}
