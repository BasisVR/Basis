using UnityEngine;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;


namespace Basis.BasisUI
{
    public class PlayersProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new PlayersProvider());
        }

        public override string Title => "Players";
        public override string IconAddress => AddressableAssets.Sprites.Avatars;
        public override int Order => 3;

        public void OpenMenu(BasisNetworkPlayer highlightPlayer = null)
        {
            if (BasisMainMenu.ActiveMenuTitle == Title) return;
            BasisLocalPlayer.Instance?.SetSafeDisplayname();
            BasisMenuPanel panel = BasisMainMenu.CreateActiveMenu(
                BasisMenuPanel.PanelData.Standard(Title),
                BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);

            PanelPlayerList avatarList = PanelPlayerList.CreateNew(panel.Descriptor.ContentParent);
            if (highlightPlayer != null)
            {
                avatarList.ShowPlayer(highlightPlayer);
            }
        }
        public override void RunAction()
        {
            OpenMenu();
        }
    }
}
