
namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisUISettings : BasisUIBase
    {
        public static string SettingsPanel = "SettingsPanel";
        public BasisUIMovementDriver BasisUIMovementDriver;
        public override void DestroyEvent()
        {
            BasisCursorManagement.LockCursor(nameof(BasisUISettings));
            BasisUINeedsVisibleTrackers.Instance.Remove(this);
            BasisUIMovementDriver.DeInitalize();
        }

        public override void InitalizeEvent()
        {
            BasisCursorManagement.UnlockCursor(nameof(BasisUISettings));
            BasisUINeedsVisibleTrackers.Instance.Add(this);
        }
        public void OpenConsole()
        {
            BasisUIManagement.CloseAllMenus();
            OpenMenuNow("LoggerUI");
        }
        public void OpenAdminPanel()
        {
            BasisUIManagement.CloseAllMenus();
            OpenMenuNow("BasisUIAdminPanel");
        }
        public void OpenControllerConfig()
        {
            BasisUIManagement.CloseAllMenus();
            OpenMenuNow("Packages/com.basis.sdk/Prefabs/UI/ControllerConfig.prefab");
        }
    }
}
