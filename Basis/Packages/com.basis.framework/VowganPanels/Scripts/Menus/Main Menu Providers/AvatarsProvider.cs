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
        public override Sprite Icon => AddressableAssets.GetSprite(AddressableAssets.Sprites.Avatars);
        public override bool IconIsAddressable => true;
        public override int Order => 2;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) return;

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.Styles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
        }
    }
}
