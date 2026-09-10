using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Basis;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The video recorder's edit-mode surface, mirroring the GIF suite: clamped setters,
    /// defaults that agree with the settings file, the idle state machine, the audio buffer's
    /// conversion maths, and real session runs — frames and listener audio in, a decodable
    /// MJPEG AVI out with the two streams aligned.
    /// </summary>
    public class BasisCameraVideoRecorderTests
    {
        private GameObject _host;
        private BasisHandHeldCamera _camera;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("VideoRecorderTest");
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
            _camera.SetVideoRecordingFrameRate(1);
            Assert.That(_camera.VideoRecordingFrameRate, Is.EqualTo(BasisCameraRecordingLimits.MinVideoFrameRate));
            _camera.SetVideoRecordingFrameRate(500);
            Assert.That(_camera.VideoRecordingFrameRate, Is.EqualTo(BasisCameraRecordingLimits.MaxVideoFrameRate));

            _camera.SetVideoRecordingDuration(0f);
            Assert.That(_camera.VideoRecordingDurationSeconds, Is.EqualTo(BasisCameraRecordingLimits.MinVideoDurationSeconds));
            _camera.SetVideoRecordingDuration(9000f);
            Assert.That(_camera.VideoRecordingDurationSeconds, Is.EqualTo(BasisCameraRecordingLimits.MaxVideoDurationSeconds));

            _camera.SetVideoRecordingWidth(8);
            Assert.That(_camera.VideoRecordingWidth, Is.EqualTo(BasisCameraRecordingLimits.MinVideoWidth));
            _camera.SetVideoRecordingWidth(99999);
            Assert.That(_camera.VideoRecordingWidth, Is.EqualTo(BasisCameraRecordingLimits.MaxVideoWidth));

            _camera.SetVideoRecordingQuality(1);
            Assert.That(_camera.VideoRecordingQuality, Is.EqualTo(BasisCameraRecordingLimits.MinVideoQuality));
            _camera.SetVideoRecordingQuality(101);
            Assert.That(_camera.VideoRecordingQuality, Is.EqualTo(BasisCameraRecordingLimits.MaxVideoQuality));

            _camera.SetVideoRecordingWidth(1600);
            Assert.That(_camera.VideoRecordingWidth, Is.EqualTo(1600), "In-range values pass through unsnapped.");
        }

        [Test]
        public void FreshCameraAndFreshSettingsFileAgreeOnEveryVideoDefault()
        {
            var defaults = new BasisHandHeldCameraUI.CameraSettings();

            Assert.That(_camera.VideoRecordingDurationSeconds, Is.EqualTo(defaults.videoDurationSeconds));
            Assert.That(_camera.VideoRecordingFrameRate, Is.EqualTo(defaults.videoFrameRate));
            Assert.That(_camera.VideoRecordingWidth, Is.EqualTo(defaults.videoWidth));
            Assert.That(_camera.VideoRecordingQuality, Is.EqualTo(defaults.videoQuality));
            Assert.That(_camera.VideoRecordingTimeLimit, Is.EqualTo(defaults.videoTimeLimit));
            Assert.That(_camera.VideoContinuousClips, Is.EqualTo(defaults.videoContinuousClips));

            Assert.That(defaults.videoDurationSeconds, Is.GreaterThan(0f));
            Assert.That(defaults.videoFrameRate, Is.GreaterThan(0));
            Assert.That(defaults.videoWidth, Is.GreaterThan(0));
            Assert.That(defaults.videoQuality, Is.GreaterThan(0));
            Assert.That(defaults.videoTimeLimit, Is.True, "Recordings stop themselves unless the player opts out.");
            Assert.That(defaults.videoContinuousClips, Is.False, "The length ends the recording until the player asks for a run of clips.");
        }

        [Test]
        public void EveryPanelWidthPresetIsInsideTheSetterRange()
        {
            foreach (int preset in BasisCameraRecordingLimits.VideoWidthPresets)
            {
                Assert.That(preset, Is.InRange(BasisCameraRecordingLimits.MinVideoWidth, BasisCameraRecordingLimits.MaxVideoWidth));
            }
        }

        [Test]
        public void RecorderStartsIdleAndRefusesWithoutAFeed()
        {
            Assert.That(_camera.VideoRecordingState, Is.EqualTo(BasisCameraRecordingState.Idle));
            Assert.That(_camera.IsVideoRecording, Is.False);

            Assert.That(_camera.StartVideoRecording(), Is.False);
            Assert.That(_camera.VideoRecordingState, Is.EqualTo(BasisCameraRecordingState.Idle));

            _camera.StopVideoRecording();
            Assert.That(_camera.VideoRecordingState, Is.EqualTo(BasisCameraRecordingState.Idle));
        }

        // ---- the audio buffer ------------------------------------------------------------

        [Test]
        public void StereoAudioComesBackAsTheSamePcm()
        {
            var buffer = new BasisRecordingAudioBuffer(1000);
            buffer.Write(new[] { 0.5f, -0.5f, 1f, -1f }, 2);

            byte[] pcm = new byte[64];
            int bytes = buffer.Read(pcm);

            Assert.That(bytes, Is.EqualTo(8));
            Assert.That(Sample(pcm, 0), Is.EqualTo((short)(0.5f * short.MaxValue)));
            Assert.That(Sample(pcm, 1), Is.EqualTo((short)(-0.5f * short.MaxValue)));
            Assert.That(Sample(pcm, 2), Is.EqualTo(short.MaxValue));
            Assert.That(Sample(pcm, 3), Is.EqualTo((short)-short.MaxValue));
        }

        [Test]
        public void MonoIsDoubledAndWideLayoutsKeepTheirFrontPair()
        {
            var buffer = new BasisRecordingAudioBuffer(1000);
            buffer.Write(new[] { 0.25f }, 1);
            buffer.Write(new[] { 0.5f, -0.5f, 0.9f, 0.9f }, 4);

            byte[] pcm = new byte[64];
            int bytes = buffer.Read(pcm);

            Assert.That(bytes, Is.EqualTo(8));
            Assert.That(Sample(pcm, 0), Is.EqualTo(Sample(pcm, 1)), "Mono goes to both ears.");
            Assert.That(Sample(pcm, 2), Is.EqualTo((short)(0.5f * short.MaxValue)), "Quad keeps front left.");
            Assert.That(Sample(pcm, 3), Is.EqualTo((short)(-0.5f * short.MaxValue)), "Quad keeps front right.");
        }

        [Test]
        public void OverflowBecomesSilenceOnceTheRingDrains()
        {
            // Ten sample frames of capacity; writing fifteen drops five.
            var buffer = new BasisRecordingAudioBuffer(100, capacitySeconds: 0.1f);
            float[] frame = { 0.5f, 0.5f };
            for (int Write = 0; Write < 15; Write++) buffer.Write(frame, 2);

            byte[] pcm = new byte[256];
            int bytes = buffer.Read(pcm);

            Assert.That(bytes, Is.EqualTo(15 * BasisRecordingAudioBuffer.BytesPerSampleFrame),
                "Dropped frames must come back as silence so the stream keeps its duration.");
            Assert.That(Sample(pcm, 0), Is.Not.Zero);
            Assert.That(Sample(pcm, 10 * 2), Is.Zero, "The dropped tail is silent.");
            Assert.That(Sample(pcm, 14 * 2 + 1), Is.Zero);
        }

        [Test]
        public void ClearForgetsBufferedAudioAndSilenceDebt()
        {
            var buffer = new BasisRecordingAudioBuffer(100, capacitySeconds: 0.1f);
            float[] frame = { 0.5f, 0.5f };
            for (int Write = 0; Write < 15; Write++) buffer.Write(frame, 2);

            buffer.Clear();
            Assert.That(buffer.Read(new byte[64]), Is.Zero);
        }

        // ---- the session end to end --------------------------------------------------------

        [Test]
        public void SessionEncodesFramesAndAlignedAudioIntoADecodableFile()
        {
            const int Width = 8, Height = 6;
            const int SampleRate = 1000;
            string finalPath = Path.Combine(Path.GetTempPath(), $"BasisVideoSessionTest_{Guid.NewGuid():N}.avi");

            var audio = new BasisRecordingAudioBuffer(SampleRate);
            // 300 sample frames of a ramp, buffered from the moment the "tap" attached at t=100.0.
            float[] ramp = new float[300 * 2];
            for (int Frame = 0; Frame < 300; Frame++)
            {
                ramp[Frame * 2] = Frame / 1000f;
                ramp[Frame * 2 + 1] = Frame / 1000f;
            }
            audio.Write(ramp, 2);

            var session = new BasisVideoRecorderSession(Width, Height, quality: 80, frameRate: 10, finalPath, audio, audioStartTime: 100.0);
            Assert.That(session.Start(), Is.True);

            try
            {
                // First frame captured at 100.1: the tenth of a second of audio before it must be skipped.
                AddFrame(session, Width, Height, shade: 30, timestamp: 100.1);
                AddFrame(session, Width, Height, shade: 128, timestamp: 100.2);
                AddFrame(session, Width, Height, shade: 220, timestamp: 100.3);
                session.CompleteAdding();

                WaitUntilFinished(session);

                Assert.That(session.FailureMessage, Is.Null);
                Assert.That(session.FramesEncoded, Is.EqualTo(3));
                Assert.That(File.Exists(finalPath), Is.True);
                Assert.That(File.Exists(finalPath + ".tmp"), Is.False);

                var parsed = BasisMjpegAviWriterTests.ParsedAvi.Parse(File.ReadAllBytes(finalPath));
                Assert.That(parsed.Width, Is.EqualTo(Width));
                Assert.That(parsed.Height, Is.EqualTo(Height));
                Assert.That(parsed.Frames.Count, Is.EqualTo(3));
                foreach (byte[] frame in parsed.Frames)
                {
                    Assert.That(frame[0], Is.EqualTo(0xFF));
                    Assert.That(frame[1], Is.EqualTo(0xD8), "Every video chunk must be a JPEG.");
                }

                Assert.That(parsed.Rate, Is.EqualTo(10000), "Two 0.1s gaps measure as 10fps at scale 1000.");
                Assert.That(parsed.StreamCount, Is.EqualTo(2));
                Assert.That(parsed.AudioSampleRate, Is.EqualTo(SampleRate));
                Assert.That(parsed.AudioChannels, Is.EqualTo(BasisRecordingAudioBuffer.Channels));
                Assert.That(parsed.AudioBitsPerSample, Is.EqualTo(16));

                int audioBytes = 0;
                foreach (byte[] chunk in parsed.AudioChunks) audioBytes += chunk.Length;
                Assert.That(audioBytes, Is.EqualTo(200 * BasisRecordingAudioBuffer.BytesPerSampleFrame),
                    "One hundred of the three hundred buffered sample frames precede the first video frame and are skipped.");
                Assert.That(parsed.AudioStreamLength, Is.EqualTo(200));

                short firstSample = (short)(parsed.AudioChunks[0][0] | (parsed.AudioChunks[0][1] << 8));
                Assert.That(firstSample, Is.EqualTo((short)(0.1f * short.MaxValue)).Within(2),
                    "Audio must start at the first video frame's moment, not the tap's.");
            }
            finally
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                if (File.Exists(finalPath + ".tmp")) File.Delete(finalPath + ".tmp");
            }
        }

        [Test]
        public void SessionWithoutAudioWritesASingleStreamFile()
        {
            const int Width = 8, Height = 6;
            string finalPath = Path.Combine(Path.GetTempPath(), $"BasisVideoSessionSilent_{Guid.NewGuid():N}.avi");
            var session = new BasisVideoRecorderSession(Width, Height, quality: 60, frameRate: 15, finalPath);
            Assert.That(session.Start(), Is.True);

            try
            {
                AddFrame(session, Width, Height, shade: 90, timestamp: 10.0);
                session.CompleteAdding();
                WaitUntilFinished(session);

                Assert.That(session.FailureMessage, Is.Null);
                var parsed = BasisMjpegAviWriterTests.ParsedAvi.Parse(File.ReadAllBytes(finalPath));
                Assert.That(parsed.StreamCount, Is.EqualTo(1));
                Assert.That(parsed.AudioChunks.Count, Is.Zero);
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
            // schedules the JPEG encodes, frame N of the file must still decode to shade N.
            const int Width = 16, Height = 16, Frames = 24;
            string finalPath = Path.Combine(Path.GetTempPath(), $"BasisVideoSessionOrder_{Guid.NewGuid():N}.avi");
            var session = new BasisVideoRecorderSession(Width, Height, quality: 90, frameRate: 30, finalPath);
            Assert.That(session.Start(), Is.True);

            Texture2D decoded = new Texture2D(2, 2);
            try
            {
                for (int Frame = 0; Frame < Frames; Frame++)
                {
                    AddFrame(session, Width, Height, shade: (byte)(10 * Frame), timestamp: 100.0 + Frame / 30.0);
                }
                session.CompleteAdding();
                WaitUntilFinished(session);

                Assert.That(session.FailureMessage, Is.Null);
                var parsed = BasisMjpegAviWriterTests.ParsedAvi.Parse(File.ReadAllBytes(finalPath));
                Assert.That(parsed.Frames.Count, Is.EqualTo(Frames));

                for (int Frame = 0; Frame < Frames; Frame++)
                {
                    Assert.That(ImageConversion.LoadImage(decoded, parsed.Frames[Frame]), Is.True,
                        $"Frame {Frame} is not a decodable JPEG.");
                    Assert.That((int)decoded.GetPixels32()[0].r, Is.EqualTo(10 * Frame).Within(12),
                        $"Frame {Frame} does not carry its own capture's colour — ordering broke in the pool.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(decoded);
                if (File.Exists(finalPath)) File.Delete(finalPath);
                if (File.Exists(finalPath + ".tmp")) File.Delete(finalPath + ".tmp");
            }
        }

        [Test]
        public void SessionWithNoFramesReportsFailureAndLeavesNoFile()
        {
            string finalPath = Path.Combine(Path.GetTempPath(), $"BasisVideoSessionEmpty_{Guid.NewGuid():N}.avi");
            var session = new BasisVideoRecorderSession(4, 4, quality: 80, frameRate: 30, finalPath);
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
            string finalPath = Path.Combine(Path.GetTempPath(), $"BasisVideoSessionSize_{Guid.NewGuid():N}.avi");
            var session = new BasisVideoRecorderSession(8, 8, quality: 80, frameRate: 30, finalPath);
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

        // ---- a recording that rolls into new clips ------------------------------------------

        [Test]
        public void ReachingTheLengthOpensTheNextClipWithoutLeavingTheRecordingState()
        {
            var opened = new List<FakeSession>();
            Func<IBasisFrameRecorderSession> factory = () =>
            {
                var next = new FakeSession($"clip{opened.Count + 1}.avi");
                opened.Add(next);
                return next;
            };

            var recorder = new BasisCameraFrameRecorder("Test");
            // A zero length makes every tick a tick the deadline has already passed on.
            Assert.That(recorder.Start(factory(), 4, 4, 30, 0f, false, factory), Is.True);

            try
            {
                recorder.Tick(null, false);

                Assert.That(recorder.State, Is.EqualTo(BasisCameraRecordingState.Recording),
                    "A run of clips must never leave the recording state at a join — the feed the capture reads is gated on it.");
                Assert.That(recorder.SegmentsCompleted, Is.EqualTo(1));
                Assert.That(recorder.SegmentNumber, Is.EqualTo(2));
                Assert.That(opened.Count, Is.EqualTo(2), "The roll opens the next clip in the same tick.");
                Assert.That(opened[0].CompleteAddingCalls, Is.EqualTo(1), "The clip that closed is told exactly once.");
                Assert.That(opened[1].CompleteAddingCalls, Is.Zero);
                Assert.That(recorder.LastFileName, Is.EqualTo("clip1.avi"), "Each clip is reported as it lands.");

                recorder.Tick(null, false);

                Assert.That(recorder.SegmentsCompleted, Is.EqualTo(2));
                Assert.That(opened.Count, Is.EqualTo(3));
                Assert.That(recorder.LastFileName, Is.EqualTo("clip2.avi"));
            }
            finally
            {
                recorder.Shutdown();
            }
        }

        [Test]
        public void ReachingTheLengthWithoutAFactoryStillEndsTheRecording()
        {
            var only = new FakeSession("single.avi");
            var recorder = new BasisCameraFrameRecorder("Test");
            Assert.That(recorder.Start(only, 4, 4, 30, 0f, false), Is.True);
            Assert.That(recorder.SegmentNumber, Is.Zero, "A single-clip recording has no clip number to show.");

            recorder.Tick(null, false);

            Assert.That(recorder.State, Is.EqualTo(BasisCameraRecordingState.Idle));
            Assert.That(only.CompleteAddingCalls, Is.EqualTo(1));
            Assert.That(recorder.LastFileName, Is.EqualTo("single.avi"));
        }

        [Test]
        public void AClipThatWillNotOpenEndsTheRunAndKeepsWhatWasRecorded()
        {
            var first = new FakeSession("clip1.avi");
            var recorder = new BasisCameraFrameRecorder("Test");
            Assert.That(recorder.Start(first, 4, 4, 30, 0f, false, () => null), Is.True);

            recorder.Tick(null, false);

            Assert.That(recorder.State, Is.EqualTo(BasisCameraRecordingState.Idle));
            Assert.That(first.CompleteAddingCalls, Is.EqualTo(1));
            Assert.That(recorder.LastFileName, Is.EqualTo("clip1.avi"));
        }

        [Test]
        public void StoppingMidRunFinishesTheClipInHandAndOpensNoMore()
        {
            var opened = new List<FakeSession>();
            Func<IBasisFrameRecorderSession> factory = () =>
            {
                var next = new FakeSession($"clip{opened.Count + 1}.avi");
                opened.Add(next);
                return next;
            };

            var recorder = new BasisCameraFrameRecorder("Test");
            recorder.Start(factory(), 4, 4, 30, 0f, false, factory);
            recorder.Tick(null, false);

            recorder.Stop();
            recorder.Tick(null, false);

            Assert.That(recorder.State, Is.EqualTo(BasisCameraRecordingState.Idle));
            Assert.That(recorder.SegmentsCompleted, Is.EqualTo(1), "Stopping ends the run rather than rolling it again.");
            Assert.That(opened.Count, Is.EqualTo(2));
            Assert.That(opened[1].CompleteAddingCalls, Is.EqualTo(1));
            Assert.That(recorder.LastFileName, Is.EqualTo("clip2.avi"));
        }

        [Test]
        public void AClipWithTimeLeftIsNotRolled()
        {
            var opened = new List<FakeSession>();
            Func<IBasisFrameRecorderSession> factory = () =>
            {
                var next = new FakeSession($"clip{opened.Count + 1}.avi");
                opened.Add(next);
                return next;
            };

            var recorder = new BasisCameraFrameRecorder("Test");
            recorder.Start(factory(), 4, 4, 30, 600f, false, factory);

            try
            {
                recorder.Tick(null, false);

                Assert.That(recorder.SegmentsCompleted, Is.Zero);
                Assert.That(recorder.SegmentNumber, Is.EqualTo(1));
                Assert.That(opened.Count, Is.EqualTo(1));
                Assert.That(opened[0].CompleteAddingCalls, Is.Zero);
            }
            finally
            {
                recorder.Shutdown();
            }
        }

        /// <summary>
        /// A session with no encoder behind it: records what the recorder hands it and reports
        /// itself finished the moment it is told nothing more is coming, so a roll's bookkeeping
        /// can be driven a tick at a time without a GPU, a worker or a file.
        /// </summary>
        private sealed class FakeSession : IBasisFrameRecorderSession
        {
            public FakeSession(string finalPath) => FinalPath = finalPath;

            public int CompleteAddingCalls { get; private set; }

            public string FinalPath { get; }
            public int FramesQueued => 0;
            public int FramesEncoded { get; private set; }
            public bool IsFinished => CompleteAddingCalls > 0;
            public string FailureMessage => null;

            public bool TryAddFrame(NativeArray<byte> rgba, double timestamp)
            {
                FramesEncoded++;
                return true;
            }

            public void CompleteAdding() => CompleteAddingCalls++;
        }

        private static short Sample(byte[] pcm, int index) =>
            (short)(pcm[index * 2] | (pcm[index * 2 + 1] << 8));

        private static void AddFrame(BasisVideoRecorderSession session, int width, int height, byte shade, double timestamp)
        {
            var frame = new NativeArray<byte>(width * height * 4, Allocator.Temp);
            try
            {
                for (int Pixel = 0; Pixel < width * height; Pixel++)
                {
                    frame[Pixel * 4] = shade;
                    frame[Pixel * 4 + 1] = (byte)(255 - shade);
                    frame[Pixel * 4 + 2] = 60;
                    frame[Pixel * 4 + 3] = 255;
                }
                Assert.That(session.TryAddFrame(frame, timestamp), Is.True);
            }
            finally
            {
                frame.Dispose();
            }
        }

        private static void WaitUntilFinished(BasisVideoRecorderSession session)
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
