using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisHashShimTests
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

        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        static byte[] Filled(int length, byte value)
        {
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = value;
            return bytes;
        }

        [Test]
        public void Sha256_MatchesKnownDigests()
        {
            Assert.AreEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Hex(BasisHashShim.Sha256(new byte[0])));
            Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Hex(BasisHashShim.Sha256(Ascii("abc"))));
        }

        [Test]
        public void HmacSha256_MatchesRfc4231()
        {
            Assert.AreEqual("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843",
                Hex(BasisHashShim.HmacSha256(Ascii("Jefe"), Ascii("what do ya want for nothing?"))));
            Assert.AreEqual("60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54",
                Hex(BasisHashShim.HmacSha256(Filled(131, 0xaa), Ascii("Test Using Larger Than Block-Size Key - Hash Key First"))));
        }

        [Test]
        public void NullOrOversizedInput_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => BasisHashShim.Sha256(null));
            Assert.Throws<ArgumentNullException>(() => BasisHashShim.HmacSha256(null, new byte[1]));
            Assert.Throws<ArgumentNullException>(() => BasisHashShim.HmacSha256(new byte[1], null));
            Assert.Throws<ArgumentOutOfRangeException>(() => BasisHashShim.Sha256(new byte[BasisHashShim.MaxLength + 1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => BasisHashShim.HmacSha256(new byte[1], new byte[BasisHashShim.MaxLength + 1]));
        }

        [Test]
        public void EveryBox_CanCallTheShim()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                GameObject host = new GameObject(boxType.Name + " hash test");
                hosts.Add(host);
                CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
                box.classes = new Dictionary<string, int>();
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisHashShim)), boxType.Name);
                Assert.IsTrue(box.CheckMethodAllowed(out MethodInfo _, typeof(BasisHashShim), nameof(BasisHashShim.Sha256), Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), "Byte[] Sha256(Byte[])"), boxType.Name + " Sha256");
                Assert.IsTrue(box.CheckMethodAllowed(out MethodInfo _, typeof(BasisHashShim), nameof(BasisHashShim.HmacSha256), Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), "Byte[] HmacSha256(Byte[], Byte[])"), boxType.Name + " HmacSha256");
            }
        }
    }
}
