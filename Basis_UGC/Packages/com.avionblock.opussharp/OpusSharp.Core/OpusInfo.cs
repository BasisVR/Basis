using System;
using System.Runtime.InteropServices;

namespace OpusSharp.Core
{
    /// <summary>
    /// Provides information about the opus DLL.
    /// </summary>
    public class OpusInfo
    {
        /// <summary>
        /// Gets the libopus version string.
        /// </summary>
        /// <returns>Version string.</returns>
        public static unsafe string Version()
        {
            var version = NativeOpus.opus_get_version_string();
            return Marshal.PtrToStringAnsi((IntPtr)version) ?? "";
        }

        /// <summary>
        /// Converts an opus error code into a human-readable string.
        /// </summary>
        /// <param name="error">Error number.</param>
        /// <returns>Error string.</returns>
        public static unsafe string StringError(int error)
        {
            var stringError = NativeOpus.opus_strerror(error);
            return Marshal.PtrToStringAnsi((IntPtr)stringError) ?? "";
        }
    }
}
