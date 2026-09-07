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

    /// <summary>Widths the panel offers, labelled 480p/720p. The height always follows the photo aspect.</summary>
    public static readonly int[] PhotogrammetryWidthPresets = { 854, 1280 };

    private float photogrammetryDistanceMeters = 0.3f;
    private float photogrammetryAngleDegrees = 15f;
    private int photogrammetryWidth = 1280;

    private readonly BasisPhotogrammetrySession photogrammetrySession = new BasisPhotogrammetrySession();
    private Vector3 lastPhotogrammetryPosition;
    private Quaternion lastPhotogrammetryRotation;

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
        if (!TryBeginClipRecording("Photogrammetry", photogrammetryWidth, MinPhotogrammetryWidth, MaxPhotogrammetryWidth,
            out int width, out int height, out string timestamp)) return false;

        string sessionFolder = Path.Combine(PhotosDirectory, "Photogrammetry", timestamp);
        if (!photogrammetrySession.Start(width, height, sessionFolder)) return false;

        captureCamera.transform.GetPositionAndRotation(out lastPhotogrammetryPosition, out lastPhotogrammetryRotation);
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
        photogrammetrySession.Stop();
        UpdateRenderGate();
    }

    /// <summary>
    /// Forces one capture immediately, ignoring the movement thresholds, and resets the baseline
    /// they measure from — a detail worth an extra shot without having to move away and back to
    /// re-trip the trigger.
    /// </summary>
    public bool CapturePhotogrammetryFrameNow()
    {
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
        if (photogrammetrySession.State == BasisCameraRecordingState.Recording)
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

        photogrammetrySession.Tick();
    }

    private void ShutdownPhotogrammetry() => photogrammetrySession.Shutdown();
}
