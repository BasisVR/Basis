using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis
{
    /// <summary>Where a clip recording is in its life. Saving means frames are still draining into the file.</summary>
    public enum BasisCameraRecordingState
    {
        Idle,
        Recording,
        Saving,
    }

    /// <summary>
    /// One recording's encode side, owned by a worker thread: takes raw frames off the main
    /// thread, writes a finished file, and reports progress through counters the panel polls.
    /// </summary>
    public interface IBasisFrameRecorderSession
    {
        /// <summary>Main thread. Copies one readback and queues it; false when full or failed.</summary>
        bool TryAddFrame(NativeArray<byte> rgba, double timestamp);

        /// <summary>Main thread. No more frames are coming; the worker finalises once the queue drains.</summary>
        void CompleteAdding();

        int FramesQueued { get; }
        int FramesEncoded { get; }
        bool IsFinished { get; }
        string FailureMessage { get; }
        string FinalPath { get; }
    }

    /// <summary>
    /// The capture half every clip recorder shares: a paced, aspect-cropped blit of the camera
    /// feed into its own small target, GPU readbacks polled oldest-first (they complete in order,
    /// so frames can never reach the file out of order), and a bounded hand-off into a session.
    /// The GIF and video recorders differ only in the session they plug in and whether the blit
    /// flips — GIF wants rows top-down, JPEG reads the readback's bottom-up rows upright.
    ///
    /// <para>A recording can also be a run of clips rather than one: given a factory, the length
    /// limit rolls the recording into a fresh session instead of ending it. The roll happens
    /// between two capture ticks with the pacing accumulator untouched, so the join costs exactly
    /// one frame interval — the same gap as any two frames inside a clip — and no frame is
    /// dropped or duplicated. The clip that just closed drains into its file on its own worker
    /// while the next one is already recording.</para>
    /// </summary>
    public sealed class BasisCameraFrameRecorder
    {
        /// <summary>
        /// Raw bytes allowed in flight — pending readbacks plus frames the encoder has not
        /// consumed. When the encoder falls behind, capture skips ticks instead of queueing
        /// without bound; the skipped time is carried by the frame timestamps, so playback
        /// speed stays true. A byte budget rather than a frame count, because one 4K video
        /// frame outweighs sixteen GIF frames.
        /// </summary>
        private const long MaxPendingBytes = 64L * 1024 * 1024;

        /// <summary>
        /// Clips allowed to be writing their files behind the one being recorded. Reaching this
        /// means the encoder is losing to real time; the roll then waits for a slot instead of
        /// opening another file, which makes a clip longer rather than costing the frames a
        /// refused capture would.
        /// </summary>
        private const int MaxDrainingSegments = 4;

        private struct PendingReadback
        {
            public AsyncGPUReadbackRequest Request;
            public double Timestamp;

            /// <summary>
            /// The session this frame was captured for. A readback issued before a roll belongs
            /// to the clip that was recording when it was issued, never to the one that took
            /// over while it was still in flight.
            /// </summary>
            public IBasisFrameRecorderSession Owner;
        }

        /// <summary>A clip that has stopped recording and is still writing its file.</summary>
        private struct DrainingSegment
        {
            public IBasisFrameRecorderSession Session;
            public bool Completed;
        }

        private readonly string label;
        private readonly List<PendingReadback> pendingReadbacks = new List<PendingReadback>();
        private readonly List<DrainingSegment> draining = new List<DrainingSegment>();
        private RenderTexture target;
        private BasisRenderRateLimiter pacing;
        private Func<IBasisFrameRecorderSession> nextSegment;
        private double deadline;
        private float segmentSeconds;
        private int frameRate;
        private int maxPendingFrames;
        private bool flipVertically;
        private bool completeSignalled;
        private bool rollWaitingOnEncoder;

        public BasisCameraFrameRecorder(string label)
        {
            this.label = label;
        }

        public BasisCameraRecordingState State { get; private set; } = BasisCameraRecordingState.Idle;
        public IBasisFrameRecorderSession Session { get; private set; }

        public bool IsRecording => State == BasisCameraRecordingState.Recording;

        /// <summary>Capture rate of the running recording, for the camera's render-rate floor.</summary>
        public int FrameRate => State != BasisCameraRecordingState.Idle ? frameRate : 0;

        /// <summary>Frames handed to the GPU for readback for the clip being recorded.</summary>
        public int FramesCaptured { get; private set; }

        /// <summary>Frames the worker has finished encoding into the file.</summary>
        public int FramesEncoded => Session != null ? Session.FramesEncoded : 0;

        /// <summary>Clips this run has already closed off and handed to their encoders.</summary>
        public int SegmentsCompleted { get; private set; }

        /// <summary>
        /// Which clip of a run of them is being recorded, counting from one, or zero when the
        /// recording is a single clip. The panel's wording hangs off this.
        /// </summary>
        public int SegmentNumber => nextSegment != null && State != BasisCameraRecordingState.Idle
            ? SegmentsCompleted + 1
            : 0;

        /// <summary>Seconds of recording time left, for the panel's stop-button label.</summary>
        public float SecondsRemaining => State == BasisCameraRecordingState.Recording
            ? Mathf.Max(0f, (float)(deadline - Time.unscaledTimeAsDouble))
            : 0f;

        /// <summary>Filename of the last clip this recorder saved, or null.</summary>
        public string LastFileName { get; private set; }

        /// <summary>Why the last recording failed, or null. Cleared when a new one starts.</summary>
        public string LastFailure { get; private set; }

        public Action<string> Saved;

        /// <summary>
        /// Adopts a started session and begins capturing into it. With <paramref name="nextSegment"/>
        /// given, the duration running out rolls the recording into whatever that returns instead
        /// of ending it — the factory opens the next clip's file and encoder, and returning null
        /// from it ends the run.
        /// </summary>
        public bool Start(IBasisFrameRecorderSession session, int width, int height, int framesPerSecond, float durationSeconds, bool flip,
            Func<IBasisFrameRecorderSession> nextSegment = null)
        {
            if (State != BasisCameraRecordingState.Idle || session == null) return false;

            target = new RenderTexture(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0) { sRGB = true })
            {
                name = $"Basis{label}Capture"
            };
            target.Create();

            Session = session;
            this.nextSegment = nextSegment;
            frameRate = framesPerSecond;
            flipVertically = flip;
            maxPendingFrames = (int)Mathf.Clamp(MaxPendingBytes / (width * height * 4L), 2, 16);
            pacing = default;
            pendingReadbacks.Clear();
            segmentSeconds = durationSeconds;
            deadline = Time.unscaledTimeAsDouble + durationSeconds;
            FramesCaptured = 0;
            SegmentsCompleted = 0;
            completeSignalled = false;
            rollWaitingOnEncoder = false;
            LastFileName = null;
            LastFailure = null;
            State = BasisCameraRecordingState.Recording;
            return true;
        }

        /// <summary>
        /// Ends the capture phase and lets the frames already taken drain into the file. Reached
        /// by the stop button, a capture lock landing mid-recording, and the duration running out
        /// on a recording that is a single clip.
        /// </summary>
        public void Stop()
        {
            if (State != BasisCameraRecordingState.Recording) return;
            State = BasisCameraRecordingState.Saving;
        }

        /// <summary>Per-frame upkeep, run from the camera's render-phase tick.</summary>
        public void Tick(RenderTexture source, bool captureBlocked)
        {
            if (State != BasisCameraRecordingState.Idle)
            {
                if (Session == null)
                {
                    pendingReadbacks.Clear();
                    ReleaseTarget();
                    State = BasisCameraRecordingState.Idle;
                }
                else
                {
                    TickCapture(source, captureBlocked);
                }
            }

            // Also while idle: the clips behind the one that just ended are still writing.
            TickDrainingSegments();
        }

        private void TickCapture(RenderTexture source, bool captureBlocked)
        {
            if (State == BasisCameraRecordingState.Recording)
            {
                if (captureBlocked || Session.FailureMessage != null)
                {
                    Stop();
                }
                else
                {
                    // The roll goes before the capture and leaves the pacing accumulator alone,
                    // so the tick the deadline falls on still takes its frame — into the new
                    // clip. That is what keeps the join free: one frame interval between the
                    // last frame of one clip and the first of the next, nothing dropped.
                    if (Time.unscaledTimeAsDouble >= deadline && !TryRollSegment()) Stop();
                    if (State == BasisCameraRecordingState.Recording) CaptureFrameIfDue(source);
                }
            }

            DrainReadbacks(blocking: false);

            if (State == BasisCameraRecordingState.Saving)
            {
                // Readbacks left over from an earlier clip are none of this one's business.
                if (!completeSignalled && CountPendingReadbacks(Session) == 0)
                {
                    Session.CompleteAdding();
                    completeSignalled = true;
                }
                if (Session.IsFinished) Finish();
            }
        }

        /// <summary>
        /// The duration has run out on a recording that continues in a new clip: closes the
        /// current session off and adopts the next one, in this tick, without leaving the
        /// recording state. False when the run should end instead — no factory at all, or a
        /// next clip that would not open.
        /// </summary>
        private bool TryRollSegment()
        {
            if (nextSegment == null) return false;

            if (draining.Count >= MaxDrainingSegments)
            {
                // Nothing is lost by waiting — capture carries on into the current clip, which
                // simply runs long — and the roll happens the moment a slot frees.
                if (!rollWaitingOnEncoder)
                {
                    rollWaitingOnEncoder = true;
                    BasisDebug.LogWarning(
                        $"{label} recording is holding clip {SegmentsCompleted + 1} open: {draining.Count} earlier clips are still saving.",
                        BasisDebug.LogTag.Camera);
                }
                return true;
            }
            rollWaitingOnEncoder = false;

            IBasisFrameRecorderSession opened;
            try
            {
                opened = nextSegment();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"{label} recording could not open the next clip: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
                return false;
            }
            if (opened == null) return false;

            // The readbacks already in flight keep pointing at the clip that asked for them;
            // it is told no more are coming once the last of them has landed.
            draining.Add(new DrainingSegment { Session = Session });
            Session = opened;
            SegmentsCompleted++;
            FramesCaptured = 0;

            // From the deadline, not from now, so a run of clips does not drift a tick longer
            // every time — unless the deadline is already a whole clip behind, which only a
            // deferred roll or a hitch that long can do.
            double now = Time.unscaledTimeAsDouble;
            deadline += segmentSeconds;
            if (deadline <= now) deadline = now + segmentSeconds;
            return true;
        }

        /// <summary>
        /// Clips handed off at a roll: told no more frames are coming once the last readback
        /// that belongs to them has landed, then reported and dropped when their worker has
        /// closed the file.
        /// </summary>
        private void TickDrainingSegments()
        {
            for (int Index = draining.Count - 1; Index >= 0; Index--)
            {
                DrainingSegment segment = draining[Index];

                if (!segment.Completed && CountPendingReadbacks(segment.Session) == 0)
                {
                    segment.Session.CompleteAdding();
                    segment.Completed = true;
                    draining[Index] = segment;
                }

                if (segment.Completed && segment.Session.IsFinished)
                {
                    Report(segment.Session);
                    draining.RemoveAt(Index);
                }
            }
        }

        /// <summary>
        /// Readbacks still in flight for one session. A plain loop rather than a predicate: this
        /// runs every tick for every clip in the pipeline, and the list never exceeds the
        /// pending-frame cap.
        /// </summary>
        private int CountPendingReadbacks(IBasisFrameRecorderSession session)
        {
            int owned = 0;
            for (int Index = 0; Index < pendingReadbacks.Count; Index++)
            {
                if (pendingReadbacks[Index].Owner == session) owned++;
            }
            return owned;
        }

        /// <summary>
        /// Teardown for a camera that closes mid-recording. The frames already read back are
        /// handed over synchronously and the worker finishes the file on its own thread — it
        /// holds no engine objects, so the clip still lands even though the camera is gone.
        /// </summary>
        public void Shutdown()
        {
            if (State == BasisCameraRecordingState.Idle && draining.Count == 0) return;

            DrainReadbacks(blocking: true);

            if (Session != null && !completeSignalled) Session.CompleteAdding();
            for (int Index = 0; Index < draining.Count; Index++)
            {
                if (!draining[Index].Completed) draining[Index].Session.CompleteAdding();
            }
            draining.Clear();

            pendingReadbacks.Clear();
            Session = null;
            nextSegment = null;
            completeSignalled = false;
            ReleaseTarget();
            State = BasisCameraRecordingState.Idle;
        }

        private void CaptureFrameIfDue(RenderTexture source)
        {
            if (source == null || target == null) return;
            if (!pacing.AllowThisFrame(Time.unscaledDeltaTime, frameRate, true)) return;
            // This clip's own frames only: leftovers owed to the clip before it are already
            // paid for and about to land, and counting them would stall capture at a join.
            if (Session.FramesQueued + CountPendingReadbacks(Session) >= maxPendingFrames) return;

            BasisHandHeldCamera.GetStreamBlitCrop(source, target, out Vector2 scale, out Vector2 offset);
            if (flipVertically)
            {
                // Readback rows come back bottom-up; a format that wants them top-down gets the
                // crop flipped — negate its scale and move the offset to the far edge of the band.
                Graphics.Blit(source, target, new Vector2(scale.x, -scale.y), new Vector2(offset.x, offset.y + scale.y));
            }
            else
            {
                Graphics.Blit(source, target, scale, offset);
            }

            pendingReadbacks.Add(new PendingReadback
            {
                Request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32),
                Timestamp = Time.unscaledTimeAsDouble,
                Owner = Session,
            });
            FramesCaptured++;
        }

        private void DrainReadbacks(bool blocking)
        {
            while (pendingReadbacks.Count > 0)
            {
                PendingReadback pending = pendingReadbacks[0];
                if (blocking) pending.Request.WaitForCompletion();
                if (!pending.Request.done) break;

                pendingReadbacks.RemoveAt(0);
                if (pending.Request.hasError) continue;

                // To the clip that asked for it, which is not always the one recording now.
                pending.Owner?.TryAddFrame(pending.Request.GetData<byte>(), pending.Timestamp);
            }
        }

        private void Finish()
        {
            Report(Session);
            Session = null;
            nextSegment = null;
            ReleaseTarget();
            State = BasisCameraRecordingState.Idle;
        }

        /// <summary>
        /// One finished clip's outcome, logged and left where the panel reads it. A run of clips
        /// reports each in turn, so the panel shows the most recent one either way.
        /// </summary>
        private void Report(IBasisFrameRecorderSession session)
        {
            LastFailure = session.FailureMessage;
            LastFileName = LastFailure == null ? System.IO.Path.GetFileName(session.FinalPath) : null;

            if (LastFailure != null)
            {
                BasisDebug.LogError($"{label} recording failed: {LastFailure}", BasisDebug.LogTag.Camera);
            }
            else
            {
                BasisDebug.Log($"{label} saved: {session.FinalPath} ({session.FramesEncoded} frames).", BasisDebug.LogTag.Camera);
                try
                {
                    Saved?.Invoke(session.FinalPath);
                }
                catch (Exception exception)
                {
                    BasisDebug.LogError($"{label} saved handler failed: {exception.Message}", BasisDebug.LogTag.Camera);
                }
            }
        }

        private void ReleaseTarget()
        {
            if (target == null) return;
            target.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
            target = null;
        }
    }
}
