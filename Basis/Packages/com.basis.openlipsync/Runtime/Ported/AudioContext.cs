using System;
using System.Runtime.InteropServices;
using OpenLipSync.Inference.Audio;
using OpenLipSync.Inference.OVRCompat;

namespace OpenLipSync.Inference
{
    /// <summary>
    /// Per-player audio -> viseme state.
    ///
    /// Streaming models (the ones this package now ships) advance one 10 ms mel frame at a
    /// time through cached convolutions. The previous path accumulated a 150-frame mel
    /// window and re-ran the ENTIRE window through the network on every audio callback,
    /// keeping only the last frame -- about 83x more compute per player than necessary, and
    /// the reason OpenLipSync needed a slot cap.
    ///
    /// The legacy windowed path is retained so an older model.onnx still loads.
    /// </summary>
    internal sealed class AudioContext : IDisposable
    {
        private readonly AudioRingBuffer _ringBuffer;
        private readonly MelSpectrogramProcessor _melProcessor;
        private readonly AudioResampler _resampler;

        private readonly VisemeSmoother _smoother;
        private readonly float[] _probabilityBuffer;
        private readonly int _melBands;

        private int _frameNumber;
        private bool _disposed;

        // --- streaming path ---
        private readonly StreamingSession _streaming;   // null => legacy
        private readonly bool _multiLabel;
#if UNITY_WEBGL && !UNITY_EDITOR
        private readonly WebStreamingSession _web;
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
        private readonly NativeStreamingSession _native;
        private readonly int _settledRun;
#endif
#if !UNITY_WEBGL || UNITY_EDITOR
        private readonly float[] _lastMel;
        private int _identicalRun;
        private bool _settled;
#endif

        // --- legacy windowed path ---
        private readonly int _maxMelFrames;
        private readonly float[] _melSequence;
        private readonly float[] _inferenceInputBuffer;
        private int _melFrameCount;

        public int MelBands => _melBands;
#if UNITY_WEBGL && !UNITY_EDITOR
        public bool IsStreaming => _streaming != null || _web != null;
#elif UNITY_ANDROID && !UNITY_EDITOR
        public bool IsStreaming => _streaming != null || _native != null;
        public bool IsNative => _native != null;
        public NativeStreamingSession Native => _native;
#else
        public bool IsStreaming => _streaming != null;
#endif
        public int AccumulatedMelFrames => _melFrameCount;

#if UNITY_WEBGL && !UNITY_EDITOR
        public AudioContext(int inputSampleRate, AudioProcessingConfig audioConfig, int modelVisemeCount, WebStreamingSession web, bool multiLabel)
        {
            _ringBuffer = new AudioRingBuffer(audioConfig.SampleRate * 3);
            _melProcessor = new MelSpectrogramProcessor(audioConfig);
            _resampler = inputSampleRate != audioConfig.SampleRate ? new AudioResampler(inputSampleRate, audioConfig.SampleRate) : null;
            int visemeCount = modelVisemeCount > 0 ? modelVisemeCount : Frame.VisemeCount;
            _smoother = new VisemeSmoother(visemeCount, 0.3f);
            _probabilityBuffer = new float[visemeCount];
            _melBands = audioConfig.NMels;
            _web = web;
            _multiLabel = multiLabel;
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        public AudioContext(int inputSampleRate, AudioProcessingConfig audioConfig, int modelVisemeCount, NativeStreamingSession native, bool multiLabel)
        {
            _ringBuffer = new AudioRingBuffer(audioConfig.SampleRate * 3);
            _melProcessor = new MelSpectrogramProcessor(audioConfig);
            _resampler = inputSampleRate != audioConfig.SampleRate ? new AudioResampler(inputSampleRate, audioConfig.SampleRate) : null;
            int visemeCount = modelVisemeCount > 0 ? modelVisemeCount : Frame.VisemeCount;
            _smoother = new VisemeSmoother(visemeCount, 0.3f);
            _probabilityBuffer = new float[visemeCount];
            _melBands = audioConfig.NMels;
            _native = native;
            _multiLabel = multiLabel;
            _lastMel = new float[_melBands];
            _settledRun = BasisNativeLipSync.ReceptiveField;
        }
#endif

        public AudioContext(int inputSampleRate, AudioProcessingConfig audioConfig, int modelVisemeCount,
                            StreamingSession streaming, bool multiLabel)
        {
            _ringBuffer = new AudioRingBuffer(audioConfig.SampleRate * 3);
            _melProcessor = new MelSpectrogramProcessor(audioConfig);
            _resampler = inputSampleRate != audioConfig.SampleRate
                ? new AudioResampler(inputSampleRate, audioConfig.SampleRate)
                : null;

            int visemeCount = modelVisemeCount > 0 ? modelVisemeCount : Frame.VisemeCount;
            // Smoothing keeps its historical meaning -- arg1/100 is the weight kept on the
            // previous value -- and is applied once per MEL FRAME (a fixed 100 Hz) rather than
            // once per audio callback. Callbacks deliver a variable number of samples, so
            // smoothing per callback silently changed with buffer size and output sample rate;
            // the same "0.7" meant a different time constant on different machines. The pole's
            // own group delay is cancelled inside the smoother -- see VisemeSmoother.
            _smoother = new VisemeSmoother(visemeCount, 0.3f);
            _probabilityBuffer = new float[visemeCount];

            _melBands = audioConfig.NMels;
            _streaming = streaming;
            _multiLabel = multiLabel;
#if !UNITY_WEBGL || UNITY_EDITOR
            _lastMel = new float[_melBands];
#endif

            if (_streaming == null)
            {
                _maxMelFrames = 150;
                _melSequence = new float[_maxMelFrames * _melBands];
                _inferenceInputBuffer = new float[_maxMelFrames * _melBands];
            }
        }

        public void ProcessAudio(ReadOnlySpan<float> audioSamples)
        {
            if (_disposed) return;
            if (_resampler == null) _ringBuffer.Write(audioSamples);
            else _ringBuffer.Write(_resampler.Resample(audioSamples));
        }

        public bool TryGetNextMelFrame(out float[] melFeatures)
            => _melProcessor.TryProcessNextHop(_ringBuffer, out melFeatures);

        /// <summary>
        /// Streaming: one mel frame in, one set of viseme probabilities out.
        /// The mel frame is RAW dB -- the model applies CMVN internally. Do not normalize here.
        /// </summary>
        public bool StepStreaming(float[] melFrame)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_disposed || _web == null) return false;
            _web.Submit(melFrame);
            return true;
#else
            if (_disposed) return false;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_native != null)
            {
                bool settled = Repeats(melFrame) && _identicalRun > _settledRun;
                _native.Submit(melFrame, settled);
                return !settled;
            }
#endif
            if (_streaming == null) return false;
            bool repeat = Repeats(melFrame);
            if (repeat && _settled)
            {
                Smooth(_probabilityBuffer);
                return false;
            }
            _streaming.Step(melFrame, _probabilityBuffer, _multiLabel);
            if (repeat) _settled = _streaming.StateUnchanged();
            Smooth(_probabilityBuffer);
            return true;
#endif
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        private bool Repeats(float[] melFrame)
        {
            ReadOnlySpan<float> current = melFrame.AsSpan(0, _melBands);
            if (_identicalRun > 0 && MemoryMarshal.Cast<float, int>(current).SequenceEqual(MemoryMarshal.Cast<float, int>(new ReadOnlySpan<float>(_lastMel))))
            {
                if (_identicalRun < int.MaxValue) _identicalRun++;
                return true;
            }
            current.CopyTo(_lastMel);
            _identicalRun = 1;
            _settled = false;
            return false;
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        public void ApplyNative(float[] logits, ref int offset, Frame target)
        {
            NativeStreamingSession native = _native;
            int submitted = native.Submitted, row = offset;
            offset += submitted * BasisNativeLipSync.Visemes;
            if (_disposed || submitted != native.Computed)
            {
                native.Clear();
                return;
            }
            for (int f = 0; f < native.Frames; f++)
            {
                if (!native.Settled[f])
                {
                    NativeStreamingSession.Activate(logits, row, _probabilityBuffer, _multiLabel);
                    row += BasisNativeLipSync.Visemes;
                }
                Smooth(_probabilityBuffer);
            }
            native.Clear();
            UpdateFrame(ref target);
        }
#endif

        /// <summary>
        /// The model is trained to emit calibrated per-viseme weights, so all that is left
        /// to do is smooth them.
        ///
        /// The previous build ran a hand-tuned "suppress silence, boost speech by up to 3x,
        /// renormalize" pass here. That existed only to rescue a model whose outputs were
        /// saturated by being fed unnormalized features; with the feature contract fixed it
        /// is actively harmful, because it distorts exactly the graded weights that drive
        /// blendshape blending. It is gone.
        /// </summary>
        private void Smooth(float[] probabilities) => _smoother.Step(probabilities);

        // ------------------------------------------------------------------ legacy path
        public void AccumulateMelFrame(float[] melFrame)
        {
            if (_disposed || melFrame == null || _melSequence == null) return;
            int bands = Math.Min(melFrame.Length, _melBands);

            if (_melFrameCount >= _maxMelFrames)
            {
                _melSequence.AsSpan(_melBands, (_maxMelFrames - 1) * _melBands)
                            .CopyTo(_melSequence.AsSpan(0));
                _melFrameCount = _maxMelFrames - 1;
            }
            melFrame.AsSpan(0, bands).CopyTo(_melSequence.AsSpan(_melFrameCount * _melBands));
            _melFrameCount++;
        }

        public float[] GetMelSequence(out int frameCount)
        {
            frameCount = _melFrameCount;
            return _melSequence;
        }

        public float[] GetInferenceBuffer() => _probabilityBuffer;
        public float[] GetInferenceInputBuffer() => _inferenceInputBuffer;

        public void ApplyLegacyResults(float[] visemeProbs)
        {
            if (_disposed) return;
            Smooth(visemeProbs);
        }

        // ------------------------------------------------------------------
        public void UpdateFrame(ref Frame frame)
        {
            if (_disposed) return;
#if UNITY_WEBGL && !UNITY_EDITOR
            while (_web != null && _web.TryTake(_probabilityBuffer, _multiLabel)) Smooth(_probabilityBuffer);
#endif

            frame.frameNumber = ++_frameNumber;
            frame.frameDelay = 0;

            float[] latest = _smoother.Output;
            int copy = Math.Min(latest.Length, frame.Visemes.Length);
            Array.Copy(latest, frame.Visemes, copy);
            if (copy < frame.Visemes.Length)
                Array.Clear(frame.Visemes, copy, frame.Visemes.Length - copy);

            frame.laughterScore = 0f;
        }

        public Result SendSignal(Signals signal, int arg1)
        {
            if (_disposed) return Result.Unknown;

            switch (signal)
            {
                case Signals.VisemeSmoothing:
                    // Same 0..100 scale and same meaning as before (weight kept on the previous
                    // value), so existing callers -- e.g. BasisOpenLipSyncContext passes 70 --
                    // keep their tuning. What changed is that it is now applied once per mel
                    // frame at a fixed 100 Hz rather than once per variable-sized audio callback.
                    _smoother.Alpha = arg1 / 100f;
                    return Result.Success;

                default:
                    return Result.Success;
            }
        }

        public void Reset()
        {
            if (_disposed) return;

            _ringBuffer.Clear();
            _smoother.Reset();
            _frameNumber = 0;
#if !UNITY_WEBGL || UNITY_EDITOR
            _identicalRun = 0;
            _settled = false;
#endif

            _streaming?.Reset();
#if UNITY_WEBGL && !UNITY_EDITOR
            _web?.Reset();
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
            _native?.Reset();
#endif

            if (_melSequence != null)
            {
                _melFrameCount = 0;
                Array.Clear(_melSequence, 0, _melSequence.Length);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ringBuffer.Dispose();
            _melProcessor.Dispose();
            _streaming?.Dispose();
#if UNITY_WEBGL && !UNITY_EDITOR
            _web?.Dispose();
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
            _native?.Dispose();
#endif
        }
    }
}
