using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;

namespace OpenLipSync.Inference
{
    /// <summary>
    /// Per-player streaming inference state for a cached-TCN viseme model.
    ///
    /// The old path re-ran a 150-frame mel window through the whole network on every 10 ms
    /// hop and then used only the last frame -- roughly 7.5 GMAC/s per player, which is why
    /// OpenLipSync needed a slot cap at all. A cached model instead carries each dilated
    /// conv's history in a small ring, so one hop costs exactly one frame of work
    /// (~91 MMAC/s per player, ~83x less).
    ///
    /// Caches are ping-ponged between two IoBindings: binding A reads caches[0] and writes
    /// caches[1], binding B does the reverse. Alternating them means no buffer is ever
    /// copied and nothing is allocated per frame.
    /// </summary>
    internal sealed class StreamingSession : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly int _nCaches;
        private readonly int _nMels;
        private readonly int _nVisemes;

        private readonly OrtMemoryInfo _memInfo;
        private readonly float[] _melBuffer;
        private readonly float[] _logitBuffer;
        private readonly OrtValue _melValue;
        private readonly OrtValue _logitValue;

        // caches[0] and caches[1] alternate as (input, output) each frame.
        private readonly float[][][] _cacheBuffers = new float[2][][];
        private readonly OrtValue[][] _cacheValues = new OrtValue[2][];
        private readonly OrtIoBinding[] _bindings = new OrtIoBinding[2];
        private readonly RunOptions _runOptions = new RunOptions();
        private int _phase;
        private bool _disposed;

        public int MelBands => _nMels;
        public int VisemeCount => _nVisemes;

        /// <summary>True if this ONNX graph exposes cache_* inputs, i.e. is a streaming model.</summary>
        public static bool IsStreamingModel(InferenceSession session)
        {
            return session.InputMetadata.Keys.Any(k => k.StartsWith("cache_", StringComparison.Ordinal));
        }

        public StreamingSession(InferenceSession session, int nMels, int nVisemes)
        {
            _session = session;
            _nMels = nMels;
            _nVisemes = nVisemes;
            _nCaches = session.InputMetadata.Keys.Count(k => k.StartsWith("cache_", StringComparison.Ordinal));

            _memInfo = OrtMemoryInfo.DefaultInstance;

            _melBuffer = new float[nMels];
            _logitBuffer = new float[nVisemes];
            _melValue = OrtValue.CreateTensorValueFromMemory(_memInfo, _melBuffer.AsMemory(), new long[] { 1, 1, nMels });
            _logitValue = OrtValue.CreateTensorValueFromMemory(_memInfo, _logitBuffer.AsMemory(), new long[] { 1, 1, nVisemes });

            // Cache shapes come from the graph itself, so C# can never disagree with the model.
            var shapes = new long[_nCaches][];
            for (int i = 0; i < _nCaches; i++)
            {
                var meta = session.InputMetadata[$"cache_{i}"];
                shapes[i] = meta.Dimensions.Select(d => (long)d).ToArray();
                if (shapes[i].Any(d => d <= 0))
                    throw new InvalidOperationException($"cache_{i} has a dynamic dimension; streaming caches must be fixed-size");
            }

            for (int p = 0; p < 2; p++)
            {
                _cacheBuffers[p] = new float[_nCaches][];
                _cacheValues[p] = new OrtValue[_nCaches];
                for (int i = 0; i < _nCaches; i++)
                {
                    int len = 1;
                    foreach (var d in shapes[i]) len *= (int)d;
                    _cacheBuffers[p][i] = new float[len];
                    _cacheValues[p][i] = OrtValue.CreateTensorValueFromMemory(_memInfo, _cacheBuffers[p][i].AsMemory(), shapes[i]);
                }
            }

            for (int p = 0; p < 2; p++)
            {
                var b = _session.CreateIoBinding();
                b.BindInput("audio_features", _melValue);
                b.BindOutput("viseme_logits", _logitValue);
                for (int i = 0; i < _nCaches; i++)
                {
                    b.BindInput($"cache_{i}", _cacheValues[p][i]);          // read from phase p
                    b.BindOutput($"cache_out_{i}", _cacheValues[1 - p][i]); // write to phase 1-p
                }
                _bindings[p] = b;
            }
        }

        /// <summary>
        /// Advance one 10 ms hop. <paramref name="melFrame"/> is RAW dB mel -- the model
        /// applies CMVN internally, so do not normalize here. Returns viseme probabilities.
        /// </summary>
        public void Step(ReadOnlySpan<float> melFrame, Span<float> probabilities, bool multiLabel)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StreamingSession));

            melFrame.Slice(0, _nMels).CopyTo(_melBuffer);
            // _runOptions is held for the lifetime of the context: allocating one per frame
            // would be 100 allocations/second per player, which defeats the whole point of
            // the ping-ponged bindings below.
            _session.RunWithBinding(_runOptions, _bindings[_phase]);
            _phase ^= 1;   // last run's outputs become next run's inputs -- no copy

            if (multiLabel)
            {
                for (int i = 0; i < _nVisemes; i++)
                {
                    float x = Math.Clamp(_logitBuffer[i], -50f, 50f);
                    probabilities[i] = 1f / (1f + MathF.Exp(-x));
                }
            }
            else
            {
                float max = float.MinValue;
                for (int i = 0; i < _nVisemes; i++)
                    if (_logitBuffer[i] > max) max = _logitBuffer[i];
                float sum = 0f;
                for (int i = 0; i < _nVisemes; i++)
                {
                    float e = MathF.Exp(_logitBuffer[i] - max);
                    probabilities[i] = e;
                    sum += e;
                }
                float inv = sum > 0f ? 1f / sum : 0f;
                for (int i = 0; i < _nVisemes; i++) probabilities[i] *= inv;
            }
        }

        /// <summary>Clear all conv history. Call when a player's audio stream restarts.</summary>
        public void Reset()
        {
            for (int p = 0; p < 2; p++)
                for (int i = 0; i < _nCaches; i++)
                    Array.Clear(_cacheBuffers[p][i], 0, _cacheBuffers[p][i].Length);
            _phase = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var b in _bindings) b?.Dispose();
            for (int p = 0; p < 2; p++)
                if (_cacheValues[p] != null)
                    foreach (var v in _cacheValues[p]) v?.Dispose();
            _melValue?.Dispose();
            _logitValue?.Dispose();
            _runOptions?.Dispose();
        }
    }
}
