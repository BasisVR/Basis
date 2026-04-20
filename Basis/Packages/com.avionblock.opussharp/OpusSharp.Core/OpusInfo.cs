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
        /// <param name="use_static">Set to <see langword="true"/> to force static imports, <see langword="false"/> to force dynamic imports, or <see langword="null"/> to auto-select based on platform.</param>
        /// <returns>Version string.</returns>
        public static string Version(bool? use_static = null)
        {
            var useStaticImports = OpusRuntime.ShouldUseStaticImports(use_static);
            return useStaticImports ? Static.OpusInfo.Version() : Dynamic.OpusInfo.Version();
        }

        /// <summary>
        /// Converts an opus error code into a human-readable string.
        /// </summary>
        /// <param name="error">Error number.</param>
        /// <param name="use_static">Set to <see langword="true"/> to force static imports, <see langword="false"/> to force dynamic imports, or <see langword="null"/> to auto-select based on platform.</param>
        /// <returns>Error string.</returns>
        public static string StringError(int error, bool? use_static = null)
        {
            var useStaticImports = OpusRuntime.ShouldUseStaticImports(use_static);
            return useStaticImports ? Static.OpusInfo.StringError(error) : Dynamic.OpusInfo.StringError(error);
        }
    }
}
