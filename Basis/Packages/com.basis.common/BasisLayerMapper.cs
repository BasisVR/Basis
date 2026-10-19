using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public static partial class BasisLayerMapper
{
    public static int RemoteAvatarLayer = 7;
    public static int LocalAvatarLayer = 6;
    public static int HandHeldCameraUILayer = 11;

    public const string HandHeldCameraUI = "HandHeldCameraUI";

    public static int HandHeldCameraUIMask => 1 << HandHeldCameraUILayer;
}
