using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Concentus.Native
{
    internal partial class KernelInteropWindows
    {
        // if we use this flag then we can improperly load libraries that don't match the current architecture, so avoid it
        internal const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

        internal const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

        internal const ushort PROCESSOR_ARCHITECTURE_INTEL = 0x0000;
        internal const ushort PROCESSOR_ARCHITECTURE_AMD64 = 0x0009;
        internal const ushort PROCESSOR_ARCHITECTURE_ARM = 0x0005;
        internal const ushort PROCESSOR_ARCHITECTURE_ARM64 = 0x0012;
        internal const ushort PROCESSOR_ARCHITECTURE_IA64 = 0x0006;
        internal const ushort PROCESSOR_ARCHITECTURE_UNKNOWN = 0xFFFF;

#if NET8_0_OR_GREATER
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool FreeLibrary(IntPtr hModule);

        [LibraryImport("kernel32.dll", SetLastError = false)]
        internal static partial uint GetLastError();
#else
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", SetLastError = false)]
        internal static extern uint GetLastError();
#endif

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_INFO
        {
            internal ushort wProcessorArchitecture;
            internal ushort wReserved;
            internal uint dwPageSize;
            internal IntPtr lpMinimumApplicationAddress;
            internal IntPtr lpMaximumApplicationAddress;
            internal IntPtr dwActiveProcessorMask;
            internal uint dwNumberOfProcessors;
            internal uint dwProcessorType;
            internal uint dwAllocationGranularity;
            internal ushort wProcessorLevel;
            internal ushort wProcessorRevision;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void GetSystemInfo(ref SYSTEM_INFO Info);
    }
}
