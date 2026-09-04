using Basis.Scripts.Common;
using Basis.Scripts.Settings;
using NUnit.Framework;

namespace Basis.Tests.Graphics
{
    public class BasisGpuDetectionTests
    {
        private static BasisGpuFacts Facts(string name, string vendor, int vendorId, string processor, bool mobileRuntime) =>
            new BasisGpuFacts(name, vendor, vendorId, processor, mobileRuntime);

        [TestCase("Turnip Adreno (TM) 750", "", 0x5143, "FEX-2506 (Cortex-A720)", TestName = "FrameProtonWithVendorId")]
        [TestCase("Turnip Adreno (TM) 750", "", 0, "FEX-2506 (Cortex-A720)", TestName = "FrameProtonWithoutVendorId")]
        [TestCase("Virtio-GPU Venus (Turnip Adreno (TM) 750)", "", 0, "unknown", TestName = "FrameThroughVenusPassthrough")]
        [TestCase("", "", 0, "FEX-Emu Snapdragon 8 Gen 3", TestName = "FrameWithNothingButTheCpuName")]
        public void WindowsPlayerOnFrameHardware_ReadsAsMobile(string name, string vendor, int vendorId, string processor)
        {
            Assert.That(BasisGpuDetection.ResolveMobileGpu(Facts(name, vendor, vendorId, processor, false)), Is.True,
                "A Windows build under Proton reports a desktop runtime platform, so the GPU strings and the ARM host are the only things that can say this is a Frame.");
        }

        [TestCase("Adreno (TM) 740", "Qualcomm", 0, BasisGpuVendor.Qualcomm, TestName = "QuestOnGles")]
        [TestCase("Adreno (TM) 740", "Qualcomm", 0x5143, BasisGpuVendor.Qualcomm, TestName = "QuestOnVulkan")]
        [TestCase("Mali-G78", "ARM", 0, BasisGpuVendor.Arm, TestName = "MaliPhone")]
        [TestCase("Samsung Xclipse 940", "Samsung", 0x144D, BasisGpuVendor.Samsung, TestName = "XclipsePhone")]
        [TestCase("PowerVR Rogue GE8320", "Imagination Technologies", 0, BasisGpuVendor.Imagination, TestName = "PowerVrPhone")]
        [TestCase("NVIDIA Tegra X1", "NVIDIA", 0x10DE, BasisGpuVendor.Nvidia, TestName = "TegraConsole")]
        public void AndroidPlayers_ReadAsMobile(string name, string vendor, int vendorId, BasisGpuVendor expected)
        {
            BasisGpuFacts facts = Facts(name, vendor, vendorId, "ARMv8 Processor", true);
            Assert.That(BasisGpuDetection.ResolveMobileGpu(facts), Is.True);
            Assert.That(BasisGpuDetection.ResolveVendor(facts), Is.EqualTo(expected));
        }

        [Test]
        public void SnapdragonWindowsOnArm_ReadsAsMobile()
        {
            BasisGpuFacts facts = Facts("Qualcomm(R) Adreno(TM) X1-85 GPU", "Qualcomm", 0x5143, "Snapdragon(R) X Elite - X1E80100", false);
            Assert.That(BasisGpuDetection.ResolveMobileGpu(facts), Is.True);
            Assert.That(BasisGpuDetection.ResolveArmHost(facts), Is.True);
        }

        [TestCase("NVIDIA GeForce RTX 4090", "NVIDIA", 0x10DE, "AMD Ryzen 9 7950X", BasisGpuVendor.Nvidia, TestName = "DesktopNvidia")]
        [TestCase("AMD Radeon RX 7900 XTX", "ATI", 0x1002, "Intel Core i9-13900K", BasisGpuVendor.Amd, TestName = "DesktopAmd")]
        [TestCase("AMD Custom GPU 0405", "AMD", 0x1002, "AMD Custom APU 0405", BasisGpuVendor.Amd, TestName = "SteamDeck")]
        [TestCase("Intel(R) UHD Graphics 630", "Intel", 0x8086, "Intel Core i7-8700K", BasisGpuVendor.Intel, TestName = "IntelIntegrated")]
        [TestCase("Intel(R) Arc(TM) A770 Graphics", "Intel", 0x8086, "Intel Core i7-13700K", BasisGpuVendor.Intel, TestName = "IntelArc")]
        [TestCase("Apple M2 Max", "Apple", 0x106B, "Apple M2 Max", BasisGpuVendor.Apple, TestName = "AppleSiliconMac")]
        public void DesktopParts_ReadAsDesktop(string name, string vendor, int vendorId, string processor, BasisGpuVendor expected)
        {
            BasisGpuFacts facts = Facts(name, vendor, vendorId, processor, false);
            Assert.That(BasisGpuDetection.ResolveMobileGpu(facts), Is.False,
                "An integrated or console-class desktop part still has desktop bandwidth and a desktop memory model; only phone and headset parts belong in the mobile class.");
            Assert.That(BasisGpuDetection.ResolveVendor(facts), Is.EqualTo(expected));
        }

        [Test]
        public void IosPlayer_ReadsAsMobile()
        {
            Assert.That(BasisGpuDetection.ResolveMobileGpu(Facts("Apple A17 Pro GPU", "Apple", 0x106B, "Apple A17", true)), Is.True);
        }

        [TestCase("Null Device", "", 0, "AMD Ryzen 9 7950X", TestName = "HeadlessNullDevice")]
        [TestCase("llvmpipe (LLVM 17.0.6, 128 bits)", "Mesa", 0x10005, "aarch64", TestName = "LlvmpipeOnArm")]
        [TestCase("Microsoft Basic Render Driver", "Microsoft", 0x1414, "Intel Core i7-8700K", TestName = "WarpFallback")]
        public void CpuRasterizers_ReadAsSoftwareAndNeverMobile(string name, string vendor, int vendorId, string processor)
        {
            BasisGpuFacts facts = Facts(name, vendor, vendorId, processor, false);
            Assert.That(BasisGpuDetection.ResolveSoftwareRenderer(facts), Is.True);
            Assert.That(BasisGpuDetection.ResolveMobileGpu(facts), Is.False,
                "A CPU rasterizer needs its own handling; calling it mobile would hand it the tile-based assumptions it cannot honour.");
        }

        [Test]
        public void PlatformDefaults_FollowTheGpuClass()
        {
            BasisPlatformDefault<string> probe = new BasisPlatformDefault<string>
            {
                windows = "windows",
                android = "android",
                ios = "ios",
                linux = "linux",
                other = "other",
            };

            if (BasisGpuDetection.IsMobileGpu)
            {
                Assert.That(probe.GetDefault(), Is.EqualTo("android"),
                    "Mobile-class hardware takes the android column whatever the build target is: that is what starts a Frame on the low graphics defaults.");
                return;
            }

            Assert.That(probe.GetDefault(), Is.Not.EqualTo("android"),
                "A desktop GPU must never be handed the mobile defaults, so the mobile branch has to stay behind the GPU class.");
        }

        [Test]
        public void UnknownDesktopGpuOnArmHost_StaysDesktop()
        {
            BasisGpuFacts facts = Facts("Quadro P2000", "", 0, "Snapdragon(R) X Elite - X1E80100", false);
            Assert.That(BasisGpuDetection.ResolveMobileGpu(facts), Is.False,
                "The ARM-host fallback is the last resort for an unrecognised GPU, so a named desktop card has to outvote it.");
        }
    }
}
