using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisCompressionShimTests
    {
        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        static byte[] Repeating(int length)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++) data[i] = (byte)"hello basis world "[i % 18];
            return data;
        }

        static byte[] Noise(System.Random random, int length)
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            return data;
        }

        [Test]
        public void EveryPayload_RoundTrips_AndNeverGrowsByMoreThanOneByte()
        {
            System.Random random = new System.Random(1234);
            foreach (int length in new[] { 0, 1, 2, 16, 17, 127, 128, 129, 4096, 16384, 65536, BasisCompressionShim.MaxLength })
            {
                foreach (byte[] data in new[] { Noise(random, length), Repeating(length), new byte[length] })
                {
                    byte[] packed = BasisCompressionShim.Compress(data);
                    Assert.LessOrEqual(packed.Length, data.Length + 1, $"{length} bytes grew by more than the one byte header.");
                    CollectionAssert.AreEqual(data, BasisCompressionShim.Decompress(packed), $"{length} bytes did not survive the round trip.");
                }
            }
        }

        [Test]
        public void CompressibleData_GetsMuchSmaller()
        {
            byte[] json = Encoding.UTF8.GetBytes("{\"questions\":[" + string.Join(",", Enumerable.Repeat("{\"q\":\"Which way is north?\",\"a\":[\"Up\",\"Down\"],\"correct\":0}", 60)) + "]}");
            Assert.Less(BasisCompressionShim.Compress(json).Length, json.Length / 4);
        }

        [Test]
        public void OffsetAndCount_LetAScriptFrameItsOwnMessage()
        {
            byte[] payload = Repeating(5000), buffer = new byte[7000];
            Buffer.BlockCopy(payload, 0, buffer, 1000, payload.Length);
            byte[] packed = BasisCompressionShim.Compress(buffer, 1000, payload.Length);
            byte[] message = new byte[packed.Length + 1];
            message[0] = 42;
            Buffer.BlockCopy(packed, 0, message, 1, packed.Length);
            CollectionAssert.AreEqual(payload, BasisCompressionShim.Decompress(message, 1, packed.Length));
        }

        [Test]
        public void Compress_RejectsBadArguments()
        {
            Assert.Throws<ArgumentNullException>(() => BasisCompressionShim.Compress(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => BasisCompressionShim.Compress(new byte[4], -1, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BasisCompressionShim.Compress(new byte[4], 3, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BasisCompressionShim.Compress(new byte[BasisCompressionShim.MaxLength + 1]));
        }

        [Test]
        public void Decompress_ReturnsNull_ForHostileInput_InsteadOfThrowing()
        {
            byte[] bomb = new byte[4 + 4111];
            bomb[0] = 1; bomb[1] = 0x80; bomb[2] = 0x80; bomb[3] = 0x40;
            for (int i = 4; i < bomb.Length; i++) bomb[i] = 0xFF;
            byte[][] hostile =
            {
                null,
                new byte[0],
                new byte[] { 1 },
                new byte[] { 2, 1, 2, 3 },
                new byte[] { 1, 0 },
                new byte[] { 1, 0x80, 0x80, 0x80, 0x80, 0x01 },
                new byte[] { 1, 0xFF, 0xFF, 0x7F, 0 },
                new byte[] { 1, 0x80, 0x80, 0x40, 0xFF },
                bomb,
            };
            foreach (byte[] input in hostile)
            {
                Assert.IsNull(BasisCompressionShim.Decompress(input), input == null ? "null" : BitConverter.ToString(input, 0, Math.Min(8, input.Length)));
            }
            Assert.IsNull(BasisCompressionShim.Decompress(new byte[] { 0, 1 }, 5, 1));
            Assert.IsNull(BasisCompressionShim.Decompress(new byte[] { 0, 1 }, -1, 1));
            Assert.IsNull(BasisCompressionShim.Decompress(new byte[] { 0, 1 }, 1, int.MaxValue));

            System.Random random = new System.Random(99);
            byte[] valid = BasisCompressionShim.Compress(Repeating(3000));
            Assert.AreEqual(1, valid[0], "the fixture should take the LZ4 path");
            for (int i = 0; i < 20000; i++)
            {
                byte[] input = i % 2 == 0 ? Noise(random, random.Next(64)) : (byte[])valid.Clone();
                if (i % 2 == 1) input[random.Next(input.Length)] ^= (byte)(1 << random.Next(8));
                int count = random.Next(4) == 0 ? random.Next(input.Length + 1) : input.Length;
                byte[] result = null;
                Assert.DoesNotThrow(() => result = BasisCompressionShim.Decompress(input, 0, count));
                if (result != null) Assert.LessOrEqual(result.Length, BasisCompressionShim.MaxLength);
            }
        }

        [Test]
        public void EveryBox_CanCallTheShim()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                GameObject host = new GameObject(boxType.Name + " compression test");
                hosts.Add(host);
                CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
                box.classes = new Dictionary<string, int>();
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisCompressionShim)), boxType.Name);
                foreach (string method in new[] { nameof(BasisCompressionShim.Compress), nameof(BasisCompressionShim.Decompress) })
                {
                    Assert.IsTrue(box.CheckMethodAllowed(out MethodInfo _, typeof(BasisCompressionShim), method, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), "Byte[] " + method + "(Byte[])"), boxType.Name + " " + method);
                }
            }
        }
    }
}
