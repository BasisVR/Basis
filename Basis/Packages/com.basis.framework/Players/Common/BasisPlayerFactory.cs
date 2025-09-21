using Basis.Scripts.BasisSdk.Players;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;
using static SerializableBasis;
namespace Basis.Scripts.Player
{
    public static class BasisPlayerFactory
    {
        public static GameObject LocalPlayerReadyToSpawn;
        public static GameObject RemotePlayerReadyToSpawn;
        public static string LocalPlayerId = "LocalPlayer";
        public static string RemotePlayerId = "RemotePlayer";
        public static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> LocalHandle;
        public static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> RemoteHandle;
        public static void Initalize()
        {
            LocalHandle = Addressables.LoadAssetAsync<GameObject>(LocalPlayerId);
            LocalPlayerReadyToSpawn = LocalHandle.WaitForCompletion();

            RemoteHandle = Addressables.LoadAssetAsync<GameObject>(RemotePlayerId);
            RemotePlayerReadyToSpawn = RemoteHandle.WaitForCompletion();
        }
        public static void DeInitalize()
        {
            Addressables.Release(LocalHandle);
            Addressables.Release(RemoteHandle);
        }
        public static async Task<BasisLocalPlayer> CreateLocalPlayer(InstantiationParameters InstantiationParameters)
        {
            GameObject gameObject = GameObject.Instantiate(LocalPlayerReadyToSpawn, InstantiationParameters.Position, InstantiationParameters.Rotation, InstantiationParameters.Parent);
            if (gameObject.TryGetComponent<BasisLocalPlayer>(out BasisLocalPlayer CreatedLocalPlayer))
            {

            }
            else
            {
                BasisDebug.LogError("Missing LocalPlayer");
            }
            CreatedLocalPlayer.PlayerSelf = CreatedLocalPlayer.transform;
            await CreatedLocalPlayer.LocalInitialize();
            return CreatedLocalPlayer;
        }
        public static BasisRemotePlayer CreateRemotePlayer(InstantiationParameters InstantiationParameters, ClientAvatarChangeMessage AvatarURL, ClientMetaDataMessage PlayerMetaDataMessage)
        {
            GameObject gameObject = GameObject.Instantiate(RemotePlayerReadyToSpawn, InstantiationParameters.Position, InstantiationParameters.Rotation, InstantiationParameters.Parent);
            if (gameObject.TryGetComponent<BasisRemotePlayer>(out BasisRemotePlayer CreatedRemotePlayer))
            {

            }
            else
            {
                BasisDebug.LogError("Missing RemotePlayer");
            }
            CreatedRemotePlayer.PlayerSelf = CreatedRemotePlayer.transform;
            CreatedRemotePlayer.RemoteInitialize(AvatarURL, PlayerMetaDataMessage);
            return CreatedRemotePlayer;
        }
    }
}
