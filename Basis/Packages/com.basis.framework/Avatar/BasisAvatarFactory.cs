using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;
namespace Basis.Scripts.Avatar
{
    public static class BasisAvatarFactory
    {
        public static BasisLoadableBundle LoadingAvatar = new BasisLoadableBundle()
        {
            BasisBundleConnector = new BasisBundleConnector()
            {
                BasisBundleDescription = new BasisBundleDescription()
                {
                    AssetBundleDescription = BasisLocalPlayer.DefaultAvatar,
                    AssetBundleName = BasisLocalPlayer.DefaultAvatar
                },
                BasisBundleGenerated = new BasisBundleGenerated[]
                 {
                    new BasisBundleGenerated("N/A","Gameobject",string.Empty,0,true,string.Empty,string.Empty,0)
                 },
            },
            UnlockPassword = "N/A",
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle()
            {
                RemoteBeeFileLocation = BasisLocalPlayer.DefaultAvatar,
            },
            BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
            {
                DownloadedBeeFileLocation = BasisLocalPlayer.DefaultAvatar,
            },
        };
        public static bool IsLoadingAvatar(BasisLoadableBundle BasisLoadableBundle)
        {
            return BasisLoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation == BasisAvatarFactory.LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation;
        }
        public static bool IsFaultyAvatar(BasisLoadableBundle BasisLoadableBundle)
        {
            return string.IsNullOrEmpty(BasisLoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation);
        }
        public static async Task LoadAvatarLocal(BasisLocalPlayer Player, byte Mode, BasisLoadableBundle BasisLoadableBundle, Vector3 Position, Quaternion Rotation)
        {
            if (string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                BasisDebug.LogError("Avatar Address was empty or null! Falling back to loading avatar.");
                await LoadAvatarAfterError(Player, Position, Rotation);
                return;
            }

            RemoveOldAvatarAndLoadFallback(Player, LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation, Position, Rotation);///delete
            try
            {
                GameObject Output = null;
                switch (Mode)
                {
                    case 0://download
                        BasisDebug.Log("Requested Avatar was a AssetBundle Avatar " + BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, BasisDebug.LogTag.Avatar);
                        Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player, Position, Rotation);
                        break;
                    case 1://Local Load
                        BasisDebug.Log("Requested Avatar was a Addressable Avatar " + BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, BasisDebug.LogTag.Avatar);
                        InstantiationParameters Para = InstantiationParameters(Player, Position, Rotation);
                        ChecksRequired Required = new ChecksRequired(true, false, true);
                        (List<GameObject> GameObjects, AddressableGenericResource resource) = await AddressableResourceProcess.LoadAsGameObjectsAsync(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, Para, Required, BundledContentHolder.Selector.Avatar);
                        if (GameObjects.Count > 0)
                        {
                            BasisDebug.Log("Found Avatar for " + BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, BasisDebug.LogTag.Avatar);
                            Output = GameObjects[0];
                        }
                        else
                        {
                            BasisDebug.LogError("Cant Find Local Avatar for " + BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, BasisDebug.LogTag.Avatar);
                        }
                        break;
                    case 2:
                        Output = BasisLoadableBundle.LoadableGameobject.InSceneItem;
                        Output.transform.SetPositionAndRotation(Position, Rotation);
                        break;
                    default:
                        BasisDebug.Log("Using Default, this means index was out of acceptable range! " + BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, BasisDebug.LogTag.Avatar);
                        Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player, Position, Rotation);
                        break;
                }
                Player.AvatarMetaData = BasisLoadableBundle;
                Player.AvatarLoadMode = Mode;

                InitializePlayerAvatar(Player, Output);//delete loading avatar
                BasisHeightDriver.ChangeEyeHeightMode(Player, BasisSelectedHeightMode.EyeHeight);
                Player.AvatarSwitched();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Loading avatar failed: {e}");
                await LoadAvatarAfterError(Player, Position, Rotation);
            }
        }
        public static async Task LoadAvatarRemote(BasisRemotePlayer Player, byte Mode, BasisLoadableBundle BasisLoadableBundle, Vector3 Position, Quaternion Rotation)
        {
            if (string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                BasisDebug.LogError("Avatar Address was empty or null! Falling back to loading avatar.");
                await LoadAvatarAfterError(Player, Position, Rotation);
                return;
            }
            RemoveOldAvatarAndLoadFallback(Player, LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation, Position, Rotation);
            try
            {
                GameObject Output = null;
                switch (Mode)
                {
                    case 0://download
                        Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player, Position, Rotation);
                        break;
                    case 1://Local Load
                           //  BasisDebug.Log("Requested Avatar was a Addressable Avatar " + BasisLoadableBundle.BasisRemoteBundleEncrypted.CombinedURL, BasisDebug.LogTag.Avatar);
                        ChecksRequired Required = new ChecksRequired(false, false, true);
                        InstantiationParameters Para = InstantiationParameters(Player, Position, Rotation);
                        (List<GameObject> GameObjects, AddressableGenericResource resource) = await AddressableResourceProcess.LoadAsGameObjectsAsync(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, Para, Required, BundledContentHolder.Selector.Avatar);

                        if (GameObjects.Count > 0)
                        {
                            //  BasisDebug.Log("Found Avatar for " + BasisLoadableBundle.BasisRemoteBundleEncrypted.CombinedURL, BasisDebug.LogTag.Avatar);
                            Output = GameObjects[0];
                        }
                        else
                        {
                            BasisDebug.LogError("Cant Find Local Avatar for " + BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, BasisDebug.LogTag.Avatar);
                        }
                        break;
                    case 2:
                        Output = BasisLoadableBundle.LoadableGameobject.InSceneItem;
                        Output.transform.SetPositionAndRotation(Position, Rotation);
                        break;
                    default:
                        Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player, Position, Rotation);
                        break;
                }
                Player.AvatarMetaData = BasisLoadableBundle;
                Player.AvatarLoadMode = Mode;

                InitializePlayerAvatar(Player, Output);
                Player.AvatarSwitched();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Loading avatar failed: {e}");
                await LoadAvatarAfterError(Player, Position, Rotation);
            }
        }
        public static async Task<GameObject> DownloadAndLoadAvatar(BasisLoadableBundle BasisLoadableBundle, BasisPlayer BasisPlayer, Vector3 Position, Quaternion Rotation)
        {
            string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
            GameObject Output = await BasisLoadHandler.LoadGameObjectBundle(BasisLoadableBundle, true, BasisPlayer.ProgressReportAvatarLoad, new CancellationToken(), Position, Rotation, Vector3.one, false, BundledContentHolder.Selector.Avatar, BasisPlayer.transform, true);
            BasisPlayer.ProgressReportAvatarLoad.ReportProgress(UniqueID, 100, "Setting Position");
            // Output.transform.SetPositionAndRotation(Position,Rotation);//BasisPlayer.transform.position, Quaternion.identity);
            return Output;
        }
        /// <summary>
        /// No content searching is done here since it's local content.
        /// </summary>
        /// <param name="Player">The player to apply the fallback avatar to.</param>
        /// <param name="LoadingAvatarToUse">The address of the avatar to load.</param>
        public static void RemoveOldAvatarAndLoadFallback(BasisPlayer Player, string LoadingAvatarToUse, Vector3 Position, Quaternion Rotation)
        {
            var op = Addressables.LoadAssetAsync<GameObject>(LoadingAvatarToUse);
            var loadingAvatar = op.WaitForCompletion();
            var inSceneLoadingAvatar = GameObject.Instantiate(loadingAvatar, Position, Rotation, Player.transform);// Player.transform.position, Quaternion.identity,

            if (inSceneLoadingAvatar.TryGetComponent(out BasisAvatar avatar))
            {
                SetupPlayerAvatar(Player, avatar, inSceneLoadingAvatar, isFallback: true);
            }
            else
            {
                BasisDebug.LogError("Missing Basis Avatar Component On Fallback Avatar");
            }
        }

        /// <summary>
        /// Initializes the player's avatar with the given prefab.
        /// </summary>
        /// <param name="Player">The player to initialize.</param>
        /// <param name="Output">The GameObject representing the avatar prefab.</param>
        private static void InitializePlayerAvatar(BasisPlayer Player, GameObject Output)
        {
            if (Output.TryGetComponent(out BasisAvatar avatar))
            {
                SetupPlayerAvatar(Player, avatar, Output, isFallback: false);
            }
        }

        /// <summary>
        /// Shared logic for setting up a player's avatar.
        /// </summary>
        /// <param name="Player">The player.</param>
        /// <param name="avatar">The BasisAvatar component.</param>
        /// <param name="rootObject">The root GameObject of the avatar instance.</param>
        /// <param name="isFallback">Whether this is a fallback avatar.</param>
        private static void SetupPlayerAvatar(BasisPlayer Player, BasisAvatar avatar, GameObject rootObject, bool isFallback)
        {
            DeleteLastAvatar(Player);
            Player.IsConsideredFallBackAvatar = isFallback;
            Player.BasisAvatar = avatar;
            Player.BasisAvatarTransform = avatar.transform;
            Player.BasisAvatar.Renders = avatar.GetComponentsInChildren<Renderer>(true);
            Player.BasisAvatar.IsOwnedLocally = Player.IsLocal;

            switch (Player)
            {
                case BasisLocalPlayer localPlayer:
                    SetupLocalAvatar(localPlayer);
                    break;

                case BasisRemotePlayer remotePlayer:
                    SetupRemoteAvatar(remotePlayer);
                    break;
            }
        }
        public static async Task LoadAvatarAfterError(BasisPlayer Player, Vector3 Position, Quaternion Rotation)
        {
            try
            {
                ChecksRequired Required = new ChecksRequired(false, false, true);
                InstantiationParameters Para = InstantiationParameters(Player, Position, Rotation);
                (List<GameObject> GameObjects, AddressableGenericResource resource) = await AddressableResourceProcess.LoadAsGameObjectsAsync(LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation, Para, Required, BundledContentHolder.Selector.Avatar);

                if (GameObjects.Count != 0)
                {
                    InitializePlayerAvatar(Player, GameObjects[0]);
                }
                Player.AvatarMetaData = BasisAvatarFactory.LoadingAvatar;
                Player.AvatarLoadMode = 1;
                Player.AvatarSwitched();

                //we want to use Avatar Switched instead of the fallback version to let the server know this is what we actually want to use.
            }
            catch (Exception Exception)
            {
                BasisDebug.LogError($"Fallback avatar loading failed: {Exception}");
            }
        }
        public static InstantiationParameters InstantiationParameters(BasisPlayer Player, Vector3 Position, Quaternion Rotation)
        {
            return new InstantiationParameters(Position, Rotation, Player.transform);
        }
        /// <summary>
        /// this is not awaited,
        /// the reason for that is the DeIncrementation happens instantly but
        /// the clean up is delayed so we dont spam Unload Request.
        /// </summary>
        /// <param name="Player"></param>
        public static async void DeleteLastAvatar(BasisPlayer Player)
        {
            if (Player.BasisAvatar != null)
            {
                if (Player.IsConsideredFallBackAvatar)
                {
                    GameObject.Destroy(Player.BasisAvatar.gameObject);
                }
                else
                {
                    GameObject.Destroy(Player.BasisAvatar.gameObject);
                    if (Player.AvatarLoadMode == 1 || Player.AvatarLoadMode == 0)
                    {
                        //   BasisDebug.Log("Unloading Last Avatar for Player " + Player.DisplayName);
                        await BasisLoadHandler.RequestDeIncrementOfBundle(Player.AvatarMetaData);
                    }
                    else
                    {
                        BasisDebug.Log("Skipping remove DeInCrement was load mode " + Player.AvatarLoadMode);
                    }
                }
            }
            else
            {
                //if the avatar has been nuked lets assume its been responsibly decremented.
                //its worse to nuke content instead of keeping it around in memory from a bad Act.
                // BasisDebug.LogError("trying to remove Deleted Avatar");

            }
        }
        public static void SetupRemoteAvatar(BasisRemotePlayer Player)
        {
            Player.RemoteAvatarDriver.RemoteCalibration(Player);
            Player.AvatarInitalize();
            SetupAvatar(Player, BasisLayerMapper.RemoteAvatarLayer);
            Player.BasisAvatar.OnAvatarReady?.Invoke(false);
        }
        public static void SetupLocalAvatar(BasisLocalPlayer Player)
        {
            Player.LocalAvatarDriver.InitialLocalCalibration(Player);
            Player.AvatarInitalize();
            SetupAvatar(Player, BasisLayerMapper.LocalAvatarLayer);
            Player.BasisAvatar.OnAvatarReady?.Invoke(true);
        }
        public static void SetupAvatar(BasisPlayer Player, int Layer)
        {
            int RenderCount = Player.BasisAvatar.Renders.Length;
            for (int Index = 0; Index < RenderCount; Index++)
            {
                Player.BasisAvatar.Renders[Index].gameObject.layer = Layer;
            }
        }
    }
}
