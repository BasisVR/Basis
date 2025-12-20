using System;
using System.Runtime.InteropServices;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable InconsistentNaming
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
        /// <param name="use_static">Whether to use a statically linked version of opus.</param>
        /// <returns>Version string.</returns>
        public static unsafe string Version(bool use_static = false)
        {
            var version = use_static
                ? StaticNativeOpus.opus_get_version_string()
                : NativeOpus.opus_get_version_string();
            return Marshal.PtrToStringAnsi((IntPtr)version) ?? "";
        }

        /// <summary>
        /// Converts an opus error code into a human-readable string.
        /// </summary>
        /// <param name="error">Error number.</param>
        /// <param name="use_static">Whether to use a statically linked version of opus.</param>
        /// <returns>Error string.</returns>
        public static unsafe string StringError(int error, bool use_static = false)
        {
            var stringError = use_static ? StaticNativeOpus.opus_strerror(error) : NativeOpus.opus_strerror(error);
            return Marshal.PtrToStringAnsi((IntPtr)stringError) ?? "";
        }
    }
}