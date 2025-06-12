using System;
using System.Runtime.InteropServices;

namespace RNNoise.NET
{
    public static class Native
    {
        public const string LIBRARY_NAME = "rnnoise";
        public const int FRAME_SIZE = 480;

        public const float SIGNAL_SCALE = short.MaxValue;
        public const float SIGNAL_SCALE_INV = 1f / short.MaxValue;

        [DllImport(LIBRARY_NAME)]
        public static extern int rnnoise_get_size();

        [DllImport(LIBRARY_NAME)]
        public static extern int rnnoise_init(IntPtr state, IntPtr model);

        [DllImport(LIBRARY_NAME)]
        public static extern IntPtr rnnoise_create(IntPtr model);

        [DllImport(LIBRARY_NAME)]
        public static extern void rnnoise_destroy(IntPtr state);

        [DllImport(LIBRARY_NAME)]
        public static extern unsafe float rnnoise_process_frame(IntPtr state, float* dataOut, float* dataIn);

        // TODO!!! Creating model from file and releasing isn't currently supported, it just uses the internal model
    }
}
