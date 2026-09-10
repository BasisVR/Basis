using Basis;
using Basis.ImagePickup;
using System;
using UnityEngine;
public partial class BasisHandHeldCamera
{
    [NonSerialized] public bool GifLoop = true, GifDither = true;
    private readonly BasisCameraFrameRecorder gifRecorder = new BasisCameraFrameRecorder("GIF");
    private int gifFrameRate = 15, gifWidth = 480;
    private float gifDurationSeconds = 5f;
    public int GifFrameRate => gifFrameRate;
    public float GifDurationSeconds => gifDurationSeconds;
    public int GifWidth => gifWidth;
    public BasisCameraRecordingState GifState => gifRecorder.State;
    public bool IsGifRecording => gifRecorder.IsRecording;
    public string LastGifFileName => gifRecorder.LastFileName;
    public string LastGifFailure => gifRecorder.LastFailure;
    public int GifFramesCaptured => gifRecorder.FramesCaptured;
    public int GifFramesEncoded => gifRecorder.FramesEncoded;
    public float GifSecondsRemaining => gifRecorder.SecondsRemaining;
    public void SetGifFrameRate(int framesPerSecond) => gifFrameRate = Mathf.Clamp(framesPerSecond, BasisCameraRecordingLimits.MinGifFrameRate, BasisCameraRecordingLimits.MaxGifFrameRate);
    public void SetGifDuration(float seconds) => gifDurationSeconds = Mathf.Clamp(seconds, BasisCameraRecordingLimits.MinGifDurationSeconds, BasisCameraRecordingLimits.MaxGifDurationSeconds);
    public void SetGifWidth(int width) => gifWidth = Mathf.Clamp(width, BasisCameraRecordingLimits.MinGifWidth, BasisCameraRecordingLimits.MaxGifWidth);
    public bool StartGifRecording()
    {
        if (gifRecorder.State != BasisCameraRecordingState.Idle) return false;
        if (!TryBeginClipRecording("GIF", gifWidth, BasisCameraRecordingLimits.MinGifWidth, BasisCameraRecordingLimits.MaxGifWidth, out int width, out int height, out string timestamp)) return false;

        BasisGifRecorderSession session = new BasisGifRecorderSession(width, height, GifLoop, GifDither, gifFrameRate, BasisCameraPhotoFolder.PathFor($"Gif_{timestamp}_{width}x{height}.gif"));
        if (!session.Start()) return false;

        gifRecorder.Saved = SpawnGifInWorldIfEnabled;
        gifRecorder.Start(session, width, height, gifFrameRate, Mathf.Clamp(gifDurationSeconds, BasisCameraRecordingLimits.MinGifDurationSeconds, BasisCameraRecordingLimits.MaxGifDurationSeconds), flip: true);
        UpdateRenderGate();
        BasisCameraShutter.Announce(captureCamera);

        BasisDebug.Log($"GIF recording started: {width}x{height} @ {gifFrameRate}fps for up to {gifDurationSeconds:0.#}s.", BasisDebug.LogTag.Camera);
        return true;
    }
    public void StopGifRecording()
    {
        gifRecorder.Stop();
        UpdateRenderGate();
    }
    private bool TryBeginClipRecording(string label, int requestedWidth, int minWidth, int maxWidth, out int width, out int height, out string timestamp)
    {
        width = 0;
        height = 0;
        timestamp = null;

        if (captureCamera == null || renderTexture == null || BasisCameraShutter.CaptureBlocked($"{label} recording")) return false;

        BasisCameraRecordingLimits.ClipSize(requestedWidth, minWidth, maxWidth, captureWidth, captureHeight, out width, out height);
        if (!BasisCameraPhotoFolder.TryEnsure(label)) return false;

        timestamp = BasisCameraPhotoFolder.Timestamp();
        return true;
    }
    private void SpawnGifInWorldIfEnabled(string path)
    {
        if (printPhotoEnabled) BasisImagePickupManager.SpawnFromFile(path);
    }
    private void TickGifRecorder() => gifRecorder.Tick(renderTexture, BasisNetworkModeration.CameraCaptureBlockedLocally);
    private void ShutdownGifRecorder() => gifRecorder.Shutdown();
}
