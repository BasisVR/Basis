using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.BasisUI
{
    public class RespawnProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new RespawnProvider());
        }

        public override string Title => BasisLocalization.Get("menu.provider.respawn");
        public override string IconAddress => AddressableAssets.Sprites.Respawn;
        public override int Order => 60;

        public override bool Hidden => false;

        public override void RunAction()
        {
            if (BasisLocalPlayer.Instance)
            {
                BasisSceneFactory.OnUserRespawn?.Invoke();
                BasisSceneFactory.RespawnPlayer(BasisLocalPlayer.Instance);
                BasisMainMenu.Close();
            }
        }
    }
}
