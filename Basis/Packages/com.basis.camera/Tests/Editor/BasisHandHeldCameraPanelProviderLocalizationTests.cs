using Basis.BasisUI;
using Basis.BasisUI.HandHeldCamera;
using NUnit.Framework;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The Camera Settings hotbar button used to read
    /// <c>public const string StaticTitle = "Camera Settings"</c> — a compile-time literal that
    /// bypassed BasisLocalization.Get() entirely, so the button never translated no matter how many
    /// times the menu rebuilt on a language switch. Fixed by making it a live Get()-backed property;
    /// this guards against that regressing.
    /// </summary>
    [TestFixture]
    public class BasisHandHeldCameraPanelProviderLocalizationTests
    {
        private string _originalLanguage;

        [SetUp]
        public void SetUp()
        {
            _originalLanguage = BasisLocalization.CurrentLanguage;
        }

        [TearDown]
        public void TearDown()
        {
            BasisLocalization.SetLanguage(_originalLanguage);
        }

        [Test]
        public void StaticTitle_ResolvesThroughLocalizationRatherThanBeingAFrozenLiteral()
        {
            BasisLocalization.SetLanguage("en");
            Assert.That(BasisHandHeldCameraPanelProvider.StaticTitle,
                Is.EqualTo(BasisLocalization.Get(BasisHandHeldCameraPanelProvider.StaticTitleKey)));
        }

        [Test]
        public void StaticTitle_ChangesLiveWhenTheLanguageChanges()
        {
            BasisLocalization.SetLanguage("en");
            string english = BasisHandHeldCameraPanelProvider.StaticTitle;

            BasisLocalization.SetLanguage("ja");
            string japanese = BasisHandHeldCameraPanelProvider.StaticTitle;

            Assert.That(japanese, Is.Not.EqualTo(english),
                "a const string here would return the exact same text in every language forever — the original bug");
        }
    }
}
