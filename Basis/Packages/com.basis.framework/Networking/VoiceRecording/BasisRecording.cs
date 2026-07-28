using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Networking.VoiceRecording
{
    /// <summary>
    /// Selects which outputs a recording produces. All are additive and independent.
    /// </summary>
    public struct BasisRecordingOptions
    {
        /// <summary>Raise <see cref="BasisVoiceRecording.OnSamples"/> as decoded PCM arrives.</summary>
        public bool LiveSamples;
        /// <summary>Accumulate samples so <see cref="BasisRecording.ToAudioClip"/> can build a clip.</summary>
        public bool BuildClip;
        /// <summary>Stream the recording to a .wav file on disk (trusted callers only).</summary>
        public bool SaveWav;

        public static BasisRecordingOptions Default => new BasisRecordingOptions
        {
            LiveSamples = true,
            BuildClip = true,
            SaveWav = false,
        };
    }

    /// <summary>
    /// A single consented capture of one remote player's decoded voice. Samples are
    /// appended on the main thread from <see cref="BasisVoiceRecording.Tick"/>. Depending
    /// on <see cref="BasisRecordingOptions"/> it accumulates an in-memory clip and/or
    /// streams to a .wav file.
    /// </summary>
    public sealed class BasisRecording
    {
        public string RecordeeUuid { get; }
        public ushort RecordeePlayerId { get; internal set; }
        public int SampleRate { get; }
        public bool IsFinalized { get; private set; }
        public string WavPath => _wav?.Path;
        public int SampleCount => _sampleCount;
        public float DurationSeconds => SampleRate > 0 ? (float)_sampleCount / SampleRate : 0f;

        private const int MaxSamples = 48000 * 60 * 10;
        private readonly BasisRecordingOptions _options;
        private readonly List<float[]> _chunks;
        private int _sampleCount;
        private int _appendedTotal;
        private BasisVoiceWavWriter _wav;

        internal BasisRecording(string uuid, ushort playerId, int sampleRate, BasisRecordingOptions options)
        {
            RecordeeUuid = uuid;
            RecordeePlayerId = playerId;
            SampleRate = sampleRate;
            _options = options;
            if (options.BuildClip)
            {
                _chunks = new List<float[]>();
            }
            if (options.SaveWav)
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "VoiceRecordings");
                string safeUuid = string.IsNullOrEmpty(uuid) ? "unknown" : uuid.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
                string file = System.IO.Path.Combine(dir, $"{safeUuid}_{playerId}_{Time.frameCount}.wav");
                try
                {
                    _wav = new BasisVoiceWavWriter(file, sampleRate);
                }
                catch (System.Exception ex)
                {
                    BasisDebug.LogError($"[BasisRecording] Could not open wav for {uuid}: {ex}");
                    _wav = null;
                }
            }
        }

        internal void Append(float[] buffer, int count)
        {
            if (IsFinalized || count <= 0 || _appendedTotal >= MaxSamples)
            {
                return;
            }
            if (_appendedTotal + count > MaxSamples)
            {
                count = MaxSamples - _appendedTotal;
            }
            _appendedTotal += count;
            if (_chunks != null)
            {
                float[] copy = new float[count];
                System.Array.Copy(buffer, 0, copy, 0, count);
                _chunks.Add(copy);
                _sampleCount += count;
            }
            _wav?.Write(buffer, count);
        }

        /// <summary>
        /// Builds a Unity <see cref="AudioClip"/> from the accumulated samples. Requires
        /// <see cref="BasisRecordingOptions.BuildClip"/>. Returns null if nothing was captured.
        /// </summary>
        public AudioClip ToAudioClip(string clipName = null)
        {
            if (_chunks == null || _sampleCount <= 0)
            {
                return null;
            }
            float[] all = new float[_sampleCount];
            int offset = 0;
            foreach (float[] chunk in _chunks)
            {
                System.Array.Copy(chunk, 0, all, offset, chunk.Length);
                offset += chunk.Length;
            }
            AudioClip clip = AudioClip.Create(clipName ?? $"VoiceRecording_{RecordeePlayerId}", _sampleCount, 1, SampleRate, false);
            clip.SetData(all, 0);
            return clip;
        }

        internal void FinalizeRecording()
        {
            if (IsFinalized)
            {
                return;
            }
            IsFinalized = true;
            _wav?.Dispose();
        }
    }

    /// <summary>
    /// Opaque handle returned by <see cref="BasisVoiceRecording.StartRecording"/>; pass it
    /// back to stop and to reach the captured <see cref="BasisRecording"/>.
    /// </summary>
    public sealed class BasisRecordingHandle
    {
        public ushort PlayerId { get; }
        public BasisRecording Recording { get; }

        internal BasisRecordingHandle(ushort playerId, BasisRecording recording)
        {
            PlayerId = playerId;
            Recording = recording;
        }
    }
}
