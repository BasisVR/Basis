using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Threading;
namespace Basis.Capture
{
    public sealed class BasisCaptureFrameWriter : IDisposable
    {
        private readonly BlockingCollection<(byte[] data, int length)> queue = new BlockingCollection<(byte[], int)>(new ConcurrentQueue<(byte[], int)>());
        private readonly ConcurrentBag<byte[]> pool = new ConcurrentBag<byte[]>();
        private readonly Thread thread;
        private readonly FileStream file;
        private readonly MemoryStream chunk = new MemoryStream(1 << 22);
        private readonly int framesPerChunk, bytesPerChunk;
        private int chunkFirst, chunkCount, written;
        private volatile Exception failure;
        public long BytesWritten { get; private set; }
        public int FramesWritten => Volatile.Read(ref written);
        public Exception Failure => failure;
        public BasisCaptureFrameWriter(string path, int framesPerChunk = 90, int bytesPerChunk = 4 << 20)
        {
            this.framesPerChunk = Math.Max(1, framesPerChunk);
            this.bytesPerChunk = Math.Max(1 << 16, bytesPerChunk);
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            using (BinaryWriter header = new BinaryWriter(file, System.Text.Encoding.UTF8, true)) { header.Write(BasisCaptureFormat.FramesMagic); header.Write(BasisCaptureFormat.Version); }
            thread = new Thread(Run) { IsBackground = true, Name = "BasisCaptureWriter", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }
        public byte[] Rent(int size)
        {
            while (pool.TryTake(out byte[] b)) if (b.Length >= size) return b;
            return new byte[Math.Max(size, 1 << 16)];
        }
        public void Submit(byte[] data, int length)
        {
            if (failure != null) { pool.Add(data); return; }
            queue.Add((data, length));
        }
        private void Run()
        {
            try
            {
                foreach ((byte[] data, int length) in queue.GetConsumingEnumerable())
                {
                    chunk.Write(data, 0, length);
                    pool.Add(data);
                    chunkCount++;
                    Interlocked.Increment(ref written);
                    if (chunkCount >= framesPerChunk || chunk.Length >= bytesPerChunk) Flush();
                }
                Flush();
            }
            catch (Exception e)
            {
                failure = e;
            }
        }
        private void Flush()
        {
            if (chunkCount == 0) return;
            int raw = (int)chunk.Length;
            using MemoryStream compressed = new MemoryStream(raw / 2 + 64);
            using (DeflateStream deflate = new DeflateStream(compressed, CompressionLevel.Fastest, true)) deflate.Write(chunk.GetBuffer(), 0, raw);
            using (BinaryWriter w = new BinaryWriter(file, System.Text.Encoding.UTF8, true))
            {
                w.Write(chunkFirst);
                w.Write(chunkCount);
                w.Write(raw);
                w.Write((int)compressed.Length);
                w.Write(compressed.GetBuffer(), 0, (int)compressed.Length);
            }
            file.Flush();
            BytesWritten = file.Length;
            chunkFirst += chunkCount;
            chunkCount = 0;
            chunk.SetLength(0);
        }
        public void Dispose()
        {
            queue.CompleteAdding();
            thread.Join();
            file.Dispose();
            queue.Dispose();
        }
    }
}
