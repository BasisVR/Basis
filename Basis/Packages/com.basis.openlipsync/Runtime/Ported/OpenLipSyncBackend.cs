using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json;
using OpenLipSync.Inference.Audio;
using OpenLipSync.Inference.OVRCompat;
using UnityEngine;

namespace OpenLipSync.Inference
{
    public sealed class OpenLipSyncBackend : IDisposable
    {
        private readonly object _lock = new object();
        private readonly ConcurrentDictionary<uint, AudioContext> _contexts = new ConcurrentDictionary<uint, AudioContext>();
        private InferenceSession _onnxSession;
        private ModelConfig _modelConfig;
        private AudioProcessingConfig _audioConfig;
        private int _nextContextId = 1;
        private int _inputSampleRate;
        private bool _initialized;
        private bool _disposed;

        private bool _isMultiLabel;
        private bool _isStreaming;
        private bool _legacyNormalizeWindow;
        private int _numVisemes = Frame.VisemeCount;

        public bool IsStreaming => _isStreaming;

        public bool IsInitialized => _initialized;
        public int SampleRate => _inputSampleRate;
        public string LastError { get; private set; }

        // Debug counters
        public int DebugMelFramesProduced { get; private set; }
        public int DebugInferenceRuns { get; private set; }
        public float DebugLastInferenceMax { get; private set; }
        public string DebugPipelineStatus { get; private set; } = "idle";
        public string DebugInferenceDetail { get; private set; } = "";

        public Result InitializeFromBytes(int sampleRate, byte[] modelBytes, string configJson)
        {
            if (_disposed) return Result.Unknown;
            if (_initialized) return Result.Success;

            try
            {
                _inputSampleRate = sampleRate;

                if (modelBytes == null || modelBytes.Length == 0)
                {
                    LastError = "Model bytes are null or empty";
                    return Result.Unknown;
                }

                if (!string.IsNullOrEmpty(configJson))
                {
                    _modelConfig = JsonConvert.DeserializeObject<ModelConfig>(configJson);

                    if (_modelConfig != null)
                    {
                        _audioConfig = AudioProcessingConfig.FromModelConfig(_modelConfig);
                        // Current models declare this under "model"; older ones under "training".
                        _isMultiLabel = _modelConfig.Model?.MultiLabel
                                        ?? _modelConfig.Training?.MultiLabel
                                        ?? false;
                        _numVisemes = _modelConfig.Model?.NumVisemes ?? Frame.VisemeCount;

                        // The C# front-end emits RAW dB mel. A model that declares it wants
                        // normalized input is telling us its input contract does not match
                        // what we produce -- which is exactly the bug that cost the previous
                        // model 12.7 points of frame accuracy (trained on per-utterance
                        // normalized mel, fed raw dB, ~36 sigma off-distribution).
                        //
                        // We cannot reproduce per-utterance stats in a live stream (they need
                        // the whole utterance), but normalizing each mel WINDOW by its own
                        // mean/std is causal and recovers most of the loss: measured on
                        // dev-clean, 60.0% -> 69.5% frame accuracy with the shipped weights.
                        // So honour the contract as best we can instead of refusing to run.
                        string norm = _modelConfig.Audio?.Normalization;
                        _legacyNormalizeWindow = !string.IsNullOrEmpty(norm)
                                                 && !norm.StartsWith("none", StringComparison.OrdinalIgnoreCase)
                                                 && !norm.Equals("baked_into_graph", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        LastError = "Failed to parse config JSON";
                    }
                }
                else
                {
                    LastError = "No config JSON provided";
                }

                var sessionOptions = new SessionOptions();
                sessionOptions.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                sessionOptions.InterOpNumThreads = 1;
                sessionOptions.IntraOpNumThreads = 1;

                _onnxSession = new InferenceSession(modelBytes, sessionOptions);

                // A streaming graph exposes cache_* inputs and costs one frame of work per
                // 10 ms hop. A legacy graph takes a whole mel window and is re-run in full on
                // every hop -- ~83x more compute per player. We support both so an older
                // model.onnx keeps working, but the shipped model should be streaming.
                _isStreaming = StreamingSession.IsStreamingModel(_onnxSession);
                Debug.Log(_isStreaming
                    ? "[OpenLipSync] streaming model detected (cached convolutions, ~1 frame of work per hop)"
                    : "[OpenLipSync] legacy windowed model detected - full window re-run per hop. Consider re-exporting as streaming.");

                _initialized = true;
                return Result.Success;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OpenLipSync] Initialization failed: {ex.Message}");
                LastError = $"Initialization failed: {ex.Message}";
                return Result.CannotCreateContext;
            }
        }

        public void Shutdown()
        {
            if (!_initialized) return;

            lock (_lock)
            {
                foreach (var context in _contexts.Values)
                {
                    context.Dispose();
                }
                _contexts.Clear();

                _onnxSession?.Dispose();
                _onnxSession = null;

                _initialized = false;
            }
        }

        public Result CreateContext(ref uint context)
        {
            if (!_initialized) return Result.Unknown;

            if (_audioConfig == null)
            {
                LastError = "Audio config not loaded";
                return Result.CannotCreateContext;
            }

            try
            {
                // Each player needs its OWN convolution cache state. The InferenceSession is
                // shared and thread-safe; only the caches and IoBindings are per-context.
                StreamingSession streaming = _isStreaming
                    ? new StreamingSession(_onnxSession, _audioConfig.NMels, _numVisemes)
                    : null;

                var audioContext = new AudioContext(_inputSampleRate, _audioConfig, _numVisemes,
                                                    streaming, _isMultiLabel);
                uint id = (uint)Interlocked.Increment(ref _nextContextId);
                context = id;
                _contexts[context] = audioContext;

                return Result.Success;
            }
            catch (Exception ex)
            {
                LastError = $"Failed to create audio context: {ex.Message}";
                return Result.CannotCreateContext;
            }
        }

        public Result DestroyContext(uint context)
        {
            if (!_initialized) return Result.Unknown;

            if (_contexts.TryRemove(context, out var audioContext))
            {
                audioContext.Dispose();
                return Result.Success;
            }

            return Result.InvalidParam;
        }

        public Result ResetContext(uint context)
        {
            if (!_initialized) return Result.Unknown;

            if (_contexts.TryGetValue(context, out var audioContext))
            {
                audioContext.Reset();
                return Result.Success;
            }

            return Result.InvalidParam;
        }

        public Result SendSignal(uint context, Signals signal, int arg1)
        {
            if (!_initialized) return Result.Unknown;

            if (_contexts.TryGetValue(context, out var audioContext))
            {
                return audioContext.SendSignal(signal, arg1);
            }

            return Result.InvalidParam;
        }

        public Result ProcessFrameFloat(uint context, ReadOnlySpan<float> audio, bool stereo, ref Frame frame)
        {
            return ProcessFrameFloatInternal(context, audio, stereo, ref frame);
        }

        public Result ProcessFrameFloat(uint context, float[] audio, bool stereo, ref Frame frame)
        {
            return ProcessFrameFloatInternal(context, audio, stereo, ref frame);
        }

        private Result ProcessFrameFloatInternal(uint context, ReadOnlySpan<float> audio, bool stereo, ref Frame frame)
        {
            if (!_initialized || _onnxSession == null)
            {
                LastError = "Backend not initialized or model not loaded";
                return Result.Unknown;
            }

            if (!_contexts.TryGetValue(context, out var audioContext))
            {
                LastError = $"Invalid context: {context}";
                return Result.InvalidParam;
            }

            try
            {
                if (stereo)
                {
                    var monoAudio = ConvertStereoToMono(audio);
                    audioContext.ProcessAudio(monoAudio);
                }
                else
                {
                    audioContext.ProcessAudio(audio);
                }

                int melCount = 0;

                if (audioContext.IsStreaming)
                {
                    // One 10 ms hop of work per mel frame. The conv caches carry all the
                    // temporal context, so there is no window to re-run.
                    while (audioContext.TryGetNextMelFrame(out var melFeatures))
                    {
                        audioContext.StepStreaming(melFeatures);
                        melCount++;
                        DebugInferenceRuns++;
                    }
                }
                else
                {
                    while (audioContext.TryGetNextMelFrame(out var melFeatures))
                    {
                        audioContext.AccumulateMelFrame(melFeatures);
                        melCount++;
                    }

                    if (audioContext.AccumulatedMelFrames >= 5)
                    {
                        var melSeq = audioContext.GetMelSequence(out int seqLen);
                        RunSequenceInference(melSeq, seqLen, audioContext.MelBands,
                                             audioContext.GetInferenceBuffer(),
                                             audioContext.GetInferenceInputBuffer());
                        audioContext.ApplyLegacyResults(audioContext.GetInferenceBuffer());
                        DebugInferenceRuns++;
                    }
                }

                DebugMelFramesProduced += melCount;

                float max = 0f;
                var probs = audioContext.GetInferenceBuffer();
                for (int i = 0; i < probs.Length; i++)
                    if (probs[i] > max) max = probs[i];
                DebugLastInferenceMax = max;

                audioContext.UpdateFrame(ref frame);

                return Result.Success;
            }
            catch (ObjectDisposedException)
            {
                // Expected during teardown — context disposed while thread pool task
                // was still processing. Not an error.
                return Result.Unknown;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenLipSync] ProcessFrameFloat error: {ex}");
                LastError = $"ProcessFrameFloat: {ex.Message}";
                return Result.Unknown;
            }
        }

        // Cached input wrapper: DenseTensor + NamedOnnxValue + the int[] dims live
        // for the lifetime of the steady-state shape, avoiding per-inference allocations.
        // Rebuild only when shape OR the underlying array reference changes.
        private DenseTensor<float> _cachedInputTensor;
        private readonly NamedOnnxValue[] _runInputs = new NamedOnnxValue[1];
        private int _cachedSeqLen = -1;
        private int _cachedMelBands = -1;
        private int _cachedInputSize = -1;
        private float[] _cachedInputArrayRef;

        private void RunSequenceInference(float[] melSequenceFlat, int seqLen, int melBands, float[] destination, float[] inputBuffer = null)
        {
            if (_onnxSession == null || seqLen <= 0)
            {
                Array.Clear(destination, 0, destination.Length);
                return;
            }

            try
            {
                int inputSize = seqLen * melBands;

                float[] inputData;
                if (inputBuffer != null && inputBuffer.Length >= inputSize)
                {
                    inputData = inputBuffer;
                }
                else
                {
                    inputData = new float[inputSize];
                }
                melSequenceFlat.AsSpan(0, inputSize).CopyTo(inputData);

                // Legacy models were trained on mel normalized to zero-mean / unit-variance,
                // but this front-end emits raw dB. Feeding raw dB leaves the network ~36 sigma
                // off-distribution: its logits blow up (the conv stack is bias-free + ReLU, so
                // it is near scale-equivariant) and the sigmoid saturates to a near-binary 0/1,
                // which destroys exactly the graded weights that blendshape blending needs.
                //
                // Per-utterance stats are not available in a stream, so normalize this window
                // by its own mean/std. Causal, and measured to take the shipped model from
                // 60.0% -> 69.5% frame accuracy on dev-clean.
                if (_legacyNormalizeWindow)
                {
                    var span = inputData.AsSpan(0, inputSize);
                    double sum = 0.0;
                    for (int i = 0; i < inputSize; i++) sum += span[i];
                    float mean = (float)(sum / inputSize);

                    double sq = 0.0;
                    for (int i = 0; i < inputSize; i++)
                    {
                        float d = span[i] - mean;
                        sq += (double)d * d;
                    }
                    float std = MathF.Sqrt((float)(sq / inputSize));
                    float inv = 1f / MathF.Max(std, 1e-5f);

                    for (int i = 0; i < inputSize; i++) span[i] = (span[i] - mean) * inv;
                }

                // Rebuild the input tensor wrapper only when shape OR the backing
                // array changes. In steady state none of these vary, so this is
                // effectively zero allocation per inference.
                if (_cachedInputTensor == null
                    || _cachedSeqLen != seqLen
                    || _cachedMelBands != melBands
                    || _cachedInputSize != inputSize
                    || !ReferenceEquals(_cachedInputArrayRef, inputData))
                {
                    _cachedInputTensor = new DenseTensor<float>(
                        new Memory<float>(inputData, 0, inputSize),
                        new[] { 1, seqLen, melBands });
                    _runInputs[0] = NamedOnnxValue.CreateFromTensor("audio_features", _cachedInputTensor);
                    _cachedSeqLen = seqLen;
                    _cachedMelBands = melBands;
                    _cachedInputSize = inputSize;
                    _cachedInputArrayRef = inputData;
                }

                using var results = _onnxSession.Run(_runInputs);

                var firstResult = results.First();
                var outputTensor = firstResult.AsTensor<float>();
                var dims = outputTensor.Dimensions;

                int numVisemes = Math.Min(destination.Length, _numVisemes);

                // Compute the linear-buffer offset for the last-frame slice once,
                // so the inner loops below are just sequential reads. Layout
                // assumed for ORT: row-major / C-contiguous.
                int outBase;
                if (dims.Length == 3)
                {
                    int lastIndex = dims[1] - 1;
                    int rowStride = dims[2];
                    outBase = lastIndex * rowStride;
                }
                else if (dims.Length == 2)
                {
                    int lastIndex = dims[0] - 1;
                    int rowStride = dims[1];
                    outBase = lastIndex * rowStride;
                }
                else if (dims.Length == 1)
                {
                    outBase = 0;
                }
                else
                {
                    Array.Clear(destination, 0, destination.Length);
                    return;
                }

                // Get raw linear span once instead of paying for the multidim
                // DenseTensor indexer (stride math + bounds check) per element.
                ReadOnlySpan<float> outSpan;
                if (outputTensor is DenseTensor<float> denseOut)
                {
                    outSpan = denseOut.Buffer.Span;
                }
                else
                {
                    // Fallback for non-DenseTensor implementations — copies once.
                    outSpan = outputTensor.ToArray();
                }

                ReadOnlySpan<float> logits = outSpan.Slice(outBase, numVisemes);

                if (_isMultiLabel)
                {
                    for (int i = 0; i < numVisemes; i++)
                    {
                        float x = logits[i];
                        x = Math.Clamp(x, -50f, 50f);
                        destination[i] = 1f / (1f + MathF.Exp(-x));
                    }
                }
                else
                {
                    float maxLogit = float.MinValue;
                    for (int i = 0; i < numVisemes; i++)
                    {
                        float v = logits[i];
                        if (v > maxLogit) maxLogit = v;
                    }
                    float sum = 0f;
                    for (int i = 0; i < numVisemes; i++)
                    {
                        float e = MathF.Exp(logits[i] - maxLogit);
                        destination[i] = e;
                        sum += e;
                    }
                    if (sum > 0f)
                    {
                        float inv = 1f / sum;
                        for (int i = 0; i < numVisemes; i++) destination[i] *= inv;
                    }
                    else
                    {
                        Array.Clear(destination, 0, numVisemes);
                    }
                }

                if (numVisemes < destination.Length)
                    Array.Clear(destination, numVisemes, destination.Length - numVisemes);

                float destMax = 0f;
                for (int i = 0; i < numVisemes; i++)
                    if (destination[i] > destMax) destMax = destination[i];
                DebugLastInferenceMax = destMax;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenLipSync] Sequence inference error: {ex}");
                Array.Clear(destination, 0, destination.Length);
            }
        }

        // Cached buffer for stereo-to-mono conversion — avoids per-call allocation.
        private float[] _monoConvertBuffer;

        private float[] ConvertStereoToMono(ReadOnlySpan<float> stereoAudio)
        {
            int monoLen = stereoAudio.Length / 2;
            if (_monoConvertBuffer == null || _monoConvertBuffer.Length < monoLen)
                _monoConvertBuffer = new float[monoLen];

            for (int i = 0; i < monoLen; i++)
            {
                _monoConvertBuffer[i] = (stereoAudio[i * 2] + stereoAudio[i * 2 + 1]) * 0.5f;
            }
            return _monoConvertBuffer;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Shutdown();
                _disposed = true;
            }
        }
    }
}
