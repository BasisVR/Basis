using System;
using System.Collections.Generic;
using HVR.Basis.Comms.OSC;
using HVR.Basis.Comms.OSC.Lyuma;
using HVR.Osushi;
using NUnit.Framework;

namespace HVR.Basis.Comms.Tests
{
    public class BasisVrcftNativeFaceDecoderTests
    {
        private const float Tolerance = 2.5f / 255f;
        private const int JawOpenByte = 14 + 22;

        private static byte B(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255);

        private static byte[] Encode(float lx, float ly, float rx, float ry, float lo, float ro, Func<int, float> shape)
        {
            var blob = new byte[102];
            blob[0] = B(rx); blob[1] = B(-rx); blob[2] = B(ry); blob[3] = B(-ry); blob[8] = B(1 - ro);
            blob[4] = B(-lx); blob[5] = B(lx); blob[6] = B(ly); blob[7] = B(-ly); blob[9] = B(1 - lo);
            for (int i = 14; i < blob.Length; i++) blob[i] = B(shape(i - 14));
            return blob;
        }

        private static Dictionary<string, float> Decode(BasisVrcftNativeFaceDecoder decoder, byte[] blob)
        {
            var output = new List<SimpleOSC.OSCMessage>();
            decoder.Decode(blob, output);
            var values = new Dictionary<string, float>();
            foreach (var m in output) values[m.path.Substring("/avatar/parameters/FT/v2/".Length)] = (float)m.arguments[0];
            return values;
        }

        [Test]
        public void It_should_emit_every_advertised_parameter()
        {
            var decoder = new BasisVrcftNativeFaceDecoder(OsushiUtil.FaceTrackingParameterNames);
            var values = Decode(decoder, Encode(0, 0, 0, 0, 1, 1, _ => 0.5f));
            foreach (string name in OsushiUtil.FaceTrackingParameterNames)
            {
                Assert.That(values.ContainsKey(name), name);
            }
        }

        [Test]
        public void It_should_decode_gaze_and_lids_like_vrcft()
        {
            var decoder = new BasisVrcftNativeFaceDecoder(OsushiUtil.FaceTrackingParameterNames);
            var values = Decode(decoder, Encode(-0.42f, 0.31f, 0.18f, 0.27f, 0.83f, 0.64f, i => i == 3 ? 0.6f : 0f));
            Assert.That(values["EyeLeftX"], Is.EqualTo(-0.42f).Within(Tolerance));
            Assert.That(values["EyeRightX"], Is.EqualTo(0.18f).Within(Tolerance));
            Assert.That(values["EyeY"], Is.EqualTo(0.29f).Within(Tolerance));
            Assert.That(values["EyeLidLeft"], Is.EqualTo(0.83f * .75f + 0.6f * .25f).Within(Tolerance));
            Assert.That(values["EyeLidRight"], Is.EqualTo(0.64f * .75f).Within(Tolerance));
        }

        [Test]
        public void It_should_combine_shapes_like_vrcft()
        {
            var decoder = new BasisVrcftNativeFaceDecoder(OsushiUtil.FaceTrackingParameterNames);
            var values = Decode(decoder, Encode(0, 0, 0, 0, 1, 1, i => i == 23 ? 0.8f : i == 24 ? 0.2f : i == 56 ? 1f : i == 58 ? 0.5f : 0f));
            Assert.That(values["JawX"], Is.EqualTo(0.6f).Within(Tolerance));
            Assert.That(values["MouthSmileRight"], Is.EqualTo(0.9f).Within(Tolerance));
        }

        [Test]
        public void It_should_only_emit_changed_values()
        {
            var decoder = new BasisVrcftNativeFaceDecoder(OsushiUtil.FaceTrackingParameterNames);
            var blob = Encode(0, 0, 0, 0, 1, 1, _ => 0.25f);
            Decode(decoder, blob);
            Assert.That(Decode(decoder, blob), Is.Empty);

            blob[JawOpenByte] = 0;
            var changed = Decode(decoder, blob);
            Assert.That(changed.Keys, Is.EquivalentTo(new[] { "JawOpen" }));
        }

        [Test]
        public void It_should_accept_short_blobs()
        {
            var decoder = new BasisVrcftNativeFaceDecoder(OsushiUtil.FaceTrackingParameterNames);
            var message = new SimpleOSC.OSCMessage { path = BasisVrcftNativeFaceDecoder.Address, arguments = new object[] { new byte[20] } };
            Assert.That(BasisVrcftNativeFaceDecoder.TryGetBlob(message, out byte[] blob));
            Assert.DoesNotThrow(() => Decode(decoder, blob));
            Assert.That(BasisVrcftNativeFaceDecoder.TryGetBlob(new SimpleOSC.OSCMessage { path = BasisVrcftNativeFaceDecoder.Address, arguments = new object[] { new byte[4] } }, out _), Is.False);
        }
    }
}
