using UnityEngine;

namespace Basis.VowganUI
{
    public class SettingsMenuProvider : BasisMenuActionProvider<BasisMainMenu>
    {

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SettingsMenuProvider());
        }

        public override string Title => "Settings";
        public override Sprite Icon => null;
        public override int Order => 0;

        public override void RunAction()
        {
            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.Styles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
        }
    }
}
