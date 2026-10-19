using NUnit.Framework;

namespace Basis.Tests.Calibration
{
    public class BasisSwapHipHeightTests
    {
        float eye, span, hip, drop;
        bool eyeGenuine, spanGenuine, hipGenuine;
        [SetUp]
        public void Save()
        {
            eye = BasisHeightDriver.PlayerEyeHeight;
            span = BasisHeightDriver.PlayerArmSpan;
            hip = BasisHeightDriver.PlayerHipHeight;
            drop = BasisHeightDriver.PlayerEyeToHipDrop;
            eyeGenuine = BasisHeightDriver.HasGenuinePlayerEyeHeight;
            spanGenuine = BasisHeightDriver.HasGenuinePlayerArmSpan;
            hipGenuine = BasisHeightDriver.HasGenuinePlayerHipHeight;
        }
        [TearDown]
        public void Restore()
        {
            BasisHeightDriver.PlayerEyeHeight = eye;
            BasisHeightDriver.PlayerArmSpan = span;
            BasisHeightDriver.PlayerHipHeight = hip;
            BasisHeightDriver.PlayerEyeToHipDrop = drop;
            BasisHeightDriver.HasGenuinePlayerEyeHeight = eyeGenuine;
            BasisHeightDriver.HasGenuinePlayerArmSpan = spanGenuine;
            BasisHeightDriver.HasGenuinePlayerHipHeight = hipGenuine;
        }
        [Test]
        public void AvatarSwapKeepsTheCalibratedHipHeight()
        {
            BasisHeightDriver.PlayerEyeHeight = 1.6f;
            BasisHeightDriver.PlayerArmSpan = 1.62f;
            BasisHeightDriver.PlayerHipHeight = 0.95f;
            BasisHeightDriver.PlayerEyeToHipDrop = 0.65f;
            BasisHeightDriver.HasGenuinePlayerEyeHeight = true;
            BasisHeightDriver.HasGenuinePlayerArmSpan = true;
            BasisHeightDriver.HasGenuinePlayerHipHeight = true;

            Assert.DoesNotThrow(() => BasisHeightDriver.CapturePlayerHeight(recaptureEyeHeight: false));

            Assert.AreEqual(0.95f, BasisHeightDriver.PlayerHipHeight, 1e-6f);
            Assert.AreEqual(0.65f, BasisHeightDriver.PlayerEyeToHipDrop, 1e-6f);
            Assert.IsTrue(BasisHeightDriver.HasGenuinePlayerHipHeight);
        }
    }
}
