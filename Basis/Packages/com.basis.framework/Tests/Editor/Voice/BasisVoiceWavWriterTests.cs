using System;
using System.IO;
using Basis.Scripts.Networking.VoiceRecording;
using NUnit.Framework;

namespace Basis.Tests.Voice
{
    /// <summary>
    /// The wav writer moved its disk IO onto a background thread; these pin the contract that
    /// survives that: Dispose drains everything queued before patching the header, the header
    /// describes exactly the samples written, and the 16-bit payload matches the input ordering.
    /// </summary>
    public class BasisVoiceWavWriterTests
    {
        private string path;

        [SetUp]
        public void SetUp()
        {
            path = Path.Combine(Path.GetTempPath(), $"BasisWavWriterTest_{Guid.NewGuid():N}.wav");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(path)) File.Delete(path);
        }

        [Test]
        public void Write_ThenDispose_ProducesAValidHeaderAndEverySample()
        {
            const int sampleRate = 48000;
            float[] first = { 0f, 0.5f, -0.5f, 1f };
            float[] second = { -1f, 0.25f, 2f, -2f };

            var writer = new BasisVoiceWavWriter(path, sampleRate);
            writer.Write(first, first.Length);
            writer.Write(second, second.Length);
            writer.Dispose();

            byte[] bytes = File.ReadAllBytes(path);
            int sampleCount = first.Length + second.Length;
            Assert.AreEqual(44 + sampleCount * 2, bytes.Length, "file length");

            Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.AreEqual(36 + sampleCount * 2, BitConverter.ToInt32(bytes, 4), "riff size");
            Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.AreEqual("fmt ", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
            Assert.AreEqual(16, BitConverter.ToInt32(bytes, 16), "fmt chunk size");
            Assert.AreEqual(1, BitConverter.ToInt16(bytes, 20), "pcm format");
            Assert.AreEqual(1, BitConverter.ToInt16(bytes, 22), "mono");
            Assert.AreEqual(sampleRate, BitConverter.ToInt32(bytes, 24), "sample rate");
            Assert.AreEqual(sampleRate * 2, BitConverter.ToInt32(bytes, 28), "byte rate");
            Assert.AreEqual(2, BitConverter.ToInt16(bytes, 32), "block align");
            Assert.AreEqual(16, BitConverter.ToInt16(bytes, 34), "bits per sample");
            Assert.AreEqual("data", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));
            Assert.AreEqual(sampleCount * 2, BitConverter.ToInt32(bytes, 40), "data size");

            short[] expected = { 0, 16383, -16383, 32767, -32767, 8191, 32767, -32767 };
            for (int i = 0; i < expected.Length; i++)
            {
                short actual = BitConverter.ToInt16(bytes, 44 + i * 2);
                Assert.That(actual, Is.EqualTo(expected[i]).Within(1), $"sample {i}");
            }
        }

        [Test]
        public void Dispose_DrainsALargeBacklog_BeforePatchingTheHeader()
        {
            const int chunk = 960;
            const int chunks = 200;
            float[] samples = new float[chunk];
            for (int i = 0; i < chunk; i++) samples[i] = (i % 100) / 100f;

            var writer = new BasisVoiceWavWriter(path, 48000);
            for (int i = 0; i < chunks; i++) writer.Write(samples, chunk);
            writer.Dispose();

            byte[] bytes = File.ReadAllBytes(path);
            Assert.AreEqual(44 + chunk * chunks * 2, bytes.Length, "every queued chunk must land before the header patch");
            Assert.AreEqual(chunk * chunks * 2, BitConverter.ToInt32(bytes, 40), "data size");
        }

        [Test]
        public void WriteAfterDispose_IsIgnored()
        {
            var writer = new BasisVoiceWavWriter(path, 48000);
            writer.Write(new float[] { 0.1f, 0.2f }, 2);
            writer.Dispose();
            writer.Write(new float[] { 0.3f }, 1);

            byte[] bytes = File.ReadAllBytes(path);
            Assert.AreEqual(44 + 4, bytes.Length);
        }
    }
}
