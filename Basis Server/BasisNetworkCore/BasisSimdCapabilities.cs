using System.Numerics;
using System.Text;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Basis.Network.Core
{
    /// <summary>
    /// What vector width and instruction sets this process actually got, for the boot log.
    ///
    /// <para>Every vectorised path in the server is written against <see cref="Vector{T}"/> and
    /// selected by the JIT at runtime, which means the same binary runs 16, 32 or 64 bytes at a time
    /// depending on the host and nothing in the build says which. That is the right trade — one
    /// implementation, ARM included — but it makes the width invisible exactly when someone is trying
    /// to explain a throughput difference between two machines. Printing it once at boot is the whole
    /// point of this type.</para>
    ///
    /// <para><b>Two traps worth knowing before changing how the server is built:</b></para>
    /// <list type="bullet">
    /// <item><description><b>ReadyToRun / AOT silently costs the vector width.</b> R2R precompiles
    /// against a conservative ISA baseline (SSE2-era on x64), so <c>Vector&lt;byte&gt;.Count</c> is
    /// baked at 16 and the AVX2 path is never generated. Tiered compilation does re-JIT hot methods at
    /// the real width, but a startup-time optimisation that quietly halves the width of every vector
    /// loop until then is not free. If R2R is ever adopted here, publish with an explicit
    /// <c>&lt;PublishReadyToRunUseCrossgen2&gt;</c> instruction-set baseline and re-measure — do not
    /// assume it is a pure win.</description></item>
    /// <item><description><b>512-bit <see cref="Vector{T}"/> is opt-in, and only from the environment.</b>
    /// The runtime caps <see cref="Vector{T}"/> at 256 bits on every x64 host and widens it only when
    /// <c>DOTNET_MaxVectorTBitWidth=512</c> is set before the process starts; it reads that knob from
    /// the environment alone, so nothing in runtimeconfig or in Main can flip it. That is a separate
    /// decision from whether 512-bit is accelerated at all: <c>Vector512.IsHardwareAccelerated</c> is
    /// already true on AVX-512 hosts except the ones the runtime knows down-clock (Skylake-SP, Cascade
    /// Lake, Cooper Lake, Cannon Lake), where <c>DOTNET_PreferredVectorBitWidth=512</c> is the override
    /// and the automatic 256 preference also clamps <see cref="Vector{T}"/> back down. Measured on a
    /// 7950X3D (Zen 4 double-pumps 512-bit ops), 64-byte <see cref="Vector{T}"/> is a wash on both vector
    /// loops here and the codec and sweep tests pass at that width, so the knob is left to the launch
    /// environment for an A/B on a host with a native 512-bit datapath rather than set by default.</description></item>
    /// </list>
    /// </summary>
    public static class BasisSimdCapabilities
    {
        /// <summary>Bytes processed per <see cref="Vector{T}"/> operation on this host.</summary>
        public static int VectorByteWidth => Vector<byte>.Count;

        /// <summary>False means every vector path is running as a scalar loop and is a red flag.</summary>
        public static bool HardwareAccelerated => Vector.IsHardwareAccelerated;

        /// <summary>
        /// One line for the boot log: the width actually in force, then the instruction sets behind it.
        /// </summary>
        public static string Describe()
        {
            var sb = new StringBuilder(200);
            sb.Append(Vector.IsHardwareAccelerated
                ? $"{Vector<byte>.Count * 8}-bit Vector<T> ({Vector<byte>.Count} B/op)"
                : "NO hardware vectors - every vector path is running scalar");

#if NET8_0_OR_GREATER
            sb.Append(" [");
            bool any = false;
            void Add(string name, bool supported)
            {
                if (!supported) return;
                if (any) sb.Append(' ');
                sb.Append(name);
                any = true;
            }

            Add("AVX512F", Avx512F.IsSupported);
            Add("AVX2", Avx2.IsSupported);
            Add("SSE4.2", Sse42.IsSupported);
            Add("BMI2", Bmi2.IsSupported);
            Add("NEON", AdvSimd.IsSupported);
            Add("CRC32", Crc32.IsSupported);
            if (!any) sb.Append("baseline only");
            sb.Append(']');

            // Worth surfacing rather than leaving to be discovered: the runtime runs 512-bit here and
            // Vector<T> is still capped at 256, which is a one-environment-variable difference.
            if (Vector512.IsHardwareAccelerated && Vector<byte>.Count < 64)
            {
                sb.Append(" - Vector512 accelerated too; Vector<T> is capped at 256 unless DOTNET_MaxVectorTBitWidth=512 is set in the launch environment");
            }
            else if (Avx512F.IsSupported && !Vector512.IsHardwareAccelerated)
            {
                sb.Append(" - AVX-512 present but the runtime keeps 512-bit off on this CPU (down-clocks); DOTNET_PreferredVectorBitWidth=512 overrides");
            }
#endif
            return sb.ToString();
        }
    }
}
