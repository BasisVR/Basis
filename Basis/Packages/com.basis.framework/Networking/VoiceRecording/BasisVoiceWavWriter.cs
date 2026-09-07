using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Basis.Scripts.Networking.VoiceRecording
{
    /// <summary>
    /// Streams mono 16-bit PCM to a .wav file. The 44-byte header is written as a
    /// placeholder up front and patched with the final sizes on <see cref="Dispose"/>,
    /// so a recording of unknown length can be written incrementally.
    /// </summary>
    public sealed class BasisVoiceWavWriter : IDisposable
    {
        private FileStream _stream;
        private BinaryWriter _writer;
        private readonly int _sampleRate;
        private int _samplesWritten;
        private bool _closed;
        private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new ConcurrentQueue<PendingWrite>();
        private readonly ConcurrentQueue<byte[]> _bufferPool = new ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent _writeSignal = new AutoResetEvent(false);
        private Thread _writeThread;
        private volatile bool _stopRequested;
        private volatile Exception _writeException;

        private struct PendingWrite
        {
            public byte[] Buffer;
            public int Bytes;
        }

        public string Path { get; }

        public BasisVoiceWavWriter(string path, int sampleRate)
        {
            Path = path;
            _sampleRate = sampleRate;
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new BinaryWriter(_stream);
            for (int i = 0; i < 44; i++)
            {
                _writer.Write((byte)0);
            }
            _writer.Flush();
            _writeThread = new Thread(WriteThreadLoop)
            {
                IsBackground = true,
                Name = "Basis Voice Wav Writer",
            };
            _writeThread.Start();
        }

        public void Write(float[] samples, int count)
        {
            if (_closed || samples == null || count <= 0)
            {
                return;
            }
            if (!_bufferPool.TryDequeue(out byte[] buffer) || buffer.Length < count * 2)
            {
                buffer = new byte[Math.Max(count * 2, 8192)];
            }
            int bytes = 0;
            for (int i = 0; i < count; i++)
            {
                float f = samples[i];
                if (f > 1f) f = 1f;
                else if (f < -1f) f = -1f;
                short s = (short)(f * 32767f);
                buffer[bytes++] = (byte)s;
                buffer[bytes++] = (byte)(s >> 8);
            }
            _samplesWritten += count;
            _pendingWrites.Enqueue(new PendingWrite { Buffer = buffer, Bytes = bytes });
            _writeSignal.Set();
        }

        private void WriteThreadLoop()
        {
            while (true)
            {
                _writeSignal.WaitOne();
                while (_pendingWrites.TryDequeue(out PendingWrite item))
                {
                    try
                    {
                        _stream.Write(item.Buffer, 0, item.Bytes);
                    }
                    catch (Exception ex)
                    {
                        if (_writeException == null)
                        {
                            _writeException = ex;
                        }
                    }
                    if (_bufferPool.Count < 8)
                    {
                        _bufferPool.Enqueue(item.Buffer);
                    }
                }
                if (_stopRequested && _pendingWrites.IsEmpty)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            _stopRequested = true;
            _writeSignal.Set();
            try
            {
                _writeThread?.Join();
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisVoiceWavWriter] Writer thread join failed for {Path}: {ex}");
            }
            _writeThread = null;
            if (_writeException != null)
            {
                BasisDebug.LogError($"[BasisVoiceWavWriter] Background write failed for {Path}: {_writeException}");
            }
            try
            {
                int dataBytes = _samplesWritten * 2;
                _stream.Seek(0, SeekOrigin.Begin);
                WriteTag('R', 'I', 'F', 'F');
                _writer.Write(36 + dataBytes);
                WriteTag('W', 'A', 'V', 'E');
                WriteTag('f', 'm', 't', ' ');
                _writer.Write(16);
                _writer.Write((short)1);
                _writer.Write((short)1);
                _writer.Write(_sampleRate);
                _writer.Write(_sampleRate * 2);
                _writer.Write((short)2);
                _writer.Write((short)16);
                WriteTag('d', 'a', 't', 'a');
                _writer.Write(dataBytes);
                _writer.Flush();
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisVoiceWavWriter] Failed to finalize {Path}: {ex}");
            }
            finally
            {
                _writer?.Dispose();
                _writer = null;
                _stream = null;
                _writeSignal.Dispose();
            }
        }

        private void WriteTag(char a, char b, char c, char d)
        {
            _writer.Write((byte)a);
            _writer.Write((byte)b);
            _writer.Write((byte)c);
            _writer.Write((byte)d);
        }
    }
}
