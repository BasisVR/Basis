
using Basis.Scripts.UI.UI_Panels;
using TMPro;
using UnityEngine;

public class BasisUINotification : BasisUIBase
{
    public static string Path = "Packages/com.basis.sdk/Prefabs/UI/BasisUINotification.prefab";
    public static string CursorRequest = "BasisUINotification";
    public TextMeshProUGUI Text;
    public BasisUIMovementDriver BasisUIMovementDriver;
    public override void DestroyEvent()
    {
        BasisUIMovementDriver.DeInitalize();
        if (BasisUIMovementDriver != null)
        {
            BasisCursorManagement.LockCursor(CursorRequest);
        }
    }
    public static void OpenNotification(string Reason, bool OverridePosition, Vector3 Position)
    {
        BasisUIBase Base = OpenMenuNow(Path);
        BasisUINotification Notification = (BasisUINotification)Base;
        Notification.Text.text = Reason;
        if (OverridePosition)
        {
            if (Notification.BasisUIMovementDriver != null)
            {
                Notification.BasisUIMovementDriver.enabled = false;
            }
            Notification.transform.position = Position;
        }
        else
        {
            if (Notification.BasisUIMovementDriver != null)
            {
                Notification.BasisUIMovementDriver.enabled = true;
            }
        }
    }
    public override void InitalizeEvent()
    {
        BasisCursorManagement.UnlockCursor(CursorRequest);
    }
}
