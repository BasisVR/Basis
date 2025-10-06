using UnityEngine;

namespace Basis.VowganUI
{
    public class ServersProvider : BasisMenuActionProvider
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMainMenu.AddActionProvider(new ServersProvider());
        }

        public override string Title => "Servers";
        public override Sprite Icon => null;
        public override int Order => 1;

        public override void RunAction()
        {
            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.Styles.Page);
        }

    }
}
