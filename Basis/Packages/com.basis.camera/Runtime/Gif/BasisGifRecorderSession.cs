using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Collections;
namespace Basis
{
    public sealed class BasisGifRecorderSession : IBasisFrameRecorderSession
    {
        private static readonly int EncodeThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
        private sealed class EncodeSlot
        {
            public readonly BasisGifQuantizer Quantizer = new BasisGifQuantizer();
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public byte[] Indices, Rgba;
            public double Timestamp;
            public volatile string Error;
        }
        private struct QueuedFrame
        {
            public byte[] Rgba;
            public double Timestamp;
        }
        private readonly ConcurrentStack<EncodeSlot> slotPool = new ConcurrentStack<EncodeSlot>();
        private readonly ConcurrentQueue<QueuedFrame> pendingFrames = new ConcurrentQueue<QueuedFrame>();
        private readonly ConcurrentStack<byte[]> bufferPool = new ConcurrentStack<byte[]>();
        private readonly AutoResetEvent frameReady = new AutoResetEvent(false);
        private readonly int width, height, fallbackDelayCentiseconds;
        private readonly bool loop, dither;
        private readonly string temporaryPath;
        private Thread worker;
        private int framesQueued, framesEncoded;
        private volatile bool completeAdding, finished;
        private volatile string failureMessage;
        public string FinalPath { get; }
        public int FramesQueued => Volatile.Read(ref framesQueued);
        public int FramesEncoded => Volatile.Read(ref framesEncoded);
        public bool IsFinished => finished;
        public string FailureMessage => failureMessage;
        public BasisGifRecorderSession(int width, int height, bool loop, bool dither, int frameRate, string finalPath)
        {
            this.width = width;
            this.height = height;
            this.loop = loop;
            this.dither = dither;
            fallbackDelayCentiseconds = Math.Max(BasisGifWriter.MinDelayCentiseconds, (int)Math.Round(100.0 / Math.Max(1, frameRate)));
            FinalPath = finalPath;
            temporaryPath = finalPath + ".tmp";
        }
        public bool Start()
        {
            try
            {
                worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BasisGifEncoder" };
                worker.Start();
                return true;
            }
            catch (Exception e)
            {
                failureMessage = $"Could not start the encode worker ({e.GetType().Name}: {e.Message})";
                finished = true;
                return false;
            }
        }
        public bool TryAddFrame(NativeArray<byte> rgba, double timestamp)
        {
            if (completeAdding || failureMessage != null || rgba.Length != width * height * 4) return false;

            if (!bufferPool.TryPop(out byte[] buffer) || buffer.Length != rgba.Length) buffer = new byte[rgba.Length];
            rgba.CopyTo(buffer);

            pendingFrames.Enqueue(new QueuedFrame { Rgba = buffer, Timestamp = timestamp });
            Interlocked.Increment(ref framesQueued);
            frameReady.Set();
            return true;
        }
        public void CompleteAdding()
        {
            completeAdding = true;
            frameReady.Set();
        }
        private void WorkerLoop()
        {
            FileStream file = null;
            bool success = false;
            var inFlight = new Queue<EncodeSlot>();
            try
            {
                file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var writer = new BasisGifWriter(file, width, height, loop);
                EncodeSlot held = null;
                double delayCarry = 0;

                while (failureMessage == null)
                {
                    bool dispatched = false;
                    while (inFlight.Count < EncodeThreads && pendingFrames.TryDequeue(out QueuedFrame frame))
                    {
                        Interlocked.Decrement(ref framesQueued);
                        inFlight.Enqueue(DispatchEncode(frame));
                        dispatched = true;
                    }

                    if (inFlight.Count == 0)
                    {
                        if (completeAdding && pendingFrames.IsEmpty) break;
                        if (!dispatched) frameReady.WaitOne(100);
                        continue;
                    }

                    EncodeSlot ready = inFlight.Dequeue();
                    ready.Done.Wait();
                    if (ready.Error != null)
                    {
                        failureMessage = ready.Error;
                        break;
                    }

                    if (held != null) WriteSlot(writer, held, NextDelay(ready.Timestamp - held.Timestamp, ref delayCarry));
                    held = ready;
                }

                if (failureMessage == null)
                {
                    if (held != null) WriteSlot(writer, held, fallbackDelayCentiseconds);

                    writer.Finish();
                    file.Flush();
                    file.Dispose();
                    file = null;

                    if (writer.FrameCount == 0)
                    {
                        failureMessage = "No frames were captured.";
                    }
                    else
                    {
                        File.Move(temporaryPath, FinalPath);
                        success = true;
                    }
                }
            }
            catch (Exception e)
            {
                failureMessage = $"{e.GetType().Name}: {e.Message}";
            }
            finally
            {
                while (inFlight.Count > 0) inFlight.Dequeue().Done.Wait();
                try { file?.Dispose(); } catch (Exception) { }
                if (!success)
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (Exception) { }
                }
                while (pendingFrames.TryDequeue(out _)) { }
                finished = true;
            }
        }
        private EncodeSlot DispatchEncode(QueuedFrame frame)
        {
            if (!slotPool.TryPop(out EncodeSlot slot)) slot = new EncodeSlot();
            if (slot.Indices == null || slot.Indices.Length != width * height) slot.Indices = new byte[width * height];
            slot.Rgba = frame.Rgba;
            slot.Timestamp = frame.Timestamp;
            slot.Error = null;
            slot.Done.Reset();
            ThreadPool.QueueUserWorkItem(QuantizeSlot, slot);
            return slot;
        }
        private void QuantizeSlot(object state)
        {
            var slot = (EncodeSlot)state;
            try
            {
                slot.Quantizer.Quantize(slot.Rgba, width, height, dither, slot.Indices);
            }
            catch (Exception e)
            {
                slot.Error = $"{e.GetType().Name}: {e.Message}";
            }
            finally
            {
                slot.Done.Set();
            }
        }
        private void WriteSlot(BasisGifWriter writer, EncodeSlot slot, int delayCentiseconds)
        {
            writer.WriteFrame(slot.Indices, slot.Quantizer.PaletteRgb, slot.Quantizer.PaletteCount, delayCentiseconds);
            Interlocked.Increment(ref framesEncoded);
            bufferPool.Push(slot.Rgba);
            slot.Rgba = null;
            slotPool.Push(slot);
        }
        private static int NextDelay(double gapSeconds, ref double carry)
        {
            double target = Math.Max(0, gapSeconds) + carry;
            int centiseconds = Math.Max(BasisGifWriter.MinDelayCentiseconds, (int)Math.Round(target * 100.0));
            carry = target - centiseconds / 100.0;
            return centiseconds;
        }
    }
}
