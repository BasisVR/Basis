using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Concentus.Native
{
    /// <summary>
    /// Global helpers for handling platform and OS-specific tasks regarding native P/Invoke libraries
    /// (mostly to make sure that the right one for the current platform actually gets invoked).
    /// </summary>
    internal static class NativePlatformUtils
    {
        private static bool _cachedPlatformInfoExists = false;
        private static OSAndArchitecture _cachedPlatformInfo = new OSAndArchitecture(PlatformOperatingSystem.Unknown, PlatformArchitecture.Unknown);
        private static readonly IDictionary<string, NativeLibraryStatus> _loadedLibraries = new Dictionary<string, NativeLibraryStatus>();
        private static readonly object _mutex = new object();

        // folder layout is based on https://learn.microsoft.com/en-us/nuget/create-packages/supporting-multiple-target-frameworks#architecture-specific-folders
        // runtimes
        //  |- linux-x64
        //     |- native
        //        |- libopus.so (amd64)
        //  |- osx-x64
        //     |- native
        //        |- libopus.dylib (amd64)
        //  |- win-x86
        //     |- native
        //        |- opus.dll (i386)
        //  |- win-x64
        //     |- native
        //        |- opus.dll (amd64)
        //  |- win10-arm
        //     |- native
        //        |- opus.dll (arm)

        /// <summary>
        /// Gets information about the current runtime OS and processor, in parity with .Net's Runtime Identifier (RID) system.
        /// </summary>
        /// <returns>The current OS and architecture.</returns>
        internal static OSAndArchitecture GetCurrentPlatform(TextWriter logger)
        {
            // can't use Lazy<T> for this because the producer function uses parameters
            lock (_mutex)
            {
                if (!_cachedPlatformInfoExists)
                {
                    _cachedPlatformInfo = GetCurrentPlatformInternal(logger);
                }

                return _cachedPlatformInfo;
            }
        }

        internal static OSAndArchitecture GetCurrentPlatformInternal(TextWriter logger)
        {
            PlatformOperatingSystem os = PlatformOperatingSystem.Unknown;
            PlatformArchitecture arch = PlatformArchitecture.Unknown;
#if NETCOREAPP
            logger?.WriteLine($"Runtime ID is \"{RuntimeInformation.RuntimeIdentifier}\"");
            OSAndArchitecture fromRid = ParseRuntimeId(RuntimeInformation.RuntimeIdentifier);
            os = fromRid.OS;
            arch = fromRid.Architecture;
#endif // NETCOREAPP

            // We can sometimes fail to parse new runtime IDs (like if they add "debian" as a runtime ID in the future), so fall back if needed
#if NET452_OR_GREATER
            if (os == PlatformOperatingSystem.Unknown)
            {
                try
                {
                    // Figure out our OS
                    switch (Environment.OSVersion.Platform)
                    {
                        case PlatformID.Win32NT:
                        case PlatformID.Win32S:
                        case PlatformID.Win32Windows:
                        case PlatformID.WinCE:
                            os = PlatformOperatingSystem.Windows;
                            break;
                        case PlatformID.Unix:
                        case PlatformID.MacOSX:
                            if (File.Exists(@"/proc/sys/kernel/ostype") &&
                                File.ReadAllText(@"/proc/sys/kernel/ostype").StartsWith("Linux", StringComparison.OrdinalIgnoreCase))
                            {
                                os = PlatformOperatingSystem.Linux;
                            }
                            else if (File.Exists(@"/System/Library/CoreServices/SystemVersion.plist"))
                            {
                                os = PlatformOperatingSystem.MacOS;
                            }
                            else
                            {
                                os = PlatformOperatingSystem.Unix;
                            }
                            break;
                    }
                }
                catch (Exception e)
                {
                    logger?.WriteLine(e.ToString());
                }
            }
#else
            if (os == PlatformOperatingSystem.Unknown)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    os = PlatformOperatingSystem.Windows;
                }
#if !NETSTANDARD1_1
                else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANDROID_STORAGE")))
                {
                    os = PlatformOperatingSystem.Android;
                }
#endif // !NETSTANDARD1_1
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    os = PlatformOperatingSystem.MacOS;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    os = PlatformOperatingSystem.Linux;
                }
#if NET6_0_OR_GREATER
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
                {
                    os = PlatformOperatingSystem.FreeBSD;
                }
#endif // NET6_0_OR_GREATER
            }
#endif // !NET452_OR_GREATER

            // Figure out our architecture
            if (os != PlatformOperatingSystem.Unknown && arch == PlatformArchitecture.Unknown)
            {
                // First try native kernel interop
                arch = TryGetNativeArchitecture(os, logger);
            }

            // Then just fall back to the .net runtime values
            if (arch == PlatformArchitecture.Unknown)
            {
#if NET452_OR_GREATER
                arch = Environment.Is64BitProcess ? PlatformArchitecture.X64 : PlatformArchitecture.I386;
#else
                switch (RuntimeInformation.ProcessArchitecture)
                {
                    case Architecture.X86:
                        arch = PlatformArchitecture.I386;
                        break;
                    case Architecture.X64:
                        arch = PlatformArchitecture.X64;
                        break;
                    case Architecture.Arm:
                        arch = PlatformArchitecture.ArmV7;
                        break;
                    case Architecture.Arm64:
                        arch = PlatformArchitecture.Arm64;
                        break;
#if NET6_0_OR_GREATER
                    case Architecture.LoongArch64:
                        arch = PlatformArchitecture.Loongarch64;
                        break;
                    case Architecture.Ppc64le:
                        arch = PlatformArchitecture.PowerPC64;
                        break;
                    case Architecture.Armv6:
                        arch = PlatformArchitecture.ArmV6;
                        break;
                    case Architecture.S390x:
                        arch = PlatformArchitecture.S390x;
                        break;
#endif // NET6_0_OR_GREATER
                }
#endif // !NET452_OR_GREATER
            }

            return new OSAndArchitecture(os, arch);
        }

        /// <summary>
        /// Given a native developer-provided library name, such as "mynativelib",
        /// search the current runtime directory + /runtimes/{runtime ID}/native for files like "mynativelib.dll" / "mynativelib.so",
        /// matching the given library name and current runtime OS and architecture, and then prepare that library file
        /// in such a way that future P/Invoke calls to that library should succeed and should invoke the correct
        /// architecture-specific code.
        /// </summary>
        /// <param name="libraryName">The library name to prepare (without platform-specific extensions such as ".dll")</param>
        /// <param name="logger">A logger to log the result of the operation</param>
        /// <returns>Whether the runtime believes the given library is now available for loading or not.</returns>
        internal static NativeLibraryStatus PrepareNativeLibrary(string libraryName, TextWriter logger)
        {
#if NETSTANDARD1_1
            // In .Net Standard mode the best we can do it just look for a library that already exists using kernel load calls
            logger?.WriteLine("Looking for native library \"{0}\"", libraryName);
            logger?.WriteLine("(Since this is .NetStandard 1.1, native lib functionality is limited to loading already existing libraries that are on the system's default search path. Switch to at least .NetStandard 2.0 to get a better experience)");
            return ProbeLibrary(libraryName, GetCurrentPlatform(logger), logger);
#else
            logger?.WriteLine("Preparing native library \"{0}\"", libraryName);

            OSAndArchitecture platform = GetCurrentPlatform(logger);
            logger?.WriteLine("Detected current platform as \"{0}\"", platform);

            string normalizedLibraryName = NormalizeLibraryName(libraryName, platform);
            lock (_mutex)
            {
                NativeLibraryStatus prevStatus;
                if (_loadedLibraries.TryGetValue(normalizedLibraryName, out prevStatus))
                {
                    logger?.WriteLine("Native library \"{0}\" has already been prepared; nothing to do", libraryName);
                    return prevStatus;
                }

                if (platform.OS == PlatformOperatingSystem.Android)
                {
                    // On android we're not allowed to dlopen shared system binaries directly because of Private API
                    // (see https://android-developers.googleblog.com/2016/06/android-changes-for-ndk-developers.html)
                    // So we have to probe and see if there's a native .so provided to us by this application's .apk
                    logger?.WriteLine($"Probing for {normalizedLibraryName} within local Android .apk");
                    NativeLibraryStatus androidApkLibStatus = ProbeLibrary(normalizedLibraryName, platform, logger);
                    if (androidApkLibStatus != NativeLibraryStatus.Available)
                    {
                        logger?.WriteLine("Native library \"{0}\" was not found in the local .apk.", libraryName);
                        return NativeLibraryStatus.Unavailable;
                    }

                    return NativeLibraryStatus.Available;
                }

                // See if the library is actually provided by the system already
                logger?.WriteLine($"Probing for an already existing {normalizedLibraryName}");
                NativeLibraryStatus builtInLibStatus = ProbeLibrary(normalizedLibraryName, platform, logger);
                if (builtInLibStatus == NativeLibraryStatus.Available)
                {
                    logger?.WriteLine("Native library \"{0}\" resolved to an already-existing library. Loading current file as-is.", libraryName);
                    _loadedLibraries[normalizedLibraryName] = builtInLibStatus;
                    return builtInLibStatus;
                }

                // If the dll was not found or couldn't be loaded because it was the wrong format or something, start trying to pull
                // the matching library from our current /runtimes/{PLATFORM} directory tree

                // Clean up any loose local binaries first
                DeleteLocalLibraryIfPresent(normalizedLibraryName, logger);

                // Search the most applicable /runtimes source directory for a matching library file
                string baseDirectory = Path.Combine(Environment.CurrentDirectory, "runtimes");
                List<string> possibleLibraryNames = PermuteLibraryNames(libraryName, platform);
                List<string> possibleDirectoryNames = PermuteArchitectureSpecificDirectoryNames(platform);
                foreach (string possibleDirectory in possibleDirectoryNames)
                {
                    DirectoryInfo probeDir = new DirectoryInfo(Path.Combine(baseDirectory, possibleDirectory, "native"));
                    if (!probeDir.Exists)
                    {
                        continue;
                    }

                    foreach (string possibleSourceLibrary in possibleLibraryNames)
                    {
                        FileInfo sourceLibraryFile = new FileInfo(Path.Combine(probeDir.FullName, possibleSourceLibrary));
                        if (!sourceLibraryFile.Exists)
                        {
                            continue;
                        }

                        // Do platform-specific work to make this library discoverable by the platform's default library lookup
                        // Apparently in legacy .NetFx (and Mono), Linux .so libraries would not be picked up from the current
                        // executable directory. This seems to have changed in .Net core so that .so files are discovered
                        // the same way as .dlls. "lib" is also prepended to Linux lib search paths automatically.
                        if (platform.OS == PlatformOperatingSystem.Windows ||
                            platform.OS == PlatformOperatingSystem.Linux ||
                            platform.OS == PlatformOperatingSystem.MacOS)
                        {
                            FileInfo desiredBinplacePath = new FileInfo(Path.Combine(Environment.CurrentDirectory, normalizedLibraryName));
                            try
                            {
                                logger?.WriteLine($"Resolved native library \"{libraryName}\" to {sourceLibraryFile.FullName}");
                                sourceLibraryFile.CopyTo(desiredBinplacePath.FullName);
                                _loadedLibraries[normalizedLibraryName] = NativeLibraryStatus.Available;
                                return NativeLibraryStatus.Available;
                            }
                            catch (Exception e)
                            {
                                logger?.WriteLine(e.Message);
                                logger?.WriteLine($"Could not prepare native library \"{libraryName}\" (is the existing library file locked or in use?)");
                                _loadedLibraries[normalizedLibraryName] = NativeLibraryStatus.Unknown;
                                return NativeLibraryStatus.Unknown;
                            }
                        }
                        else
                        {
                            throw new PlatformNotSupportedException($"Don't know yet how to load libraries for {platform.OS}");
                        }
                    }
                }

                logger?.WriteLine("Failed to resolve native library \"{0}\".", libraryName);
                _loadedLibraries[normalizedLibraryName] = NativeLibraryStatus.Unavailable;
                return NativeLibraryStatus.Unavailable;
            }
#endif // !NETSTANDARD1_1
        }

        /// <summary>
        /// Gets the runtime ID string for a given architecture, e.g. "arm64"
        /// </summary>
        /// <param name="architecture"></param>
        /// <returns></returns>
        /// <exception cref="PlatformNotSupportedException"></exception>
        internal static string GetRuntimeIdString(this PlatformArchitecture architecture)
        {
            switch (architecture)
            {
                case PlatformArchitecture.Unknown:
                    return "unknown";
                case PlatformArchitecture.Any:
                    return "any";
                case PlatformArchitecture.I386:
                    return "x86";
                case PlatformArchitecture.X64:
                    return "x64";
                case PlatformArchitecture.ArmV7:
                    return "arm";
                case PlatformArchitecture.Arm64:
                    return "arm64";
                case PlatformArchitecture.Armel:
                    return "armel";
                case PlatformArchitecture.ArmV6:
                    return "armv6";
                case PlatformArchitecture.Mips64:
                    return "mips64";
                case PlatformArchitecture.PowerPC64:
                    return "ppc64le";
                case PlatformArchitecture.RiscFive:
                    return "riscv64";
                case PlatformArchitecture.S390x:
                    return "s390x";
                case PlatformArchitecture.Loongarch64:
                    return "loongarch64";
                case PlatformArchitecture.Itanium64:
                    return "ia64";
                default:
                    throw new PlatformNotSupportedException("No runtime ID defined for " + architecture.ToString());
            }
        }

        /// <summary>
        /// Gets the runtime ID string for a given operating system, e.g. "osx"
        /// </summary>
        /// <param name="os"></param>
        /// <returns></returns>
        /// <exception cref="PlatformNotSupportedException"></exception>
        internal static string GetRuntimeIdString(this PlatformOperatingSystem os)
        {
            switch (os)
            {
                case PlatformOperatingSystem.Unknown:
                    return "unknown";
                case PlatformOperatingSystem.Any:
                    return "any";
                case PlatformOperatingSystem.Windows:
                    return "win";
                case PlatformOperatingSystem.Linux:
                    return "linux";
                case PlatformOperatingSystem.MacOS:
                    return "osx";
                case PlatformOperatingSystem.iOS:
                    return "ios";
                case PlatformOperatingSystem.iOS_Simulator:
                    return "iossimulator";
                case PlatformOperatingSystem.Android:
                    return "android";
                case PlatformOperatingSystem.FreeBSD:
                    return "freebsd";
                case PlatformOperatingSystem.Illumos:
                    return "illumos";
                case PlatformOperatingSystem.Linux_Bionic:
                    return "linux-bionic";
                case PlatformOperatingSystem.Linux_Musl:
                    return "linux-musl";
                case PlatformOperatingSystem.MacCatalyst:
                    return "maccatalyst";
                case PlatformOperatingSystem.Solaris:
                    return "solaris";
                case PlatformOperatingSystem.TvOS:
                    return "tvos";
                case PlatformOperatingSystem.TvOS_Simulator:
                    return "tvossimulator";
                case PlatformOperatingSystem.Unix:
                    return "unix";
                case PlatformOperatingSystem.Browser:
                    return "browser";
                case PlatformOperatingSystem.Wasi:
                    return "wasi";
                default:
                    throw new PlatformNotSupportedException("No runtime ID defined for " + os.ToString());
            }
        }

        /// <summary>
        /// Given a runtime ID, such as "android-arm64", get the list of all inherited runtime IDs in descending order of specificity, 
        /// not including the requested ID.
        /// </summary>
        /// <param name="runtimeId"></param>
        /// <returns></returns>
        internal static string[] GetInheritedRuntimeIds(string runtimeId)
        {
            string[] returnVal;
            if (RidInheritanceMappings.TryGetValue(runtimeId, out returnVal))
            {
                return returnVal;
            }

#if NETCOREAPP
            return Array.Empty<string>();
#else
            return new string[0];
#endif
        }

        /// <summary>
        /// Parses the output from NetCore's RuntimeInformation.RuntimeIdentifier into an <see cref="OSAndArchitecture"/> struct.
        /// </summary>
        /// <param name="runtimeId">The runtime identifier.</param>
        /// <returns>A parsed identifier struct.</returns>
        internal static OSAndArchitecture ParseRuntimeId(ReadOnlySpan<char> runtimeId)
        {
            int splitIdx = runtimeId.IndexOf('-');

            if (splitIdx < 0)
            {
                // No processor architecture. Hmmmm
                return new OSAndArchitecture(TryParseOperatingSystemString(runtimeId), PlatformArchitecture.Unknown);
            }
            else
            {
                return new OSAndArchitecture(
                    TryParseOperatingSystemString(runtimeId.Slice(0, splitIdx)),
                    TryParseArchitectureString(runtimeId.Slice(splitIdx + 1)));
            }
        }

        /// <summary>
        /// Attempts to parse a runtime OS identifier string (e.g. "win10", "osx") into a structured
        /// operating system enum. Returns <see cref="PlatformOperatingSystem.Unknown"/> if parsing failed.
        /// </summary>
        /// <param name="os">The OS identifier string (should be lowercase but not strictly necessary)</param>
        /// <returns>A parsed OS enumeration</returns>
        internal static PlatformOperatingSystem TryParseOperatingSystemString(string os)
        {
            return TryParseOperatingSystemString(os.AsSpan());
        }

        /// <summary>
        /// Attempts to parse a runtime OS identifier string (e.g. "win10", "osx") into a structured
        /// operating system enum. Returns <see cref="PlatformOperatingSystem.Unknown"/> if parsing failed.
        /// </summary>
        /// <param name="os">The OS identifier string (should be lowercase but not strictly necessary)</param>
        /// <returns>A parsed OS enumeration</returns>
        internal static PlatformOperatingSystem TryParseOperatingSystemString(ReadOnlySpan<char> os)
        {
            if (os.Length >= "win".Length && os.Slice(0, "win".Length).Equals("win".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Windows;
            }
            else if (os.Length >= "linux".Length && os.Slice(0, "linux".Length).Equals("linux".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Linux;
            }
            else if (os.Length >= "ubuntu".Length && os.Slice(0, "ubuntu".Length).Equals("ubuntu".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Linux;
            }
            else if (os.Length >= "debian".Length && os.Slice(0, "debian".Length).Equals("debian".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Linux;
            }
            else if (os.Length >= "osx".Length && os.Slice(0, "osx".Length).Equals("osx".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.MacOS;
            }
            else if (os.Length >= "ios".Length && os.Slice(0, "ios".Length).Equals("ios".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.iOS;
            }
            else if (os.Length >= "iossimulator".Length && os.Slice(0, "iossimulator".Length).Equals("iossimulator".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.iOS_Simulator;
            }
            else if (os.Length >= "android".Length && os.Slice(0, "android".Length).Equals("android".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Android;
            }
            else if (os.Length >= "freebsd".Length && os.Slice(0, "freebsd".Length).Equals("freebsd".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.FreeBSD;
            }
            else if (os.Length >= "illumos".Length && os.Slice(0, "illumos".Length).Equals("illumos".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Illumos;
            }
            else if (os.Length >= "linux-bionic".Length && os.Slice(0, "linux-bionic".Length).Equals("linux-bionic".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Linux_Bionic;
            }
            else if (os.Length >= "linux-musl".Length && os.Slice(0, "linux-musl".Length).Equals("linux-musl".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Linux_Musl;
            }
            else if (os.Length >= "maccatalyst".Length && os.Slice(0, "maccatalyst".Length).Equals("maccatalyst".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.MacCatalyst;
            }
            else if (os.Length >= "solaris".Length && os.Slice(0, "solaris".Length).Equals("solaris".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Solaris;
            }
            else if (os.Length >= "tvos".Length && os.Slice(0, "tvos".Length).Equals("tvos".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.TvOS;
            }
            else if (os.Length >= "tvossimulator".Length && os.Slice(0, "tvossimulator".Length).Equals("tvossimulator".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.TvOS_Simulator;
            }
            else if (os.Length >= "unix".Length && os.Slice(0, "unix".Length).Equals("unix".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Unix;
            }
            else if (os.Length >= "browser".Length && os.Slice(0, "browser".Length).Equals("browser".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Browser;
            }
            else if (os.Length >= "wasi".Length && os.Slice(0, "wasi".Length).Equals("wasi".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformOperatingSystem.Wasi;
            }
            else
            {
                return PlatformOperatingSystem.Unknown;
            }
        }

        /// <summary>
        /// Attempts to parse a runtime platform architecture string (e.g. "x64", "arm") into a structured
        /// architecture enum. Returns <see cref="PlatformArchitecture.Unknown"/> if parsing failed.
        /// </summary>
        /// <param name="arch">The architecture identifier string (should be lowercase but not strictly necessary)</param>
        /// <returns>A parsed architecture enumeration</returns>
        internal static PlatformArchitecture TryParseArchitectureString(string arch)
        {
            return TryParseArchitectureString(arch.AsSpan());
        }

        /// <summary>
        /// Attempts to parse a runtime platform architecture string (e.g. "x64", "arm") into a structured
        /// architecture enum. Returns <see cref="PlatformArchitecture.Unknown"/> if parsing failed.
        /// </summary>
        /// <param name="arch">The architecture identifier string (should be lowercase but not strictly necessary)</param>
        /// <returns>A parsed architecture enumeration</returns>
        internal static PlatformArchitecture TryParseArchitectureString(ReadOnlySpan<char> arch)
        {
            if (arch.Equals("any".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.Any;
            }
            else if (arch.Equals("x86".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.I386;
            }
            else if (arch.Equals("x64".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.X64;
            }
            else if (arch.Equals("arm".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.ArmV7;
            }
            else if (arch.Equals("arm64".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.Arm64;
            }
            else if (arch.Equals("armel".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.Armel;
            }
            else if (arch.Equals("armv6".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.ArmV6;
            }
            else if (arch.Equals("mips64".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.Mips64;
            }
            else if (arch.Equals("ppc64le".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.PowerPC64;
            }
            else if (arch.Equals("riscv64".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.RiscFive;
            }
            else if (arch.Equals("s390x".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.S390x;
            }
            else if (arch.Equals("loongarch64".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.Loongarch64;
            }
            else if (arch.Equals("ia64".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return PlatformArchitecture.Itanium64;
            }
            else
            {
                return PlatformArchitecture.Unknown;
            }
        }

        internal static PlatformArchitecture TryGetNativeArchitecture(PlatformOperatingSystem os, TextWriter logger)
        {
            if (os == PlatformOperatingSystem.Windows)
            {
                KernelInteropWindows.SYSTEM_INFO info = default;
                KernelInteropWindows.GetSystemInfo(ref info);
                switch (info.wProcessorArchitecture)
                {
                    case KernelInteropWindows.PROCESSOR_ARCHITECTURE_INTEL:
                    case KernelInteropWindows.PROCESSOR_ARCHITECTURE_AMD64:
                        return Marshal.SizeOf(new IntPtr()) == 4 ? PlatformArchitecture.I386 : PlatformArchitecture.X64;
                    case KernelInteropWindows.PROCESSOR_ARCHITECTURE_ARM:
                        return PlatformArchitecture.ArmV7;
                    case KernelInteropWindows.PROCESSOR_ARCHITECTURE_ARM64:
                        return PlatformArchitecture.Arm64;
                    case KernelInteropWindows.PROCESSOR_ARCHITECTURE_IA64:
                        return PlatformArchitecture.Itanium64;
                    default:
                        return PlatformArchitecture.Unknown;
                }
            }
#if !NETSTANDARD1_1
            else if (os == PlatformOperatingSystem.Linux ||
                    os == PlatformOperatingSystem.Unix ||
                    os == PlatformOperatingSystem.Linux_Musl ||
                    os == PlatformOperatingSystem.Linux_Bionic ||
                    os == PlatformOperatingSystem.Android)
            {
                PlatformArchitecture? possibleArch = KernelInteropLinux.TryGetArchForUnix(logger);
                if (possibleArch.HasValue)
                {
                    return possibleArch.Value;
                }
            }
#endif

            return PlatformArchitecture.Unknown;
        }

#if !NETSTANDARD1_1
        private static void DeleteLocalLibraryIfPresent(string normalizedLibraryName, TextWriter logger)
        {
            FileInfo existingLocalLibPath = new FileInfo(Path.Combine(Environment.CurrentDirectory, normalizedLibraryName));

            if (existingLocalLibPath.Exists)
            {
                try
                {
                    logger?.WriteLine($"Clobbering existing file {existingLocalLibPath.FullName}");
                    existingLocalLibPath.Delete();
                }
                catch (Exception)
                {
                    logger?.WriteLine($"Failed to clean up \"{existingLocalLibPath.FullName}\" (is it locked or in use?)");
                }
            }
        }
#endif

        private static List<string> PermuteArchitectureSpecificDirectoryNames(OSAndArchitecture platformInfo)
        {
            string mostSpecificRid = $"{platformInfo.OS.GetRuntimeIdString()}-{platformInfo.Architecture.GetRuntimeIdString()}";

            string[] inheritedRids = GetInheritedRuntimeIds(mostSpecificRid);
            List<string> returnVal = new List<string>(inheritedRids.Length + 1);
            returnVal.Add(mostSpecificRid);

            // handle legacy windows IDs that might come up somewhere
            // this is a hack because we don't have proper handling of OS versioning in runtime IDs
            if (platformInfo.OS == PlatformOperatingSystem.Windows)
            {
                returnVal.Add($"win10-{platformInfo.Architecture.GetRuntimeIdString()}");
                returnVal.Add($"win81-{platformInfo.Architecture.GetRuntimeIdString()}");
                returnVal.Add($"win8-{platformInfo.Architecture.GetRuntimeIdString()}");
                returnVal.Add($"win7-{platformInfo.Architecture.GetRuntimeIdString()}");
                returnVal.Add($"win10");
                returnVal.Add($"win81");
                returnVal.Add($"win8");
                returnVal.Add($"win7");
            }

            returnVal.AddRange(inheritedRids);
            return returnVal;
        }

        private static string LibraryNameWithoutExtension(string libraryName)
        {
            if (!libraryName.Contains("."))
            {
                return libraryName;
            }

            string libNameLowercase = libraryName.ToLowerInvariant();
            if (libNameLowercase.EndsWith(".dll") ||
                libNameLowercase.EndsWith(".so") ||
                libNameLowercase.EndsWith(".dylib"))
            {
                return libraryName.Substring(0, libraryName.LastIndexOf('.'));
            }

            return libraryName;
        }

        private static string NormalizeLibraryName(string requestedName, OSAndArchitecture platformInfo)
        {
            string nameWithoutExtension = LibraryNameWithoutExtension(requestedName);

            if (platformInfo.OS == PlatformOperatingSystem.Windows)
            {
                return nameWithoutExtension + ".dll";
            }
            else if (platformInfo.OS == PlatformOperatingSystem.Linux ||
                platformInfo.OS == PlatformOperatingSystem.Android ||
                platformInfo.OS == PlatformOperatingSystem.Linux_Bionic ||
                platformInfo.OS == PlatformOperatingSystem.Linux_Musl ||
                platformInfo.OS == PlatformOperatingSystem.Unix)
            {
                if (!nameWithoutExtension.StartsWith("lib", StringComparison.Ordinal))
                {
                    return $"lib{nameWithoutExtension}.so";
                }
                else
                {
                    return nameWithoutExtension + ".so";
                }
            }
            else if (platformInfo.OS == PlatformOperatingSystem.iOS ||
                platformInfo.OS == PlatformOperatingSystem.iOS_Simulator ||
                platformInfo.OS == PlatformOperatingSystem.MacOS ||
                platformInfo.OS == PlatformOperatingSystem.MacCatalyst)
            {
                if (!nameWithoutExtension.StartsWith("lib", StringComparison.Ordinal))
                {
                    return $"lib{nameWithoutExtension}.dylib";
                }
                else
                {
                    return nameWithoutExtension + ".dylib";
                }
            }
            else
            {
                return requestedName;
            }
        }

        private static List<string> PermuteLibraryNames(string requestedName, OSAndArchitecture platformInfo)
        {
            List<string> returnVal = new List<string>(16);
            string nameWithoutExtension = LibraryNameWithoutExtension(requestedName);

            if (platformInfo.OS == PlatformOperatingSystem.Windows)
            {
                returnVal.Add($"{nameWithoutExtension}.dll");
                returnVal.Add($"lib{nameWithoutExtension}.dll");
                if (platformInfo.Architecture == PlatformArchitecture.I386)
                {
                    returnVal.Add($"{nameWithoutExtension}_x86.dll");
                    returnVal.Add($"lib{nameWithoutExtension}_x86.dll");
                    returnVal.Add($"{nameWithoutExtension}x86.dll");
                    returnVal.Add($"lib{nameWithoutExtension}x86.dll");
                    returnVal.Add($"{nameWithoutExtension}32.dll");
                    returnVal.Add($"lib{nameWithoutExtension}32.dll");
                    returnVal.Add($"{nameWithoutExtension}_32.dll");
                    returnVal.Add($"lib{nameWithoutExtension}_32.dll");
                    returnVal.Add($"{nameWithoutExtension}-32.dll");
                    returnVal.Add($"lib{nameWithoutExtension}-32.dll");
                }

                if (platformInfo.Architecture == PlatformArchitecture.X64)
                {
                    returnVal.Add($"{nameWithoutExtension}_x64.dll");
                    returnVal.Add($"lib{nameWithoutExtension}_x64.dll");
                    returnVal.Add($"{nameWithoutExtension}x64.dll");
                    returnVal.Add($"lib{nameWithoutExtension}x64.dll");
                    returnVal.Add($"{nameWithoutExtension}64.dll");
                    returnVal.Add($"lib{nameWithoutExtension}64.dll");
                    returnVal.Add($"{nameWithoutExtension}_64.dll");
                    returnVal.Add($"lib{nameWithoutExtension}_64.dll");
                    returnVal.Add($"{nameWithoutExtension}-64.dll");
                    returnVal.Add($"lib{nameWithoutExtension}-64.dll");
                }
            }
            else if (platformInfo.OS == PlatformOperatingSystem.Linux ||
                platformInfo.OS == PlatformOperatingSystem.Android ||
                platformInfo.OS == PlatformOperatingSystem.Linux_Bionic ||
                platformInfo.OS == PlatformOperatingSystem.Linux_Musl ||
                platformInfo.OS == PlatformOperatingSystem.Unix)
            {
                returnVal.Add($"{nameWithoutExtension}.so");
                returnVal.Add($"lib{nameWithoutExtension}.so");
            }
            else if (platformInfo.OS == PlatformOperatingSystem.MacOS ||
                platformInfo.OS == PlatformOperatingSystem.MacCatalyst ||
                platformInfo.OS == PlatformOperatingSystem.iOS ||
                platformInfo.OS == PlatformOperatingSystem.iOS_Simulator)
            {
                returnVal.Add($"{nameWithoutExtension}.dylib");
                returnVal.Add($"lib{nameWithoutExtension}.dylib");
            }
            else
            {
                returnVal.Add(requestedName);
            }

            return returnVal;
        }

        /// <summary>
        /// Attempts to load the given library using kernel hooks for the current runtime operating system.
        /// </summary>
        /// <param name="libName">The name of the library to open, e.g. "libc"</param>
        /// <param name="platformInfo">The currently running platform</param>
        /// <param name="logger">A logger</param>
        /// <returns>The availability of the given library after the probe attempt (it may load a locally provided or system-installed version of the requested library).</returns>
        internal static NativeLibraryStatus ProbeLibrary(
            string libName,
            OSAndArchitecture platformInfo,
            TextWriter logger)
        {
            try
            {
                if (platformInfo.OS == PlatformOperatingSystem.Windows)
                {
                    IntPtr dllHandle = IntPtr.Zero;
                    try
                    {
                        if (logger != null) logger.WriteLine($"Attempting to load {libName} as a windows .dll");
                        KernelInteropWindows.GetLastError(); // clear any previous error
                        dllHandle = KernelInteropWindows.LoadLibraryExW(libName, hFile: IntPtr.Zero, dwFlags: KernelInteropWindows.LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
                        if (dllHandle == IntPtr.Zero)
                        {
                            uint lastError = KernelInteropWindows.GetLastError();
                            if (lastError != 0)
                            {
                                uint hresult = 0x80070000 | lastError;
                                if (hresult == 0x8007007EU)
                                {
                                    logger?.WriteLine(string.Format("Win32 error 0x{0:X8}: File {1} not found", hresult, libName));
                                }
                                else if (hresult == 0x800700C1U)
                                {
                                    logger?.WriteLine(string.Format("Win32 error 0x{0:X8}: Invalid binary format while loading library {1}", hresult, libName));
                                }
                                else
                                {
                                    logger?.WriteLine(string.Format("Win32 error 0x{0:X8} while loading library {1}", hresult, libName));
                                }
                            }
                            else
                            {
                                logger?.WriteLine($"Native library {libName} not found.");
                            }

                            return NativeLibraryStatus.Unavailable;
                        }
                        else
                        {
                            logger?.WriteLine($"Native library {libName} found!");
                            return NativeLibraryStatus.Available;
                        }
                    }
                    finally
                    {
                        if (dllHandle != IntPtr.Zero)
                        {
                            KernelInteropWindows.FreeLibrary(dllHandle);
                        }
                    }
                }
                else if (platformInfo.OS == PlatformOperatingSystem.Linux ||
                    platformInfo.OS == PlatformOperatingSystem.Android ||
                    platformInfo.OS == PlatformOperatingSystem.Linux_Bionic ||
                    platformInfo.OS == PlatformOperatingSystem.Linux_Musl ||
                    platformInfo.OS == PlatformOperatingSystem.Unix)
                {
                    IntPtr soHandle = IntPtr.Zero;
                    try
                    {
                        logger?.WriteLine($"Attempting to load {libName} as a linux .so");
                        soHandle = KernelInteropLinux.dlopen(libName, KernelInteropLinux.RTLD_NOW);
                        if (soHandle == IntPtr.Zero)
                        {
                            IntPtr lastError = KernelInteropLinux.dlerror();
                            if (lastError != IntPtr.Zero)
                            {
                                string dlErrorMsg = Marshal.PtrToStringAnsi(lastError);
                                if (!string.IsNullOrEmpty(dlErrorMsg))
                                {
                                    logger?.WriteLine(string.Format("Error while loading library {0}: {1}", libName, dlErrorMsg));
                                }
                                else
                                {
                                    logger?.WriteLine($"{libName} could not be loaded.");
                                }
                            }
                            else
                            {
                                logger?.WriteLine($"{libName} could not be loaded.");
                            }

                            return NativeLibraryStatus.Unavailable;
                        }
                        else
                        {
                            logger?.WriteLine($"Native library {libName} found!");
                            return NativeLibraryStatus.Available;
                        }
                    }
                    finally
                    {
                        if (soHandle != IntPtr.Zero)
                        {
                            KernelInteropLinux.dlclose(soHandle);
                        }
                    }
                }
                else if (platformInfo.OS == PlatformOperatingSystem.MacOS ||
                    platformInfo.OS == PlatformOperatingSystem.MacCatalyst ||
                    platformInfo.OS == PlatformOperatingSystem.iOS ||
                    platformInfo.OS == PlatformOperatingSystem.iOS_Simulator)
                {
                    IntPtr dylibHandle = IntPtr.Zero;
                    try
                    {
                        logger?.WriteLine($"Attempting to load {libName} as a macOS .dylib");
                        dylibHandle = KernelInteropMacOS.dlopen(libName, KernelInteropMacOS.RTLD_NOW);
                        if (dylibHandle == IntPtr.Zero)
                        {
                            IntPtr lastError = KernelInteropMacOS.dlerror();
                            if (lastError != IntPtr.Zero)
                            {
                                string dlErrorMsg = Marshal.PtrToStringAnsi(lastError);
                                if (!string.IsNullOrEmpty(dlErrorMsg))
                                {
                                    logger?.WriteLine(string.Format("Error while loading library {0}: {1}", libName, dlErrorMsg));
                                }
                                else
                                {
                                    logger?.WriteLine($"{libName} could not be loaded.");
                                }
                            }
                            else
                            {
                                logger?.WriteLine($"{libName} could not be loaded.");
                            }

                            return NativeLibraryStatus.Unavailable;
                        }
                        else
                        {
                            logger?.WriteLine($"Native library {libName} found!");
                            return NativeLibraryStatus.Available;
                        }
                    }
                    finally
                    {
                        if (dylibHandle != IntPtr.Zero)
                        {
                            KernelInteropMacOS.dlclose(dylibHandle);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger?.WriteLine(e);
            }

            return NativeLibraryStatus.Unknown;
        }

        #region Big tables
        // Used for full inheritance lookups
        private static readonly IReadOnlyDictionary<string, string[]> RidInheritanceMappings =
            new Dictionary<string, string[]>()
            {
                { "android", new string[] { "linux-bionic", "linux", "unix", "any" } },
                { "android-arm", new string[] { "android", "linux-bionic-arm", "linux-bionic", "linux-arm", "linux", "unix-arm", "unix", "any" } },
                { "android-arm64", new string[] { "android", "linux-bionic-arm64", "linux-bionic", "linux-arm64", "linux", "unix-arm64", "unix", "any" } },
                { "android-x64", new string[] { "android", "linux-bionic-x64", "linux-bionic", "linux-x64", "linux", "unix-x64", "unix", "any" } },
                { "android-x86", new string[] { "android", "linux-bionic-x86", "linux-bionic", "linux-x86", "linux", "unix-x86", "unix", "any" } },
                { "any", new string[] { } },
                { "base", new string[] { } },
                { "browser", new string[] { "any" } },
                { "browser-wasm", new string[] { "browser", "any" } },
                { "freebsd", new string[] { "unix", "any" } },
                { "freebsd-arm64", new string[] { "freebsd", "unix-arm64", "unix", "any" } },
                { "freebsd-x64", new string[] { "freebsd", "unix-x64", "unix", "any" } },
                { "illumos", new string[] { "unix", "any" } },
                { "illumos-x64", new string[] { "illumos", "unix-x64", "unix", "any" } },
                { "ios", new string[] { "unix", "any" } },
                { "ios-arm", new string[] { "ios", "unix-arm", "unix", "any" } },
                { "ios-arm64", new string[] { "ios", "unix-arm64", "unix", "any" } },
                { "iossimulator", new string[] { "ios", "unix", "any" } },
                { "iossimulator-arm64", new string[] { "iossimulator", "ios-arm64", "ios", "unix-arm64", "unix", "any" } },
                { "iossimulator-x64", new string[] { "iossimulator", "ios-x64", "ios", "unix-x64", "unix", "any" } },
                { "iossimulator-x86", new string[] { "iossimulator", "ios-x86", "ios", "unix-x86", "unix", "any" } },
                { "ios-x64", new string[] { "ios", "unix-x64", "unix", "any" } },
                { "ios-x86", new string[] { "ios", "unix-x86", "unix", "any" } },
                { "linux", new string[] { "unix", "any" } },
                { "linux-arm", new string[] { "linux", "unix-arm", "unix", "any" } },
                { "linux-arm64", new string[] { "linux", "unix-arm64", "unix", "any" } },
                { "linux-armel", new string[] { "linux", "unix-armel", "unix", "any" } },
                { "linux-armv6", new string[] { "linux", "unix-armv6", "unix", "any" } },
                { "linux-bionic", new string[] { "linux", "unix", "any" } },
                { "linux-bionic-arm", new string[] { "linux-bionic", "linux-arm", "linux", "unix-arm", "unix", "any" } },
                { "linux-bionic-arm64", new string[] { "linux-bionic", "linux-arm64", "linux", "unix-arm64", "unix", "any" } },
                { "linux-bionic-x64", new string[] { "linux-bionic", "linux-x64", "linux", "unix-x64", "unix", "any" } },
                { "linux-bionic-x86", new string[] { "linux-bionic", "linux-x86", "linux", "unix-x86", "unix", "any" } },
                { "linux-loongarch64", new string[] { "linux", "unix-loongarch64", "unix", "any" } },
                { "linux-mips64", new string[] { "linux", "unix-mips64", "unix", "any" } },
                { "linux-musl", new string[] { "linux", "unix", "any" } },
                { "linux-musl-arm", new string[] { "linux-musl", "linux-arm", "linux", "unix-arm", "unix", "any" } },
                { "linux-musl-arm64", new string[] { "linux-musl", "linux-arm64", "linux", "unix-arm64", "unix", "any" } },
                { "linux-musl-armel", new string[] { "linux-musl", "linux-armel", "linux", "unix-armel", "unix", "any" } },
                { "linux-musl-armv6", new string[] { "linux-musl", "linux-armv6", "linux", "unix-armv6", "unix", "any" } },
                { "linux-musl-ppc64le", new string[] { "linux-musl", "linux-ppc64le", "linux", "unix-ppc64le", "unix", "any" } },
                { "linux-musl-riscv64", new string[] { "linux-musl", "linux-riscv64", "linux", "unix-riscv64", "unix", "any" } },
                { "linux-musl-s390x", new string[] { "linux-musl", "linux-s390x", "linux", "unix-s390x", "unix", "any" } },
                { "linux-musl-x64", new string[] { "linux-musl", "linux-x64", "linux", "unix-x64", "unix", "any" } },
                { "linux-musl-x86", new string[] { "linux-musl", "linux-x86", "linux", "unix-x86", "unix", "any" } },
                { "linux-ppc64le", new string[] { "linux", "unix-ppc64le", "unix", "any" } },
                { "linux-riscv64", new string[] { "linux", "unix-riscv64", "unix", "any" } },
                { "linux-s390x", new string[] { "linux", "unix-s390x", "unix", "any" } },
                { "linux-x64", new string[] { "linux", "unix-x64", "unix", "any" } },
                { "linux-x86", new string[] { "linux", "unix-x86", "unix", "any" } },
                { "maccatalyst", new string[] { "ios", "unix", "any" } },
                { "maccatalyst-arm64", new string[] { "maccatalyst", "ios-arm64", "ios", "unix-arm64", "unix", "any" } },
                { "maccatalyst-x64", new string[] { "maccatalyst", "ios-x64", "ios", "unix-x64", "unix", "any" } },
                { "osx", new string[] { "unix", "any" } },
                { "osx-arm64", new string[] { "osx", "unix-arm64", "unix", "any" } },
                { "osx-x64", new string[] { "osx", "unix-x64", "unix", "any" } },
                { "solaris", new string[] { "unix", "any" } },
                { "solaris-x64", new string[] { "solaris", "unix-x64", "unix", "any" } },
                { "tvos", new string[] { "unix", "any" } },
                { "tvos-arm64", new string[] { "tvos", "unix-arm64", "unix", "any" } },
                { "tvossimulator", new string[] { "tvos", "unix", "any" } },
                { "tvossimulator-arm64", new string[] { "tvossimulator", "tvos-arm64", "tvos", "unix-arm64", "unix", "any" } },
                { "tvossimulator-x64", new string[] { "tvossimulator", "tvos-x64", "tvos", "unix-x64", "unix", "any" } },
                { "tvos-x64", new string[] { "tvos", "unix-x64", "unix", "any" } },
                { "unix", new string[] { "any" } },
                { "unix-arm", new string[] { "unix", "any" } },
                { "unix-arm64", new string[] { "unix", "any" } },
                { "unix-armel", new string[] { "unix", "any" } },
                { "unix-armv6", new string[] { "unix", "any" } },
                { "unix-loongarch64", new string[] { "unix", "any" } },
                { "unix-mips64", new string[] { "unix", "any" } },
                { "unix-ppc64le", new string[] { "unix", "any" } },
                { "unix-riscv64", new string[] { "unix", "any" } },
                { "unix-s390x", new string[] { "unix", "any" } },
                { "unix-x64", new string[] { "unix", "any" } },
                { "unix-x86", new string[] { "unix", "any" } },
                { "wasi", new string[] { "any" } },
                { "wasi-wasm", new string[] { "wasi", "any" } },
                { "win", new string[] { "any" } },
                { "win-arm", new string[] { "win", "any" } },
                { "win-arm64", new string[] { "win", "any" } },
                { "win-x64", new string[] { "win", "any" } },
                { "win-x86", new string[] { "win", "any" } },
            };
        #endregion
    }
}
