using System;
using System.IO;

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
        }

        public void Write(float[] samples, int count)
        {
            if (_closed || samples == null || count <= 0)
            {
                return;
            }
            for (int i = 0; i < count; i++)
            {
                float f = samples[i];
                if (f > 1f) f = 1f;
                else if (f < -1f) f = -1f;
                _writer.Write((short)(f * 32767f));
            }
            _samplesWritten += count;
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
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
