using System.Runtime.InteropServices;
using Basis.Logging;

namespace Basis
{
    /// <summary>
    /// Captures a real system recording device (e.g. "CABLE Output (VB-Audio Virtual Cable)"),
    /// encodes it with the same Opus settings the simulated crowd uses, and publishes 20 ms frames
    /// for VoiceSender to transmit. Lets a load test carry actual audio a listener can judge,
    /// instead of a synthetic sweep.
    ///
    /// winmm/waveIn rather than WASAPI: no package dependency, and 48 kHz 16-bit mono is all the
    /// encoder wants. waveInGetDevCaps truncates names to 31 characters, so device selection is a
    /// case-insensitive substring match and every device is logged at startup.
    /// </summary>
    public static class MicrophoneCapture
    {
        private const int SampleRate = 48000;
        private const int BufferCount = 8;
        private const int RingSize = 64;

        private const int MMSYSERR_NOERROR = 0;
        private const int WAVE_FORMAT_PCM = 1;
        private const int WHDR_DONE = 0x00000001;
        private const int CALLBACK_NULL = 0;

        private const float SilenceThreshold = 0.0007f;
        private const int RmsWindow = 10;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct WAVEINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [DllImport("winmm.dll")]
        private static extern int waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Ansi, EntryPoint = "waveInGetDevCapsA")]
        private static extern int waveInGetDevCaps(IntPtr deviceId, ref WAVEINCAPS caps, int size);

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr phwi, int deviceId, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, int flags);

        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr hdr, int size);

        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr hdr, int size);

        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hwi, IntPtr hdr, int size);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hwi);

        private static readonly byte[]?[] _ringData = new byte[RingSize][];
        private static readonly bool[] _ringSpeech = new bool[RingSize];
        private static long _ringWrite;

        private static IntPtr _hwi;
        private static IntPtr[] _headers = Array.Empty<IntPtr>();
        private static IntPtr[] _buffers = Array.Empty<IntPtr>();
        private static Thread? _thread;
        private static volatile bool _running;
        private static int _frameSamples;
        private static int _bufferBytes;

        public static bool Active { get; private set; }
        public static string DeviceName { get; private set; } = string.Empty;
        public static long FramesCaptured;
        public static long FramesSpeech;
        private static float _peak;

        /// <summary>
        /// Loudest sample seen since the last call, then resets. An exact 0 means the device is
        /// delivering digital silence (nothing routed into the cable); a small non-zero means signal
        /// is arriving but sits under the transmit threshold.
        /// </summary>
        public static float TakePeak()
        {
            float peak = _peak;
            _peak = 0f;
            return peak;
        }

        public static List<string> ListDevices()
        {
            var names = new List<string>();
            int count = waveInGetNumDevs();
            for (int i = 0; i < count; i++)
            {
                WAVEINCAPS caps = default;
                if (waveInGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf<WAVEINCAPS>()) == MMSYSERR_NOERROR)
                    names.Add(caps.szPname ?? string.Empty);
                else
                    names.Add(string.Empty);
            }
            return names;
        }

        public static bool Start(string deviceMatch, int frameMs, int bitrate)
        {
            if (Active) return true;
            if (!OperatingSystem.IsWindows())
            {
                BNL.LogError("[Mic] System microphone capture requires Windows (winmm).");
                return false;
            }

            List<string> devices;
            try
            {
                devices = ListDevices();
            }
            catch (Exception ex)
            {
                BNL.LogError($"[Mic] Could not enumerate recording devices: {ex.Message}");
                return false;
            }

            if (devices.Count == 0)
            {
                BNL.LogError("[Mic] No recording devices present.");
                return false;
            }

            BNL.Log($"[Mic] {devices.Count} recording device(s):");
            for (int i = 0; i < devices.Count; i++)
                BNL.Log($"[Mic]   [{i}] {devices[i]}");

            int deviceId = -1;
            if (!string.IsNullOrWhiteSpace(deviceMatch))
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].Contains(deviceMatch, StringComparison.OrdinalIgnoreCase))
                    {
                        deviceId = i;
                        break;
                    }
                }
            }

            if (deviceId < 0)
            {
                BNL.LogError($"[Mic] No recording device matched \"{deviceMatch}\". Falling back to synthetic voice.");
                return false;
            }

            DeviceName = devices[deviceId];
            _frameSamples = SampleRate / 1000 * Math.Max(1, frameMs);
            _bufferBytes = _frameSamples * 2;

            var format = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = SampleRate,
                nAvgBytesPerSec = SampleRate * 2,
                nBlockAlign = 2,
                wBitsPerSample = 16,
                cbSize = 0,
            };

            int result = waveInOpen(out _hwi, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
            if (result != MMSYSERR_NOERROR)
            {
                BNL.LogError($"[Mic] waveInOpen failed on \"{DeviceName}\" (code {result}). Falling back to synthetic voice.");
                _hwi = IntPtr.Zero;
                return false;
            }

            int headerSize = Marshal.SizeOf<WAVEHDR>();
            _headers = new IntPtr[BufferCount];
            _buffers = new IntPtr[BufferCount];
            for (int i = 0; i < BufferCount; i++)
            {
                _buffers[i] = Marshal.AllocHGlobal(_bufferBytes);
                _headers[i] = Marshal.AllocHGlobal(headerSize);
                var hdr = new WAVEHDR { lpData = _buffers[i], dwBufferLength = (uint)_bufferBytes };
                Marshal.StructureToPtr(hdr, _headers[i], false);
                waveInPrepareHeader(_hwi, _headers[i], headerSize);
                waveInAddBuffer(_hwi, _headers[i], headerSize);
            }

            _running = true;
            Active = true;
            _thread = new Thread(() => CaptureLoop(bitrate, headerSize))
            {
                Name = "MicrophoneCapture",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();

            waveInStart(_hwi);
            BNL.Log($"[Mic] Capturing \"{DeviceName}\" at {SampleRate} Hz mono, {frameMs} ms frames, {bitrate} bps Opus.");
            return true;
        }

        private static void CaptureLoop(int bitrate, int headerSize)
        {
            OpusSharp.Core.Dynamic.OpusEncoder? encoder = null;
            try
            {
                encoder = new OpusSharp.Core.Dynamic.OpusEncoder(
                    SampleRate, 1, OpusSharp.Core.OpusPredefinedValues.OPUS_APPLICATION_VOIP);
                encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_BITRATE, bitrate);
                encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_COMPLEXITY, 5);
                encoder.Ctl(OpusSharp.Core.EncoderCTL.OPUS_SET_INBAND_FEC, 1);
            }
            catch (Exception ex)
            {
                BNL.LogError($"[Mic] Opus encoder unavailable ({ex.Message}); microphone capture disabled.");
                Active = false;
                return;
            }

            float[] pcm = new float[_frameSamples];
            byte[] scratch = new byte[_frameSamples * 4];
            float[] rmsHistory = new float[RmsWindow];
            int rmsIndex = 0;

            while (_running)
            {
                bool progressed = false;

                for (int b = 0; b < _headers.Length && _running; b++)
                {
                    WAVEHDR hdr = Marshal.PtrToStructure<WAVEHDR>(_headers[b]);
                    if ((hdr.dwFlags & WHDR_DONE) == 0) continue;

                    progressed = true;
                    int recorded = (int)hdr.dwBytesRecorded;
                    int samples = Math.Min(recorded / 2, _frameSamples);

                    double sumSq = 0;
                    float framePeak = 0f;
                    for (int i = 0; i < samples; i++)
                    {
                        short s = Marshal.ReadInt16(hdr.lpData, i * 2);
                        float v = s / 32768f;
                        pcm[i] = v;
                        sumSq += v * v;
                        float mag = MathF.Abs(v);
                        if (mag > framePeak) framePeak = mag;
                    }
                    for (int i = samples; i < _frameSamples; i++) pcm[i] = 0f;
                    if (framePeak > _peak) _peak = framePeak;

                    rmsHistory[rmsIndex] = samples > 0 ? (float)(sumSq / samples) : 0f;
                    rmsIndex = (rmsIndex + 1) % RmsWindow;
                    float averagePower = 0f;
                    for (int i = 0; i < RmsWindow; i++) averagePower += rmsHistory[i];
                    bool speech = MathF.Sqrt(averagePower / RmsWindow) > SilenceThreshold;

                    try
                    {
                        int length = encoder.Encode(pcm, _frameSamples, scratch, scratch.Length);
                        if (length > 0)
                        {
                            byte[] frame = new byte[length];
                            Buffer.BlockCopy(scratch, 0, frame, 0, length);
                            long slot = _ringWrite;
                            _ringData[slot % RingSize] = frame;
                            _ringSpeech[slot % RingSize] = speech;
                            Volatile.Write(ref _ringWrite, slot + 1);
                            Interlocked.Increment(ref FramesCaptured);
                            if (speech) Interlocked.Increment(ref FramesSpeech);
                        }
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"[Mic] Opus encode failed: {ex.Message}");
                    }

                    waveInUnprepareHeader(_hwi, _headers[b], headerSize);
                    hdr.dwFlags = 0;
                    hdr.dwBytesRecorded = 0;
                    hdr.dwBufferLength = (uint)_bufferBytes;
                    Marshal.StructureToPtr(hdr, _headers[b], false);
                    waveInPrepareHeader(_hwi, _headers[b], headerSize);
                    waveInAddBuffer(_hwi, _headers[b], headerSize);
                }

                if (!progressed) Thread.Sleep(2);
            }

            try { encoder.Dispose(); } catch { }
        }

        public static bool TryRead(ref long cursor, out byte[] frame, out bool isSpeech)
        {
            frame = Array.Empty<byte>();
            isSpeech = false;
            if (!Active) return false;

            long write = Volatile.Read(ref _ringWrite);
            if (cursor >= write) return false;
            if (cursor < write - RingSize) cursor = write - RingSize;

            byte[]? data = _ringData[cursor % RingSize];
            isSpeech = _ringSpeech[cursor % RingSize];
            cursor++;
            if (data == null) return false;

            frame = data;
            return true;
        }

        public static long NewestFrameIndex() => Volatile.Read(ref _ringWrite);

        public static void Stop()
        {
            if (!Active) return;
            Active = false;
            _running = false;
            try { _thread?.Join(500); } catch { }

            if (_hwi != IntPtr.Zero)
            {
                waveInStop(_hwi);
                waveInReset(_hwi);
                int headerSize = Marshal.SizeOf<WAVEHDR>();
                for (int i = 0; i < _headers.Length; i++)
                {
                    if (_headers[i] == IntPtr.Zero) continue;
                    waveInUnprepareHeader(_hwi, _headers[i], headerSize);
                    Marshal.FreeHGlobal(_headers[i]);
                    _headers[i] = IntPtr.Zero;
                }
                for (int i = 0; i < _buffers.Length; i++)
                {
                    if (_buffers[i] == IntPtr.Zero) continue;
                    Marshal.FreeHGlobal(_buffers[i]);
                    _buffers[i] = IntPtr.Zero;
                }
                waveInClose(_hwi);
                _hwi = IntPtr.Zero;
            }
        }
    }
}
