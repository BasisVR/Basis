using Unity.Scripting.LifecycleManagement;
using System;
using System.Runtime.InteropServices;

namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// Detects whether this Windows build is actually running on top of a Windows
    /// compatibility layer — Wine, and by extension Valve's Proton — rather than on
    /// native Windows. Detection runs once via <see cref="Initialize"/> during startup
    /// and the results are cached as static properties for cheap repeated reads.
    /// </summary>
    /// <remarks>
    /// On non-Windows players (Android / Quest / Steam Frame native, Linux server) the
    /// Win32 probe is compiled out and every flag reports the native result:
    /// <see cref="IsWine"/> and <see cref="IsProton"/> are <c>false</c> and
    /// <see cref="WineVersion"/> is <c>null</c>. Reading any property auto-initializes,
    /// so call order is never load-bearing — startup just primes the cache and logs once.
    /// </remarks>
    [AutoStaticsCleanup]
    public static partial class BasisProtonDetection
    {
#if UNITY_STANDALONE_WIN
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleA", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr WineGetVersionDelegate();
#endif

        private static bool _hasInitialized;
        private static bool _isWine;
        private static bool _isProton;
        private static string _wineVersion;

        /// <summary>
        /// True once detection has run, via <see cref="Initialize"/> or the first property read.
        /// </summary>
        public static bool HasInitialized => _hasInitialized;

        /// <summary>
        /// True when running under Wine — which includes Proton. Detected via the
        /// <c>wine_get_version</c> export Wine adds to <c>ntdll.dll</c>, absent on native Windows.
        /// </summary>
        public static bool IsWine
        {
            get { EnsureInitialized(); return _isWine; }
        }

        /// <summary>
        /// True when launched through Steam's Proton specifically (Wine plus the Steam
        /// compatibility environment variables), as opposed to plain Wine/Lutris. Covers
        /// SteamOS desktop, the Steam Deck, and the Steam Frame's x86 compatibility path.
        /// </summary>
        public static bool IsProton
        {
            get { EnsureInitialized(); return _isProton; }
        }

        /// <summary>
        /// The underlying Wine version string (e.g. "9.0"), or <c>null</c> on native
        /// Windows and non-Windows players. Useful for diagnostic logging.
        /// </summary>
        public static string WineVersion
        {
            get { EnsureInitialized(); return _wineVersion; }
        }

        /// <summary>
        /// Runs host-environment detection once and caches the result, logging what was found.
        /// Safe to call repeatedly; subsequent calls are no-ops. Intended to be invoked during
        /// startup before anything queries the flags.
        /// </summary>
        public static void Initialize() => EnsureInitialized();

        /// <summary>
        /// Performs the one-time probe. The Win32 lookup only compiles into the Windows
        /// standalone build; everywhere else this leaves the native defaults in place.
        /// </summary>
        private static void EnsureInitialized()
        {
            if (_hasInitialized) return;
            _hasInitialized = true;

#if UNITY_STANDALONE_WIN
            IntPtr ntdll = GetModuleHandle("ntdll.dll");
            IntPtr wineGetVersion = ntdll != IntPtr.Zero ? GetProcAddress(ntdll, "wine_get_version") : IntPtr.Zero;
            _isWine = wineGetVersion != IntPtr.Zero;
            if (_isWine)
            {
                var getVersion = Marshal.GetDelegateForFunctionPointer<WineGetVersionDelegate>(wineGetVersion);
                _wineVersion = Marshal.PtrToStringAnsi(getVersion());
            }
#endif

            _isProton = _isWine && (
                HasEnvironmentVariable("STEAM_COMPAT_DATA_PATH") ||
                HasEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH") ||
                HasEnvironmentVariable("SteamGameId"));

            if (_isProton)
            {
                BasisDebug.Log($"Host environment: Proton (Wine {_wineVersion})", BasisDebug.LogTag.Device);
            }
            else if (_isWine)
            {
                BasisDebug.Log($"Host environment: Wine {_wineVersion} (not Steam/Proton)", BasisDebug.LogTag.Device);
            }
        }

        /// <summary>
        /// <c>true</c> when an environment variable exists and is non-empty.
        /// </summary>
        private static bool HasEnvironmentVariable(string name) =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));
    }
}
