using System;
using OpenLipSync.Inference.Audio;
using OpenLipSync.Inference.OVRCompat;

namespace OpenLipSync.Inference
{
    internal sealed class AudioContext : IDisposable
    {
        private readonly AudioRingBuffer _ringBuffer;
        private readonly MelSpectrogramProcessor _melProcessor;
        private readonly AudioResampler _resampler;
        private readonly float[] _latestVisemeResults;
        private readonly float[] _probabilityBuffer;
        private float _smoothing = 0.3f;
        private int _frameNumber;
        private bool _disposed;

        // Mel frame accumulation for TCN temporal context
        private readonly int _melBands;
        private readonly int _maxMelFrames;
        private readonly float[] _melSequence; // flat [maxFrames * melBands]
        private int _melFrameCount;

        public int MelBands => _melBands;
        public int AccumulatedMelFrames => _melFrameCount;

        public AudioContext(int inputSampleRate, AudioProcessingConfig audioConfig, int modelVisemeCount)
        {
            _ringBuffer = new AudioRingBuffer(audioConfig.SampleRate * 3);
            _melProcessor = new MelSpectrogramProcessor(audioConfig);
            _resampler = inputSampleRate != audioConfig.SampleRate
                ? new AudioResampler(inputSampleRate, audioConfig.SampleRate)
                : null;
            var visemeCount = modelVisemeCount > 0 ? modelVisemeCount : Frame.VisemeCount;
            _latestVisemeResults = new float[visemeCount];
            _probabilityBuffer = new float[visemeCount];
            if (_latestVisemeResults.Length > 0) _latestVisemeResults[0] = 1f;
            _frameNumber = 0;

            // Accumulate up to 150 mel frames (~1.5s at 100fps) for TCN context
            _melBands = audioConfig.NMels;
            _maxMelFrames = 150;
            _melSequence = new float[_maxMelFrames * _melBands];
            _melFrameCount = 0;
        }

        public void ProcessAudio(ReadOnlySpan<float> audioSamples)
        {
            if (_disposed) return;

            if (_resampler == null)
            {
                _ringBuffer.Write(audioSamples);
            }
            else
            {
                var resampled = _resampler.Resample(audioSamples);
                _ringBuffer.Write(resampled);
            }
        }

        public bool TryGetNextMelFrame(out float[] melFeatures)
        {
            return _melProcessor.TryProcessNextHop(_ringBuffer, out melFeatures);
        }

        /// <summary>
        /// Accumulate a mel frame into the sliding window.
        /// Shifts old frames out when the buffer is full.
        /// </summary>
        public void AccumulateMelFrame(float[] melFrame)
        {
            if (_disposed || melFrame == null) return;

            int bands = Math.Min(melFrame.Length, _melBands);

            if (_melFrameCount >= _maxMelFrames)
            {
                // Shift left by one frame
                Array.Copy(_melSequence, _melBands, _melSequence, 0, (_maxMelFrames - 1) * _melBands);
                _melFrameCount = _maxMelFrames - 1;
            }

            // Append new frame
            Array.Copy(melFrame, 0, _melSequence, _melFrameCount * _melBands, bands);
            _melFrameCount++;
        }

        /// <summary>
        /// Get the accumulated mel sequence as a flat array and its frame count.
        /// </summary>
        public float[] GetMelSequence(out int frameCount)
        {
            frameCount = _melFrameCount;
            return _melSequence;
        }

        public float[] GetInferenceBuffer()
        {
            return _probabilityBuffer;
        }

        public void UpdateLatestResults(float[] visemeProbs)
        {
            if (_disposed || visemeProbs.Length == 0) return;

            int n = Math.Min(_latestVisemeResults.Length, visemeProbs.Length);

            // Post-process: when speech visemes are active, suppress silence
            // and boost speech visemes for clearer output
            float speechSum = 0f;
            float speechMax = 0f;
            for (int i = 1; i < n; i++)
            {
                speechSum += visemeProbs[i];
                if (visemeProbs[i] > speechMax) speechMax = visemeProbs[i];
            }

            float silWeight = (n > 0) ? visemeProbs[0] : 0f;

            if (speechMax > 0.05f)
            {
                // Speech detected: suppress silence, renormalize speech visemes
                float boost = 1f / Math.Max(speechSum, 0.01f);
                boost = Math.Min(boost, 3f); // cap boost at 3x

                for (int i = 0; i < n; i++)
                {
                    float val;
                    if (i == 0)
                    {
                        // Reduce silence proportionally to speech activity
                        val = silWeight * (1f - Math.Min(speechSum * 2f, 1f));
                    }
                    else
                    {
                        val = visemeProbs[i] * boost;
                    }
                    _latestVisemeResults[i] = _latestVisemeResults[i] * _smoothing + val * (1f - _smoothing);
                }
            }
            else
            {
                // Silence: normal smoothing
                for (int i = 0; i < n; i++)
                {
                    _latestVisemeResults[i] = _latestVisemeResults[i] * _smoothing + visemeProbs[i] * (1f - _smoothing);
                }
            }
        }

        public void UpdateFrame(ref Frame frame)
        {
            if (_disposed) return;

            frame.frameNumber = ++_frameNumber;
            frame.frameDelay = 0;

            int copyCount = Math.Min(_latestVisemeResults.Length, frame.Visemes.Length);
            Array.Copy(_latestVisemeResults, frame.Visemes, copyCount);
            if (copyCount < frame.Visemes.Length)
            {
                Array.Clear(frame.Visemes, copyCount, frame.Visemes.Length - copyCount);
            }

            frame.laughterScore = 0f;
        }

        public Result SendSignal(Signals signal, int arg1)
        {
            if (_disposed) return Result.Unknown;

            switch (signal)
            {
                case Signals.VisemeSmoothing:
                    _smoothing = Math.Clamp(arg1 / 100f, 0f, 1f);
                    return Result.Success;

                default:
                    return Result.Success;
            }
        }

        public void Reset()
        {
            if (_disposed) return;

            _ringBuffer.Clear();
            Array.Clear(_latestVisemeResults, 0, _latestVisemeResults.Length);
            if (_latestVisemeResults.Length > 0) _latestVisemeResults[0] = 1f;
            _melFrameCount = 0;
            Array.Clear(_melSequence, 0, _melSequence.Length);
            _frameNumber = 0;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _ringBuffer.Dispose();
                _melProcessor.Dispose();
                _disposed = true;
            }
        }
    }
}
