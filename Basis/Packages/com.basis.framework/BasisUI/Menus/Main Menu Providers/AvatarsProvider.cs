using UnityEngine;

namespace Basis.BasisUI
{
    public class AvatarsProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new AvatarsProvider());
        }

        public override string Title => "Avatars";
        public override string IconAddress => AddressableAssets.Sprites.Avatars;
        public override int Order => 2;

        public override bool Hidden => false;

        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) BasisMainMenu.Instance.ActiveMenu.ReleaseInstance();

            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            PanelAvatarList avatarList = PanelAvatarList.CreateNew(panel.Descriptor.ContentParent);
        }
    }
}
