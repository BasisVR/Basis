using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.VowganUI
{
    public class RespawnProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new RespawnProvider());
        }

        public override string Title => "Respawn";
        public override Sprite Icon => null;
        public override int Order => 3;

        public override void RunAction()
        {
            if (BasisLocalPlayer.Instance)
            {
                BasisSceneFactory.SpawnPlayer(BasisLocalPlayer.Instance);
                BasisMainMenu.Close();
            }
        }
    }
}
