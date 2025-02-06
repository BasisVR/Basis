using System;
using System.Collections.Generic;
using System.Text;

namespace Concentus.Native
{
    // https://learn.microsoft.com/en-us/dotnet/core/rid-catalog
    /// <summary>
    /// An enumerated value for the current platform's operating system.
    /// </summary>
    internal enum PlatformOperatingSystem
    {
        /// <summary>
        /// Error case.
        /// </summary>
        Unknown,

        /// <summary>
        /// "any" OS identifier
        /// </summary>
        Any,

        /// <summary>
        /// "win" OS identifier
        /// </summary>
        Windows,

        /// <summary>
        /// "linux" OS identifier
        /// </summary>
        Linux,

        /// <summary>
        /// "osx" OS identifier
        /// </summary>
        MacOS,

        /// <summary>
        /// "ios" OS identifier
        /// </summary>
        iOS,

        /// <summary>
        /// "iossimulator" OS identifier
        /// </summary>
        iOS_Simulator,

        /// <summary>
        /// "android" OS identifier
        /// </summary>
        Android,

        /// <summary>
        /// "freebsd" OS identifier
        /// </summary>
        FreeBSD,

        /// <summary>
        /// "illumos" OS identifier
        /// </summary>
        Illumos,

        /// <summary>
        /// "linux-bionic" OS identifier
        /// </summary>
        Linux_Bionic,

        /// <summary>
        /// "linux-musl" OS identifier
        /// </summary>
        Linux_Musl,

        /// <summary>
        /// "maccatalyst" OS identifier
        /// </summary>
        MacCatalyst,

        /// <summary>
        /// "solaris" OS identifier
        /// </summary>
        Solaris,

        /// <summary>
        /// "tvos" OS identifier
        /// </summary>
        TvOS,

        /// <summary>
        /// "tvossimulator" OS identifier
        /// </summary>
        TvOS_Simulator,

        /// <summary>
        /// "unix" OS identifier
        /// </summary>
        Unix,

        /// <summary>
        /// "browser" OS identifier
        /// </summary>
        Browser,

        /// <summary>
        /// "wasi" OS identifier
        /// </summary>
        Wasi,
    }
}
