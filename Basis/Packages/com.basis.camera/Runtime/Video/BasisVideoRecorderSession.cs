using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
namespace Basis
{
    public sealed class BasisVideoRecorderSession : IBasisFrameRecorderSession
    {
        private static readonly int EncodeThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
        private sealed class EncodeSlot
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public byte[] Rgba, Jpeg;
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
        private readonly int width, height, quality, nominalFrameRate;
        private readonly string temporaryPath;
        private readonly BasisRecordingAudioBuffer audio;
        private readonly double audioStartTime;
        private long audioSkipBytes;
        private byte[] audioScratch;
        private Thread worker;
        private int framesQueued, framesEncoded;
        private volatile bool completeAdding, finished;
        private volatile string failureMessage;
        public string FinalPath { get; }
        public int FramesQueued => Volatile.Read(ref framesQueued);
        public int FramesEncoded => Volatile.Read(ref framesEncoded);
        public bool IsFinished => finished;
        public string FailureMessage => failureMessage;
        public BasisVideoRecorderSession(int width, int height, int quality, int frameRate, string finalPath, BasisRecordingAudioBuffer audio = null, double audioStartTime = 0)
        {
            this.width = width;
            this.height = height;
            this.quality = quality;
            nominalFrameRate = frameRate;
            FinalPath = finalPath;
            temporaryPath = finalPath + ".tmp";
            this.audio = audio;
            this.audioStartTime = audioStartTime;
        }
        public bool Start()
        {
            try
            {
                worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BasisVideoEncoder" };
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
                var writer = new BasisMjpegAviWriter(file, width, height, nominalFrameRate, audio != null ? audio.SampleRate : 0, audio != null ? BasisRecordingAudioBuffer.Channels : 0);
                if (audio != null) audioScratch = new byte[audio.SampleRate * BasisRecordingAudioBuffer.BytesPerSampleFrame];

                double firstTimestamp = 0, lastTimestamp = 0;

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

                    if (writer.FrameCount == 0)
                    {
                        firstTimestamp = ready.Timestamp;
                        if (audio != null) audioSkipBytes = (long)Math.Max(0, Math.Round((ready.Timestamp - audioStartTime) * audio.SampleRate)) * BasisRecordingAudioBuffer.BytesPerSampleFrame;
                    }
                    lastTimestamp = ready.Timestamp;

                    writer.WriteFrame(ready.Jpeg, ready.Jpeg.Length);
                    Interlocked.Increment(ref framesEncoded);
                    RecycleSlot(ready);
                    DrainAudio(writer);
                }

                DrainAudio(writer);

                if (failureMessage == null)
                {
                    double? measuredFps = writer.FrameCount >= 2 && lastTimestamp > firstTimestamp ? (writer.FrameCount - 1) / (lastTimestamp - firstTimestamp) : (double?)null;
                    writer.Finish(measuredFps);
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
            slot.Rgba = frame.Rgba;
            slot.Timestamp = frame.Timestamp;
            slot.Jpeg = null;
            slot.Error = null;
            slot.Done.Reset();
            ThreadPool.QueueUserWorkItem(EncodeSlotWork, slot);
            return slot;
        }
        private void EncodeSlotWork(object state)
        {
            var slot = (EncodeSlot)state;
            try
            {
                slot.Jpeg = ImageConversion.EncodeArrayToJPG(slot.Rgba, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height, 0, quality);
                if (slot.Jpeg == null || slot.Jpeg.Length == 0) slot.Error = "JPEG encode returned nothing.";
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
        private void RecycleSlot(EncodeSlot slot)
        {
            bufferPool.Push(slot.Rgba);
            slot.Rgba = null;
            slot.Jpeg = null;
            slotPool.Push(slot);
        }
        private void DrainAudio(BasisMjpegAviWriter writer)
        {
            if (audio == null) return;

            int bytes;
            while ((bytes = audio.Read(audioScratch)) > 0)
            {
                int offset = 0;
                if (audioSkipBytes > 0)
                {
                    int skipped = (int)Math.Min(audioSkipBytes, bytes);
                    audioSkipBytes -= skipped;
                    offset = skipped;
                }
                if (bytes - offset > 0) writer.WriteAudio(audioScratch, offset, bytes - offset);
            }
        }
    }
}
