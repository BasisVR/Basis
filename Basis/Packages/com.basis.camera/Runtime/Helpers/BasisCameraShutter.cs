using Basis.Scripts.Audio;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using UnityEngine;
public static class BasisCameraShutter
{
    public static bool CaptureBlocked(string action)
    {
        if (!BasisNetworkModeration.CameraCaptureBlockedLocally) return false;
        BasisDebug.LogWarning($"{action} blocked: camera capture is locked by an admin.", BasisDebug.LogTag.Camera);
        return true;
    }
    public static void PlayShutter(Camera camera)
    {
        if (BasisDeviceManagement.Instance.CameraShutterSound != null) BasisUISounds.PlayAt(BasisUISoundEvent.CameraShutter, BasisDeviceManagement.Instance.CameraShutterSound, camera.transform.position, SMModuleAudio.ActivePropVolume);
    }
    public static void PlayCountdownTick(Camera camera)
    {
        if (BasisDeviceManagement.Instance.CameraCountdownTickSound != null) BasisUISounds.PlayAt(BasisUISoundEvent.CameraCountdownTick, BasisDeviceManagement.Instance.CameraCountdownTickSound, camera.transform.position, SMModuleAudio.ActivePropVolume);
    }
    public static void Announce(Camera camera)
    {
        PlayShutter(camera);
        if (BasisNetworkConnection.LocalPlayerPeer != null) BasisNetworkPIPCameraDriver.SendShutterSound();
    }
}
