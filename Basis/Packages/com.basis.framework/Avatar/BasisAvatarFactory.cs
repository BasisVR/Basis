using Basis.Scripts.Addressable_Driver;
using Basis.Scripts.Addressable_Driver.Factory;
using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
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
        public static BasisLoadableBundle LoadingAvatar { get; } = new BasisLoadableBundle
        {
            BasisBundleConnector = new BasisBundleConnector
            {
                BasisBundleDescription = new BasisBundleDescription
                {
                    AssetBundleDescription = BasisLocalPlayer.DefaultAvatar,
                    AssetBundleName = BasisLocalPlayer.DefaultAvatar
                },
                BasisBundleGenerated = new[]
                {
                    new BasisBundleGenerated("N/A", "Gameobject", string.Empty, 0, true, string.Empty, string.Empty, 0)
                }
            },
            UnlockPassword = "N/A",
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle
            {
                CombinedURL = BasisLocalPlayer.DefaultAvatar,
                IsLocal = true
            },
            BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle
            {
                LocalConnectorPath = BasisLocalPlayer.DefaultAvatar
            }
        };

        public static bool IsLoadingAvatar(BasisLoadableBundle bundle) =>
            bundle.BasisLocalEncryptedBundle.LocalConnectorPath == LoadingAvatar.BasisLocalEncryptedBundle.LocalConnectorPath;

        public static bool IsFaultyAvatar(BasisLoadableBundle bundle) =>
            string.IsNullOrEmpty(bundle.BasisLocalEncryptedBundle.LocalConnectorPath);

        public static async Task LoadAvatarLocal(BasisLocalPlayer player, byte mode, BasisLoadableBundle bundle)
        {
            if (string.IsNullOrEmpty(bundle.BasisRemoteBundleEncrypted.CombinedURL))
            {
                BasisDebug.LogError("Avatar Address was empty or null! Falling back to loading avatar.");
                await LoadAvatarAfterError(player);
                return;
            }

            RemoveOldAvatarAndLoadFallback(player, LoadingAvatar.BasisLocalEncryptedBundle.LocalConnectorPath);

            try
            {
                GameObject output = null;
                switch (mode)
                {
                    case 0:
                        BasisDebug.Log("Requested Avatar was a AssetBundle Avatar " + bundle.BasisRemoteBundleEncrypted.CombinedURL, BasisDebug.LogTag.Avatar);
                        output = await DownloadAndLoadAvatar(bundle, player);
                        break;

                    case 1:
                        BasisDebug.Log("Requested Avatar was an Addressable Avatar " + bundle.BasisRemoteBundleEncrypted.CombinedURL, BasisDebug.LogTag.Avatar);
                        output = await LoadAddressableAvatar(bundle, player);
                        break;

                    default:
                        BasisDebug.Log("Using Default, this means index was out of acceptable range! " + bundle.BasisRemoteBundleEncrypted.CombinedURL, BasisDebug.LogTag.Avatar);
                        output = await DownloadAndLoadAvatar(bundle, player);
                        break;
                }


                player.AvatarMetaData = bundle;
                player.AvatarLoadMode = mode;
                InitializePlayerAvatar(player, output);
                BasisHeightDriver.SetPlayersEyeHeight(player, BasisLocalHeightInformation.BasisSelectedHeightMode.EyeHeight);
                player.AvatarSwitched();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Loading avatar failed: {e}");
                await LoadAvatarAfterError(player);
            }
        }

        public static async Task LoadAvatarRemote(BasisRemotePlayer player, byte mode, BasisLoadableBundle bundle)
        {
            if (string.IsNullOrEmpty(bundle.BasisRemoteBundleEncrypted.CombinedURL))
            {
                BasisDebug.LogError("Avatar Address was empty or null! Falling back to loading avatar.");
                await LoadAvatarAfterError(player);
                return;
            }

            RemoveOldAvatarAndLoadFallback(player, LoadingAvatar.BasisLocalEncryptedBundle.LocalConnectorPath);

            try
            {
                GameObject output = null;
                switch (mode)
                {
                    case 0:
                        BasisDebug.Log("Requested Avatar was a AssetBundle Avatar " + bundle.BasisRemoteBundleEncrypted.CombinedURL, BasisDebug.LogTag.Avatar);
                        output = await DownloadAndLoadAvatar(bundle, player);
                        break;

                    case 1:
                        BasisDebug.Log("Requested Avatar was an Addressable Avatar " + bundle.BasisRemoteBundleEncrypted.CombinedURL, BasisDebug.LogTag.Avatar);
                        output = await LoadAddressableAvatar(bundle, player);
                        break;

                    default:
                        BasisDebug.Log("Using Default, this means index was out of acceptable range! " + bundle.BasisRemoteBundleEncrypted.CombinedURL, BasisDebug.LogTag.Avatar);
                        output = await DownloadAndLoadAvatar(bundle, player);
                        break;
                }


                player.AvatarMetaData = bundle;
                player.AvatarLoadMode = mode;
                InitializePlayerAvatar(player, output);
                player.AvatarSwitched();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Loading avatar failed: {e}");
                await LoadAvatarAfterError(player);
            }
        }

        private static async Task<GameObject> LoadAddressableAvatar(BasisLoadableBundle bundle, BasisPlayer player)
        {
            var para = new UnityEngine.ResourceManagement.ResourceProviders.InstantiationParameters(player.transform.position, Quaternion.identity, null);
            var required = new ChecksRequired
            {
                UseContentRemoval = true,
                DisableAnimatorEvents = false,
                RemoveColliders = true
            };

            var (objects, _) = await AddressableResourceProcess.LoadAsGameObjectsAsync(bundle.BasisRemoteBundleEncrypted.CombinedURL, para, required, BundledContentHolder.Selector.Avatar);

            if (objects.Count == 0)
            {
                BasisDebug.LogError($"Can't find local avatar for {bundle.BasisRemoteBundleEncrypted.CombinedURL}");
                return null;
            }

            return objects[0];
        }

        public static async Task<GameObject> DownloadAndLoadAvatar(BasisLoadableBundle bundle, BasisPlayer player)
        {
            string id = BasisGenerateUniqueID.GenerateUniqueID();
            var obj = await BasisLoadHandler.LoadGameObjectBundle(bundle, true, player.ProgressReportAvatarLoad, new CancellationToken(), player.transform.position, Quaternion.identity, Vector3.one, false, BundledContentHolder.Selector.Avatar, player.transform);
            player.ProgressReportAvatarLoad.ReportProgress(id, 100, "Setting Position");
            obj.transform.SetPositionAndRotation(player.transform.position, Quaternion.identity);
            return obj;
        }

        private static void InitializePlayerAvatar(BasisPlayer player, GameObject obj)
        {
            if (!obj.TryGetComponent(out BasisAvatar avatar))
            {
                BasisDebug.LogError("Failed to find BasisAvatar component on instantiated object.");
                GameObject.Destroy(obj);
                return;
            }

            DeleteLastAvatar(player);
            player.IsConsideredFallBackAvatar = false;
            player.BasisAvatar = avatar;
            player.BasisAvatarTransform = avatar.transform;
            avatar.Renders = avatar.GetComponentsInChildren<Renderer>(true);
            avatar.IsOwnedLocally = player.IsLocal;

            switch (player)
            {
                case BasisLocalPlayer local:
                    SetupLocalAvatar(local);
                    avatar.OnAvatarReady?.Invoke(true);
                    break;
                case BasisRemotePlayer remote:
                    SetupRemoteAvatar(remote);
                    avatar.OnAvatarReady?.Invoke(false);
                    break;
            }
        }

        public static async Task LoadAvatarAfterError(BasisPlayer player)
        {
            try
            {
                var para = new UnityEngine.ResourceManagement.ResourceProviders.InstantiationParameters(player.transform.position, Quaternion.identity, null);
                var required = new ChecksRequired
                {
                    UseContentRemoval = false,
                    DisableAnimatorEvents = false,
                    RemoveColliders = true
                };

                var (objects, _) = await AddressableResourceProcess.LoadAsGameObjectsAsync(LoadingAvatar.BasisLocalEncryptedBundle.LocalConnectorPath, para, required, BundledContentHolder.Selector.Avatar);

                if (objects.Count > 0)
                    InitializePlayerAvatar(player, objects[0]);

                player.AvatarMetaData = LoadingAvatar;
                player.AvatarLoadMode = 1;
                player.AvatarSwitched();
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Fallback avatar loading failed: {e}");
            }
        }

        public static void RemoveOldAvatarAndLoadFallback(BasisPlayer player, string path)
        {
            var op = Addressables.LoadAssetAsync<GameObject>(path);
            var prefab = op.WaitForCompletion();
            var instance = GameObject.Instantiate(prefab, player.transform.position, Quaternion.identity, player.transform);

            if (!instance.TryGetComponent(out BasisAvatar avatar))
            {
                BasisDebug.LogError("Missing Basis Avatar Component On FallBack Avatar");
                return;
            }

            DeleteLastAvatar(player);
            player.IsConsideredFallBackAvatar = true;
            player.BasisAvatar = avatar;
            player.BasisAvatarTransform = avatar.transform;
            avatar.Renders = avatar.GetComponentsInChildren<Renderer>(true);
            avatar.IsOwnedLocally = player.IsLocal;

            switch (player)
            {
                case BasisLocalPlayer local:
                    SetupLocalAvatar(local);
                    break;
                case BasisRemotePlayer remote:
                    SetupRemoteAvatar(remote);
                    break;
            }
        }

        public static async void DeleteLastAvatar(BasisPlayer Player)
        {
            if (Player.BasisAvatar == null) return;

            GameObject.Destroy(Player.BasisAvatar.gameObject);

            if (!Player.IsConsideredFallBackAvatar)
                await BasisLoadHandler.RequestDeIncrementOfBundle(Player.AvatarMetaData);
        }

        public static void SetupRemoteAvatar(BasisRemotePlayer Player)
        {
            int RenderCount = Player.BasisAvatar.Renders.Length;
            Player.RemoteAvatarDriver.RemoteCalibration(Player);

            Player.InitalizeIKCalibration(Player.RemoteAvatarDriver);
            for (int Index = 0; Index < RenderCount; Index++)
            {
                Player.BasisAvatar.Renders[Index].gameObject.layer = 7;
            }
        }
        public static void SetupLocalAvatar(BasisLocalPlayer Player)
        {
            int RenderCount = Player.BasisAvatar.Renders.Length;
            Player.LocalAvatarDriver.InitialLocalCalibration(Player);
            Player.InitalizeIKCalibration(Player.LocalAvatarDriver);
            for (int Index = 0; Index < RenderCount; Index++)
            {
                Player.BasisAvatar.Renders[Index].gameObject.layer = 6;
            }
        }
    }
}
