using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Device_Management.Devices.Desktop;
using UnityEngine;
[AutoStaticsCleanup]
public static partial class BasisHandHeldCameraReticle
{
    private static int holders;
    public static void Acquire()
    {
        holders++;
        Apply();
    }
    public static void Release()
    {
        holders = Mathf.Max(0, holders - 1);
        Apply();
    }
    private static void Apply()
    {
        if (BasisDesktopEye.Instance != null) BasisDesktopEye.Instance.Reticle?.SetSuppressed(holders > 0);
    }
}
