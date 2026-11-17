using System;
using System.Diagnostics;
using System.IO;
namespace Basis.Capture
{
    public sealed class BasisCaptureMicWriter : IDisposable
    {
        private readonly object gate = new object();
        private readonly FileStream file;
        private readonly BinaryWriter writer;
        private readonly Stopwatch clock;
        private readonly int sampleRate;
        private long samples;
        private bool closed;
        public long StartServerTicks { get; }
        public int SampleRate => sampleRate;
        public long Samples { get { lock (gate) return samples; } }
        public BasisCaptureMicWriter(string path, int sampleRate, long startServerTicks)
        {
            this.sampleRate = sampleRate;
            StartServerTicks = startServerTicks;
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            writer = new BinaryWriter(file);
            writer.Write(0x46464952); writer.Write(0); writer.Write(0x45564157); writer.Write(0x20746d66); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write(0x61746164); writer.Write(0);
            clock = Stopwatch.StartNew();
            BasisLocalMicrophoneDriver.OnHasAudio += OnFrame;
            BasisLocalMicrophoneDriver.OnHasSilence += OnFrame;
        }
        private void OnFrame()
        {
            float[] frame = BasisLocalMicrophoneDriver.processBufferArray;
            if (frame == null) return;
            int count = Math.Min(BasisLocalMicrophoneDriver.ProcessFrameSize, frame.Length);
            lock (gate)
            {
                if (closed) return;
                long expected = (long)(clock.Elapsed.TotalSeconds * sampleRate) - count;
                long gap = expected - samples;
                if (gap > sampleRate / 10) { for (long i = 0; i < gap; i++) writer.Write((short)0); samples += gap; }
                for (int i = 0; i < count; i++) { float v = frame[i]; writer.Write((short)(v >= 1f ? 32767 : v <= -1f ? -32767 : (int)(v * 32767f))); }
                samples += count;
            }
        }
        public void Dispose()
        {
            BasisLocalMicrophoneDriver.OnHasAudio -= OnFrame;
            BasisLocalMicrophoneDriver.OnHasSilence -= OnFrame;
            lock (gate)
            {
                if (closed) return;
                closed = true;
                long data = samples * 2;
                writer.Flush();
                file.Position = 4; writer.Write((int)Math.Min(int.MaxValue, 36 + data));
                file.Position = 40; writer.Write((int)Math.Min(int.MaxValue, data));
                writer.Dispose();
            }
        }
    }
}
