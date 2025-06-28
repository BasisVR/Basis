using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Enums;
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
        BasisCursorManagement.LockCursor(CursorRequest);
    }
    public static void OpenNotification(string Reason, bool OverridePosition, Vector3 Position)
    {
        AddressableGenericResource resource = new AddressableGenericResource(Path, AddressableExpectedResult.SingleItem);
        BasisUIBase Base = OpenMenuNow(resource);
        BasisUINotification Notification = (BasisUINotification)Base;
        Notification.Text.text = Reason;
        if (OverridePosition)
        {
            Notification.BasisUIMovementDriver.enabled = false;
            Notification.transform.position = Position;
        }
        else
        {
            Notification.BasisUIMovementDriver.enabled = true;
        }
    }
    public override void InitalizeEvent()
    {
        BasisCursorManagement.UnlockCursor(CursorRequest);
    }
}
