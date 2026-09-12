using Basis.BasisUI;
using NUnit.Framework;
namespace Basis.Tests.UI
{
    [TestFixture]
    public class BasisArmSettingsDefaultsTests
    {
        static float Adopted(float stored, float shipped, params float[] superseded)
        {
            return BasisSettingsDefaults.TryAdoptKnownDefault(true, stored, shipped, superseded, out float value) ? value : stored;
        }
        [Test]
        public void AValueLeftOnAnOlderShippedDefault_MovesToTheCurrentOne()
        {
            Assert.That(Adopted(0.5f, 1f, 0.5f), Is.EqualTo(1f), "the forearm twist share must reach the known-good full distribution");
            Assert.That(Adopted(0.3f, 1f, 0.5f, 0.3f), Is.EqualTo(1f), "the upper arm twist share has two superseded defaults to move off");
            Assert.That(Adopted(0.4f, 1f, 0.4f), Is.EqualTo(1f), "shoulder elevation must reach the measured rhythm");
            Assert.That(Adopted(0.3f, 1f, 0.3f), Is.EqualTo(1f), "shoulder protraction must reach the measured rhythm");
            Assert.That(Adopted(25f, 30f, 25f), Is.EqualTo(30f), "the girdle cap must reach its anatomical value");
            Assert.That(Adopted(0.06f, 0.02f, 0.06f, 0.04f), Is.EqualTo(0.02f), "reach softness must reach the measured value");
            Assert.That(Adopted(0.04f, 0.02f, 0.06f, 0.04f), Is.EqualTo(0.02f), "the interim softness is superseded too");
            Assert.That(Adopted(1f, 0.5f, 1f), Is.EqualTo(0.5f), "the elbow prior weight must reach its measured value");
            Assert.That(Adopted(95f, 90f, 95f), Is.EqualTo(90f), "the forearm pronation ceiling must reach the anatomical value");
        }
        [Test]
        public void AValueTheUserChose_IsLeftAlone()
        {
            Assert.That(BasisSettingsDefaults.TryAdoptKnownDefault(true, 0.7f, 1f, new[] { 0.5f, 0.3f }, out _), Is.False, "a value that was never one of our defaults is the user's own");
            Assert.That(Adopted(0.7f, 1f, 0.5f, 0.3f), Is.EqualTo(0.7f));
            Assert.That(BasisSettingsDefaults.TryAdoptKnownDefault(true, 22f, 30f, new[] { 25f }, out _), Is.False);
        }
        [Test]
        public void NothingStoredOrAlreadyCurrent_IsNotWritten()
        {
            Assert.That(BasisSettingsDefaults.TryAdoptKnownDefault(false, 0.5f, 1f, new[] { 0.5f }, out _), Is.False, "with nothing stored the shipped default already applies");
            Assert.That(BasisSettingsDefaults.TryAdoptKnownDefault(true, 1f, 1f, new[] { 0.5f }, out _), Is.False, "a value already on the current default needs no write");
            Assert.That(BasisSettingsDefaults.TryAdoptKnownDefault(true, 0.5f, 1f, null, out _), Is.False, "a setting with no superseded defaults is never touched");
        }
        [Test]
        public void TheAdoptionRunsOnlyOnce()
        {
            Assert.That(BasisSettingsDefaults.ArmDefaultsAdoptedKey, Is.EqualTo("fbikarmdefaults_v2"), "the one-shot marker key is what stops this from fighting a later retune");
        }
    }
}
