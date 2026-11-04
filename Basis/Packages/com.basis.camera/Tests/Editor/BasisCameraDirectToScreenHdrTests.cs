using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    public class BasisCameraDirectToScreenHdrTests
    {
        [Test]
        public void TheMainWindowKeepsHdrOutput()
        {
            Assert.IsTrue(BasisCameraDirectToScreenOutput.AllowsHDROutput(0, false, null));
            Assert.IsTrue(BasisCameraDirectToScreenOutput.AllowsHDROutput(0, false, new HDROutputSettings[0]));
            Assert.IsTrue(BasisCameraDirectToScreenOutput.AllowsHDROutput(0), "URP decides HDR for the main window from the main display, as before.");
        }

        [Test]
        public void AnExtraWindowGetsHdrOutputOnlyWhenItsOwnDisplayHasIt()
        {
            Assert.IsFalse(BasisCameraDirectToScreenOutput.AllowsHDROutput(1, false, null));
            Assert.IsFalse(BasisCameraDirectToScreenOutput.AllowsHDROutput(1, false, new HDROutputSettings[0]));
            Assert.IsFalse(BasisCameraDirectToScreenOutput.AllowsHDROutput(1, false, new HDROutputSettings[2]), "A display with no HDR settings has no HDR output.");

            HDROutputSettings[] displays = HDROutputSettings.displays ?? new HDROutputSettings[0];
            Assert.IsFalse(BasisCameraDirectToScreenOutput.AllowsHDROutput(displays.Length, false, displays), "A display Unity does not list has no HDR output of its own.");
        }

        [Test]
        public void InTheEditorEveryWindowFollowsTheMainDisplay()
        {
            Assert.IsTrue(BasisCameraDirectToScreenOutput.AllowsHDROutput(2, true, null), "Every extra display in the editor is a Game View drawn on the editor's own monitor.");
        }
    }
}
