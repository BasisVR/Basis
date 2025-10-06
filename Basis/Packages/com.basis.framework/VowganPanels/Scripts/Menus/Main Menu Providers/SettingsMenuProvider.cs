using UnityEngine;

namespace Basis.VowganUI
{
    public class SettingsMenuProvider : BasisMenuActionProvider
    {

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMainMenu.AddActionProvider(new SettingsMenuProvider());
        }

        public override string Title => "Settings";
        public override Sprite Icon => null;
        public override int Order => 0;

        public override void RunAction()
        {
            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.Styles.Page);
        }
    }
}
