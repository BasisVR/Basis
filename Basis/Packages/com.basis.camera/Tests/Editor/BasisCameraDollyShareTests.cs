using System.Collections.Generic;
using System.IO;
using Basis.Cinematics;
using NUnit.Framework;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// Sharing a saved dolly track: what goes on the wire, and what a client does with one that
    /// arrives.
    ///
    /// <para>The receive half is the part worth testing hardest. A payload is a string another
    /// client wrote, so every test that hands one over is asking the same question twice: does a
    /// good track come back whole, and does a bad one stop here rather than reaching a waypoint.
    /// The other half is that accepting a track must never cost the receiver one of their own,
    /// which is the difference between <see cref="BasisCameraDollyPresets.Adopt"/> and the import
    /// folder's deliberate overwrite.</para>
    ///
    /// <para>Every test points the store at a temporary directory: the real one is somebody's own
    /// saved tracks.</para>
    /// </summary>
    public class BasisCameraDollyShareTests
    {
        private string _storeDirectory;

        [SetUp]
        public void SetUp()
        {
            _storeDirectory = Path.Combine(Path.GetTempPath(), "BasisCameraDollyShareTests-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_storeDirectory);
            BasisCameraDollyPresets.DirectoryOverrideForTest = _storeDirectory;
            BasisCameraDollyPresets.ResetCacheForTest();
            BasisCameraDollyShare.Register();
        }

        [TearDown]
        public void TearDown()
        {
            BasisCameraDollyPresets.DirectoryOverrideForTest = null;
            BasisCameraDollyPresets.ResetCacheForTest();

            try
            {
                if (Directory.Exists(_storeDirectory)) Directory.Delete(_storeDirectory, true);
            }
            catch (IOException)
            {
            }
        }

        private static readonly Vector3[] Track =
        {
            new Vector3(2f, 1f, 3f),
            new Vector3(-1f, 1.5f, 4f),
            new Vector3(0f, 2f, -6f),
        };

        private static BasisCameraDollyPreset Captured(string name, params Vector3[] positions)
        {
            BasisCameraDollyPreset preset = new BasisCameraDollyPreset { name = name };
            Vector3[] track = positions.Length == 0 ? Track : positions;

            List<Quaternion> rotations = new List<Quaternion>();
            for (int Index = 0; Index < track.Length; Index++)
            {
                rotations.Add(Quaternion.Euler(0f, Index * 30f, 0f));
            }

            preset.Capture(new List<Vector3>(track), rotations, new Vector3(1f, 0f, 2f), 45f, 1f);
            return preset;
        }

        private static string PayloadFor(BasisCameraDollyPreset preset) => JsonUtility.ToJson(preset);

        private static string Accept(string payload) =>
            BasisContentSharePayloadRegistry.Accept(ContentShareType.DollyTrack, payload);

        private static string Describe(string payload) =>
            BasisContentSharePayloadRegistry.Describe(ContentShareType.DollyTrack, payload);

        // ---- Registration ------------------------------------------------------------------

        [Test]
        public void TheDollyKindIsRegisteredAgainstItsOwnShareType()
        {
            Assert.IsTrue(BasisContentSharePayloadRegistry.IsHandled(ContentShareType.DollyTrack));
            Assert.IsTrue(BasisContentSharePayloadRegistry.TryGet(ContentShareType.DollyTrack,
                out BasisContentSharePayloadKind kind));
            Assert.AreEqual(BasisShareableKind.DollyTrack, kind.ShareableKind);
        }

        [Test]
        public void RegisteringTwiceLeavesOneKind()
        {
            BasisCameraDollyShare.Register();
            BasisCameraDollyShare.Register();

            Assert.IsTrue(BasisContentSharePayloadRegistry.TryGet(ContentShareType.DollyTrack, out _));
        }

        [Test]
        public void ADollyTrackCarriesItsPayloadInline()
        {
            Assert.IsTrue(ContentSharePayload.IsPayloadType(ContentShareType.DollyTrack));
            Assert.IsFalse(ContentSharePayload.IsPayloadType(ContentShareType.Prop));
        }

        // ---- The round trip ----------------------------------------------------------------

        [Test]
        public void AFullTrackFitsWellInsideTheMessageCeiling()
        {
            List<Vector3> points = new List<Vector3>();
            for (int Index = 0; Index < BasisCameraDollyPreset.MaxPoints; Index++)
            {
                points.Add(new Vector3(-123.456f, 123.456f, -123.456f));
            }

            string payload = PayloadFor(Captured("A Track Of Every Point It Can Hold", points.ToArray()));
            Assert.Less(payload.Length, ContentSharePayload.MaxLength);
        }

        [Test]
        public void AnAcceptedTrackComesBackWithItsShapeIntact()
        {
            BasisCameraDollyPreset sent = Captured("Sweep");

            Assert.AreEqual("Sweep", Accept(PayloadFor(sent)));

            BasisCameraDollyPreset received = BasisCameraDollyPresets.Find("Sweep");
            Assert.IsNotNull(received);
            Assert.IsTrue(received.SameShapeAs(sent));
        }

        [Test]
        public void TheOrbShowsTheNameTheTrackWasSharedUnder()
        {
            Assert.AreEqual("Sweep", Describe(PayloadFor(Captured("Sweep"))));
        }

        [Test]
        public void AnAcceptedTrackArrivesStoppedAndUnshared()
        {
            BasisCameraDollyPreset sent = Captured("Sweep");
            sent.motion.playing = true;
            sent.motion.syncMode = BasisCameraDollySync.Networked;

            Accept(PayloadFor(sent));

            BasisCameraDollyPreset received = BasisCameraDollyPresets.Find("Sweep");
            Assert.IsFalse(received.motion.playing, "A received track must not start running by itself.");
            Assert.AreEqual(BasisCameraDollySync.LocalOnly, received.motion.syncMode,
                "A received track must not publish itself to the instance.");
        }

        // ---- Not standing on the receiver's own tracks ---------------------------------------

        [Test]
        public void ATrackNamedLikeOneYouAlreadyHaveGoesInBesideIt()
        {
            BasisCameraDollyPreset mine = Captured("Sweep");
            Assert.IsTrue(BasisCameraDollyPresets.Store(mine, out _));

            BasisCameraDollyPreset theirs = Captured("Sweep", new Vector3(9f, 9f, 9f), new Vector3(-9f, 1f, 0f));
            Assert.AreEqual("Sweep 2", Accept(PayloadFor(theirs)));

            Assert.IsTrue(BasisCameraDollyPresets.Find("Sweep").SameShapeAs(mine),
                "Accepting a share must never write over a track the player saved themselves.");
            Assert.IsTrue(BasisCameraDollyPresets.Find("Sweep 2").SameShapeAs(theirs));
        }

        [Test]
        public void AcceptingTheSameTrackTwiceStoresItOnce()
        {
            string payload = PayloadFor(Captured("Sweep"));

            Assert.AreEqual("Sweep", Accept(payload));
            Assert.AreEqual("Sweep", Accept(payload));
            Assert.AreEqual(1, BasisCameraDollyPresets.Count);
        }

        [Test]
        public void AClashOnAFullLengthNameStillFindsRoomForTheSuffix()
        {
            string longName = new string('a', BasisCameraDollyPreset.MaxNameLength);
            Assert.IsTrue(BasisCameraDollyPresets.Store(Captured(longName), out _));

            string stored = Accept(PayloadFor(Captured(longName, new Vector3(4f, 4f, 4f), Vector3.zero)));

            Assert.IsNotNull(stored, "A name already at the length cap must not deadlock the search for a free one.");
            Assert.AreNotEqual(longName, stored);
            Assert.LessOrEqual(stored.Length, BasisCameraDollyPreset.MaxNameLength);
            Assert.AreEqual(2, BasisCameraDollyPresets.Count);
        }

        [Test]
        public void ATrackWithNoNameOfItsOwnIsGivenOne()
        {
            BasisCameraDollyPreset unnamed = Captured("   ");

            Assert.AreEqual(BasisCameraDollyPresets.UnnamedSharedTrack, Accept(PayloadFor(unnamed)));
        }

        // ---- Payloads that are not tracks ----------------------------------------------------

        [Test]
        public void GarbageIsRefusedRatherThanThrown()
        {
            Assert.IsNull(Accept("not json at all"));
            Assert.IsNull(Accept("{"));
            Assert.IsNull(Accept("{\"name\":\"Sweep\"}"));
            Assert.AreEqual(0, BasisCameraDollyPresets.Count);
        }

        [Test]
        public void AnEmptyOrNullPayloadIsRefused()
        {
            Assert.IsNull(Accept(null));
            Assert.IsNull(Accept(string.Empty));
            Assert.IsNull(Describe(null));
            Assert.AreEqual(0, BasisCameraDollyPresets.Count);
        }

        [Test]
        public void APayloadPastTheCeilingIsRefusedWithoutBeingParsed()
        {
            Assert.IsNull(Accept(new string('x', ContentSharePayload.MaxLength + 1)));
            Assert.AreEqual(0, BasisCameraDollyPresets.Count);
        }

        [Test]
        public void ATrackWithNoPointsIsRefused()
        {
            Assert.IsNull(Accept(PayloadFor(new BasisCameraDollyPreset { name = "Empty" })));
            Assert.AreEqual(0, BasisCameraDollyPresets.Count);
        }

        [Test]
        public void ShareRefusesATrackWithNothingInIt()
        {
            Assert.IsFalse(BasisCameraDollyShare.Share(null, out string error));
            Assert.AreEqual("camera.dollyPreset.error.noPoints", error);

            Assert.IsFalse(BasisCameraDollyShare.Share(new BasisCameraDollyPreset { name = "Empty" }, out error));
            Assert.AreEqual("camera.dollyPreset.error.noPoints", error);
        }

        // ---- Values that would poison the receiver -------------------------------------------

        [Test]
        public void NonFiniteNumbersNeverReachAStoredTrack()
        {
            BasisCameraDollyPreset poisoned = Captured("Poisoned");
            poisoned.points[1] = new BasisCameraDollyPresetPoint
            {
                position = new Vector3(float.NaN, float.PositiveInfinity, 1f),
                rotation = new Quaternion(float.NaN, 0f, 0f, 0f),
            };
            poisoned.anchorPosition = new Vector3(float.NaN, 0f, 0f);
            poisoned.anchorYaw = float.NaN;
            poisoned.gridSize = float.NaN;
            poisoned.motion.damping = float.NaN;
            poisoned.motion.speed = float.PositiveInfinity;
            poisoned.motion.easeInPortion = float.NaN;
            poisoned.motion.offset = new Vector3(0f, float.NegativeInfinity, 0f);

            // JsonUtility writes NaN/Infinity out and reads them straight back, so this is the
            // shape a hand-edited file or a hostile client actually delivers.
            Assert.AreEqual("Poisoned", Accept(PayloadFor(poisoned)));

            BasisCameraDollyPreset received = BasisCameraDollyPresets.Find("Poisoned");
            for (int Index = 0; Index < received.Count; Index++)
            {
                BasisCameraDollyPresetPoint point = received.points[Index];
                AssertFinite(point.position);
                Assert.IsFalse(float.IsNaN(point.rotation.x + point.rotation.y + point.rotation.z + point.rotation.w));
            }

            AssertFinite(received.anchorPosition);
            AssertFinite(received.motion.offset);
            Assert.IsFalse(float.IsNaN(received.anchorYaw));
            Assert.IsFalse(float.IsNaN(received.gridSize));
            Assert.IsFalse(float.IsNaN(received.motion.damping));
            Assert.IsFalse(float.IsInfinity(received.motion.speed));
            Assert.IsFalse(float.IsNaN(received.motion.easeInPortion));
        }

        [Test]
        public void AZeroAnchorScaleDoesNotDivideTheShapeAway()
        {
            BasisCameraDollyPreset preset = Captured("Flat");
            preset.anchorScale = 0f;

            Assert.AreEqual("Flat", Accept(PayloadFor(preset)));
            Assert.Greater(BasisCameraDollyPresets.Find("Flat").anchorScale, 0.001f);
        }

        [Test]
        public void MorePointsThanTheFormatHoldsAreTrimmedOff()
        {
            BasisCameraDollyPreset preset = Captured("Long");
            for (int Index = 0; Index < BasisCameraDollyPreset.MaxPoints * 2; Index++)
            {
                preset.points.Add(new BasisCameraDollyPresetPoint { rotation = Quaternion.identity });
            }

            Assert.AreEqual("Long", Accept(PayloadFor(preset)));
            Assert.AreEqual(BasisCameraDollyPreset.MaxPoints, BasisCameraDollyPresets.Find("Long").Count);
        }

        private static void AssertFinite(Vector3 value)
        {
            Assert.IsFalse(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z), $"{value} carries NaN");
            Assert.IsFalse(float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z),
                $"{value} carries an infinity");
        }
    }
}
