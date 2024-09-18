using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.Unity.Helpers;
using UnityEngine;
using UnityEngine.Profiling;
namespace FFmpeg.Unity
{
    public class FFUnity : MonoBehaviour
    {
        public List<FFUnityAudio> AudioOutput = new List<FFUnityAudio>();
        public bool IsPaused => _paused;
        public int materialIndex = -1;
        [SerializeField]
        public bool _paused;
        private bool _wasPaused = false;
        [SerializeField]
        public bool CanSeek = true;
        // time controls
        [SerializeField]
        public double _offset = 0.0d;
        private double _prevTime = 0.0d;
        private double _timeOffset = 0.0d;
        [SerializeField]
        public double _videoOffset = -0.0d;
        private Stopwatch _videoWatch;
        private double? _lastPts;
        private int? _lastPts2;
        public double timer;
        public double PlaybackTime => _lastVideoTex?.pts ?? _elapsedOffset;
        public double _elapsedTotalSeconds => _videoWatch?.Elapsed.TotalSeconds ?? 0d;
        public double _elapsedOffsetVideo => _elapsedTotalSeconds + _videoOffset - _timeOffset;
        public double _elapsedOffset => _elapsedTotalSeconds - _timeOffset;
        private double? seekTarget = null;
        // buffer controls
        private int _videoBufferCount = 4;
        private int _audioBufferCount = 1;
        [SerializeField]
        public double _videoTimeBuffer = 1d;
        [SerializeField]
        public double _videoSkipBuffer = 0.25d;
        [SerializeField]
        public double _audioTimeBuffer = 1d;
        [SerializeField]
        public double _audioSkipBuffer = 0.25d;
        private int _audioBufferSize = 128;
        // unity assets
        private Queue<TexturePool.TexturePoolState> _videoTextures;
        private TexturePool.TexturePoolState _lastVideoTex;
        private TexturePool _texturePool;
        private FFTexData? _lastTexData;
        private AudioClip _audioClip;
        private MaterialPropertyBlock propertyBlock;
        public Action<Texture2D> OnDisplay = null;
        // decoders
        [SerializeField]
        public AVHWDeviceType _hwType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        private FFmpegCtx _streamVideoCtx;
        private FFmpegCtx _streamAudioCtx;
        private VideoStreamDecoder _videoDecoder;
        private VideoStreamDecoder _audioDecoder;
        private VideoFrameConverter _videoConverter;
        private Queue<FFTexData> _videoFrameClones;
        private Mutex _videoMutex = new Mutex();
        private Thread _decodeThread;
        private Mutex _audioLocker = new Mutex();
        private Queue<float> _audioStream;
        private MemoryStream _audioMemStream;
        // buffers
        private AVFrame[] _videoFrames;
        private AVFrame[] _audioFrames;
        private int _videoWriteIndex = 0;
        private int _audioWriteIndex = 0;
        private bool _lastAudioRead = false;
        private int _audioMissCount = 0;
        private double _lastVideoDecodeTime;
        private double _lastAudioDecodeTime;
        [NonSerialized]
        public int skippedFrames = 0;
        public double VideoCatchupMultiplier = 5;
        public int LastTotalSize;
        public int synchronizingmaxIterations = 128;
        Stopwatch FillVideoBuffersStopWatch = new Stopwatch();
        [SerializeField]
        public FFUnityTextureGeneration unityTextureGeneration = new FFUnityTextureGeneration();
        private void OnEnable()
        {
            _paused = true;
        }
        private void OnDisable()
        {
            _paused = true;
        }
        private void OnDestroy()
        {
            DeInit();
        }
        public void DeInit()
        {
            _paused = true;
            _decodeThread?.Abort();
            _decodeThread?.Join();
            _texturePool?.Dispose();
            _videoDecoder?.Dispose();
            _audioDecoder?.Dispose();
            _streamVideoCtx?.Dispose();
            _streamAudioCtx?.Dispose();
        }
        public void Seek(double seek)
        {
            UnityEngine.Debug.Log(nameof(Seek));
            _paused = true;
            seekTarget = seek;
        }
        public void SeekInternal(double seek)
        {
            FFUnityAudioHelper.StopAll(AudioOutput);

            if (_audioLocker.WaitOne())
            {
                _audioMemStream.Position = 0;
                _audioStream.Clear();
                _audioLocker.ReleaseMutex();
            }
            _videoFrameClones.Clear();
            foreach (TexturePool.TexturePoolState tex in _videoTextures)
            {
                _texturePool.Release(tex);
            }
            _videoTextures.Clear();
            _lastVideoTex = null;
            _lastTexData = null;
            _videoWatch.Restart();
            ResetTimers();
            _timeOffset = -seek;
            _prevTime = _offset;
            _lastPts = null;
            _lastPts2 = null;
            if (CanSeek)
            {
                _streamVideoCtx.Seek(_videoDecoder, seek);
                _streamAudioCtx.Seek(_audioDecoder, seek);
            }
            _videoDecoder.Seek();
            _audioDecoder?.Seek();
            FFUnityAudioHelper.PlayAll(AudioOutput, _audioClip);
            seekTarget = null;
            _paused = false;
            StartDecodeThread();
        }
        public void Play(Stream video, Stream audio)
        {
            DeInit();
            _streamVideoCtx = new FFmpegCtx(video);
            _streamAudioCtx = new FFmpegCtx(audio);
            Init();
        }
        public void Play(string urlV, string urlA)
        {
            DeInit();
            _streamVideoCtx = new FFmpegCtx(urlV);
            _streamAudioCtx = new FFmpegCtx(urlA);
            Init();
        }
        public void Resume()
        {
            if (!CanSeek)
            {
                Init();
            }
            _paused = false;
        }
        public void Pause()
        {
            _paused = true;
        }
        private void ResetTimers()
        {
            _videoWriteIndex = 0;
            _audioWriteIndex = 0;
            _lastPts = null;
            _lastPts2 = null;
            _offset = 0d;
            _prevTime = 0d;
            _timeOffset = 0d;
            timer = 0d;
        }
        private void Init()
        {
            _paused = true;
            // Stopwatches are more accurate than Time.timeAsDouble(?)
            _videoWatch = new Stopwatch();
            // pre-allocate buffers, prevent the C# GC from using CPU
            _texturePool = new TexturePool(_videoBufferCount);
            _videoTextures = new Queue<TexturePool.TexturePoolState>(_videoBufferCount);
            _audioClip = null; // don't create audio clip yet, we have nothing to play.
            _videoFrames = new AVFrame[_videoBufferCount];
            _videoFrameClones = new Queue<FFTexData>(_videoBufferCount);
            _audioFrames = new AVFrame[_audioBufferCount];
            _audioMemStream = new MemoryStream();
            _audioStream = new Queue<float>(_audioBufferSize * 4);
            ResetTimers();
            _lastVideoTex = null;
            _lastTexData = null;

            unityTextureGeneration.InitializeTexture();

            // init decoders
            _videoMutex = new Mutex(false, "Video Mutex");
            _videoDecoder = new VideoStreamDecoder(_streamVideoCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, _hwType);
            _audioLocker = new Mutex(false, "Audio Mutex");
            _audioDecoder = new VideoStreamDecoder(_streamAudioCtx, AVMediaType.AVMEDIA_TYPE_AUDIO);
            _audioClip = AudioClip.Create($"{name}-AudioClip", _audioBufferSize * _audioDecoder.Channels, _audioDecoder.Channels, _audioDecoder.SampleRate, true, AudioCallback);
            FFUnityAudioHelper.PlayAll(AudioOutput, _audioClip);
            UnityEngine.Debug.Log(nameof(Play));
            Seek(0d);
        }
        private void Update()
        {
            if (!IsInitialized())
            {
                return;
            }
            HandleEndOfStream();
            if (HandleSeeking())
            {
                return;
            }
            UpdatePlaybackState();
            if (!_paused)
            {
                StartOrResumeDecodeThread();
                UpdateVideoDisplay();
            }
            _prevTime = _offset;
            _wasPaused = _paused;
        }

        /// <summary>
        /// Checks if the stream and video context are initialized.
        /// </summary>
        private bool IsInitialized()
        {
            if (_videoWatch == null || _streamVideoCtx == null)
            {
                return false;
            }
            return true;
        }
        /// <summary>
        /// Handles the end of the video stream and pauses if necessary.
        /// </summary>
        private void HandleEndOfStream()
        {
            if (CanSeek && IsStreamAtEnd() && !_paused)
            {
                Pause();
            }
        }
        /// <summary>
        /// Determines whether the video and audio streams have reached their end.
        /// </summary>
        /// <returns>True if both streams are at the end, false otherwise.</returns>
        private bool IsStreamAtEnd()
        {
            return _offset >= _streamVideoCtx.GetLength()
                || (_streamVideoCtx.EndReached &&
                    (_audioDecoder == null || _streamAudioCtx.EndReached)
                    && _videoTextures.Count == 0
                    && (_audioDecoder == null || _audioStream.Count == 0));
        }
        /// <summary>
        /// Handles seeking by performing an internal seek if necessary.
        /// </summary>
        /// <returns>True if a seek is in progress and has been handled, false otherwise.</returns>
        private bool HandleSeeking()
        {
            if (seekTarget.HasValue && (_decodeThread == null || !_decodeThread.IsAlive))
            {
                SeekInternal(seekTarget.Value);
                return true;
            }
            return false;
        }
        /// <summary>
        /// Updates the playback state, handling pause and resume logic.
        /// </summary>
        private void UpdatePlaybackState()
        {
            if (!_paused)
            {
                _offset = _elapsedOffset;

                if (!_videoWatch.IsRunning)
                {
                    _videoWatch.Start();
                    FFUnityAudioHelper.UnPauseAll(AudioOutput);
                }
            }
            else
            {
                if (_videoWatch.IsRunning)
                {
                    _videoWatch.Stop();
                    FFUnityAudioHelper.PauseAll(AudioOutput);
                }
            }
        }
        /// <summary>
        /// Starts the decode thread if necessary.
        /// </summary>
        private void StartOrResumeDecodeThread()
        {
            if (_decodeThread == null || !_decodeThread.IsAlive)
            {
                StartDecodeThread();
            }
        }
        /// <summary>
        /// Updates the video display, synchronizing with the audio playback.
        /// </summary>
        private void UpdateVideoDisplay()
        {
            int iterations = 0;

            while (ShouldUpdateVideo() && iterations < synchronizingmaxIterations)
            {
                iterations++;
                if (_videoMutex.WaitOne())
                {
                    bool updateFailed = !UpdateVideoFromClones();
                    _videoMutex.ReleaseMutex();
                }
                Present();
            }
        }
        /// <summary>
        /// Determines whether the video display should be updated.
        /// </summary>
        /// <returns>True if the video display needs updating, false otherwise.</returns>
        private bool ShouldUpdateVideo()
        {
            return Math.Abs(_elapsedOffsetVideo - (PlaybackTime + _videoOffset)) >= 0.25d || _lastVideoTex == null;
        }
        private void UpdateThread()
        {
            UnityEngine.Debug.Log("AV Thread started.");

            double targetFps = GetVideoFps();
            double frameTimeMs = GetFrameTimeMs(targetFps);
            double frameInterval = 1d / targetFps;

            // Continuously update video frames while not paused
            while (!_paused)
            {
                try
                {
                    long elapsedMs = FillVideoBuffers(false, frameInterval, frameTimeMs);

                    // Calculate the sleep duration, ensuring a minimum of 5ms sleep to avoid tight loops
                    int sleepDurationMs = (int)Math.Max(5, frameTimeMs - elapsedMs);
                    Thread.Sleep(sleepDurationMs);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError($"Error in video update thread: {e}");
                }
            }

            // Log and finalize thread operation
            UnityEngine.Debug.Log("AV Thread stopped.");
            _videoWatch.Stop();
            _paused = true;
        }

        /// <summary>
        /// Gets the frame rate of the video, defaulting to 30fps if not available.
        /// </summary>
        private double GetVideoFps()
        {
            if (!_streamVideoCtx.TryGetFps(_videoDecoder, out double fps))
            {
                fps = 30d;
            }
            return fps;
        }

        /// <summary>
        /// Calculates the time per frame in milliseconds based on the FPS.
        /// </summary>
        private double GetFrameTimeMs(double fps)
        {
            return (1d / fps) * 1000;
        }
        private void StartDecodeThread()
        {
            _decodeThread = new Thread(() => UpdateThread());
            _decodeThread.Name = $"AV Decode Thread {name}";
            _decodeThread.Start();
        }
        /// <summary>
        /// Generates an Image
        /// </summary>
        /// <returns></returns>
        private bool Present()
        {
            if (!_lastTexData.HasValue)
                return false; // Early exit if no texture data

            _lastVideoTex = new TexturePool.TexturePoolState()
            {
                pts = _lastTexData.Value.time,
            };

            unityTextureGeneration.UpdateTexture(_lastTexData.Value);
            // Invoke the display callback
            OnDisplay?.Invoke(unityTextureGeneration.texture);

            _lastTexData = null; // Clear after processing
            return true;
        }
        private long FillVideoBuffers(bool mainThread, double invFps, double fpsMs)
        {
            if (IsInitialized() == false)
            {
                return 0;
            }
            FillVideoBuffersStopWatch.Restart();

            // Main loop that runs as long as we are within the target frame time
            while (FillVideoBuffersStopWatch.ElapsedMilliseconds <= fpsMs)
            {
                // Initialize state variables
                double time = default;
                bool decodeV = ShouldDecodeVideo();
                bool decodeA = _audioDecoder != null && ShouldDecodeAudio();

                // Process video frames
                if (decodeV && TryProcessVideoFrame(mainThread, ref time))
                {
                    continue;
                }

                // Process audio frames
                if (decodeA && TryProcessAudioFrame(ref time))
                {
                    continue;
                }
            }

            return FillVideoBuffersStopWatch.ElapsedMilliseconds;
        }
        /// <summary>
        /// Determines whether the video frame should be decoded.
        /// </summary>
        private bool ShouldDecodeVideo()
        {
            if (_lastVideoTex != null)
            {
                // Adjust the time offset if playback and video are out of sync
                if (Math.Abs(_elapsedOffsetVideo - PlaybackTime) > _videoTimeBuffer * VideoCatchupMultiplier && !CanSeek)
                {
                    _timeOffset = -PlaybackTime;
                }

                // Check if the current video decoder can decode and is in sync
                if (_videoDecoder.CanDecode() && _streamVideoCtx.TryGetTime(_videoDecoder, out var time))
                {
                    if (_elapsedOffsetVideo + _videoTimeBuffer < time) return false;

                    if (_elapsedOffsetVideo > time + _videoSkipBuffer && CanSeek)
                    {
                        _streamVideoCtx.NextFrame(out _);
                        skippedFrames++;
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Determines whether the audio frame should be decoded.
        /// </summary>
        private bool ShouldDecodeAudio()
        {
            if (_audioDecoder != null && _audioDecoder.CanDecode() && _streamAudioCtx.TryGetTime(_audioDecoder, out var time))
            {
                if (_elapsedOffset + _audioTimeBuffer < time) return false;

                if (_elapsedOffset > time + _audioSkipBuffer && CanSeek)
                {
                    _streamAudioCtx.NextFrame(out _);
                    skippedFrames++;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Processes the video frame, decodes it, and manages the buffer.
        /// </summary>
        private bool TryProcessVideoFrame(bool mainThread, ref double time)
        {
            int vid = -1;
            AVFrame vFrame = default;

            // Attempt to decode the next video frame
            _streamVideoCtx.NextFrame(out _);
            vid = _videoDecoder.Decode(out vFrame);

            if (vid == 0)
            {
                if (_streamVideoCtx.TryGetTime(_videoDecoder, vFrame, out time) && _elapsedOffsetVideo > time + _videoSkipBuffer && CanSeek)
                    return false;

                if (_streamVideoCtx.TryGetTime(_videoDecoder, vFrame, out time) && time != 0)
                    _lastVideoDecodeTime = time;

                // Store the video frame in the buffer
                _videoFrames[_videoWriteIndex % _videoFrames.Length] = vFrame;

                if (mainThread)
                {
                    UpdateVideo(_videoWriteIndex % _videoFrames.Length);
                }
                else
                {
                    EnqueueVideoFrame(vFrame, time);
                }

                _videoWriteIndex++;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Processes the audio frame, decodes it, and manages the buffer.
        /// </summary>
        private bool TryProcessAudioFrame(ref double time)
        {
            int aud = -1;
            AVFrame aFrame = default;

            // Attempt to decode the next audio frame
            _streamAudioCtx.NextFrame(out _);
            aud = _audioDecoder.Decode(out aFrame);

            if (aud == 0)
            {
                if (_streamAudioCtx.TryGetTime(_audioDecoder, aFrame, out time) && _elapsedOffset > time + _audioSkipBuffer && CanSeek)
                    return false;

                if (_streamAudioCtx.TryGetTime(_audioDecoder, aFrame, out time) && time != 0)
                    _lastAudioDecodeTime = time;

                // Store the audio frame in the buffer
                _audioFrames[_audioWriteIndex % _audioFrames.Length] = aFrame;
                UpdateAudio(_audioWriteIndex % _audioFrames.Length);

                _audioWriteIndex++;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Enqueues the video frame data for background processing.
        /// </summary>
        private void EnqueueVideoFrame(AVFrame vFrame, double time)
        {
            if (_videoMutex.WaitOne(1))
            {
                byte[] frameClone = new byte[vFrame.width * vFrame.height * 3];
                if (!FFUnityFrameHelper.SaveFrame(vFrame, vFrame.width, vFrame.height, frameClone, _videoDecoder.HWPixelFormat))
                {
                    UnityEngine.Debug.LogError("Could not save frame");
                    _videoWriteIndex--;
                }
                else
                {
                    _streamVideoCtx.TryGetTime(_videoDecoder, vFrame, out time);
                    _lastPts = time;

                    _videoFrameClones.Enqueue(new FFTexData
                    {
                        time = time,
                        data = frameClone,
                        w = vFrame.width,
                        h = vFrame.height,
                    });
                }
                _videoMutex.ReleaseMutex();
            }
        }
        private unsafe Texture2D UpdateVideo(int idx)
        {
            Profiler.BeginSample(nameof(UpdateVideo), this);
            AVFrame videoFrame;
            videoFrame = _videoFrames[idx];
            if (videoFrame.data[0] == null)
            {
                Profiler.EndSample();
                return null;
            }
            var tex = _texturePool.Get();
            if (tex.texture == null)
            {
                tex.texture = FFUnityFrameHelper.SaveFrame(videoFrame, videoFrame.width, videoFrame.height, _videoDecoder.HWPixelFormat);
            }
            else
            {
                FFUnityFrameHelper.SaveFrame(videoFrame, videoFrame.width, videoFrame.height, tex.texture, _videoDecoder.HWPixelFormat);
            }
            tex.texture.name = $"{name}-Texture2D-{idx}";
            _videoTextures.Enqueue(tex);
            Profiler.EndSample();
            return tex.texture;
        }
        private unsafe bool UpdateVideoFromClones()
        {
            Profiler.BeginSample(nameof(UpdateVideoFromClones), this);
            if (_videoFrameClones.Count == 0)
            {
                Profiler.EndSample();
                return false;
            }
            FFTexData videoFrame = _videoFrameClones.Dequeue();
            _lastTexData = videoFrame;
            Profiler.EndSample();
            return true;
        }
        private unsafe void AudioCallback(float[] data)
        {
            // Attempt to acquire the lock with minimal blocking
            if (!_audioLocker.WaitOne(0))
                return;

            // Reduce locking duration by processing audio data outside the lock as much as possible
            try
            {
                int availableSamples = _audioStream.Count;

                // Check if there are enough samples to fill the audio buffer
                _lastAudioRead = availableSamples >= data.Length;

                // Batch processing: dequeue as many elements as possible
                int samplesToDequeue = Math.Min(availableSamples, data.Length);

                for (int SampleIndex = 0; SampleIndex < samplesToDequeue; SampleIndex++)
                {
                    // Efficiently dequeue audio samples
                    _audioStream.TryDequeue(out data[SampleIndex]);
                }

                // Zero out the rest of the buffer if there aren't enough samples
                if (samplesToDequeue < data.Length)
                {
                    Array.Clear(data, samplesToDequeue, data.Length - samplesToDequeue);
                }
            }
            finally
            {
                // Always release the mutex to avoid deadlocks
                _audioLocker.ReleaseMutex();
            }
        }
        private unsafe void UpdateAudio(int idx)
        {
            Profiler.BeginSample(nameof(UpdateAudio), this);
            var audioFrame = _audioFrames[idx];
            if (audioFrame.data[0] == null)
            {
                Profiler.EndSample();
                return;
            }
            List<float> vals = new List<float>();
            for (uint ch = 0; ch < _audioDecoder.Channels; ch++)
            {
                int size = ffmpeg.av_samples_get_buffer_size(null, 1, audioFrame.nb_samples, _audioDecoder.SampleFormat, 1);
                if (size < 0)
                {
                    UnityEngine.Debug.LogError("audio buffer size is less than zero");
                    Profiler.EndSample();
                    return;
                }
                byte[] backBuffer2 = new byte[size];
                float[] backBuffer3 = new float[size / sizeof(float)];
                Marshal.Copy((IntPtr)audioFrame.data[ch], backBuffer2, 0, size);
                Buffer.BlockCopy(backBuffer2, 0, backBuffer3, 0, backBuffer2.Length);
                for (int i = 0; i < backBuffer3.Length; i++)
                {
                    vals.Add(backBuffer3[i]);
                }
            }
            if (_audioLocker.WaitOne(0))
            {
                int c = vals.Count / _audioDecoder.Channels;
                for (int valueIndex = 0; valueIndex < c; valueIndex++)
                {
                    for (int ChannelIndex = 0; ChannelIndex < _audioDecoder.Channels; ChannelIndex++)
                    {
                        _audioStream.Enqueue(vals[valueIndex + c * ChannelIndex]);
                    }
                }
                _audioLocker.ReleaseMutex();
            }
            Profiler.EndSample();
        }
    }
}