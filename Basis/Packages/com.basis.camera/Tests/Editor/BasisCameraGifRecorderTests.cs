using System;
using System.IO;
using System.Threading;
using Basis;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The recorder's edit-mode surface: the clamped setters a settings file loads through, the
    /// defaults the panel and the constructor must agree on, the idle state machine, and one
    /// real session run — frames in, a decodable GIF file out, delays taken from timestamps.
    /// </summary>
    public class BasisCameraGifRecorderTests
    {
        private GameObject _host;
        private BasisHandHeldCamera _camera;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("GifRecorderTest");
            _camera = _host.AddComponent<BasisHandHeldCamera>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
        }

        [Test]
        public void SettersClampToTheRangesThePanelPromises()
        {
            _camera.SetGifFrameRate(1);
            Assert.That(_camera.GifFrameRate, Is.EqualTo(BasisCameraRecordingLimits.MinGifFrameRate));
            _camera.SetGifFrameRate(500);
            Assert.That(_camera.GifFrameRate, Is.EqualTo(BasisCameraRecordingLimits.MaxGifFrameRate));

            _camera.SetGifDuration(0f);
            Assert.That(_camera.GifDurationSeconds, Is.EqualTo(BasisCameraRecordingLimits.MinGifDurationSeconds));
            _camera.SetGifDuration(600f);
            Assert.That(_camera.GifDurationSeconds, Is.EqualTo(BasisCameraRecordingLimits.MaxGifDurationSeconds));

            _camera.SetGifWidth(8);
            Assert.That(_camera.GifWidth, Is.EqualTo(BasisCameraRecordingLimits.MinGifWidth));
            _camera.SetGifWidth(9999);
            Assert.That(_camera.GifWidth, Is.EqualTo(BasisCameraRecordingLimits.MaxGifWidth));

            _camera.SetGifWidth(500);
            Assert.That(_camera.GifWidth, Is.EqualTo(500), "In-range values pass through unsnapped.");
        }

        [Test]
        public void FreshCameraAndFreshSettingsFileAgreeOnEveryGifDefault()
        {
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(_camera.GifDurationSeconds, Is.EqualTo(defaults.gifDurationSeconds));
            Assert.That(_camera.GifFrameRate, Is.EqualTo(defaults.gifFrameRate));
            Assert.That(_camera.GifWidth, Is.EqualTo(defaults.gifWidth));
            Assert.That(_camera.GifLoop, Is.EqualTo(defaults.gifLoop));
            Assert.That(_camera.GifDither, Is.EqualTo(defaults.gifDither));

            // A slider that opens at zero reads as broken; these also gate the recorder's maths.
            Assert.That(defaults.gifDurationSeconds, Is.GreaterThan(0f));
            Assert.That(defaults.gifFrameRate, Is.GreaterThan(0));
            Assert.That(defaults.gifWidth, Is.GreaterThan(0));
        }

        [Test]
        public void EveryPanelWidthPresetIsInsideTheSetterRange()
        {
            foreach (int preset in BasisCameraRecordingLimits.GifWidthPresets)
            {
                Assert.That(preset, Is.InRange(BasisCameraRecordingLimits.MinGifWidth, BasisCameraRecordingLimits.MaxGifWidth));
            }
        }

        [Test]
        public void RecorderStartsIdleAndRefusesWithoutAFeed()
        {
            Assert.That(_camera.GifState, Is.EqualTo(BasisCameraRecordingState.Idle));
            Assert.That(_camera.IsGifRecording, Is.False);

            // No capture camera or render texture exists in edit mode; a start must refuse
            // cleanly rather than spin up a session with nothing to record.
            Assert.That(_camera.StartGifRecording(), Is.False);
            Assert.That(_camera.GifState, Is.EqualTo(BasisCameraRecordingState.Idle));

            // Stop from idle is a no-op, not an error.
            _camera.StopGifRecording();
            Assert.That(_camera.GifState, Is.EqualTo(BasisCameraRecordingState.Idle));
        }

        // ---- the session end to end ----------------------------------------------------

        [Test]
        public void SessionEncodesQueuedFramesIntoADecodableFileWithTimestampDelays()
        {
            const int Width = 6, Height = 4;
            string finalPath = Path.Combine(Path.GetTempPath(), $"BasisGifSessionTest_{Guid.NewGuid():N}.gif");

            var session = new BasisGifRecorderSession(Width, Height, loop: true, dither: false, frameRate: 10, finalPath);
            Assert.That(session.Start(), Is.True);

            try
            {
                // 0.2s then 0.1s gaps: the delays must come from the timestamps, not the rate.
                AddFrame(session, Width, Height, shade: 40, timestamp: 100.0);
                AddFrame(session, Width, Height, shade: 120, timestamp: 100.2);
                AddFrame(session, Width, Height, shade: 220, timestamp: 100.3);
                session.CompleteAdding();

                WaitUntilFinished(session);

                Assert.That(session.FailureMessage, Is.Null);
                Assert.That(session.FramesEncoded, Is.EqualTo(3));
                Assert.That(File.Exists(finalPath), Is.True, "The finished file was not renamed into place.");
                Assert.That(File.Exists(finalPath + ".tmp"), Is.False, "The temporary file must not survive.");

                var parsed = BasisGifEncoderTests.ParsedGif.Parse(File.ReadAllBytes(finalPath));
                Assert.That(parsed.Width, Is.EqualTo(Width));
                Assert.That(parsed.Height, Is.EqualTo(Height));
                Assert.That(parsed.FrameIndices.Count, Is.EqualTo(3));
                Assert.That(parsed.HasNetscapeLoop, Is.True);
                Assert.That(parsed.Delays[0], Is.EqualTo(20), "First frame shows for the gap to the second.");
                Assert.That(parsed.Delays[1], Is.EqualTo(10), "Second frame shows for the gap to the third.");
                Assert.That(parsed.Delays[2], Is.EqualTo(10), "The last frame falls back to the nominal rate.");
            }
            finally
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                if (File.Exists(finalPath + ".tmp")) File.Delete(finalPath + ".tmp");
            }
        }

        [Test]
        public void ManyFramesComeOutInCaptureOrderThroughTheParallelPipeline()
        {
            // More frames than encode slots, each a distinct solid colour: however the pool
            // schedules the quantisations, frame N of the file must still be shade N.
            const int Width = 8, Height = 8, Frames = 24;
            string finalPath = Path.Combine(Path.GetTempPath(), $"BasisGifSessionOrder_{Guid.NewGuid():N}.gif");
            var session = new BasisGifRecorderSession(Width, Height, loop: false, dither: false, frameRate: 30, finalPath);
            Assert.That(session.Start(), Is.True);

            try
            {
                for (int Frame = 0; Frame < Frames; Frame++)
                {
                    AddFrame(session, Width, Height, shade: (byte)(10 * Frame), timestamp: 100.0 + Frame / 30.0);
                }
                session.CompleteAdding();
                WaitUntilFinished(session);

                Assert.That(session.FailureMessage, Is.Null);
                var parsed = BasisGifEncoderTests.ParsedGif.Parse(File.ReadAllBytes(finalPath));
                Assert.That(parsed.FrameIndices.Count, Is.EqualTo(Frames));

                for (int Frame = 0; Frame < Frames; Frame++)
                {
                    int paletteIndex = parsed.FrameIndices[Frame][0] * 3;
                    Assert.That(parsed.Palettes[Frame][paletteIndex], Is.EqualTo((byte)(10 * Frame)),
                        $"Frame {Frame} does not carry its own capture's colour — ordering broke in the pool.");
                }
            }
            finally
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                if (File.Exists(finalPath + ".tmp")) File.Delete(finalPath + ".tmp");
            }
        }

        [Test]
        public void SessionWithNoFramesReportsFailureAndLeavesNoFile()
        {
            string finalPath = Path.Combine(Path.GetTempPath(), $"BasisGifSessionEmpty_{Guid.NewGuid():N}.gif");
            var session = new BasisGifRecorderSession(4, 4, loop: false, dither: true, frameRate: 15, finalPath);
            Assert.That(session.Start(), Is.True);

            session.CompleteAdding();
            WaitUntilFinished(session);

            Assert.That(session.FailureMessage, Is.Not.Null);
            Assert.That(File.Exists(finalPath), Is.False);
            Assert.That(File.Exists(finalPath + ".tmp"), Is.False);
        }

        [Test]
        public void SessionRefusesAFrameOfTheWrongSize()
        {
            string finalPath = Path.Combine(Path.GetTempPath(), $"BasisGifSessionSize_{Guid.NewGuid():N}.gif");
            var session = new BasisGifRecorderSession(8, 8, loop: true, dither: true, frameRate: 15, finalPath);
            Assert.That(session.Start(), Is.True);

            try
            {
                var wrong = new NativeArray<byte>(16, Allocator.Temp);
                try
                {
                    Assert.That(session.TryAddFrame(wrong, 1.0), Is.False);
                }
                finally
                {
                    wrong.Dispose();
                }
            }
            finally
            {
                session.CompleteAdding();
                WaitUntilFinished(session);
                if (File.Exists(finalPath)) File.Delete(finalPath);
                if (File.Exists(finalPath + ".tmp")) File.Delete(finalPath + ".tmp");
            }
        }

        private static void AddFrame(BasisGifRecorderSession session, int width, int height, byte shade, double timestamp)
        {
            var frame = new NativeArray<byte>(width * height * 4, Allocator.Temp);
            try
            {
                for (int Pixel = 0; Pixel < width * height; Pixel++)
                {
                    frame[Pixel * 4] = shade;
                    frame[Pixel * 4 + 1] = (byte)(255 - shade);
                    frame[Pixel * 4 + 2] = 90;
                    frame[Pixel * 4 + 3] = 255;
                }
                Assert.That(session.TryAddFrame(frame, timestamp), Is.True);
            }
            finally
            {
                frame.Dispose();
            }
        }

        private static void WaitUntilFinished(BasisGifRecorderSession session)
        {
            for (int Waited = 0; Waited < 500; Waited++)
            {
                if (session.IsFinished) return;
                Thread.Sleep(10);
            }
            Assert.Fail("The encode worker did not finish within five seconds.");
        }
    }
}
