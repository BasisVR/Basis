#if UNITY_EDITOR_LINUX || (UNITY_STANDALONE_LINUX && !UNITY_EDITOR)
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
namespace Basis
{
    public sealed unsafe class BasisV4L2VideoOutputSink : IBasisVideoOutputSink
    {
        private const int oRdwr = 0x0002, EINTR = 4;
        private const uint FourccXBGR32 = 0x34325258;
        private const ulong vidiocQuerycap = 0x80685600, vidiocSFmt = 0xC0D05605;
        private const uint v4l2BufTypeVideoOutput = 2, v4l2FieldNone = 1, v4l2ColorspaceSrgb = 8;
        private const uint v4l2CapVideoOutput = 0x00000002, v4l2CapDeviceCaps = 0x80000000;
        private const string LoopbackDriverName = "v4l2 loopback";
        private static readonly HashSet<string> ClaimedDevices = new HashSet<string>();
        private int fd = -1;
        private string devicePath;
        private Action<AsyncGPUReadbackRequest> readbackCallback;
        public string FailureMessage { get; private set; }
        public bool SupportsAlpha => false;
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(string path, int flags);
        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);
        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, ulong request, void* argp);
        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        private static extern IntPtr Write(int fd, void* buffer, UIntPtr count);
        [StructLayout(LayoutKind.Sequential)]
        private struct V4L2Capability
        {
            public fixed byte Driver[16];
            public fixed byte Card[32];
            public fixed byte BusInfo[32];
            public uint Version, Capabilities, DeviceCaps;
            public fixed uint Reserved[3];
        }
        [StructLayout(LayoutKind.Explicit, Size = 208)]
        private struct V4L2Format
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public uint Width;
            [FieldOffset(12)] public uint Height;
            [FieldOffset(16)] public uint PixelFormat;
            [FieldOffset(20)] public uint Field;
            [FieldOffset(24)] public uint BytesPerLine;
            [FieldOffset(28)] public uint SizeImage;
            [FieldOffset(32)] public uint Colorspace;
        }
        public bool Start(BasisVideoOutputSettings settings, GameObject host)
        {
            fd = OpenDevice(settings.DevicePath, out devicePath);
            if (fd < 0)
            {
                BasisDebug.LogError("No v4l2loopback output device found. Load the module first: sudo modprobe v4l2loopback exclusive_caps=1", BasisDebug.LogTag.Camera);
                return false;
            }

            var format = new V4L2Format
            {
                Type = v4l2BufTypeVideoOutput,
                Width = (uint)settings.Width,
                Height = (uint)settings.Height,
                PixelFormat = FourccXBGR32,
                Field = v4l2FieldNone,
                BytesPerLine = (uint)settings.Width * 4,
                SizeImage = (uint)(settings.Width * settings.Height * 4),
                Colorspace = v4l2ColorspaceSrgb
            };
            if (Ioctl(fd, vidiocSFmt, &format) != 0)
            {
                BasisDebug.LogError($"VIDIOC_S_FMT on {devicePath} failed (errno {Marshal.GetLastWin32Error()})", BasisDebug.LogTag.Camera);
                Stop();
                return false;
            }

            readbackCallback = OnReadbackComplete;
            BasisDebug.Log($"V4L2 video output writing to {devicePath}", BasisDebug.LogTag.Camera);
            return true;
        }
        public void PushFrame(RenderTexture frame, bool keepAlpha)
        {
            if (fd < 0 || FailureMessage != null) return;
            AsyncGPUReadback.Request(frame, 0, TextureFormat.BGRA32, readbackCallback);
        }
        public void Stop()
        {
            if (fd >= 0)
            {
                Close(fd);
                fd = -1;
            }
            if (devicePath != null)
            {
                ClaimedDevices.Remove(devicePath);
                devicePath = null;
            }
        }
        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            if (fd < 0 || FailureMessage != null) return;
            if (request.hasError)
            {
                FailureMessage = "GPU readback for the video output failed.";
                return;
            }

            NativeArray<byte> data = request.GetData<byte>();
            byte* source = (byte*)data.GetUnsafeReadOnlyPtr();
            long total = data.Length, offset = 0;
            while (offset < total)
            {
                long written = Write(fd, source + offset, (UIntPtr)(total - offset)).ToInt64();
                if (written < 0)
                {
                    int errno = Marshal.GetLastWin32Error();
                    if (errno == EINTR) continue;
                    FailureMessage = $"Writing to {devicePath} failed (errno {errno}).";
                    return;
                }
                offset += written;
            }
        }
        private static int OpenDevice(string overridePath, out string chosenPath)
        {
            if (!string.IsNullOrEmpty(overridePath))
            {
                chosenPath = overridePath;
                if (!ClaimedDevices.Add(overridePath)) return -1;
                int overrideFd = Open(overridePath, oRdwr);
                if (overrideFd < 0) ClaimedDevices.Remove(overridePath);
                return overrideFd;
            }

            string[] candidates;
            try
            {
                candidates = Directory.GetFiles("/dev", "video*");
            }
            catch (Exception)
            {
                chosenPath = null;
                return -1;
            }
            Array.Sort(candidates);

            foreach (string candidate in candidates)
            {
                if (ClaimedDevices.Contains(candidate)) continue;
                int candidateFd = Open(candidate, oRdwr);
                if (candidateFd < 0) continue;

                var capability = default(V4L2Capability);
                if (Ioctl(candidateFd, vidiocQuerycap, &capability) == 0)
                {
                    uint caps = (capability.Capabilities & v4l2CapDeviceCaps) != 0 ? capability.DeviceCaps : capability.Capabilities;
                    if ((caps & v4l2CapVideoOutput) != 0 && DriverNameMatches(capability.Driver))
                    {
                        chosenPath = candidate;
                        ClaimedDevices.Add(candidate);
                        return candidateFd;
                    }
                }
                Close(candidateFd);
            }

            chosenPath = null;
            return -1;
        }
        private static bool DriverNameMatches(byte* driver)
        {
            for (int Index = 0; Index < LoopbackDriverName.Length; Index++)
            {
                if (driver[Index] != (byte)LoopbackDriverName[Index]) return false;
            }
            return driver[LoopbackDriverName.Length] == 0;
        }
    }
}
#endif
