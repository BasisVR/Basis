using System;
using System.Collections.Generic;
using System.Text;

namespace Concentus.Native
{
    /// <summary>
    /// Represents a tuple combination of operating system + processor architecture.
    /// </summary>
    internal readonly struct OSAndArchitecture : IEquatable<OSAndArchitecture>
    {
        /// <summary>
        /// An operating system specifier, e.g. Linux
        /// </summary>
        public PlatformOperatingSystem OS { get; }

        /// <summary>
        /// A processor architecture specifier, e.g. i386
        /// </summary>
        public PlatformArchitecture Architecture { get; }

        // not implemented yet...
        //public Version OSVersion { get; set; }

        /// <summary>
        /// Constructs a new <see cref="OSAndArchitecture"/>.
        /// </summary>
        /// <param name="OS">An operating system specifier.</param>
        /// <param name="architecture">A proceses architecture specifier.</param>
        public OSAndArchitecture(PlatformOperatingSystem OS, PlatformArchitecture architecture)
        {
            this.OS = OS;
            this.Architecture = architecture;
        }

        public override string ToString()
        {
            return $"{NativePlatformUtils.GetRuntimeIdString(OS)}-{NativePlatformUtils.GetRuntimeIdString(Architecture)}";
        }

        public override int GetHashCode()
        {
            return (OS.GetHashCode() * 17) +
                (Architecture.GetHashCode() * 37119);
        }

        public override bool Equals(object other)
        {
            if (other == null || GetType() != other.GetType())
            {
                return false;
            }

            return Equals((OSAndArchitecture)other);
        }

        public bool Equals(OSAndArchitecture other)
        {
            return OS == other.OS &&
                Architecture == other.Architecture;
        }

        public static bool operator ==(OSAndArchitecture left, OSAndArchitecture right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(OSAndArchitecture left, OSAndArchitecture right)
        {
            return !(left == right);
        }
    }
}
