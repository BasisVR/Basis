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
        private int _numVisemes = Frame.VisemeCount;

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
                        _isMultiLabel = _modelConfig.Training?.MultiLabel ?? false;
                        _numVisemes = _modelConfig.Model?.NumVisemes ?? Frame.VisemeCount;
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
                var audioContext = new AudioContext(_inputSampleRate, _audioConfig, _numVisemes);
                uint id = (uint)Interlocked.Increment(ref _nextContextId);
                context = id;
                _contexts[context] = audioContext;

                return Result.Success;
            }
            catch
            {
                LastError = "Failed to create audio context";
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
                while (audioContext.TryGetNextMelFrame(out var melFeatures))
                {
                    audioContext.AccumulateMelFrame(melFeatures);
                    melCount++;
                }

                DebugMelFramesProduced += melCount;

                int accFrames = audioContext.AccumulatedMelFrames;

                if (accFrames >= 5)
                {
                    var melSeq = audioContext.GetMelSequence(out int seqLen);
                    RunSequenceInference(melSeq, seqLen, audioContext.MelBands, audioContext.GetInferenceBuffer(), audioContext.GetInferenceInputBuffer());
                    audioContext.UpdateLatestResults(audioContext.GetInferenceBuffer());

                    DebugInferenceRuns++;
                    float max = 0f;
                    var buf = audioContext.GetInferenceBuffer();
                    for (int i = 0; i < buf.Length; i++)
                        if (buf[i] > max) max = buf[i];
                    DebugLastInferenceMax = max;
                }

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
                // Use pre-allocated buffer when available to avoid per-inference GC allocation.
                // DenseTensor's Memory<T> constructor accepts a slice, so the backing array
                // can be larger than the tensor's element count.
                float[] inputData;
                if (inputBuffer != null && inputBuffer.Length >= inputSize)
                {
                    inputData = inputBuffer;
                    Array.Copy(melSequenceFlat, 0, inputData, 0, inputSize);
                }
                else
                {
                    inputData = new float[inputSize];
                    Array.Copy(melSequenceFlat, 0, inputData, 0, inputSize);
                }
                var inputTensor = new DenseTensor<float>(new Memory<float>(inputData, 0, inputSize), new[] { 1, seqLen, melBands });

                using var results = _onnxSession.Run(new[] { NamedOnnxValue.CreateFromTensor("audio_features", inputTensor) });

                var firstResult = results.First();
                var outputTensor = firstResult.AsTensor<float>();
                var dims = outputTensor.Dimensions;

                int numVisemes = Math.Min(destination.Length, _numVisemes);

                Func<int, float> getLogit;
                if (dims.Length == 3)
                {
                    int lastT = dims[1] - 1;
                    getLogit = i => outputTensor[0, lastT, i];
                }
                else if (dims.Length == 2)
                {
                    int lastRow = dims[0] - 1;
                    getLogit = i => outputTensor[lastRow, i];
                }
                else if (dims.Length == 1)
                {
                    getLogit = i => outputTensor[i];
                }
                else
                {
                    Array.Clear(destination, 0, destination.Length);
                    return;
                }

                if (_isMultiLabel)
                {
                    for (int i = 0; i < numVisemes; i++)
                    {
                        float x = getLogit(i);
                        x = Math.Clamp(x, -50f, 50f);
                        destination[i] = 1f / (1f + MathF.Exp(-x));
                    }
                }
                else
                {
                    float maxLogit = float.MinValue;
                    for (int i = 0; i < numVisemes; i++)
                    {
                        float v = getLogit(i);
                        if (v > maxLogit) maxLogit = v;
                    }
                    float sum = 0f;
                    for (int i = 0; i < numVisemes; i++)
                    {
                        float e = MathF.Exp(getLogit(i) - maxLogit);
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

        private static float[] ConvertStereoToMono(ReadOnlySpan<float> stereoAudio)
        {
            var monoAudio = new float[stereoAudio.Length / 2];
            for (int i = 0; i < monoAudio.Length; i++)
            {
                monoAudio[i] = (stereoAudio[i * 2] + stereoAudio[i * 2 + 1]) * 0.5f;
            }
            return monoAudio;
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
