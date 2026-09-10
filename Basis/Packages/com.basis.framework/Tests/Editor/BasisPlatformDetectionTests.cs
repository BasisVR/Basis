using System;
using Basis.Scripts.Device_Management;
using NUnit.Framework;

namespace Basis.Tests.DeviceManagement
{
    public class BasisPlatformDetectionTests
    {
        [Test]
        public void EveryCondition_Evaluates()
        {
            foreach (BasisPlatformCondition condition in Enum.GetValues(typeof(BasisPlatformCondition)))
            {
                Assert.DoesNotThrow(() => BasisPlatformDetection.IsDetected(condition), condition.ToString());
            }
        }

        [Test]
        public void ConditionNames_RoundTripCaseInsensitively()
        {
            Assert.That(BasisPlatformDetection.ConditionNames.Length, Is.EqualTo(Enum.GetValues(typeof(BasisPlatformCondition)).Length));
            foreach (string name in BasisPlatformDetection.ConditionNames)
            {
                Assert.That(BasisPlatformDetection.TryParse(name, out BasisPlatformCondition parsed), Is.True, name);
                Assert.That(parsed.ToString(), Is.EqualTo(name));
                Assert.That(BasisPlatformDetection.TryParse(name.ToLowerInvariant(), out BasisPlatformCondition lower), Is.True, name);
                Assert.That(lower, Is.EqualTo(parsed));
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Quest")]
        [TestCase("VR,Desktop")]
        [TestCase("11")]
        [TestCase(" VR")]
        public void UnknownNames_AreRejected(string name)
        {
            Assert.That(BasisPlatformDetection.TryParse(name, out BasisPlatformCondition parsed), Is.False);
            Assert.That(parsed, Is.EqualTo(default(BasisPlatformCondition)));
        }

        [TestCase(BasisPlatformCondition.VR, false)]
        [TestCase(BasisPlatformCondition.Desktop, false)]
        [TestCase(BasisPlatformCondition.OpenVR, false)]
        [TestCase(BasisPlatformCondition.OpenXR, false)]
        [TestCase(BasisPlatformCondition.SimulateXR, false)]
        [TestCase(BasisPlatformCondition.HeadsetWorn, false)]
        [TestCase(BasisPlatformCondition.MobileGpu, true)]
        [TestCase(BasisPlatformCondition.Android, true)]
        [TestCase(BasisPlatformCondition.Windows, true)]
        [TestCase(BasisPlatformCondition.Editor, true)]
        [TestCase(BasisPlatformCondition.Proton, true)]
        public void OnlyModeConditions_CanChangeAtRuntime(BasisPlatformCondition condition, bool expectedStatic)
        {
            Assert.That(BasisPlatformDetection.IsStatic(condition), Is.EqualTo(expectedStatic));
        }

        [Test]
        public void Editor_IsDetected()
        {
            Assert.That(BasisPlatformDetection.IsDetected(BasisPlatformCondition.Editor), Is.True);
        }

        [Test]
        public void VRAndDesktop_NeverBothHold()
        {
            Assert.That(BasisPlatformDetection.IsDetected(BasisPlatformCondition.VR) && BasisPlatformDetection.IsDetected(BasisPlatformCondition.Desktop), Is.False);
        }
    }
}
