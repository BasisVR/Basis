#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using OpenLipSync.Inference.OVRCompat;
using Unity.Scripting.LifecycleManagement;

namespace OpenLipSync.Inference
{
    [NoAutoStaticsCleanup]
    internal static class BasisNativeLipSync
    {
        private const string Library = "basis_openlipsync";
        public const int Mels = 80, Channels = 256, Hidden = 512, Visemes = 15;
        public static readonly int[] Dilations = { 1, 2, 4, 8, 16, 32, 1, 2, 4, 8 };
        [DllImport(Library)] public static extern int basis_ols_weight_count();
        [DllImport(Library)] public static extern IntPtr basis_ols_net_create(float[] weights, int count);
        [DllImport(Library)] public static extern void basis_ols_net_destroy(IntPtr net);
        [DllImport(Library)] public static extern IntPtr basis_ols_session_create();
        [DllImport(Library)] public static extern void basis_ols_session_destroy(IntPtr session);
        [DllImport(Library)] public static extern void basis_ols_session_reset(IntPtr session);
        [DllImport(Library)] public static extern int basis_ols_step(IntPtr net, IntPtr[] sessions, int[] frames, int count, float[] mel, float[] logits);

        public static int ReceptiveField
        {
            get
            {
                int frames = 1;
                for (int i = 0; i < Dilations.Length; i++) frames += 2 * Dilations[i];
                return frames;
            }
        }

        private struct Tensor
        {
            public int Type, Start, End;
            public long[] Dims;
        }

        public static float[] ReadWeights(byte[] model, out string error)
        {
            var tensors = new Dictionary<string, Tensor>();
            var inputs = new Dictionary<string, long[]>();
            try
            {
                int pos = 0;
                while (pos < model.Length)
                {
                    if (Next(model, ref pos, model.Length, out int wire, out _, out int start, out int stop) == 7 && wire == 2) ReadGraph(model, start, stop, tensors, inputs);
                }
            }
            catch (FormatException ex)
            {
                error = "the model is not a readable ONNX file: " + ex.Message;
                return null;
            }
            for (int c = 0; c < Dilations.Length; c++)
            {
                long[] expected = { 1, Channels, 2 * Dilations[c] };
                if (!inputs.TryGetValue("cache_" + c, out long[] dims) || !Same(dims, expected))
                {
                    error = $"cache_{c} is [{(dims == null ? "missing" : string.Join(",", dims))}], the native kernel expects [{string.Join(",", expected)}]";
                    return null;
                }
            }
            string head = null;
            long[] headDims = { Channels, Visemes };
            foreach (KeyValuePair<string, Tensor> pair in tensors)
            {
                if (!Same(pair.Value.Dims, headDims)) continue;
                if (head != null)
                {
                    error = "expected one 256x15 head weight, found several";
                    return null;
                }
                head = pair.Key;
            }
            if (head == null)
            {
                error = "expected one 256x15 head weight, found none";
                return null;
            }
            var plan = new List<(string name, long[] dims)> { ("m.cmvn.mean", new long[] { Mels }), ("m.cmvn.std", new long[] { Mels }), ("m.input_proj.weight", new long[] { Channels, Mels, 1 }), ("m.input_proj.bias", new long[] { Channels }) };
            for (int b = 0; b < Dilations.Length; b++)
            {
                string p = "m.blocks." + b + ".";
                plan.Add((p + "norm.weight", new long[] { Channels }));
                plan.Add((p + "norm.bias", new long[] { Channels }));
                plan.Add((p + "dw.weight", new long[] { Channels, 1, 3 }));
                plan.Add((p + "dw.bias", new long[] { Channels }));
                plan.Add((p + "pw1.weight", new long[] { Hidden, Channels, 1 }));
                plan.Add((p + "pw1.bias", new long[] { Hidden }));
                plan.Add((p + "pw2.weight", new long[] { Channels, Hidden, 1 }));
                plan.Add((p + "pw2.bias", new long[] { Channels }));
            }
            plan.Add(("m.out_norm.weight", new long[] { Channels }));
            plan.Add(("m.out_norm.bias", new long[] { Channels }));
            plan.Add(("m.head.bias", new long[] { Visemes }));
            plan.Add((head, headDims));
            int total = 0;
            for (int i = 0; i < plan.Count; i++) total += Count(plan[i].dims);
            var weights = new float[total];
            int offset = 0;
            for (int i = 0; i < plan.Count; i++)
            {
                (string name, long[] dims) = plan[i];
                int count = Count(dims);
                if (!tensors.TryGetValue(name, out Tensor tensor))
                {
                    error = "the model has no " + name;
                    return null;
                }
                if (tensor.Type != 1 || !Same(tensor.Dims, dims) || tensor.End - tensor.Start != count * 4)
                {
                    error = $"{name} is [{string.Join(",", tensor.Dims)}], the native kernel expects [{string.Join(",", dims)}]";
                    return null;
                }
                Buffer.BlockCopy(model, tensor.Start, weights, offset * 4, count * 4);
                offset += count;
            }
            error = null;
            return weights;
        }

        private static int Count(long[] dims)
        {
            long count = 1;
            for (int i = 0; i < dims.Length; i++) count *= dims[i];
            return (int)count;
        }

        private static bool Same(long[] a, long[] b)
        {
            if (a == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static ulong Varint(byte[] data, ref int pos, int end)
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (pos >= end) throw new FormatException("truncated varint");
                byte b = data[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw new FormatException("varint too long");
        }

        private static int Next(byte[] data, ref int pos, int end, out int wire, out ulong value, out int start, out int stop)
        {
            ulong key = Varint(data, ref pos, end);
            wire = (int)(key & 7);
            value = 0;
            start = stop = pos;
            switch (wire)
            {
                case 0:
                    value = Varint(data, ref pos, end);
                    break;
                case 1:
                    pos += 8;
                    break;
                case 2:
                    ulong length = Varint(data, ref pos, end);
                    if (length > (ulong)(end - pos)) throw new FormatException("truncated field");
                    start = pos;
                    stop = pos + (int)length;
                    pos = stop;
                    break;
                case 5:
                    pos += 4;
                    break;
                default:
                    throw new FormatException("unsupported wire type " + wire);
            }
            if (pos > end) throw new FormatException("truncated field");
            return (int)(key >> 3);
        }

        private static bool Find(byte[] data, int pos, int end, int target, out int start, out int stop)
        {
            while (pos < end)
            {
                if (Next(data, ref pos, end, out int wire, out _, out start, out stop) == target && wire == 2) return true;
            }
            start = stop = 0;
            return false;
        }

        private static void ReadGraph(byte[] data, int pos, int end, Dictionary<string, Tensor> tensors, Dictionary<string, long[]> inputs)
        {
            while (pos < end)
            {
                int field = Next(data, ref pos, end, out int wire, out _, out int start, out int stop);
                if (wire != 2) continue;
                if (field == 5) ReadTensor(data, start, stop, tensors);
                else if (field == 11) ReadInput(data, start, stop, inputs);
            }
        }

        private static void ReadTensor(byte[] data, int pos, int end, Dictionary<string, Tensor> tensors)
        {
            var dims = new List<long>();
            var tensor = new Tensor();
            string name = "";
            while (pos < end)
            {
                int field = Next(data, ref pos, end, out int wire, out ulong value, out int start, out int stop);
                if (field == 1 && wire == 0) dims.Add((long)value);
                else if (field == 1 && wire == 2)
                {
                    for (int at = start; at < stop;) dims.Add((long)Varint(data, ref at, stop));
                }
                else if (field == 2 && wire == 0) tensor.Type = (int)value;
                else if (field == 8 && wire == 2) name = Encoding.UTF8.GetString(data, start, stop - start);
                else if ((field == 9 || field == 4) && wire == 2)
                {
                    tensor.Start = start;
                    tensor.End = stop;
                }
            }
            tensor.Dims = dims.ToArray();
            tensors[name] = tensor;
        }

        private static void ReadInput(byte[] data, int pos, int end, Dictionary<string, long[]> inputs)
        {
            string name = "";
            var dims = new List<long>();
            while (pos < end)
            {
                int field = Next(data, ref pos, end, out int wire, out _, out int start, out int stop);
                if (field == 1 && wire == 2) name = Encoding.UTF8.GetString(data, start, stop - start);
                else if (field == 2 && wire == 2 && Find(data, start, stop, 1, out int tensorStart, out int tensorStop) && Find(data, tensorStart, tensorStop, 2, out int shapeStart, out int shapeStop))
                {
                    dims.Clear();
                    for (int at = shapeStart; at < shapeStop;)
                    {
                        if (Next(data, ref at, shapeStop, out int dimWire, out _, out int dimStart, out int dimStop) != 1 || dimWire != 2) continue;
                        long dim = -1;
                        for (int d = dimStart; d < dimStop;)
                        {
                            if (Next(data, ref d, dimStop, out int valueWire, out ulong value, out _, out _) == 1 && valueWire == 0) dim = (long)value;
                        }
                        dims.Add(dim);
                    }
                }
            }
            inputs[name] = dims.ToArray();
        }
    }

    internal sealed class NativeStreamingSession : IDisposable
    {
        public float[] Mel = new float[BasisNativeLipSync.Mels * 8];
        public bool[] Settled = new bool[8];
        public int Frames, Computed, Submitted;

        public IntPtr Handle { get; private set; }

        public NativeStreamingSession()
        {
            Handle = BasisNativeLipSync.basis_ols_session_create();
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("the native lip sync session could not be allocated");
        }

        public void Submit(float[] melFrame, bool settled)
        {
            if (Frames == Settled.Length) Array.Resize(ref Settled, Frames * 2);
            Settled[Frames++] = settled;
            if (settled) return;
            int at = Computed * BasisNativeLipSync.Mels;
            if (at + BasisNativeLipSync.Mels > Mel.Length) Array.Resize(ref Mel, Mel.Length * 2);
            Array.Copy(melFrame, 0, Mel, at, BasisNativeLipSync.Mels);
            Computed++;
        }

        public static void Activate(float[] logits, int offset, float[] probabilities, bool multiLabel)
        {
            int count = Math.Min(probabilities.Length, BasisNativeLipSync.Visemes);
            if (multiLabel)
            {
                for (int i = 0; i < count; i++)
                {
                    float x = Math.Clamp(logits[offset + i], -50f, 50f);
                    probabilities[i] = 1f / (1f + MathF.Exp(-x));
                }
                return;
            }
            float max = float.MinValue;
            for (int i = 0; i < count; i++) if (logits[offset + i] > max) max = logits[offset + i];
            float sum = 0f;
            for (int i = 0; i < count; i++)
            {
                float e = MathF.Exp(logits[offset + i] - max);
                probabilities[i] = e;
                sum += e;
            }
            float inv = sum > 0f ? 1f / sum : 0f;
            for (int i = 0; i < count; i++) probabilities[i] *= inv;
        }

        public void Clear()
        {
            Frames = Computed = Submitted = 0;
        }

        public void Reset()
        {
            Clear();
            if (Handle != IntPtr.Zero) BasisNativeLipSync.basis_ols_session_reset(Handle);
        }

        public void Dispose()
        {
            IntPtr handle = Handle;
            Handle = IntPtr.Zero;
            Clear();
            if (handle != IntPtr.Zero) BasisNativeLipSync.basis_ols_session_destroy(handle);
        }
    }

    internal sealed class NativeLipSyncNet : IDisposable
    {
        private struct Entry
        {
            public AudioContext Context;
            public Frame Target;
        }

        private readonly List<Entry> _queued = new List<Entry>(16);
        private IntPtr _net;
        private IntPtr[] _sessions = new IntPtr[16];
        private int[] _counts = new int[16];
        private float[] _mel = new float[BasisNativeLipSync.Mels * 64];
        private float[] _logits = new float[BasisNativeLipSync.Visemes * 64];

        private NativeLipSyncNet(IntPtr net)
        {
            _net = net;
        }

        public static NativeLipSyncNet Create(byte[] model, out string error)
        {
            try
            {
                float[] weights = BasisNativeLipSync.ReadWeights(model, out error);
                if (weights == null) return null;
                int expected = BasisNativeLipSync.basis_ols_weight_count();
                if (weights.Length != expected)
                {
                    error = $"the model has {weights.Length} weights, the native kernel expects {expected}";
                    return null;
                }
                IntPtr net = BasisNativeLipSync.basis_ols_net_create(weights, weights.Length);
                if (net == IntPtr.Zero)
                {
                    error = "the native kernel could not allocate the network";
                    return null;
                }
                return new NativeLipSyncNet(net);
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        public void Queue(AudioContext context, Frame target)
        {
            for (int i = 0; i < _queued.Count; i++)
            {
                if (_queued[i].Context != context) continue;
                _queued[i] = new Entry { Context = context, Target = target };
                return;
            }
            _queued.Add(new Entry { Context = context, Target = target });
        }

        public bool Flush()
        {
            int count = _queued.Count;
            if (count == 0) return true;
            bool ok = false;
            try
            {
                int sessions = 0, frames = 0;
                for (int i = 0; i < count; i++)
                {
                    NativeStreamingSession session = _queued[i].Context.Native;
                    session.Submitted = session.Handle != IntPtr.Zero ? session.Computed : 0;
                    if (session.Submitted == 0) continue;
                    if (sessions == _sessions.Length)
                    {
                        Array.Resize(ref _sessions, sessions * 2);
                        Array.Resize(ref _counts, sessions * 2);
                    }
                    int needed = (frames + session.Submitted) * BasisNativeLipSync.Mels;
                    if (needed > _mel.Length) Array.Resize(ref _mel, Math.Max(needed, _mel.Length * 2));
                    Array.Copy(session.Mel, 0, _mel, frames * BasisNativeLipSync.Mels, session.Submitted * BasisNativeLipSync.Mels);
                    _sessions[sessions] = session.Handle;
                    _counts[sessions++] = session.Submitted;
                    frames += session.Submitted;
                }
                if (frames * BasisNativeLipSync.Visemes > _logits.Length) Array.Resize(ref _logits, Math.Max(frames * BasisNativeLipSync.Visemes, _logits.Length * 2));
                ok = sessions == 0 || BasisNativeLipSync.basis_ols_step(_net, _sessions, _counts, sessions, _mel, _logits) == frames;
                if (ok)
                {
                    int offset = 0;
                    for (int i = 0; i < count; i++) _queued[i].Context.ApplyNative(_logits, ref offset, _queued[i].Target);
                }
            }
            finally
            {
                for (int i = 0; i < count; i++) _queued[i].Context.Native.Clear();
                _queued.Clear();
            }
            return ok;
        }

        public void Dispose()
        {
            _queued.Clear();
            IntPtr net = _net;
            _net = IntPtr.Zero;
            if (net != IntPtr.Zero) BasisNativeLipSync.basis_ols_net_destroy(net);
        }
    }
}
#endif
