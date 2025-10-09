using UnityEngine;

namespace Basis.VowganUI
{
    public class AvatarsProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new AvatarsProvider());
        }

        public override string Title => "Avatars";
        public override Sprite Icon => null;
        public override int Order => 2;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuName == Title) return;

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.Styles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
        }
    }
}
