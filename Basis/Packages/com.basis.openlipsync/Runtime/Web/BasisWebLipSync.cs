#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using Unity.Scripting.LifecycleManagement;

namespace OpenLipSync.Inference
{
    [NoAutoStaticsCleanup]
    internal static class BasisWebLipSync
    {
        public const int Loading = 1, Ready = 2, Failed = 3;
        public const int KernelVisemes = 15;
        [DllImport("__Internal")] public static extern int BasisWebLipSync_Initialize(byte[] model, int length);
        [DllImport("__Internal")] public static extern int BasisWebLipSync_State();
        [DllImport("__Internal")] public static extern int BasisWebLipSync_CreateSession();
        [DllImport("__Internal")] public static extern void BasisWebLipSync_DestroySession(int id);
        [DllImport("__Internal")] public static extern void BasisWebLipSync_ResetSession(int id);
        [DllImport("__Internal")] public static extern int BasisWebLipSync_Push(int id, float[] mel, int frames);
        [DllImport("__Internal")] public static extern int BasisWebLipSync_Flush();
        [DllImport("__Internal")] public static extern int BasisWebLipSync_Take(int id, float[] logits, int maxFrames);
        [DllImport("__Internal")] public static extern int BasisWebLipSync_Stats(float[] output);
    }

    internal sealed class WebStreamingSession : IDisposable
    {
        private const int Batch = 32;
        private readonly int _id;
        private readonly float[] _logits = new float[BasisWebLipSync.KernelVisemes * Batch];
        private int _available;
        private int _read;
        private bool _disposed;

        public WebStreamingSession()
        {
            _id = BasisWebLipSync.BasisWebLipSync_CreateSession();
            if (_id == 0) throw new InvalidOperationException("the web lip sync worker is not ready");
        }

        public void Submit(float[] melFrame)
        {
            if (!_disposed) BasisWebLipSync.BasisWebLipSync_Push(_id, melFrame, 1);
        }

        public bool TryTake(float[] probabilities, bool multiLabel)
        {
            if (_disposed) return false;
            if (_read >= _available)
            {
                _available = BasisWebLipSync.BasisWebLipSync_Take(_id, _logits, Batch);
                _read = 0;
                if (_available <= 0) return false;
            }
            int offset = _read++ * BasisWebLipSync.KernelVisemes;
            int count = Math.Min(probabilities.Length, BasisWebLipSync.KernelVisemes);
            if (multiLabel)
            {
                for (int i = 0; i < count; i++)
                {
                    float x = Math.Clamp(_logits[offset + i], -50f, 50f);
                    probabilities[i] = 1f / (1f + MathF.Exp(-x));
                }
                return true;
            }
            float max = float.MinValue;
            for (int i = 0; i < count; i++) if (_logits[offset + i] > max) max = _logits[offset + i];
            float sum = 0f;
            for (int i = 0; i < count; i++)
            {
                float e = MathF.Exp(_logits[offset + i] - max);
                probabilities[i] = e;
                sum += e;
            }
            float inv = sum > 0f ? 1f / sum : 0f;
            for (int i = 0; i < count; i++) probabilities[i] *= inv;
            return true;
        }

        public void Reset()
        {
            _available = 0;
            _read = 0;
            if (!_disposed) BasisWebLipSync.BasisWebLipSync_ResetSession(_id);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            BasisWebLipSync.BasisWebLipSync_DestroySession(_id);
        }
    }
}
#endif
