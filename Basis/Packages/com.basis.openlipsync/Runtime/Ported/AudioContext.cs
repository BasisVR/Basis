using System;
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

        // --- legacy windowed path ---
        private readonly int _maxMelFrames;
        private readonly float[] _melSequence;
        private readonly float[] _inferenceInputBuffer;
        private int _melFrameCount;

        public int MelBands => _melBands;
        public bool IsStreaming => _streaming != null;
        public int AccumulatedMelFrames => _melFrameCount;

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
        public void StepStreaming(float[] melFrame)
        {
            if (_disposed || _streaming == null) return;
            _streaming.Step(melFrame, _probabilityBuffer, _multiLabel);
            Smooth(_probabilityBuffer);
        }

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

            _streaming?.Reset();

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
        }
    }
}
