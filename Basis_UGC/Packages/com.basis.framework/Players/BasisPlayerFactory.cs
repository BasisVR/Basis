using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.BasisSdk.Players;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using static SerializableBasis;
namespace Basis.Scripts.Player
{
    public static class BasisPlayerFactory
    {
        public static async Task<BasisLocalPlayer> CreateLocalPlayer(InstantiationParameters InstantiationParameters, string RemotePlayerId = "LocalPlayer")
        {
            BasisPlayer Player = await CreatePlayer(RemotePlayerId, InstantiationParameters);
            BasisLocalPlayer CreatedLocalPlayer = (BasisLocalPlayer)Player;
            await CreatedLocalPlayer.LocalInitialize();
            return CreatedLocalPlayer;
        }
        public static async Task<BasisRemotePlayer> CreateRemotePlayer(InstantiationParameters InstantiationParameters, ClientAvatarChangeMessage AvatarURL, PlayerMetaDataMessage PlayerMetaDataMessage, string LocalPlayerId = "RemotePlayer")
        {
            BasisPlayer Player = await CreatePlayer(LocalPlayerId, InstantiationParameters);
            BasisRemotePlayer CreatedRemotePlayer = (BasisRemotePlayer)Player;
            await CreatedRemotePlayer.RemoteInitialize(AvatarURL, PlayerMetaDataMessage);
            return CreatedRemotePlayer;
        }
        public static async Task<BasisPlayer> CreatePlayer(string PlayerAddressableID, InstantiationParameters InstantiationParameters)
        {
            ChecksRequired Required = new ChecksRequired();
            Required.UseContentRemoval = false;
            var data = await AddressableResourceProcess.LoadAsGameObjectsAsync(PlayerAddressableID, InstantiationParameters, Required, BundledContentHolder.Selector.System);
            List<GameObject> Gameobjects = data.Item1;
            if (Gameobjects.Count != 0)
            {
                foreach (GameObject gameObject in Gameobjects)
                {
                    if (gameObject.TryGetComponent(out BasisPlayer Player))
                    {
                        return Player;
                    }
                }
            }
            else
            {
                BasisDebug.LogError("Missing ");
            }
            BasisDebug.LogError("Error Missing Player!");
            return null;
        }
    }
}
