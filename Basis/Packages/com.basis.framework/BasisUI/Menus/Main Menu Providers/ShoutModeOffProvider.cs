using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using BasisPermissions;
using UnityEngine;

namespace Basis.BasisUI
{
    public class ShoutModeOffProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        static ShoutModeOffProvider _instance;
        static bool _added;

        [RuntimeInitializeOnLoadMethod]
        public static void Init()
        {
            _instance = new ShoutModeOffProvider();
            BasisNetworkModeration.OnShoutModeChanged += OnShoutModeChanged;
        }

        static void OnShoutModeChanged(ushort playerId, bool enabled)
        {
            if (BasisNetworkPlayer.LocalPlayer == null || playerId != BasisNetworkPlayer.LocalPlayer.playerId)
                return;

            if (!BasisNetworkManagement.LocalPermissions.Contains(PermNodes.ModerationShout))
                return;

            if (enabled && !_added)
            {
                _added = true;
                BasisMenuBase<BasisMainMenu>.AddProvider(_instance);
            }
            else if (!enabled && _added)
            {
                _added = false;
                BasisMenuBase<BasisMainMenu>.RemoveProvider(_instance);
            }
        }

        public override string Title => "Shout Mode Off";
        public override string IconAddress => AddressableAssets.Sprites.Admin;
        public override int Order => 1;
        public override bool Hidden => false;

        public override void RunAction()
        {
            if (BasisNetworkPlayer.LocalPlayer != null)
            {
                BasisNetworkModeration.DisableShoutMode(BasisNetworkPlayer.LocalPlayer.playerId);
            }
        }
    }
}
