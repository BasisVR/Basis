using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Factory;
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
        public static async Task LoadAvatarLocal(BasisLocalPlayer Player, byte Mode, BasisLoadableBundle BasisLoadableBundle)
        {
            if (string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                BasisDebug.LogError("Avatar Address was empty or null! Falling back to loading avatar.");
                await LoadAvatarAfterError(Player);
                return;
            }

            RemoveOldAvatarAndLoadFallback(Player, LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation);///delete
            try
            {
                GameObject Output = null;
                switch (Mode)
                {
                    case 0://download
                        BasisDebug.Log("Requested Avatar was a AssetBundle Avatar " + BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, BasisDebug.LogTag.Avatar);
                        Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player);
                        break;
                    case 1://Local Load
                        BasisDebug.Log("Requested Avatar was a Addressable Avatar " + BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, BasisDebug.LogTag.Avatar);
                        UnityEngine.ResourceManagement.ResourceProviders.InstantiationParameters Para = new UnityEngine.ResourceManagement.ResourceProviders.InstantiationParameters(Player.transform.position, Quaternion.identity, null);
                        ChecksRequired Required = new ChecksRequired
                        {
                            UseContentRemoval = true,
                            DisableAnimatorEvents = false,
                            RemoveColliders = true,
                        };
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
                        Output.transform.SetPositionAndRotation(Player.transform.position, Quaternion.identity);
                        break;
                    default:
                        BasisDebug.Log("Using Default, this means index was out of acceptable range! " + BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, BasisDebug.LogTag.Avatar);
                        Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player);
                        break;
                }
                Player.AvatarMetaData = BasisLoadableBundle;
                Player.AvatarLoadMode = Mode;

                InitializePlayerAvatar(Player, Output);//delete loading avatar
                BasisHeightDriver.SetPlayersEyeHeight(Player, BasisSelectedHeightMode.EyeHeight);
                Player.AvatarSwitched();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Loading avatar failed: {e}");
                await LoadAvatarAfterError(Player);
            }
        }
        public static async Task LoadAvatarRemote(BasisRemotePlayer Player, byte Mode, BasisLoadableBundle BasisLoadableBundle)
        {
            if (string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                BasisDebug.LogError("Avatar Address was empty or null! Falling back to loading avatar.");
                await LoadAvatarAfterError(Player);
                return;
            }
            RemoveOldAvatarAndLoadFallback(Player, LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation);
            try
            {
                GameObject Output = null;
                switch (Mode)
                {
                    case 0://download
                        Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player);
                        break;
                    case 1://Local Load
                      //  BasisDebug.Log("Requested Avatar was a Addressable Avatar " + BasisLoadableBundle.BasisRemoteBundleEncrypted.CombinedURL, BasisDebug.LogTag.Avatar);
                        ChecksRequired Required = new ChecksRequired
                        {
                            UseContentRemoval = false,
                            DisableAnimatorEvents = false,
                            RemoveColliders = true,
                        };
                        UnityEngine.ResourceManagement.ResourceProviders.InstantiationParameters Para = new UnityEngine.ResourceManagement.ResourceProviders.InstantiationParameters(Player.transform.position, Quaternion.identity, null);
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
                        Output.transform.SetPositionAndRotation(Player.transform.position, Quaternion.identity);
                        break;
                    default:
                        Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player);
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
                await LoadAvatarAfterError(Player);
            }
        }
        public static async Task<GameObject> DownloadAndLoadAvatar(BasisLoadableBundle BasisLoadableBundle, BasisPlayer BasisPlayer)
        {
            string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
            GameObject Output = await BasisLoadHandler.LoadGameObjectBundle(BasisLoadableBundle, true, BasisPlayer.ProgressReportAvatarLoad, new CancellationToken(), BasisPlayer.transform.position, Quaternion.identity, Vector3.one, false, BundledContentHolder.Selector.Avatar, BasisPlayer.transform, true);
            BasisPlayer.ProgressReportAvatarLoad.ReportProgress(UniqueID, 100, "Setting Position");
            Output.transform.SetPositionAndRotation(BasisPlayer.transform.position, Quaternion.identity);
            return Output;
        }
        private static void InitializePlayerAvatar(BasisPlayer Player, GameObject Output)
        {
            if (Output.TryGetComponent(out BasisAvatar Avatar))
            {
                DeleteLastAvatar(Player);
                Player.IsConsideredFallBackAvatar = false;
                Player.BasisAvatar = Avatar;
                Player.BasisAvatarTransform = Avatar.transform;
                Player.BasisAvatar.Renders = Player.BasisAvatar.GetComponentsInChildren<Renderer>(true);
                Player.BasisAvatar.IsOwnedLocally = Player.IsLocal;
                switch (Player)
                {
                    case BasisLocalPlayer localPlayer:
                        {
                            SetupLocalAvatar(localPlayer);
                            Avatar.OnAvatarReady?.Invoke(true);
                            break;
                        }
                    case BasisRemotePlayer remotePlayer:
                        {
                            SetupRemoteAvatar(remotePlayer);
                            Avatar.OnAvatarReady?.Invoke(false);
                            break;
                        }
                }
                RebuildNetworkIds(Player, Output, Avatar);
            }
        }
        public static void RebuildNetworkIds(BasisPlayer Player, GameObject Output, BasisAvatar Avatar)
        {
            Avatar.Behaviours = Output.GetComponentsInChildren<BasisAvatarMonoBehaviour>(true);
            int length = Avatar.Behaviours.Length;
            if (length > 256)
            {
                BasisDebug.LogError("To Many Mono Behaviours on this Avatar!");
                return;
            }
            for (byte Index = 0; Index < length; Index++)
            {
                Avatar.Behaviours[Index].OnNetworkAssign(Index, Avatar, Player.IsLocal);
            }
        }
        public static async Task LoadAvatarAfterError(BasisPlayer Player)
        {
            try
            {
                ChecksRequired Required = new ChecksRequired
                {
                    UseContentRemoval = false,
                    DisableAnimatorEvents = false,
                    RemoveColliders = true,
                };
                UnityEngine.ResourceManagement.ResourceProviders.InstantiationParameters Para = new UnityEngine.ResourceManagement.ResourceProviders.InstantiationParameters(Player.transform.position, Quaternion.identity, null);
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
            catch (Exception e)
            {
                BasisDebug.LogError($"Fallback avatar loading failed: {e}");
            }
        }
        /// <summary>
        /// no content searching is done here since its local content.
        /// </summary>
        /// <param name="Player"></param>
        /// <param name="LoadingAvatarToUse"></param>
        public static void RemoveOldAvatarAndLoadFallback(BasisPlayer Player, string LoadingAvatarToUse)
        {
            var op = Addressables.LoadAssetAsync<GameObject>(LoadingAvatarToUse);
            var LoadingAvatar = op.WaitForCompletion();
            var InSceneLoadingAvatar = GameObject.Instantiate(LoadingAvatar, Player.transform.position, Quaternion.identity, Player.transform);
            if (InSceneLoadingAvatar.TryGetComponent(out BasisAvatar Avatar))
            {
                DeleteLastAvatar(Player);
                Player.IsConsideredFallBackAvatar = true;
                Player.BasisAvatar = Avatar;
                Player.BasisAvatarTransform = Avatar.transform;
                Player.BasisAvatar.Renders = Player.BasisAvatar.GetComponentsInChildren<Renderer>(true);
                Player.BasisAvatar.IsOwnedLocally = Player.IsLocal;
                switch (Player)
                {
                    case BasisLocalPlayer localPlayer:
                        {
                            SetupLocalAvatar(localPlayer);
                            break;
                        }
                    case BasisRemotePlayer remotePlayer:
                        {
                            SetupRemoteAvatar(remotePlayer);
                            break;
                        }
                }
                RebuildNetworkIds(Player, InSceneLoadingAvatar, Avatar);
            }
            else
            {
                BasisDebug.LogError("Missing Basis Avatar Component On FallBack Avatar");
            }
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
            Player.InitalizeIKCalibration(Player.RemoteAvatarDriver);
            SetupAvatar(Player, BasisLayerMapper.RemoteAvatarLayer);
        }
        public static void SetupLocalAvatar(BasisLocalPlayer Player)
        {
            Player.LocalAvatarDriver.InitialLocalCalibration(Player);
            Player.InitalizeIKCalibration(Player.LocalAvatarDriver);
            SetupAvatar(Player,BasisLayerMapper.LocalAvatarLayer);
        }
        public static void SetupAvatar(BasisPlayer Player,int Layer)
        {
            int RenderCount = Player.BasisAvatar.Renders.Length;
            for (int Index = 0; Index < RenderCount; Index++)
            {
                Player.BasisAvatar.Renders[Index].gameObject.layer = Layer;
            }
        }
    }
}
