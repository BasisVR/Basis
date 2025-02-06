using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Concentus.Native
{
    internal partial class KernelInteropLinux
    {
        internal const int RTLD_NOW = 2;

        private static Lazy<ILibDl> _libDlImpl = new Lazy<ILibDl>(GetLibDL, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        // helpful list from https://en.wikipedia.org/wiki/Uname
        internal static readonly IReadOnlyDictionary<string, PlatformArchitecture> POSSIBLE_UNIX_MACHINES =
            new Dictionary<string, PlatformArchitecture>(StringComparer.OrdinalIgnoreCase)
            {
                { "amd64", PlatformArchitecture.X64 },
                { "x86_64", PlatformArchitecture.X64 },
                { "i686-64", PlatformArchitecture.X64 },
                { "x86", PlatformArchitecture.I386 },
                { "i686", PlatformArchitecture.I386 },
                { "i686-AT386", PlatformArchitecture.I386 },
                { "i386", PlatformArchitecture.I386 },
                { "x86pc", PlatformArchitecture.I386 },
                { "i86pc", PlatformArchitecture.I386 },
                { "armv6l", PlatformArchitecture.ArmV6 },
                { "armv7l", PlatformArchitecture.ArmV7 },
                { "arm64", PlatformArchitecture.Arm64 },
                { "aarch64", PlatformArchitecture.Arm64 },
                { "aarch64_be", PlatformArchitecture.Arm64 },
                { "armv8l", PlatformArchitecture.Arm64 },
                { "armv8b", PlatformArchitecture.Arm64 },
                { "mips64", PlatformArchitecture.Mips64 },
                { "ppc64", PlatformArchitecture.PowerPC64 },
                { "ppc64le", PlatformArchitecture.PowerPC64 },
            };

        private static ILibDl GetLibDL()
        {
            // See which flavor of libdl exists on this system
            try
            {
                LibDL lib = new LibDL();
                lib.DLError();
                return lib;
            }
            catch (Exception)
            {
                return new LibDL2();
            }
        }

        public static IntPtr dlopen(string fileName, int flags)
        {
            return _libDlImpl.Value.DLOpen(fileName, flags);
        }

        public static int dlclose(IntPtr handle)
        {
            return _libDlImpl.Value.DLClose(handle);
        }

        public static IntPtr dlerror()
        {
            return _libDlImpl.Value.DLError();
        }

        private interface ILibDl
        {
            IntPtr DLOpen(string fileName, int flags);

            int DLClose(IntPtr handle);

            IntPtr DLError();
        }

        private partial class LibDL : ILibDl
        {
            public IntPtr DLOpen(string fileName, int flags)
            {
                return dlopen(fileName, flags);
            }

            public int DLClose(IntPtr handle)
            {
                return dlclose(handle);
            }

            public IntPtr DLError()
            {
                return dlerror();
            }

#if NET8_0_OR_GREATER
            [LibraryImport("libdl.so", StringMarshallingCustomType = typeof(System.Runtime.InteropServices.Marshalling.AnsiStringMarshaller))]
            private static partial IntPtr dlopen(string fileName, int flags);

            [LibraryImport("libdl.so")]
            private static partial int dlclose(IntPtr handle);

            [LibraryImport("libdl.so")]
            private static partial IntPtr dlerror(); // returns static pointer to null terminated CString
#else
            [DllImport("libdl.so")]
            private static extern IntPtr dlopen(string fileName, int flags);

            [DllImport("libdl.so")]
            private static extern int dlclose(IntPtr handle);

            [DllImport("libdl.so")]
            private static extern IntPtr dlerror(); // returns static pointer to null terminated CString
#endif
        }

        // not sure what the story here is - x64 linux uses libdl.so.2 and doesn't symlink it?
        private partial class LibDL2 : ILibDl
        {
            public IntPtr DLOpen(string fileName, int flags)
            {
                return dlopen(fileName, flags);
            }

            public int DLClose(IntPtr handle)
            {
                return dlclose(handle);
            }

            public IntPtr DLError()
            {
                return dlerror();
            }

#if NET8_0_OR_GREATER
            [LibraryImport("libdl.so.2", StringMarshallingCustomType = typeof(System.Runtime.InteropServices.Marshalling.AnsiStringMarshaller))]
            private static partial IntPtr dlopen(string fileName, int flags);

            [LibraryImport("libdl.so.2")]
            private static partial int dlclose(IntPtr handle);

            [LibraryImport("libdl.so.2")]
            private static partial IntPtr dlerror(); // returns static pointer to null terminated CString
#else
            [DllImport("libdl.so.2")]
            private static extern IntPtr dlopen(string fileName, int flags);

            [DllImport("libdl.so.2")]
            private static extern int dlclose(IntPtr handle);

            [DllImport("libdl.so.2")]
            private static extern IntPtr dlerror(); // returns static pointer to null terminated CString
#endif
        }

#if !NETSTANDARD1_1
        internal static PlatformArchitecture? TryGetArchForUnix(TextWriter logger)
        {
            try
            {
                logger?.WriteLine("Running uname to determine system info...");
                using (Process procInfo = Process.Start(new ProcessStartInfo()
                {
                    FileName = "uname",
                    Arguments = "-m",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                }))
                using (StreamReader stdOut = procInfo.StandardOutput)
                {
                    string output = stdOut.ReadToEnd();
                    if (string.IsNullOrEmpty(output))
                    {
                        return null;
                    }

                    output = output.Trim();
                    PlatformArchitecture parsedArch;
                    if (POSSIBLE_UNIX_MACHINES.TryGetValue(output, out parsedArch))
                    {
                        logger?.WriteLine($"Got architecture from uname: {output}");
                        return parsedArch;
                    }
                }
            }
            catch (Exception e)
            {
                logger?.WriteLine(e);
            }

            return null;
        }
#endif
    }
}
