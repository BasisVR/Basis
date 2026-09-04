using System;
using UnityEngine;

namespace Basis.Scripts.Common
{
    public enum BasisGpuVendor
    {
        Unknown,
        Nvidia,
        Amd,
        Intel,
        Apple,
        Qualcomm,
        Arm,
        Imagination,
        Broadcom,
        Samsung,
        Microsoft,
        Mesa,
    }

    /// <summary>
    /// The raw strings a GPU class is decided from, split out from <see cref="SystemInfo"/> so the
    /// decision is a pure function of them and can be exercised for hardware nobody here owns.
    /// </summary>
    public readonly struct BasisGpuFacts
    {
        public readonly string DeviceName, VendorName, ProcessorType;
        public readonly int VendorId;
        public readonly bool MobileRuntime;

        public BasisGpuFacts(string deviceName, string vendorName, int vendorId, string processorType, bool mobileRuntime)
        {
            DeviceName = deviceName ?? string.Empty;
            VendorName = vendorName ?? string.Empty;
            ProcessorType = processorType ?? string.Empty;
            VendorId = vendorId;
            MobileRuntime = mobileRuntime;
        }

        public static BasisGpuFacts FromSystemInfo() => new BasisGpuFacts(SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceVendor, SystemInfo.graphicsDeviceVendorID, SystemInfo.processorType, Application.isMobilePlatform);

        public string Device => $"{DeviceName} {VendorName}".ToLowerInvariant();

        public string Processor => ProcessorType.ToLowerInvariant();
    }

    /// <summary>
    /// Classifies the GPU the player is really rendering on, which the build target cannot answer.
    /// A Windows build launched from a Steam Frame library runs x86 under Proton on an Adreno
    /// reached through Turnip, and a Snapdragon Windows-on-ARM laptop is the same shape: every
    /// rendering cost is a mobile cost while <see cref="Application.isMobilePlatform"/> is false.
    /// Anything that wants to spend by hardware class rather than by build target asks here.
    ///
    /// <para><see cref="SystemInfo"/> is main-thread only, so the probe is primed from device
    /// management startup and every later read, on any thread, hits the cache. A read that beats
    /// the priming from another thread answers desktop and leaves the probe unrun, so the next
    /// main-thread read still gets the real hardware rather than a cached guess.</para>
    /// </summary>
    public static class BasisGpuDetection
    {
        private const int IdNvidia = 0x10DE;
        private const int IdAmd = 0x1002;
        private const int IdAmdSecondary = 0x1022;
        private const int IdIntel = 0x8086;
        private const int IdApple = 0x106B;
        private const int IdQualcomm = 0x5143;
        private const int IdArm = 0x13B5;
        private const int IdImagination = 0x1010;
        private const int IdBroadcom = 0x14E4;
        private const int IdSamsung = 0x144D;
        private const int IdMicrosoft = 0x1414;
        private const int IdMesa = 0x10005;

        private static readonly string[] MobileMarkers = { "adreno", "turnip", "freedreno", "mali", "immortalis", "powervr", "imagination", "videocore", "xclipse", "tegra", "vivante", "apple a", "qualcomm" };
        private static readonly string[] SoftwareMarkers = { "llvmpipe", "lavapipe", "softpipe", "swiftshader", "basic render driver", "warp", "null device" };
        private static readonly string[] DesktopMarkers = { "geforce", "nvidia", "radeon", "quadro", "titan", "arc(tm)", "iris", "hd graphics" };
        private static readonly string[] ArmHostMarkers = { "aarch64", "arm64", "armv8", "armv9", "cortex", "oryon", "snapdragon", "apple m", "fex" };

        private const string OverrideVariable = "BASIS_MOBILE_GPU";
        private const string ForceMobileFlag = "--mobile-gpu";
        private const string ForceDesktopFlag = "--desktop-gpu";

        private static bool hasInitialized, mobileGpu, softwareRenderer, armHost, forced;
        private static BasisGpuVendor vendor;
        private static string deviceName = string.Empty, vendorName = string.Empty, summary = string.Empty;
        private static int vendorId;

        /// <summary>
        /// True when the renderer is a phone or standalone-headset class part: tile based, sharing
        /// system memory, with a fraction of the bandwidth a desktop card has. Always true on
        /// Android and iOS players, and true for the Frame and Windows-on-ARM cases above.
        /// </summary>
        public static bool IsMobileGpu { get { EnsureInitialized(); return mobileGpu; } }

        /// <summary>True for CPU rasterizers (llvmpipe, WARP) and for the -nographics null device.</summary>
        public static bool IsSoftwareRenderer { get { EnsureInitialized(); return softwareRenderer; } }

        /// <summary>True when the CPU underneath is ARM, x86 emulated onto it by FEX included.</summary>
        public static bool IsArmHost { get { EnsureInitialized(); return armHost; } }

        public static BasisGpuVendor Vendor { get { EnsureInitialized(); return vendor; } }

        public static string DeviceName { get { EnsureInitialized(); return deviceName; } }

        public static string VendorName { get { EnsureInitialized(); return vendorName; } }

        public static int VendorId { get { EnsureInitialized(); return vendorId; } }

        /// <summary>True when the class came from a launch flag or environment variable, not the probe.</summary>
        public static bool WasForced { get { EnsureInitialized(); return forced; } }

        /// <summary>One line naming the GPU and what it was classified as, for logs and bug reports.</summary>
        public static string Summary { get { EnsureInitialized(); return summary; } }

        public static void Initialize() => EnsureInitialized();

        private static void EnsureInitialized()
        {
            if (hasInitialized) return;

            BasisGpuFacts facts;
            string api;
            int shaderLevel, graphicsMemory;
            try
            {
                facts = BasisGpuFacts.FromSystemInfo();
                api = SystemInfo.graphicsDeviceType.ToString();
                shaderLevel = SystemInfo.graphicsShaderLevel;
                graphicsMemory = SystemInfo.graphicsMemorySize;
            }
            catch (Exception)
            {
                return;
            }

            hasInitialized = true;
            deviceName = facts.DeviceName;
            vendorName = facts.VendorName;
            vendorId = facts.VendorId;
            vendor = ResolveVendor(facts);
            softwareRenderer = ResolveSoftwareRenderer(facts);
            armHost = ResolveArmHost(facts);
            mobileGpu = ResolveMobileGpu(facts);

            if (TryReadOverride(out bool forcedMobile))
            {
                forced = true;
                mobileGpu = forcedMobile;
            }

            summary = $"{(deviceName.Length == 0 ? "unnamed GPU" : deviceName)} [{vendor}, id 0x{vendorId:X4}, {api}, shader level {shaderLevel}, {graphicsMemory} MB] on {facts.ProcessorType} -> {(mobileGpu ? "mobile" : "desktop")} class{(forced ? " (forced)" : string.Empty)}";
            BasisDebug.Log($"Graphics hardware: {summary}", BasisDebug.LogTag.Rendering);
        }

        /// <summary>
        /// Vendor from the PCI id where the driver reports one, falling back to the device and
        /// vendor strings. GLES drivers leave the id at zero and Mesa passes a Vulkan id straight
        /// through DXVK, so both halves have to be able to answer alone.
        /// </summary>
        public static BasisGpuVendor ResolveVendor(BasisGpuFacts facts)
        {
            switch (facts.VendorId)
            {
                case IdNvidia: return BasisGpuVendor.Nvidia;
                case IdAmd:
                case IdAmdSecondary: return BasisGpuVendor.Amd;
                case IdIntel: return BasisGpuVendor.Intel;
                case IdApple: return BasisGpuVendor.Apple;
                case IdQualcomm: return BasisGpuVendor.Qualcomm;
                case IdArm: return BasisGpuVendor.Arm;
                case IdImagination: return BasisGpuVendor.Imagination;
                case IdBroadcom: return BasisGpuVendor.Broadcom;
                case IdSamsung: return BasisGpuVendor.Samsung;
                case IdMicrosoft: return BasisGpuVendor.Microsoft;
                case IdMesa: return BasisGpuVendor.Mesa;
            }

            string device = facts.Device;
            if (device.Contains("adreno") || device.Contains("qualcomm") || device.Contains("turnip") || device.Contains("freedreno")) return BasisGpuVendor.Qualcomm;
            if (device.Contains("mali") || device.Contains("immortalis")) return BasisGpuVendor.Arm;
            if (device.Contains("powervr") || device.Contains("imagination")) return BasisGpuVendor.Imagination;
            if (device.Contains("xclipse") || device.Contains("samsung")) return BasisGpuVendor.Samsung;
            if (device.Contains("nvidia") || device.Contains("geforce") || device.Contains("tegra")) return BasisGpuVendor.Nvidia;
            if (device.Contains("radeon") || device.Contains("amd")) return BasisGpuVendor.Amd;
            if (device.Contains("intel")) return BasisGpuVendor.Intel;
            if (device.Contains("apple")) return BasisGpuVendor.Apple;
            return BasisGpuVendor.Unknown;
        }

        public static bool ResolveSoftwareRenderer(BasisGpuFacts facts) => ContainsAny(facts.Device, SoftwareMarkers);

        public static bool ResolveArmHost(BasisGpuFacts facts) => ContainsAny(facts.Processor, ArmHostMarkers);

        /// <summary>
        /// Whether to treat this renderer as mobile class. A CPU rasterizer is never mobile, the
        /// mobile-only vendors always are, and the last line covers a driver that names neither
        /// itself nor its vendor recognisably: an ARM host with nothing desktop about the GPU.
        /// </summary>
        public static bool ResolveMobileGpu(BasisGpuFacts facts)
        {
            if (ResolveSoftwareRenderer(facts)) return false;

            string device = facts.Device;
            BasisGpuVendor resolved = ResolveVendor(facts);
            switch (resolved)
            {
                case BasisGpuVendor.Qualcomm:
                case BasisGpuVendor.Arm:
                case BasisGpuVendor.Imagination:
                case BasisGpuVendor.Broadcom:
                case BasisGpuVendor.Samsung:
                    return true;
                case BasisGpuVendor.Apple:
                    return facts.MobileRuntime;
                case BasisGpuVendor.Nvidia:
                    return device.Contains("tegra");
            }

            if (facts.MobileRuntime) return true;
            if (ContainsAny(device, MobileMarkers)) return true;
            return ResolveArmHost(facts) && resolved == BasisGpuVendor.Unknown && !ContainsAny(device, DesktopMarkers);
        }

        private static bool TryReadOverride(out bool forcedMobile)
        {
            forcedMobile = false;

            string variable = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrEmpty(variable))
            {
                forcedMobile = !variable.Equals("0", StringComparison.Ordinal) && !variable.Equals("false", StringComparison.OrdinalIgnoreCase);
                return true;
            }

            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return false; }
            if (args == null) return false;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], ForceMobileFlag, StringComparison.OrdinalIgnoreCase))
                {
                    forcedMobile = true;
                    return true;
                }
                if (string.Equals(args[i], ForceDesktopFlag, StringComparison.OrdinalIgnoreCase))
                {
                    forcedMobile = false;
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsAny(string value, string[] markers)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < markers.Length; i++)
            {
                if (value.Contains(markers[i])) return true;
            }
            return false;
        }
    }
}
