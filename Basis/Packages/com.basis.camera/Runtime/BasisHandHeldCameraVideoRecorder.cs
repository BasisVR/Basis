using Basis;
using System;
using UnityEngine;
public partial class BasisHandHeldCamera
{
    [NonSerialized] public bool VideoRecordingTimeLimit = true, VideoContinuousClips;
    private readonly BasisCameraFrameRecorder videoRecorder = new BasisCameraFrameRecorder("Video");
    private BasisVideoAudioTap videoAudioTap;
    private string videoSegmentStamp;
    private float videoDurationSeconds = 30f;
    private int videoFrameRate = 30, videoWidth = 1920, videoQuality = 80;
    private int videoSegmentWidth, videoSegmentHeight, videoSegmentIndex;
    private bool videoSegmentNumbered, ratePinHeld;
    public int VideoRecordingFrameRate => videoFrameRate;
    public float VideoRecordingDurationSeconds => videoDurationSeconds;
    public int VideoRecordingWidth => videoWidth;
    public int VideoRecordingQuality => videoQuality;
    public BasisCameraRecordingState VideoRecordingState => videoRecorder.State;
    public bool IsVideoRecording => videoRecorder.IsRecording;
    public string LastVideoFileName => videoRecorder.LastFileName;
    public string LastVideoFailure => videoRecorder.LastFailure;
    public int VideoFramesCaptured => videoRecorder.FramesCaptured;
    public int VideoFramesEncoded => videoRecorder.FramesEncoded;
    public float VideoSecondsRemaining => videoRecorder.SecondsRemaining;
    public int VideoClipNumber => videoRecorder.SegmentNumber;
    public void SetVideoRecordingFrameRate(int framesPerSecond) => videoFrameRate = Mathf.Clamp(framesPerSecond, BasisCameraRecordingLimits.MinVideoFrameRate, BasisCameraRecordingLimits.MaxVideoFrameRate);
    public void SetVideoRecordingDuration(float seconds) => videoDurationSeconds = Mathf.Clamp(seconds, BasisCameraRecordingLimits.MinVideoDurationSeconds, BasisCameraRecordingLimits.MaxVideoDurationSeconds);
    public void SetVideoRecordingWidth(int width) => videoWidth = Mathf.Clamp(width, BasisCameraRecordingLimits.MinVideoWidth, BasisCameraRecordingLimits.MaxVideoWidth);
    public void SetVideoRecordingQuality(int quality) => videoQuality = Mathf.Clamp(quality, BasisCameraRecordingLimits.MinVideoQuality, BasisCameraRecordingLimits.MaxVideoQuality);
    public bool StartVideoRecording()
    {
        if (videoRecorder.State != BasisCameraRecordingState.Idle) return false;
        if (!TryBeginClipRecording("Video", videoWidth, BasisCameraRecordingLimits.MinVideoWidth, BasisCameraRecordingLimits.MaxVideoWidth, out int width, out int height, out string timestamp)) return false;

        bool continuous = VideoRecordingTimeLimit && VideoContinuousClips;
        videoSegmentWidth = width;
        videoSegmentHeight = height;
        videoSegmentStamp = timestamp;
        videoSegmentIndex = 0;
        videoSegmentNumbered = continuous;

        IBasisFrameRecorderSession session = BeginVideoSegment();
        if (session == null)
        {
            BasisVideoAudioTap.Detach(ref videoAudioTap);
            return false;
        }

        float duration = VideoRecordingTimeLimit ? Mathf.Clamp(videoDurationSeconds, BasisCameraRecordingLimits.MinVideoDurationSeconds, BasisCameraRecordingLimits.MaxVideoDurationSeconds) : float.PositiveInfinity;
        videoRecorder.Start(session, width, height, videoFrameRate, duration, flip: false, continuous ? BeginVideoSegment : (Func<IBasisFrameRecorderSession>)null);
        SyncRenderRatePin(true);
        UpdateRenderGate();
        BasisCameraShutter.Announce(captureCamera);

        string length = !VideoRecordingTimeLimit ? "until stopped." : continuous ? $"in {videoDurationSeconds:0.#}s clips until stopped." : $"for up to {videoDurationSeconds:0.#}s.";
        BasisDebug.Log($"Video recording started: {width}x{height} @ {videoFrameRate}fps, {length}", BasisDebug.LogTag.Camera);
        return true;
    }
    public void StopVideoRecording()
    {
        videoRecorder.Stop();
        SyncRenderRatePin(false);
        UpdateRenderGate();
    }
    private IBasisFrameRecorderSession BeginVideoSegment()
    {
        videoSegmentIndex++;
        string fileName = videoSegmentNumbered ? $"Video_{videoSegmentStamp}_{videoSegmentWidth}x{videoSegmentHeight}_part{videoSegmentIndex:000}.avi" : $"Video_{videoSegmentStamp}_{videoSegmentWidth}x{videoSegmentHeight}.avi";

        BasisRecordingAudioBuffer audioBuffer = new BasisRecordingAudioBuffer(AudioSettings.outputSampleRate);
        if (videoAudioTap == null) videoAudioTap = BasisVideoAudioTap.Attach(audioBuffer);
        else videoAudioTap.Target = audioBuffer;

        BasisVideoRecorderSession session = new BasisVideoRecorderSession(videoSegmentWidth, videoSegmentHeight, videoQuality, videoFrameRate, BasisCameraPhotoFolder.PathFor(fileName), videoAudioTap != null ? audioBuffer : null, Time.unscaledTimeAsDouble);
        return session.Start() ? session : null;
    }
    private void SyncRenderRatePin(bool recording)
    {
        if (recording == ratePinHeld) return;
        ratePinHeld = recording;
        if (recording) BasisCameraRenderRate.Pin();
        else BasisCameraRenderRate.Unpin();
    }
    private void TickVideoRecorder()
    {
        videoRecorder.Tick(renderTexture, BasisNetworkModeration.CameraCaptureBlockedLocally);
        SyncRenderRatePin(videoRecorder.IsRecording);
        if (videoAudioTap != null && videoRecorder.State == BasisCameraRecordingState.Idle) BasisVideoAudioTap.Detach(ref videoAudioTap);
    }
    private void ShutdownVideoRecorder()
    {
        SyncRenderRatePin(false);
        BasisVideoAudioTap.Detach(ref videoAudioTap);
        videoRecorder.Shutdown();
    }
}
