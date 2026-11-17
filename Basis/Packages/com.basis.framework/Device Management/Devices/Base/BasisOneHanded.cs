using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisOneHanded
{
    public const string Auto = "Auto";
    public const string Off = "Off";
    public const string Left = "Left";
    public const string Right = "Right";
    private static int resolvedFrame = -1;
    private static bool active, leftController, rightController;
    private static BasisBoneTrackedRole hand;
    public static bool IsHand(BasisBoneTrackedRole role) => role == BasisBoneTrackedRole.LeftHand || role == BasisBoneTrackedRole.RightHand;
    public static bool TryGetHand(out BasisBoneTrackedRole role)
    {
        Refresh();
        role = hand;
        return active;
    }
    public static bool HasController(BasisBoneTrackedRole role)
    {
        Refresh();
        return role == BasisBoneTrackedRole.LeftHand ? leftController : role == BasisBoneTrackedRole.RightHand && rightController;
    }
    public static int ControllerCount
    {
        get
        {
            Refresh();
            return (leftController ? 1 : 0) + (rightController ? 1 : 0);
        }
    }
    private static void Refresh()
    {
        int frame = Time.frameCount;
        if (resolvedFrame == frame)
        {
            return;
        }
        resolvedFrame = frame;
        BasisDeviceManagement management = BasisDeviceManagement.Instance;
        Scan(management != null ? management.AllInputDevices : null, out leftController, out rightController);
        active = Pick(BasisSettingsDefaults.OneHandedMode.RawValue, BasisDeviceManagement.IsCurrentModeVR(), leftController, rightController, out hand);
    }
    public static BasisBoneTrackedRole HandOr(BasisBoneTrackedRole fallback) => TryGetHand(out BasisBoneTrackedRole role) ? role : fallback;
    internal static void Invalidate() => resolvedFrame = -1;
    internal static bool Resolve(string mode, bool isVR, BasisObservableList<BasisInput> devices, out BasisBoneTrackedRole role)
    {
        Scan(devices, out bool left, out bool right);
        return Pick(mode, isVR, left, right, out role);
    }
    internal static bool Pick(string mode, bool isVR, bool left, bool right, out BasisBoneTrackedRole role)
    {
        role = BasisBoneTrackedRole.RightHand;
        if (!isVR || mode == Off)
        {
            return false;
        }
        if (mode == Left)
        {
            role = BasisBoneTrackedRole.LeftHand;
            return true;
        }
        if (mode == Right)
        {
            return true;
        }
        if (left == right)
        {
            return false;
        }
        role = left ? BasisBoneTrackedRole.LeftHand : BasisBoneTrackedRole.RightHand;
        return true;
    }
    internal static void Scan(BasisObservableList<BasisInput> devices, out bool left, out bool right)
    {
        left = false;
        right = false;
        int count = devices != null ? devices.Count : 0;
        for (int Index = 0; Index < count; Index++)
        {
            BasisInput device = devices[Index];
            if (device is BasisInputController && !device.IgnoresButtons && !device.IgnoresSticks && device.TryGetRole(out BasisBoneTrackedRole found))
            {
                left |= found == BasisBoneTrackedRole.LeftHand;
                right |= found == BasisBoneTrackedRole.RightHand;
            }
        }
    }
    public static bool PointsAtUI(BasisBoneTrackedRole role)
    {
        BasisDeviceManagement management = BasisDeviceManagement.Instance;
        if (management == null)
        {
            return false;
        }
        BasisObservableList<BasisInput> devices = management.AllInputDevices;
        int count = devices.Count;
        for (int Index = 0; Index < count; Index++)
        {
            BasisInput device = devices[Index];
            if (device != null && device.HasRaycaster && device.BasisUIRaycast != null && (device.BasisUIRaycast.HadRaycastUITarget || device.BasisUIRaycast.HadToolkitPanelTarget) && device.TryGetRole(out BasisBoneTrackedRole found) && found == role)
            {
                return true;
            }
        }
        return false;
    }
}
