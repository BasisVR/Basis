using System.Collections.Generic;
using Basis.Scripts.Audio;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using UnityEngine;

namespace HVR.Basis.Comms.Tests
{
    /// Voice Gain across both sides of the seam: a real BasisAudioAndVisemeDriver recycling its
    /// pooled OpenLipSync context, read frame by frame by the real publisher, over a speak / pause
    /// / speak cycle.
    ///
    /// Two bugs are pinned here. Voice Gain used to survive the first utterance and then sit at 0
    /// for the rest of the session, because it was read off a context the driver had since pooled.
    /// And it used to BE the loudest non-"sil" viseme, so it reported lip-shape confidence instead
    /// of loudness and was flat 0 on any avatar without a viseme mesh.
    public class HVRRemoteVoiceGainTests
    {
        private const int VisemeCount = BasisOpenLipSyncContext.VisemeCount;
        private const int AaIndex = 10;
        private const int GainAddress = 9001;
        private const HVRBasisBuiltInAddressesVisemeFlags Gain = HVRBasisBuiltInAddressesVisemeFlags.Gain;

        // -20 dBFS and -40 dBFS on the shared -60..0 dB window.
        private const float SpeakingRms = 0.1f;
        private const float SpeakingLevel = 2f / 3f;
        private const float QuieterRms = 0.01f;
        private const float QuieterLevel = 1f / 3f;

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<Mesh> _meshes = new List<Mesh>();

        private int[] _addressIds;
        private HVRVariableStore _store;

        [SetUp]
        public void SetUp()
        {
            _addressIds = new int[VisemeCount];
            for (var index = 0; index < VisemeCount; index++)
            {
                _addressIds[index] = index + 1;
            }
            _store = new HVRVariableStore();
        }

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _spawned.Count; index++)
            {
                if (_spawned[index] != null) Object.DestroyImmediate(_spawned[index]);
            }
            for (var index = 0; index < _meshes.Count; index++)
            {
                if (_meshes[index] != null) Object.DestroyImmediate(_meshes[index]);
            }
            _spawned.Clear();
            _meshes.Clear();
        }

        private BasisAudioAndVisemeDriver BuildRemoteDriver(bool withVisemeMesh = true)
        {
            var root = new GameObject("RemoteVoiceAvatar");
            _spawned.Add(root);

            var avatar = root.AddComponent<BasisAvatar>();

            if (withVisemeMesh)
            {
                var mesh = new Mesh();
                _meshes.Add(mesh);
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                mesh.triangles = new[] { 0, 1, 2 };
                var delta = new[] { Vector3.up, Vector3.up, Vector3.up };
                for (var index = 0; index < VisemeCount; index++)
                {
                    mesh.AddBlendShapeFrame($"viseme{index}", 100f, delta, null, null);
                }

                var renderer = root.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;

                avatar.FaceVisemeMesh = renderer;
                avatar.FaceVisemeMovement = new int[VisemeCount];
                for (var index = 0; index < VisemeCount; index++)
                {
                    avatar.FaceVisemeMovement[index] = index;
                }
            }

            var mouth = new GameObject("Mouth");
            _spawned.Add(mouth);

            var remote = new BasisRemotePlayer
            {
                DisplayName = "remote-voice-gain-test",
                UUID = System.Guid.NewGuid().ToString("N"),
                MouthTransform = mouth.transform,
                BasisAvatar = avatar,
                FaceIsVisible = true,
            };

            var driver = new BasisAudioAndVisemeDriver();
            Assert.AreEqual(withVisemeMesh, driver.TryInitialize(remote));
            driver.InVisemeRange = true;
            driver.FaceVisible = true;

            var source = new GameObject("SpatialSource");
            _spawned.Add(source);
            driver.TrackedAudioSource = source.AddComponent<AudioSource>();
            driver.AudioSourceInactive = false;
            return driver;
        }

        /// The backend is not available offline, so speech is staged the way the audio thread would
        /// leave it: the level the receiver measured, plus — when there is a mesh to drive — a
        /// context planted the way TryAcquireOpenLipSyncContext would and the weight Apply would
        /// have written.
        private static void StartSpeaking(BasisAudioAndVisemeDriver driver, float rms, float visemeWeight = 0f)
        {
            driver.AudioSourceInactive = false;
            driver.VoiceRms = rms;

            if (visemeWeight <= 0f) return;

            driver.openLipSyncContext = new BasisOpenLipSyncContext();
            driver.UseOpenLipSync = true;
            driver.openLipSyncContext.LastApplied[AaIndex] = visemeWeight;
        }

        private static void Publish(HVRBuiltInAddressPublisher publisher, HVRVariableStore store, BasisAudioAndVisemeDriver driver)
        {
            publisher.Publish(store, driver.openLipSyncContext, driver.VoiceLevel01, Gain);
        }

        [Test]
        public void It_should_report_the_measured_voice_level_not_a_viseme_weight()
        {
            // Given — a mouth wide open on a barely audible whisper: the viseme says 1.0, the voice does not
            var publisher = new HVRBuiltInAddressPublisher(_addressIds, GainAddress);
            var driver = BuildRemoteDriver();

            // When
            StartSpeaking(driver, QuieterRms, 100f);
            driver.Simulate(0.016f);
            Publish(publisher, _store, driver);

            // Then
            Assert.AreEqual(QuieterLevel, _store.GetValue(GainAddress), 1e-4f, "gain followed the lip shape instead of the loudness");
        }

        [Test]
        public void It_should_keep_reporting_gain_after_a_pause_recycles_the_context()
        {
            // Given
            var publisher = new HVRBuiltInAddressPublisher(_addressIds, GainAddress);
            var driver = BuildRemoteDriver();

            StartSpeaking(driver, SpeakingRms, 60f);
            driver.Simulate(0.016f);
            Publish(publisher, _store, driver);
            Assert.AreEqual(SpeakingLevel, _store.GetValue(GainAddress), 1e-4f, "the first utterance always worked");

            // When — they go quiet long enough for BasisAudioReceiver to disable the source
            driver.AudioSourceInactive = true;
            driver.Simulate(0.016f);
            Publish(publisher, _store, driver);
            Assert.AreEqual(0f, _store.GetValue(GainAddress), 1e-5f, "silence has to read as silence");

            // ...and then speak again, which acquires a fresh context
            StartSpeaking(driver, QuieterRms, 80f);
            driver.Simulate(0.016f);
            Publish(publisher, _store, driver);

            // Then
            Assert.AreEqual(QuieterLevel, _store.GetValue(GainAddress), 1e-4f, "voice gain stayed at 0 for the rest of the session");
        }

        [Test]
        public void It_should_keep_reporting_gain_after_a_range_round_trip()
        {
            // Given
            var publisher = new HVRBuiltInAddressPublisher(_addressIds, GainAddress);
            var driver = BuildRemoteDriver();

            StartSpeaking(driver, SpeakingRms, 60f);
            driver.Simulate(0.016f);
            Publish(publisher, _store, driver);
            Assert.AreEqual(SpeakingLevel, _store.GetValue(GainAddress), 1e-4f);

            // When — walking out of viseme range recycles the context just as an idle gap does
            driver.InVisemeRange = false;
            driver.Simulate(0.016f);
            driver.VoiceRms = 0f;
            Publish(publisher, _store, driver);
            Assert.AreEqual(0f, _store.GetValue(GainAddress), 1e-5f);

            driver.InVisemeRange = true;
            StartSpeaking(driver, QuieterRms, 45f);
            driver.Simulate(0.016f);
            Publish(publisher, _store, driver);

            // Then
            Assert.AreEqual(QuieterLevel, _store.GetValue(GainAddress), 1e-4f, "walking back into range never restored the glow");
        }

        [Test]
        public void It_should_track_the_gain_within_a_single_utterance()
        {
            // Given
            var publisher = new HVRBuiltInAddressPublisher(_addressIds, GainAddress);
            var driver = BuildRemoteDriver();
            StartSpeaking(driver, QuieterRms, 20f);

            // When
            driver.Simulate(0.016f);
            Publish(publisher, _store, driver);
            Assert.AreEqual(QuieterLevel, _store.GetValue(GainAddress), 1e-4f);

            driver.VoiceRms = SpeakingRms;
            driver.Simulate(0.016f);
            Publish(publisher, _store, driver);

            // Then
            Assert.AreEqual(SpeakingLevel, _store.GetValue(GainAddress), 1e-4f);
        }

        /// Issue #998: the avatar wanting this is a robot tying an emissive face logo to its voice.
        /// It has no mouth to move, so it never gets an OpenLipSync context — which is exactly the
        /// state the viseme-derived gain reported as permanent silence.
        [Test]
        public void It_should_report_gain_for_an_avatar_with_no_visemes()
        {
            // Given
            var publisher = new HVRBuiltInAddressPublisher(_addressIds, GainAddress);
            var driver = BuildRemoteDriver(withVisemeMesh: false);

            // When
            StartSpeaking(driver, SpeakingRms);
            driver.Simulate(0.016f);
            Publish(publisher, _store, driver);

            // Then
            Assert.IsNull(driver.openLipSyncContext, "the fixture is meant to have no context at all");
            Assert.AreEqual(SpeakingLevel, _store.GetValue(GainAddress), 1e-4f, "an avatar with no mouth reported no voice");
        }

        [Test]
        public void It_should_report_zero_while_the_spatial_source_is_idle()
        {
            // Given — a level left over from the last utterance, which OnAudioFilterRead stops updating
            var driver = BuildRemoteDriver();
            driver.VoiceRms = SpeakingRms;

            // When
            driver.AudioSourceInactive = true;

            // Then
            Assert.AreEqual(0f, driver.VoiceLevel01, 1e-5f);

            // ...unless they are announcing, which routes their voice off the spatial source entirely
            driver.AnnounceActive = true;
            Assert.AreEqual(SpeakingLevel, driver.VoiceLevel01, 1e-4f, "an announcer read as silent for as long as they announced");
        }

        [Test]
        public void It_should_clamp_the_published_gain_to_zero_one()
        {
            // Given
            var publisher = new HVRBuiltInAddressPublisher(_addressIds, GainAddress);

            // When / Then
            publisher.Publish(_store, null, 4.2f, Gain);
            Assert.AreEqual(1f, _store.GetValue(GainAddress), 1e-5f);

            publisher.Publish(_store, null, -3f, Gain);
            Assert.AreEqual(0f, _store.GetValue(GainAddress), 1e-5f);
        }

        [Test]
        public void It_should_map_silence_to_zero_and_full_scale_to_one()
        {
            Assert.AreEqual(0f, BasisVoiceLevel.RmsToUnit(0f), 1e-5f);
            Assert.AreEqual(0f, BasisVoiceLevel.RmsToUnit(0.001f), 1e-5f, "-60 dBFS is the floor of the window");
            Assert.AreEqual(0f, BasisVoiceLevel.RmsToUnit(1e-9f), 1e-5f, "below the floor still clamps, never goes negative");
            Assert.AreEqual(1f, BasisVoiceLevel.RmsToUnit(1f), 1e-5f);
            Assert.AreEqual(1f, BasisVoiceLevel.RmsToUnit(4f), 1e-5f, "a limiter overshoot still clamps to 1");
        }
    }
}
