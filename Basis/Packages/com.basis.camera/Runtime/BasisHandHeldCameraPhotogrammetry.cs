using System.Collections;
using System.Collections.Generic;
using System.IO;
using Basis;
using Basis.Scripts.Networking;
using UnityEngine;

/// <summary>
/// Photogrammetry capture for the handheld camera: walk or fly the camera around a world and it
/// automatically takes a low-resolution still every time it has moved or turned enough since the
/// last one, tagging each with its exact pose. The output — a <c>transforms.json</c> in the
/// nerfstudio/instant-ngp convention plus the images it references — carries ground-truth camera
/// poses, so an external Gaussian-splatting or NeRF pipeline can skip structure-from-motion
/// entirely. The capture side is <see cref="BasisPhotogrammetrySession"/>; the pose conversion and
/// trigger math are pure functions on <see cref="BasisPhotogrammetryPose"/>.
/// </summary>
public partial class BasisHandHeldCamera
{
    public const int MinPhotogrammetryWidth = 640;
    public const int MaxPhotogrammetryWidth = 1920;
    public const float MinPhotogrammetryDistanceMeters = 0.05f;
    public const float MaxPhotogrammetryDistanceMeters = 2f;
    public const float MinPhotogrammetryAngleDegrees = 2f;
    public const float MaxPhotogrammetryAngleDegrees = 60f;
    public const float MinPhotogrammetryPathSettleSeconds = 0.1f;
    public const float MaxPhotogrammetryPathSettleSeconds = 10f;

    /// <summary>Widths the panel offers, labelled 480p/720p. The height always follows the photo aspect.</summary>
    public static readonly int[] PhotogrammetryWidthPresets = { 854, 1280 };

    private float photogrammetryDistanceMeters = 0.3f;
    private float photogrammetryAngleDegrees = 15f;
    private int photogrammetryWidth = 1280;

    private readonly BasisPhotogrammetrySession photogrammetrySession = new BasisPhotogrammetrySession();
    private Vector3 lastPhotogrammetryPosition;
    private Quaternion lastPhotogrammetryRotation;

    private float photogrammetryPathSettleSeconds = 0.5f;
    private readonly List<BasisPhotogrammetryPathPoint> photogrammetryPath = new List<BasisPhotogrammetryPathPoint>();
    private bool isRecordingPhotogrammetryPath;
    private Vector3 lastPathPointPosition;
    private Quaternion lastPathPointRotation;
    private bool isReplayingPhotogrammetryPath;
    private Coroutine photogrammetryPathReplayCoroutine;
    private CameraPinSpace pinSpaceBeforePhotogrammetryPathReplay;

    public float PhotogrammetryDistanceMeters => photogrammetryDistanceMeters;
    public float PhotogrammetryAngleDegrees => photogrammetryAngleDegrees;
    public int PhotogrammetryWidth => photogrammetryWidth;

    public void SetPhotogrammetryDistance(float meters) =>
        photogrammetryDistanceMeters = Mathf.Clamp(meters, MinPhotogrammetryDistanceMeters, MaxPhotogrammetryDistanceMeters);

    public void SetPhotogrammetryAngle(float degrees) =>
        photogrammetryAngleDegrees = Mathf.Clamp(degrees, MinPhotogrammetryAngleDegrees, MaxPhotogrammetryAngleDegrees);

    public void SetPhotogrammetryWidth(int width) =>
        photogrammetryWidth = Mathf.Clamp(width, MinPhotogrammetryWidth, MaxPhotogrammetryWidth);

    public BasisCameraRecordingState PhotogrammetryState => photogrammetrySession.State;

    /// <summary>True while a session is running or its last frames are still draining — the phase that needs the feed live.</summary>
    public bool IsPhotogrammetryActive => photogrammetrySession.State != BasisCameraRecordingState.Idle;

    /// <summary>Frames handed to the GPU for readback this session.</summary>
    public int PhotogrammetryFramesCaptured => photogrammetrySession.FramesCaptured;

    /// <summary>Frames that have finished encoding to disk.</summary>
    public int PhotogrammetryFramesEncoded => photogrammetrySession.FramesEncoded;

    /// <summary>Filename of the last photo this camera's session saved, or null.</summary>
    public string LastPhotogrammetryFileName => photogrammetrySession.LastFileName;

    /// <summary>Why the last session failed, or null. Cleared when a new one starts.</summary>
    public string LastPhotogrammetryFailure => photogrammetrySession.LastFailure;

    /// <summary>
    /// Starts a session: a fresh folder under Photos/Photogrammetry, capturing a still — with its
    /// pose and intrinsics — every time the camera moves or turns past the configured thresholds.
    /// Refused under the same conditions any other recording is: no live feed, or an admin has
    /// locked capture.
    /// </summary>
    public bool StartPhotogrammetrySession()
    {
        if (photogrammetrySession.State != BasisCameraRecordingState.Idle) return false;
        if (isRecordingPhotogrammetryPath || isReplayingPhotogrammetryPath) return false;
        if (!TryBeginClipRecording("Photogrammetry", photogrammetryWidth, MinPhotogrammetryWidth, MaxPhotogrammetryWidth,
            out int width, out int height, out string timestamp)) return false;

        string sessionFolder = Path.Combine(PhotosDirectory, "Photogrammetry", timestamp);
        if (!photogrammetrySession.Start(width, height, sessionFolder)) return false;

        captureCamera.transform.GetPositionAndRotation(out lastPhotogrammetryPosition, out lastPhotogrammetryRotation);
        SetAutoBrightnessMeteringHeld(true);
        UpdateRenderGate();
        AnnounceClipRecording();

        BasisDebug.Log(
            $"Photogrammetry session started: {width}x{height}, {photogrammetryDistanceMeters:0.##}m / {photogrammetryAngleDegrees:0.#} deg trigger.",
            BasisDebug.LogTag.Camera);
        return true;
    }

    /// <summary>Ends capture and lets the frames already taken drain into their files and the manifest.</summary>
    public void StopPhotogrammetrySession()
    {
        // A path replay drives this same session itself and tears it down through
        // StopPhotogrammetryPathReplay, which also stops the coroutine — stopping the session out
        // from under it here would leave that coroutine's capture retry loop spinning forever.
        if (isReplayingPhotogrammetryPath) return;

        photogrammetrySession.Stop();
        SetAutoBrightnessMeteringHeld(false);
        UpdateRenderGate();
    }

    /// <summary>
    /// Forces one capture immediately, ignoring the movement thresholds, and resets the baseline
    /// they measure from — a detail worth an extra shot without having to move away and back to
    /// re-trip the trigger.
    /// </summary>
    public bool CapturePhotogrammetryFrameNow()
    {
        if (isReplayingPhotogrammetryPath) return false;
        if (photogrammetrySession.State != BasisCameraRecordingState.Recording) return false;

        captureCamera.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        if (!photogrammetrySession.TryCapture(renderTexture, position, rotation, captureCamera.fieldOfView)) return false;

        lastPhotogrammetryPosition = position;
        lastPhotogrammetryRotation = rotation;
        return true;
    }

    /// <summary>Per-frame recorder upkeep, run from <see cref="SimulateLate"/>.</summary>
    private void TickPhotogrammetry()
    {
        // A path replay drives this same session itself (see PhotogrammetryPathReplayRoutine) —
        // the live trigger below must stand aside, or the camera visibly jumping between recorded
        // points would spuriously trip it against the very session the replay is filling.
        if (photogrammetrySession.State == BasisCameraRecordingState.Recording && !isReplayingPhotogrammetryPath)
        {
            if (BasisNetworkModeration.CameraCaptureBlockedLocally)
            {
                StopPhotogrammetrySession();
            }
            else
            {
                captureCamera.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                bool due = BasisPhotogrammetryPose.ShouldCapture(lastPhotogrammetryPosition, lastPhotogrammetryRotation, position, rotation,
                    photogrammetryDistanceMeters, photogrammetryAngleDegrees);

                if (due && photogrammetrySession.TryCapture(renderTexture, position, rotation, captureCamera.fieldOfView))
                {
                    lastPhotogrammetryPosition = position;
                    lastPhotogrammetryRotation = rotation;
                }
            }
        }

        if (isRecordingPhotogrammetryPath)
        {
            captureCamera.transform.GetPositionAndRotation(out Vector3 pathPosition, out Quaternion pathRotation);
            if (BasisPhotogrammetryPose.ShouldCapture(lastPathPointPosition, lastPathPointRotation, pathPosition, pathRotation,
                photogrammetryDistanceMeters, photogrammetryAngleDegrees))
            {
                photogrammetryPath.Add(new BasisPhotogrammetryPathPoint(pathPosition, pathRotation));
                lastPathPointPosition = pathPosition;
                lastPathPointRotation = pathRotation;
            }
        }

        photogrammetrySession.Tick();
    }

    private void ShutdownPhotogrammetry()
    {
        if (photogrammetryPathReplayCoroutine != null)
        {
            StopCoroutine(photogrammetryPathReplayCoroutine);
            photogrammetryPathReplayCoroutine = null;
            isReplayingPhotogrammetryPath = false;
        }
        photogrammetrySession.Shutdown();
    }

    // ---- Path: record a route, then replay it for a deliberate per-point capture -----------

    public float PhotogrammetryPathSettleSeconds => photogrammetryPathSettleSeconds;

    public void SetPhotogrammetryPathSettleSeconds(float seconds) =>
        photogrammetryPathSettleSeconds = Mathf.Clamp(seconds, MinPhotogrammetryPathSettleSeconds, MaxPhotogrammetryPathSettleSeconds);

    /// <summary>Points recorded so far along the current path.</summary>
    public int PhotogrammetryPathCount => photogrammetryPath.Count;

    public bool IsRecordingPhotogrammetryPath => isRecordingPhotogrammetryPath;

    /// <summary>True while a route is being visited and shot; the camera is not held by the operator during this.</summary>
    public bool IsReplayingPhotogrammetryPath => isReplayingPhotogrammetryPath;

    /// <summary>
    /// Starts recording a route: every time the camera moves or turns past the same thresholds
    /// the live capture mode uses, its pose (not an image) is appended to the path. Refused while
    /// a session or a replay already owns the camera.
    /// </summary>
    public bool StartRecordingPhotogrammetryPath()
    {
        if (isRecordingPhotogrammetryPath || isReplayingPhotogrammetryPath) return false;
        if (photogrammetrySession.State != BasisCameraRecordingState.Idle) return false;
        if (captureCamera == null) return false;

        isRecordingPhotogrammetryPath = true;
        captureCamera.transform.GetPositionAndRotation(out lastPathPointPosition, out lastPathPointRotation);
        return true;
    }

    public void StopRecordingPhotogrammetryPath() => isRecordingPhotogrammetryPath = false;

    /// <summary>Discards the recorded route. Safe at any time — a replay already under way is working from its own copy.</summary>
    public void ClearPhotogrammetryPath() => photogrammetryPath.Clear();

    /// <summary>
    /// Visits every recorded point in turn: holds there for <see cref="PhotogrammetryPathSettleSeconds"/>
    /// (deliberately slow, so any temporal rendering has time to settle before the shot), takes
    /// one still, then moves on. Takes the camera out of the operator's hand for the duration and
    /// hands it back — or wherever else it was pinned to — when done or cancelled. Refused with an
    /// empty path, while already recording or replaying, or under the same conditions any other
    /// session start is.
    /// </summary>
    public bool StartPhotogrammetryPathReplay()
    {
        if (photogrammetryPath.Count == 0) return false;
        if (isRecordingPhotogrammetryPath || isReplayingPhotogrammetryPath) return false;
        if (photogrammetrySession.State != BasisCameraRecordingState.Idle) return false;
        if (!TryBeginClipRecording("Photogrammetry", photogrammetryWidth, MinPhotogrammetryWidth, MaxPhotogrammetryWidth,
            out int width, out int height, out string timestamp)) return false;

        string sessionFolder = Path.Combine(PhotosDirectory, "Photogrammetry", timestamp);
        if (!photogrammetrySession.Start(width, height, sessionFolder)) return false;

        isReplayingPhotogrammetryPath = true;
        SetAutoBrightnessMeteringHeld(true);
        UpdateRenderGate();
        AnnounceClipRecording();
        photogrammetryPathReplayCoroutine = StartCoroutine(
            PhotogrammetryPathReplayRoutine(new List<BasisPhotogrammetryPathPoint>(photogrammetryPath)));

        BasisDebug.Log(
            $"Photogrammetry path replay started: {photogrammetryPath.Count} points, {photogrammetryPathSettleSeconds:0.##}s settle each.",
            BasisDebug.LogTag.Camera);
        return true;
    }

    /// <summary>Cancels a replay in progress. Frames already captured still land — only the points not yet visited are skipped.</summary>
    public void StopPhotogrammetryPathReplay()
    {
        if (!isReplayingPhotogrammetryPath) return;

        if (photogrammetryPathReplayCoroutine != null)
        {
            StopCoroutine(photogrammetryPathReplayCoroutine);
            photogrammetryPathReplayCoroutine = null;
        }
        photogrammetrySession.Stop();
        FinishPhotogrammetryPathReplay();
    }

    private IEnumerator PhotogrammetryPathReplayRoutine(List<BasisPhotogrammetryPathPoint> route)
    {
        pinSpaceBeforePhotogrammetryPathReplay = PinSpace;
        if (PinSpace == CameraPinSpace.HandHeld) PinSpace = CameraPinSpace.WorldSpace;

        foreach (BasisPhotogrammetryPathPoint point in route)
        {
            if (BasisNetworkModeration.CameraCaptureBlockedLocally) break;

            float settled = 0f;
            while (settled < photogrammetryPathSettleSeconds && !BasisNetworkModeration.CameraCaptureBlockedLocally)
            {
                captureCamera.transform.SetPositionAndRotation(point.Position, point.Rotation);
                yield return null;
                settled += Time.unscaledDeltaTime;
            }
            if (BasisNetworkModeration.CameraCaptureBlockedLocally) break;

            captureCamera.transform.SetPositionAndRotation(point.Position, point.Rotation);
            while (!photogrammetrySession.TryCapture(renderTexture, point.Position, point.Rotation, captureCamera.fieldOfView))
            {
                yield return null;
            }
        }

        photogrammetrySession.Stop();
        photogrammetryPathReplayCoroutine = null;
        FinishPhotogrammetryPathReplay();
    }

    private void FinishPhotogrammetryPathReplay()
    {
        isReplayingPhotogrammetryPath = false;
        SetAutoBrightnessMeteringHeld(false);
        PinSpace = pinSpaceBeforePhotogrammetryPathReplay;
        UpdateRenderGate();
    }
}
