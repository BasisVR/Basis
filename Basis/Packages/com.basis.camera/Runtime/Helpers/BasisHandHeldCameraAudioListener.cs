using Basis.Scripts.Drivers;
using UnityEngine;
public static class BasisHandHeldCameraAudioListener
{
    public static BasisHandHeldCamera Owner { get; private set; }
    public static bool IsHeldBy(BasisHandHeldCamera camera) => !ReferenceEquals(camera, null) && Owner == camera;
    public static void Set(BasisHandHeldCamera camera, bool enabled)
    {
        if (ReferenceEquals(camera, null)) return;
        if (enabled)
        {
            Owner = camera;
            BasisLocalCameraDriver.AudioListenerPoseOverride = Pose;
        }
        else if (Owner == camera)
        {
            Owner = null;
            BasisLocalCameraDriver.AudioListenerPoseOverride = null;
        }
    }
    private static (Vector3 position, Quaternion rotation)? Pose()
    {
        BasisHandHeldCamera owner = Owner;
        if (owner == null || owner.captureCamera == null) return null;
        owner.captureCamera.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        return (position, rotation);
    }
}
