using System;
using System.Collections.Generic;
using System.Text;

namespace Concentus.Native
{
    // https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-target-framework-and-target-platform?view=vs-2022#target-platform
    /// <summary>
    /// An enumerated value for the current platform's processor architecture.
    /// </summary>
    internal enum PlatformArchitecture
    {
        /// <summary>
        /// Error case.
        /// </summary>
        Unknown,

        /// <summary>
        /// "any" platform identifier
        /// </summary>
        Any,

        /// <summary>
        /// "x86" platform identifier
        /// </summary>
        I386,

        /// <summary>
        /// "x64" platform identifier
        /// </summary>
        X64,

        /// <summary>
        /// "arm" platform identifier (Implies hard float support)
        /// </summary>
        ArmV7,

        /// <summary>
        /// "arm64" platform identifier, also called AArch64
        /// </summary>
        Arm64,

        /// <summary>
        /// "armel" platform identifier (ARM v5 or older)
        /// </summary>
        Armel,

        /// <summary>
        /// "armv6" platform identifier
        /// </summary>
        ArmV6,

        /// <summary>
        /// "mips64" platform identifier
        /// </summary>
        Mips64,

        /// <summary>
        /// "ppc64le" platform identifier
        /// </summary>
        PowerPC64,

        /// <summary>
        /// "riscv64" platform identifier
        /// </summary>
        RiscFive,

        /// <summary>
        /// "s390x" platform identifier
        /// </summary>
        S390x,

        /// <summary>
        /// "loongarch64" platform identifier
        /// </summary>
        Loongarch64,

        /// <summary>
        /// Nobody supports this
        /// </summary>
        Itanium64,
    }
}
