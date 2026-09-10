using System.Collections.Generic;
using Basis.BasisUI.HandHeldCamera;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The Output tab: where the shot goes once it has been taken. These settings are unusual in
    /// that several of them are not per-camera at all — the audio listener is a single scene-wide
    /// resource that whichever camera asked for it last holds — so the interesting failures are
    /// about two cameras rather than one control.
    ///
    /// <para>
    /// The streaming values are clamped in their setters rather than by the widget, because the
    /// panel's field lets you type. A port outside the bindable range or a quality outside 1-100
    /// fails at the socket or the encoder, a long way from the control that caused it.
    /// </para>
    /// </summary>
    public class BasisCameraOutputSettingTests
    {
        private BasisCameraSettingsRig _rig;

        [SetUp]
        public void SetUp() => _rig = new BasisCameraSettingsRig();

        [TearDown]
        public void TearDown()
        {
            BasisHandHeldCameraAudioListener.Set(_rig?.Camera, false);
            _rig?.Dispose();
        }

        // ---------- Streaming ----------

        [Test]
        public void StreamPort_IsHeldWhereASocketCanActuallyBind()
        {
            _rig.Camera.SetWebStreamPort(80);
            Assert.That(_rig.Camera.VideoOutputSettings.WebPort, Is.GreaterThanOrEqualTo(1024),
                "Ports below 1024 need privileges the game does not have.");

            _rig.Camera.SetWebStreamPort(99999);
            Assert.That(_rig.Camera.VideoOutputSettings.WebPort, Is.LessThanOrEqualTo(65535));

            _rig.Camera.SetWebStreamPort(9000);
            Assert.That(_rig.Camera.VideoOutputSettings.WebPort, Is.EqualTo(9000));
        }

        [Test]
        public void ThePanelsPortValidatorAgreesWithTheClampBehindIt()
        {
            // The field rejects what it says it rejects, and accepts what the setter would keep.
            // Disagreement here means either a value refused that would have worked, or one
            // accepted that is silently changed the moment it lands.
            _rig.Camera.SetWebStreamPort(BasisHandHeldCameraPanelProvider.WebPortMinForTest);
            Assert.That(_rig.Camera.VideoOutputSettings.WebPort,
                Is.EqualTo(BasisHandHeldCameraPanelProvider.WebPortMinForTest));

            _rig.Camera.SetWebStreamPort(BasisHandHeldCameraPanelProvider.WebPortMaxForTest);
            Assert.That(_rig.Camera.VideoOutputSettings.WebPort,
                Is.EqualTo(BasisHandHeldCameraPanelProvider.WebPortMaxForTest));
        }

        [Test]
        public void StreamQuality_IsHeldWhereAJpegEncoderAcceptsIt()
        {
            _rig.Camera.SetWebStreamQuality(0);
            Assert.That(_rig.Camera.VideoOutputSettings.WebQuality, Is.InRange(1, 100));

            _rig.Camera.SetWebStreamQuality(500);
            Assert.That(_rig.Camera.VideoOutputSettings.WebQuality, Is.InRange(1, 100));

            _rig.Camera.SetWebStreamQuality(60);
            Assert.That(_rig.Camera.VideoOutputSettings.WebQuality, Is.EqualTo(60));
        }

        [Test]
        public void StreamResolutionAndFrameRate_ReachTheStreamSettings()
        {
            _rig.Camera.SetVideoOutputResolution(2560, 1440);
            _rig.Camera.SetVideoOutputFrameRate(60f);

            Assert.That(_rig.Camera.VideoOutputSettings.Width, Is.EqualTo(2560));
            Assert.That(_rig.Camera.VideoOutputSettings.Height, Is.EqualTo(1440));
            Assert.That(_rig.Camera.VideoOutputSettings.FrameRate, Is.EqualTo(60f).Within(1e-3f));
        }

        [Test]
        public void EveryStreamResolutionThePanelOffersIsOneTheCameraAccepts()
        {
            // The dropdown reads the two tables at one index and hands the pair straight over.
            int[] widths = BasisHandHeldCameraPanelProvider.VideoResolutionWidthsForTest;
            int[] heights = BasisHandHeldCameraPanelProvider.VideoResolutionHeightsForTest;

            for (int Index = 0; Index < widths.Length; Index++)
            {
                _rig.Camera.SetVideoOutputResolution(widths[Index], heights[Index]);

                Assert.That(_rig.Camera.VideoOutputSettings.Width, Is.EqualTo(widths[Index]));
                Assert.That(_rig.Camera.VideoOutputSettings.Height, Is.EqualTo(heights[Index]));
            }
        }

        [Test]
        public void ThereIsAlwaysATransportToChoose()
        {
            // The web stream is pure sockets, so even a machine with no shared-texture backend has
            // one. An empty list would leave the dropdown blank and live output unreachable.
            var transports = BasisCameraVideoPlatform.Transports();

            Assert.That(transports, Is.Not.Empty);
            for (int Index = 0; Index < transports.Count; Index++)
            {
                Assert.That(BasisCameraVideoPlatform.TransportName(transports[Index]), Is.Not.Null.And.Not.Empty,
                    "A transport with no name renders as an empty dropdown row.");
                Assert.That(BasisCameraVideoPlatform.TransportRequirement(transports[Index]), Is.Not.Null,
                    "The requirement text is what tells the user what to install on the receiving side.");
            }
        }

        [Test]
        public void ChangingTransportWhileIdle_LeavesTheStreamIdle()
        {
            var transports = BasisCameraVideoPlatform.Transports();
            if (transports.Count < 2) Assert.Ignore("Only one transport is compiled in on this platform.");

            _rig.Camera.SetVideoTransport(transports[1]);

            Assert.That(_rig.Camera.VideoTransport, Is.EqualTo(transports[1]));
            Assert.That(_rig.Camera.IsAnyVideoOutputActive, Is.False,
                "Picking a transport is not the same as switching the stream on.");
        }

        // ---------- Stream presets ----------

        [Test]
        public void EveryStreamPresetOffered_UsesATransportThisBuildCanRun()
        {
            List<BasisVideoTransport> transports = BasisCameraVideoPlatform.Transports();
            List<BasisCameraStreamPreset> presets = BasisCameraStreamPresets.Available();

            Assert.That(presets, Is.Not.Empty, "The web stream is always available, so its presets always are.");
            for (int Index = 0; Index < presets.Count; Index++)
            {
                Assert.That(transports, Does.Contain(presets[Index].Transport),
                    $"{presets[Index].Key} names a transport the transport dropdown does not offer.");
            }
        }

        [Test]
        public void TheMjpegPresets_AreOfferedEverywhere_AndThePlatformOnesOnlyWithABackend()
        {
            List<BasisCameraStreamPreset> presets = BasisCameraStreamPresets.Available();
            int web = 0, platform = 0, webInRoster = 0, platformInRoster = 0;
            for (int Index = 0; Index < presets.Count; Index++)
            {
                if (presets[Index].Transport == BasisVideoTransport.Web) web++; else platform++;
            }
            for (int Index = 0; Index < BasisCameraStreamPresets.All.Length; Index++)
            {
                if (BasisCameraStreamPresets.All[Index].Transport == BasisVideoTransport.Web) webInRoster++; else platformInRoster++;
            }

            Assert.That(webInRoster, Is.GreaterThan(0));
            Assert.That(platformInRoster, Is.GreaterThan(0));
            Assert.That(web, Is.EqualTo(webInRoster),
                "MJPEG needs nothing installed, so every one of its presets is offered on every platform.");
            Assert.That(platform, Is.EqualTo(BasisCameraVideoPlatform.Supported ? platformInRoster : 0),
                "A Spout or Syphon preset on a build with no shared-texture backend would apply a transport that refuses to start.");
        }

        [Test]
        public void EveryStreamPreset_SitsOnTheRowsThePanelCanShow()
        {
            int[] widths = BasisHandHeldCameraPanelProvider.VideoResolutionWidthsForTest;
            int[] heights = BasisHandHeldCameraPanelProvider.VideoResolutionHeightsForTest;

            for (int Index = 0; Index < BasisCameraStreamPresets.All.Length; Index++)
            {
                BasisCameraStreamPreset preset = BasisCameraStreamPresets.All[Index];
                bool listed = false;
                for (int Row = 0; Row < widths.Length; Row++)
                {
                    listed |= widths[Row] == preset.Width && heights[Row] == preset.Height;
                }

                Assert.That(listed, Is.True,
                    $"{preset.Key} uses a size the resolution dropdown has no row for, so the dropdown would show nothing once it is applied.");
                Assert.That(preset.FrameRate, Is.InRange(15f, 120f), $"{preset.Key} sets a frame rate outside the slider's travel.");
                if (preset.Transport == BasisVideoTransport.Web)
                {
                    Assert.That(preset.WebQuality, Is.InRange(10, 95), $"{preset.Key} sets a JPEG quality outside the slider's travel.");
                }
            }
        }

        [Test]
        public void StreamPresetKeys_AreUniqueAndAllListedForTheTextSweep()
        {
            string[] keys = BasisCameraStreamPresets.OptionKeys;
            HashSet<string> seen = new HashSet<string>();
            for (int Index = 0; Index < keys.Length; Index++)
            {
                Assert.That(seen.Add(keys[Index]), Is.True,
                    $"{keys[Index]} is listed twice; the dropdown resolves a selection by matching its key.");
            }

            Assert.That(keys.Length, Is.EqualTo(BasisCameraStreamPresets.All.Length + 1));
            Assert.That(keys, Does.Contain(BasisCameraStreamPresets.CustomKey));
            for (int Index = 0; Index < BasisCameraStreamPresets.All.Length; Index++)
            {
                Assert.That(keys, Does.Contain(BasisCameraStreamPresets.All[Index].Key));
            }
        }

        [Test]
        public void ApplyingAStreamPreset_LandsEveryValueAndReadsBackAsThatPreset()
        {
            List<BasisCameraStreamPreset> presets = BasisCameraStreamPresets.Available();

            for (int Index = 0; Index < presets.Count; Index++)
            {
                BasisCameraStreamPreset preset = presets[Index];
                _rig.Camera.ApplyStreamPreset(preset);

                Assert.That(_rig.Camera.VideoTransport, Is.EqualTo(preset.Transport), preset.Key);
                Assert.That(_rig.Camera.VideoOutputSettings.Width, Is.EqualTo(preset.Width), preset.Key);
                Assert.That(_rig.Camera.VideoOutputSettings.Height, Is.EqualTo(preset.Height), preset.Key);
                Assert.That(_rig.Camera.VideoOutputSettings.FrameRate, Is.EqualTo(preset.FrameRate).Within(1e-3f), preset.Key);
                if (preset.Transport == BasisVideoTransport.Web)
                {
                    Assert.That(_rig.Camera.VideoOutputSettings.WebQuality, Is.EqualTo(preset.WebQuality), preset.Key);
                }

                Assert.That(_rig.Camera.MatchesStreamPreset(preset), Is.True,
                    $"{preset.Key} was applied and the camera does not read as being in it.");
                Assert.That(BasisCameraStreamPresets.IndexOf(presets, _rig.Camera.VideoTransport, _rig.Camera.VideoOutputSettings), Is.EqualTo(Index),
                    $"{preset.Key} was applied and the dropdown would show a different row.");
            }
        }

        [Test]
        public void ApplyingAStreamPreset_DoesNotSwitchTheStreamOn()
        {
            List<BasisCameraStreamPreset> presets = BasisCameraStreamPresets.Available();
            for (int Index = 0; Index < presets.Count; Index++)
            {
                _rig.Camera.ApplyStreamPreset(presets[Index]);
                Assert.That(_rig.Camera.IsAnyVideoOutputActive, Is.False,
                    "Picking a preset is not the same as switching the stream on; that is the Live Output toggle's job.");
            }
        }

        [Test]
        public void EditingAStreamSettingByHand_DropsThePresetToCustom()
        {
            BasisCameraStreamPreset preset = BasisCameraStreamPresets.Available()[0];

            _rig.Camera.ApplyStreamPreset(preset);
            Assert.That(BasisCameraStreamPresets.KeyFor(_rig.Camera.VideoTransport, _rig.Camera.VideoOutputSettings), Is.EqualTo(preset.Key));

            _rig.Camera.SetVideoOutputFrameRate(preset.FrameRate + 5f);
            Assert.That(BasisCameraStreamPresets.KeyFor(_rig.Camera.VideoTransport, _rig.Camera.VideoOutputSettings), Is.EqualTo(BasisCameraStreamPresets.CustomKey),
                "A changed frame rate is no longer the preset's frame rate, and the dropdown has to say so.");

            _rig.Camera.ApplyStreamPreset(preset);
            _rig.Camera.SetVideoOutputResolution(preset.Width / 2, preset.Height / 2);
            Assert.That(BasisCameraStreamPresets.KeyFor(_rig.Camera.VideoTransport, _rig.Camera.VideoOutputSettings), Is.EqualTo(BasisCameraStreamPresets.CustomKey),
                "A changed resolution is no longer the preset's resolution.");
        }

        [Test]
        public void AWebPresetOwnsTheJpegQuality_AndAPlatformPresetLeavesItAlone()
        {
            List<BasisCameraStreamPreset> presets = BasisCameraStreamPresets.Available();
            int webIndex = -1, platformIndex = -1;
            for (int Index = 0; Index < presets.Count; Index++)
            {
                if (presets[Index].Transport == BasisVideoTransport.Web) { if (webIndex < 0) webIndex = Index; }
                else if (platformIndex < 0) platformIndex = Index;
            }
            Assert.That(webIndex, Is.GreaterThanOrEqualTo(0), "There is always an MJPEG preset.");

            _rig.Camera.SetWebStreamQuality(33);
            _rig.Camera.ApplyStreamPreset(presets[webIndex]);
            Assert.That(_rig.Camera.VideoOutputSettings.WebQuality, Is.EqualTo(presets[webIndex].WebQuality),
                "An MJPEG preset is a bandwidth choice, and the JPEG quality is most of that.");

            _rig.Camera.SetWebStreamQuality(presets[webIndex].WebQuality - 1);
            Assert.That(_rig.Camera.MatchesStreamPreset(presets[webIndex]), Is.False,
                "The quality is part of what an MJPEG preset means, so changing it leaves the preset.");

            if (platformIndex < 0) return;

            _rig.Camera.SetWebStreamQuality(33);
            _rig.Camera.ApplyStreamPreset(presets[platformIndex]);
            Assert.That(_rig.Camera.VideoOutputSettings.WebQuality, Is.EqualTo(33),
                "A shared-texture preset encodes nothing, so it has no opinion about JPEG quality.");
            Assert.That(_rig.Camera.MatchesStreamPreset(presets[platformIndex]), Is.True);
        }

        [Test]
        public void AStreamPreset_KeepsThePortAndSenderName()
        {
            _rig.Camera.SetWebStreamPort(9123);
            _rig.Camera.VideoOutputSettings.SenderName = "Studio Cam";

            List<BasisCameraStreamPreset> presets = BasisCameraStreamPresets.Available();
            for (int Index = 0; Index < presets.Count; Index++)
            {
                _rig.Camera.ApplyStreamPreset(presets[Index]);
                Assert.That(_rig.Camera.VideoOutputSettings.WebPort, Is.EqualTo(9123), presets[Index].Key);
                Assert.That(_rig.Camera.VideoOutputSettings.SenderName, Is.EqualTo("Studio Cam"), presets[Index].Key);
            }
        }

        [Test]
        public void StreamSettingsFromAFile_AreClampedAndFallBackToTheWebStream()
        {
            string name = _rig.Camera.VideoOutputSettings.SenderName;

            _rig.Camera.ApplyStreamSettings((BasisVideoTransport)7, 4, 100000, 24f, 500, 80, "   ");

            Assert.That(_rig.Camera.VideoTransport, Is.EqualTo(BasisVideoTransport.Web),
                "A transport this build has no backend for would refuse to start; the web stream always can.");
            Assert.That(_rig.Camera.VideoOutputSettings.Width, Is.EqualTo(16));
            Assert.That(_rig.Camera.VideoOutputSettings.Height, Is.EqualTo(8192));
            Assert.That(_rig.Camera.VideoOutputSettings.FrameRate, Is.EqualTo(24f).Within(1e-3f));
            Assert.That(_rig.Camera.VideoOutputSettings.WebQuality, Is.EqualTo(100));
            Assert.That(_rig.Camera.VideoOutputSettings.WebPort, Is.EqualTo(1024));
            Assert.That(_rig.Camera.VideoOutputSettings.SenderName, Is.EqualTo(name), "A blank name is not a name.");
        }

        [Test]
        public void TheStreamSettings_ComeBackOffTheSettingsFile()
        {
            BasisHandHeldCameraUI.CameraSettings stored = new BasisHandHeldCameraUI.CameraSettings
            {
                streamTransport = (int)BasisVideoTransport.Web,
                streamWidth = 1280,
                streamHeight = 720,
                streamFrameRate = 24f,
                streamQuality = 55,
                streamPort = 9123,
                streamSenderName = "Studio Cam",
            };

            _rig.UI.ApplySettingsForTest(stored);
            BasisHandHeldCameraUI.CameraSettings captured = _rig.UI.CreateCurrentCameraSettingsForTest();

            Assert.That(captured.streamTransport, Is.EqualTo((int)BasisVideoTransport.Web));
            Assert.That(captured.streamWidth, Is.EqualTo(1280));
            Assert.That(captured.streamHeight, Is.EqualTo(720));
            Assert.That(captured.streamFrameRate, Is.EqualTo(24f).Within(1e-3f));
            Assert.That(captured.streamQuality, Is.EqualTo(55));
            Assert.That(captured.streamPort, Is.EqualTo(9123));
            Assert.That(captured.streamSenderName, Is.EqualTo("Studio Cam"));

            _rig.UI.ApplySettingsForTest(new BasisHandHeldCameraUI.CameraSettings { streamTransport = 99 });
            Assert.That(_rig.UI.CreateCurrentCameraSettingsForTest().streamTransport, Is.EqualTo((int)BasisVideoTransport.Web),
                "A transport number the build does not know loads as the one that always works.");
        }

        // ---------- Stream pacing ----------
        //
        // What decides when a live frame goes out. Everything here fails the same quiet way: the
        // stream still runs, the average frame rate still reads correctly, and the picture arrives
        // unevenly — which no assertion about settings or sockets would ever catch.

        /// <summary>Runs the pacer for a number of ticks and counts what it published.</summary>
        private static int CountPublished(ref BasisStreamFramePacer pacer, int ticks, float deltaTime, float frameRate)
        {
            int published = 0;
            for (int Index = 0; Index < ticks; Index++)
            {
                if (pacer.AllowThisFrame(deltaTime, frameRate, true, true)) published++;
            }
            return published;
        }

        [Test]
        public void StreamPacing_PublishesEveryFrameASourceAtTheStreamRateDraws()
        {
            // The bug this replaced: the capture camera was floored at the stream rate and the
            // stream paced itself at the same rate off a second accumulator. Two clocks running at
            // one rate, sampled once a frame, drift in and out of phase — so roughly every other
            // fresh render was dropped and a 30fps stream ran at 15 with no setting to explain it.
            BasisStreamFramePacer pacer = default;

            int published = CountPublished(ref pacer, 60, 1f / 30f, 30f);

            Assert.That(published, Is.EqualTo(60),
                "A source drawing at exactly the stream rate has no frames to spare; every one of them should go out.");
        }

        [Test]
        public void StreamPacing_SurvivesAJitteryFrameTime()
        {
            // Real frame times are never the nominal interval, and a source a hair early must not
            // be held back for a whole one — that is the same halving by another route.
            BasisStreamFramePacer pacer = default;
            float[] deltas = { 0.0306f, 0.0361f, 0.0328f, 0.0341f, 0.0315f };
            int published = 0;

            for (int Index = 0; Index < 60; Index++)
            {
                if (pacer.AllowThisFrame(deltas[Index % deltas.Length], 30f, true, true)) published++;
            }

            Assert.That(published, Is.GreaterThanOrEqualTo(57),
                "Jitter around the interval should cost the odd frame at most, not one in two.");
        }

        [Test]
        public void StreamPacing_HoldsAFasterSourceToTheStreamRate()
        {
            // The floor under the capture camera is a floor, not a cap: an uncapped render limit or
            // the desktop override both leave it drawing far faster than the stream rate.
            BasisStreamFramePacer pacer = default;

            int published = CountPublished(ref pacer, 120, 1f / 120f, 30f);

            Assert.That(published, Is.EqualTo(30),
                "A 120fps source on a 30fps stream should publish exactly one frame in four.");
        }

        [Test]
        public void StreamPacing_DoesNotChargeForASlotTheSinkCouldNotTake()
        {
            // The sink takes one frame at a time through readback and encode. A tick that arrives
            // while it is busy used to spend the slot anyway, so the stream then waited out a whole
            // further interval — at 30fps that is a dropped frame for every collision.
            BasisStreamFramePacer pacer = default;
            const float Delta = 1f / 90f;

            // Three ticks of a 90fps source fill one 30fps interval.
            Assert.That(pacer.AllowThisFrame(Delta, 30f, true, true), Is.False);
            Assert.That(pacer.AllowThisFrame(Delta, 30f, true, true), Is.False);
            Assert.That(pacer.AllowThisFrame(Delta, 30f, true, false), Is.False,
                "The sink is busy, so nothing is published.");

            Assert.That(pacer.AllowThisFrame(Delta, 30f, true, true), Is.True,
                "The moment the sink is free the banked interval is still there, so the newest frame goes out now rather than a whole interval later.");
        }

        [Test]
        public void StreamPacing_NeverPublishesARenderTwice()
        {
            // Encoding and sending a picture the camera has not redrawn costs a readback, a JPEG
            // and a frame's bandwidth to tell the viewer nothing.
            BasisStreamFramePacer pacer = default;

            for (int Index = 0; Index < 60; Index++)
            {
                Assert.That(pacer.AllowThisFrame(1f / 60f, 30f, false, true), Is.False);
            }
        }

        [Test]
        public void StreamPacing_DoesNotPayAStallBackAsABurst()
        {
            // A hitch, a viewer reconnecting, a camera nobody was watching: without a cap on the
            // banked time, whatever paused the stream is repaid as a run of back-to-back frames the
            // moment it resumes, which is the opposite of what a live viewer wants.
            BasisStreamFramePacer pacer = default;
            const float Delta = 1f / 120f;

            for (int Index = 0; Index < 120; Index++) pacer.AllowThisFrame(Delta, 30f, false, true);

            int published = CountPublished(ref pacer, 8, Delta, 30f);

            Assert.That(published, Is.LessThanOrEqualTo(3),
                "A second of stalled stream should be worth a frame or two of catch-up, not thirty.");
        }

        [Test]
        public void StreamPacing_WithNoRateSetPublishesEveryFreshFrame()
        {
            // 0 means "as fast as the source draws", the same as everywhere else these rates are read.
            BasisStreamFramePacer pacer = default;

            int published = CountPublished(ref pacer, 20, 1f / 144f, 0f);

            Assert.That(published, Is.EqualTo(20));
        }

        // ---------- The single audio listener ----------

        [Test]
        public void HearingFromTheCamera_MovesBetweenCamerasRatherThanDoubling()
        {
            // There is one audio listener in the scene. Two cameras both claiming to be it would
            // mean the toggle reads true on a camera that is not actually hearing anything.
            using (BasisCameraSettingsRig second = new BasisCameraSettingsRig())
            {
                BasisHandHeldCameraAudioListener.Set(_rig.Camera, true);
                Assert.That(BasisHandHeldCameraAudioListener.IsHeldBy(_rig.Camera), Is.True);
                Assert.That(BasisHandHeldCameraAudioListener.IsHeldBy(second.Camera), Is.False);

                BasisHandHeldCameraAudioListener.Set(second.Camera, true);

                Assert.That(BasisHandHeldCameraAudioListener.IsHeldBy(second.Camera), Is.True);
                Assert.That(BasisHandHeldCameraAudioListener.IsHeldBy(_rig.Camera), Is.False,
                    "Taking the listener has to take it from whoever held it.");

                BasisHandHeldCameraAudioListener.Set(second.Camera, false);
                Assert.That(BasisHandHeldCameraAudioListener.IsHeldBy(second.Camera), Is.False);
            }
        }

        [Test]
        public void ReleasingTheListenerFromACameraThatDoesNotHoldIt_DoesNotStealItFromTheOneThatDoes()
        {
            using (BasisCameraSettingsRig second = new BasisCameraSettingsRig())
            {
                BasisHandHeldCameraAudioListener.Set(_rig.Camera, true);

                BasisHandHeldCameraAudioListener.Set(second.Camera, false);

                Assert.That(BasisHandHeldCameraAudioListener.IsHeldBy(_rig.Camera), Is.True);
            }
        }

        // ---------- Closing ----------

        [Test]
        public void CloseHidesInstead_IsWhatSeparatesADismissedCameraFromAHiddenOne()
        {
            // Hiding the camera from the panel keeps its settings up so you can carry on adjusting
            // it. Closing it with hide-instead is the state that offers Bring Back. Conflating the
            // two either strands the settings page or hides it while you are using it.
            _rig.Camera.SetCameraHidden(true);

            Assert.That(_rig.Camera.IsCameraHidden, Is.True);
            Assert.That(_rig.Camera.IsClosedHidden, Is.False,
                "Merely hiding the visuals must not put the panel into its Bring Back state.");

            _rig.Camera.CloseToHidden();

            Assert.That(_rig.Camera.IsClosedHidden, Is.True);
        }

        [Test]
        public void CloseHidesCamera_DefaultsOffSoCloseStillMeansClose()
        {
            Assert.That(_rig.UI.CloseHidesCamera, Is.False);
        }

        [Test]
        public void HidingTheCameraDoesNotStopItCapturing()
        {
            // The point of hiding rather than closing is that the camera keeps running — a stream
            // stays up and the preview keeps feeding the panel.
            _rig.Camera.ChangeResolution(1);
            _rig.Camera.SetCameraHidden(true);

            Assert.That(_rig.Camera.captureWidth, Is.EqualTo(1920));
            Assert.That(_rig.Camera.captureHeight, Is.EqualTo(1080));
            Assert.That(_rig.CaptureCamera, Is.Not.Null);
        }

        // ---------- Photo destination ----------

        [Test]
        public void SavedPhotosLandUnderTheCamerasOwnPhotosFolder()
        {
            string path = BasisCameraPhotoFolder.PathFor("shot.png");

            Assert.That(path, Is.Not.Null.And.Not.Empty);
            Assert.That(path, Does.EndWith("shot.png"));
            Assert.That(path.Length, Is.GreaterThan("shot.png".Length),
                "A bare filename means the shot lands in the working directory rather than the photos folder.");
        }
    }
}
