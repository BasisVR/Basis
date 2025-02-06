using Basis.Scripts.Drivers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Basis.Scripts.Networking.Recievers
{
    public partial class BasisAudioReceiverBase
    {
        [SerializeField]
        public BasisAudioDecoder decoder;
        [SerializeField]
        public AudioSource audioSource;
        [SerializeField]
        public BasisAudioAndVisemeDriver visemeDriver;
        [SerializeField]
        public BasisOpusSettings settings;
        [SerializeField]
        public RingBuffer RingBuffer;
        public int samplingFrequency;
        public int numChannels;
        public int SampleLength;
        public bool IsPlaying = false;
        public void OnDecoded()
        {
            OnDecoded(decoder.pcmBuffer, decoder.ActualPcmLength);
        }
        public void StopAudio()
        {
            IsPlaying = false;
        //    audioSource.enabled = false;
            audioSource.Stop();
        }
        public void StartAudio()
        {
            IsPlaying = true;
          //  audioSource.enabled = true;
            audioSource.Play();
        }
        public void OnDecoded(float[] pcm, int length)
        {
            RingBuffer.Add(pcm, length);
        }
        public int OnAudioFilterRead(float[] data, int channels)
        {
            // Ensure we have enough data for the required samples
            int length = data.Length;
            int frames = length / channels; // Number of audio frames

            // If no data in the buffer, clear the output
            if (RingBuffer.IsEmpty)
            {
                // No voice data, fill with silence
                Array.Fill(data, 0);
                return length;
            }

            // Retrieve the segment of audio data from the RingBuffer
            RingBuffer.Remove(frames, out float[] segment);

            // Apply the filter: multiply the existing data by the generated samples
            for (int i = 0; i < frames; i++)
            {
                float sample = segment[i]; // Single-channel sample from the RingBuffer

                for (int c = 0; c < channels; c++)
                {
                    int index = i * channels + c;

                    // Multiply existing data with the sample
                    data[index] *= sample;
                }
            }

            // Return the processed segment back to the buffer for reuse
            RingBuffer.BufferedReturn.Enqueue(segment);
            return length;
        }
    }
    public class RingBuffer
    {
        private readonly float[] buffer;
        private int head; // Points to the next position to write
        private int tail; // Points to the next position to read
        private int size; // Current data size in the buffer
        private readonly object bufferLock = new();
        public Queue<float[]> BufferedReturn = new Queue<float[]>();

        public RingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentException("Capacity must be greater than zero.");
            buffer = new float[capacity];
            head = 0;
            tail = 0;
            size = 0;
        }

        public int Capacity => buffer.Length;

        public bool IsEmpty => Interlocked.CompareExchange(ref size, 0, 0) == 0;

        public void Add(float[] segment, int length)
        {
            if (segment == null || segment.Length == 0)
            {
                throw new ArgumentNullException(nameof(segment));
            }
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Length must be a positive number.");
            }
            if (length > segment.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Length must be less than or equal to the segment's length.");
            }

            lock (bufferLock)
            {
                if (length > Capacity)
                {
                    throw new InvalidOperationException("The segment is too large to fit into the buffer.");
                }

                // Remove old data to make room for new data
                int availableSpace = Capacity - Interlocked.CompareExchange(ref size, 0, 0);
                if (length > availableSpace)
                {
                    int itemsToRemove = length - availableSpace;
                    Interlocked.Add(ref tail, itemsToRemove);
                    Interlocked.Add(ref size, -itemsToRemove);
                    tail %= Capacity;
                }

                // Add the new segment to the buffer
                int firstPart = Math.Min(length, Capacity - head); // Space till the end of the buffer
                Array.Copy(segment, 0, buffer, head, firstPart);   // Copy first part
                int remaining = length - firstPart;
                if (remaining > 0)
                {
                    Array.Copy(segment, firstPart, buffer, 0, remaining); // Copy wrap-around part
                }

                Interlocked.Add(ref head, length);
                head %= Capacity;
                Interlocked.Add(ref size, length);
            }
        }

        public void Remove(int segmentSize, out float[] segment)
        {
            if (segmentSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(segmentSize));

            // Try to reuse an array from the buffer pool
            lock (BufferedReturn)
            {
                if (!BufferedReturn.TryDequeue(out segment) || segment.Length != segmentSize)
                {
                    segment = new float[segmentSize];
                }
            }

            lock (bufferLock)
            {
                int currentSize = Interlocked.CompareExchange(ref size, 0, 0);
                int itemsToRemove = Math.Min(segmentSize, currentSize);

                // Remove items in bulk
                int firstPart = Math.Min(itemsToRemove, Capacity - tail); // Items till the end of the buffer
                Array.Copy(buffer, tail, segment, 0, firstPart);          // Copy first part
                int remaining = itemsToRemove - firstPart;
                if (remaining > 0)
                {
                    Array.Copy(buffer, 0, segment, firstPart, remaining); // Copy wrap-around part
                }

                Interlocked.Add(ref tail, itemsToRemove);
                tail %= Capacity;
                Interlocked.Add(ref size, -itemsToRemove);

                // Warn if fewer items were available than requested
                if (itemsToRemove < segmentSize)
                {
                    Console.WriteLine($"Warning: Requested {segmentSize} items, but only {itemsToRemove} are available.");
                }
            }
        }
    }
}
