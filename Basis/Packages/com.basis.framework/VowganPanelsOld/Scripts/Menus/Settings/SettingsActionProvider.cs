using UnityEngine;

namespace Basis.VowganUIOld
{
    public class SettingsActionProvider : MenuActionProvider
    {

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            HomeRowMenu.AddActionProvider(new SettingsActionProvider());
        }

        public override string Title => "Settings";
        public override Sprite Icon => null;
        public override int Order => 0;


        public override void RunAction() => ToggleActive();
        public override void OnActionDisabled() => ReleasePanelForAction();
        public override void OnActionEnabled() => CreateMenu();


        public void CreateMenu()
        {
            SettingsMenu.CreateMenu(HomeRowMenu.Instance.Group);
            SettingsMenu.Instance.Panel.OnInstanceReleased += DisableAction;
        }

    }
}
