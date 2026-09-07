using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine;
using CameraSettings = BasisHandHeldCameraUI.CameraSettings;

namespace Basis.Tests.Camera
{
    /// <summary>The settings readout: every value the camera holds, as text on the Preset tab.</summary>
    public class BasisCameraSettingsReadoutTests
    {
        /// <summary>The colour a row that has left its mode is written in.</summary>
        private const string Changed = "<color=#5FA8FF>";

        private GameObject _go;
        private BasisHandHeldCamera _camera;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("ReadoutCameraUnderTest");
            _camera = _go.AddComponent<BasisHandHeldCamera>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [Test]
        public void TheReadoutShowsEverySectionAndResolvesItsIndexes()
        {
            CameraSettings settings = BasisCameraSettingsRig.DistinctiveSettings();
            BasisHandHeldCameraMetaData metaData = new BasisHandHeldCameraMetaData();

            string text = BasisCameraSettingsReadout.Build(
                settings,
                (int)BasisHandHeldCameraInteractable.CameraPinSpace.WorldSpace,
                metaData);

            Assert.That(text, Is.Not.Null.And.Not.Empty);

            // The values that reach the camera as an index into a preset table are the ones a raw
            // readout would render meaningless.
            Assert.That(text, Does.Contain(metaData.apertures[settings.apertureIndex]));
            Assert.That(text, Does.Contain(metaData.shutterSpeeds[settings.shutterSpeedIndex]));
            Assert.That(text, Does.Contain(metaData.isoValues[settings.isoIndex]));
            Assert.That(text, Does.Contain(
                $"{metaData.resolutions[settings.resolutionIndex].width} x {metaData.resolutions[settings.resolutionIndex].height}"));

            // A value from each of the settings groups, so a group dropped from the readout fails
            // here rather than going unnoticed until someone reads the page.
            Assert.That(text, Does.Contain("77"), "the field of view is missing");
            Assert.That(text, Does.Contain("0.35"), "the vignette is missing");
            Assert.That(text, Does.Contain("0.8"), "the framing radius is missing");
            Assert.That(text.Split('\n').Length, Is.GreaterThan(40),
                "the readout is meant to be every setting, not a summary");
        }

        [Test]
        public void TheReadoutSurvivesACameraWithNoPresetTables()
        {
            Assert.That(
                BasisCameraSettingsReadout.Build(BasisCameraSettingsRig.DistinctiveSettings(), 0, null),
                Is.Not.Null.And.Not.Empty);

            Assert.That(
                BasisCameraSettingsReadout.Build(null, 0, null),
                Is.Empty);
        }

        // ---- The rows that have left the mode -------------------------------------------------

        [Test]
        public void WithNothingToCompareAgainst_NoRowIsColoured()
        {
            // The panel is not the only thing that may ever build one of these, and a page of
            // markup would be a worse answer than a page of text to anything that cannot render it.
            string text = BasisCameraSettingsReadout.Build(
                BasisCameraSettingsRig.DistinctiveSettings(), 0, new BasisHandHeldCameraMetaData());

            Assert.That(text, Does.Not.Contain("<color"),
                "A readout with no mode behind it has nothing to call changed.");
        }

        [Test]
        public void AValueChangedSinceTheMode_IsTheOnlyRowColoured()
        {
            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);
            _camera.subjectSettings.anchorToBody = !_camera.subjectSettings.anchorToBody;
            _camera.RefreshCameraMode();

            Assert.That(_camera.CameraMode, Is.EqualTo(BasisCameraMode.Custom), "Precondition.");

            CameraSettings settings = BasisCameraSettingsRig.DistinctiveSettings();
            settings.modifiers = _camera.Modifiers;

            string text = BasisCameraSettingsReadout.Build(
                settings, 0, new BasisHandHeldCameraMetaData(),
                _camera.CompareToMode(_camera.ComparedMode));

            Assert.That(ColouredRowCount(text), Is.EqualTo(1),
                "One value moved, so exactly one row has left the mode.");

            // The rows are keyed by which value they show rather than by their label, so the row
            // that moved is the one carrying the flipped flag.
            string coloured = ColouredRow(text);
            Assert.That(coloured, Does.Contain(BasisLocalization.Get(
                _camera.subjectSettings.anchorToBody ? "ui.option.on" : "ui.option.off")));

            // The dropdown is showing Custom by now, so the page has to open by saying what it is
            // measuring against or the colour means nothing. Asserted on the shape rather than on
            // the sentence: the language tables come through Addressables, which a batch edit-mode
            // run may not have built, and a test of the readout is not a test of that.
            Assert.That(text.Split('\n')[0], Does.StartWith(Changed).And.EndWith("</color>"));
        }

        [Test]
        public void ACameraStillSittingOnItsMode_ColoursNothing()
        {
            _camera.ApplyCameraMode(BasisCameraMode.FollowMe);

            CameraSettings settings = BasisCameraSettingsRig.DistinctiveSettings();
            settings.modifiers = _camera.Modifiers;

            string text = BasisCameraSettingsReadout.Build(
                settings, 0, new BasisHandHeldCameraMetaData(),
                _camera.CompareToMode(_camera.ComparedMode));

            Assert.That(text, Does.Not.Contain("<color"),
                "Nothing has been changed, so there is nothing to point at.");
        }

        [Test]
        public void TheTypedSenderName_CannotBeReadAsMarkup()
        {
            CameraSettings settings = BasisCameraSettingsRig.DistinctiveSettings();
            settings.streamSenderName = "<color=#FF0000>bogus";

            string text = BasisCameraSettingsReadout.Build(
                settings, 0, new BasisHandHeldCameraMetaData());

            Assert.That(text, Does.Contain("<noparse><color=#FF0000>bogus</noparse>"),
                "The one value on the page somebody typed has to be fenced off from the parser.");
        }

        private static int ColouredRowCount(string text)
        {
            int count = 0;
            foreach (string line in text.Split('\n'))
            {
                if (line.Contains(Changed)) count++;
            }

            // The legend above the sections is coloured too, as the sample of what the colour means.
            return count - 1;
        }

        private static string ColouredRow(string text)
        {
            string[] lines = text.Split('\n');
            for (int Index = 1; Index < lines.Length; Index++)
            {
                if (lines[Index].Contains(Changed)) return lines[Index];
            }

            return string.Empty;
        }
    }
}
