using Basis;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
public partial class BasisHandHeldCamera
{
    private readonly BasisPhotogrammetrySession photogrammetrySession = new BasisPhotogrammetrySession();
    private readonly List<BasisPhotogrammetryPathPoint> photogrammetryPath = new List<BasisPhotogrammetryPathPoint>();
    private Coroutine photogrammetryPathReplayCoroutine;
    private CameraPinSpace pinSpaceBeforePhotogrammetryPathReplay;
    private Vector3 lastPhotogrammetryPosition, lastPathPointPosition;
    private Quaternion lastPhotogrammetryRotation, lastPathPointRotation;
    private float photogrammetryDistanceMeters = 0.3f, photogrammetryAngleDegrees = 15f, photogrammetryPathSettleSeconds = 0.5f;
    private int photogrammetryWidth = 1280;
    private bool isRecordingPhotogrammetryPath, isReplayingPhotogrammetryPath;
    public float PhotogrammetryDistanceMeters => photogrammetryDistanceMeters;
    public float PhotogrammetryAngleDegrees => photogrammetryAngleDegrees;
    public float PhotogrammetryPathSettleSeconds => photogrammetryPathSettleSeconds;
    public int PhotogrammetryWidth => photogrammetryWidth;
    public int PhotogrammetryFramesCaptured => photogrammetrySession.FramesCaptured;
    public int PhotogrammetryFramesEncoded => photogrammetrySession.FramesEncoded;
    public int PhotogrammetryPathCount => photogrammetryPath.Count;
    public string LastPhotogrammetryFileName => photogrammetrySession.LastFileName;
    public string LastPhotogrammetryFailure => photogrammetrySession.LastFailure;
    public BasisCameraRecordingState PhotogrammetryState => photogrammetrySession.State;
    public bool IsPhotogrammetryActive => photogrammetrySession.State != BasisCameraRecordingState.Idle;
    public bool IsRecordingPhotogrammetryPath => isRecordingPhotogrammetryPath;
    public bool IsReplayingPhotogrammetryPath => isReplayingPhotogrammetryPath;
    public void SetPhotogrammetryDistance(float meters) => photogrammetryDistanceMeters = Mathf.Clamp(meters, BasisCameraRecordingLimits.MinPhotogrammetryDistanceMeters, BasisCameraRecordingLimits.MaxPhotogrammetryDistanceMeters);
    public void SetPhotogrammetryAngle(float degrees) => photogrammetryAngleDegrees = Mathf.Clamp(degrees, BasisCameraRecordingLimits.MinPhotogrammetryAngleDegrees, BasisCameraRecordingLimits.MaxPhotogrammetryAngleDegrees);
    public void SetPhotogrammetryWidth(int width) => photogrammetryWidth = Mathf.Clamp(width, BasisCameraRecordingLimits.MinPhotogrammetryWidth, BasisCameraRecordingLimits.MaxPhotogrammetryWidth);
    public void SetPhotogrammetryPathSettleSeconds(float seconds) => photogrammetryPathSettleSeconds = Mathf.Clamp(seconds, BasisCameraRecordingLimits.MinPhotogrammetryPathSettleSeconds, BasisCameraRecordingLimits.MaxPhotogrammetryPathSettleSeconds);
    public bool StartPhotogrammetrySession()
    {
        if (photogrammetrySession.State != BasisCameraRecordingState.Idle || isRecordingPhotogrammetryPath || isReplayingPhotogrammetryPath) return false;
        if (!TryStartPhotogrammetryCapture(out int width, out int height)) return false;

        captureCamera.transform.GetPositionAndRotation(out lastPhotogrammetryPosition, out lastPhotogrammetryRotation);
        SetAutoBrightnessMeteringHeld(true);
        UpdateRenderGate();
        BasisCameraShutter.Announce(captureCamera);

        BasisDebug.Log($"Photogrammetry session started: {width}x{height}, {photogrammetryDistanceMeters:0.##}m / {photogrammetryAngleDegrees:0.#} deg trigger.", BasisDebug.LogTag.Camera);
        return true;
    }
    public void StopPhotogrammetrySession()
    {
        if (isReplayingPhotogrammetryPath) return;

        photogrammetrySession.Stop();
        SetAutoBrightnessMeteringHeld(false);
        UpdateRenderGate();
    }
    public bool CapturePhotogrammetryFrameNow()
    {
        if (isReplayingPhotogrammetryPath || photogrammetrySession.State != BasisCameraRecordingState.Recording) return false;

        captureCamera.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        if (!photogrammetrySession.TryCapture(renderTexture, position, rotation, captureCamera.fieldOfView)) return false;

        lastPhotogrammetryPosition = position;
        lastPhotogrammetryRotation = rotation;
        return true;
    }
    public bool StartRecordingPhotogrammetryPath()
    {
        if (isRecordingPhotogrammetryPath || isReplayingPhotogrammetryPath || photogrammetrySession.State != BasisCameraRecordingState.Idle || captureCamera == null) return false;

        isRecordingPhotogrammetryPath = true;
        captureCamera.transform.GetPositionAndRotation(out lastPathPointPosition, out lastPathPointRotation);
        return true;
    }
    public void StopRecordingPhotogrammetryPath() => isRecordingPhotogrammetryPath = false;
    public void ClearPhotogrammetryPath() => photogrammetryPath.Clear();
    public bool StartPhotogrammetryPathReplay()
    {
        if (photogrammetryPath.Count == 0 || isRecordingPhotogrammetryPath || isReplayingPhotogrammetryPath || photogrammetrySession.State != BasisCameraRecordingState.Idle) return false;
        if (!TryStartPhotogrammetryCapture(out _, out _)) return false;

        isReplayingPhotogrammetryPath = true;
        SetAutoBrightnessMeteringHeld(true);
        UpdateRenderGate();
        BasisCameraShutter.Announce(captureCamera);
        photogrammetryPathReplayCoroutine = StartCoroutine(PhotogrammetryPathReplayRoutine(new List<BasisPhotogrammetryPathPoint>(photogrammetryPath)));

        BasisDebug.Log($"Photogrammetry path replay started: {photogrammetryPath.Count} points, {photogrammetryPathSettleSeconds:0.##}s settle each.", BasisDebug.LogTag.Camera);
        return true;
    }
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
    private bool TryStartPhotogrammetryCapture(out int width, out int height)
    {
        if (!TryBeginClipRecording("Photogrammetry", photogrammetryWidth, BasisCameraRecordingLimits.MinPhotogrammetryWidth, BasisCameraRecordingLimits.MaxPhotogrammetryWidth, out width, out height, out string timestamp)) return false;
        return photogrammetrySession.Start(width, height, Path.Combine(BasisCameraPhotoFolder.Location, "Photogrammetry", timestamp));
    }
    private void TickPhotogrammetry()
    {
        if (photogrammetrySession.State == BasisCameraRecordingState.Recording && !isReplayingPhotogrammetryPath)
        {
            if (BasisNetworkModeration.CameraCaptureBlockedLocally)
            {
                StopPhotogrammetrySession();
            }
            else
            {
                captureCamera.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                bool due = BasisPhotogrammetryPose.ShouldCapture(lastPhotogrammetryPosition, lastPhotogrammetryRotation, position, rotation, photogrammetryDistanceMeters, photogrammetryAngleDegrees);

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
            if (BasisPhotogrammetryPose.ShouldCapture(lastPathPointPosition, lastPathPointRotation, pathPosition, pathRotation, photogrammetryDistanceMeters, photogrammetryAngleDegrees))
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
