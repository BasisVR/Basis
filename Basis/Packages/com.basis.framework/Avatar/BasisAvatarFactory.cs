using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Factory class for creating, loading, and managing player avatars.
    /// Provides methods for local and remote avatar loading, fallback handling,
    /// initialization, and cleanup.
    /// </summary>
    public static class BasisAvatarFactory
    {
        /// <summary>
        /// Default loading avatar used as a fallback when no valid avatar is available.
        /// </summary>
        public static BasisLoadableBundle LoadingAvatar = new BasisLoadableBundle()
        {
            BasisBundleConnector = new BasisBundleConnector()
            {
                BasisBundleDescription = new BasisBundleDescription()
                {
                    AssetBundleDescription = BasisBeeConstants.DefaultAvatar,
                    AssetBundleName = BasisBeeConstants.DefaultAvatar
                },
                BasisBundleGenerated = new BasisBundleGenerated[]
                 {
                    new BasisBundleGenerated("N/A","Gameobject",string.Empty,0,true,string.Empty,string.Empty,0)
                 },
            },
            UnlockPassword = "N/A",
            BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle()
            {
                RemoteBeeFileLocation = BasisBeeConstants.DefaultAvatar,
            },
            BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle()
            {
                DownloadedBeeFileLocation = BasisBeeConstants.DefaultAvatar,
            },
        };

        /// <summary>
        /// Checks if a given bundle matches the default "loading avatar."
        /// </summary>
        public static bool IsLoadingAvatar(BasisLoadableBundle BasisLoadableBundle)
        {
            return BasisLoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation ==
                   BasisAvatarFactory.LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation;
        }

        /// <summary>
        /// Checks if a given bundle is faulty (missing or empty address).
        /// </summary>
        public static bool IsFaultyAvatar(BasisLoadableBundle BasisLoadableBundle)
        {
            return string.IsNullOrEmpty(BasisLoadableBundle.BasisLocalEncryptedBundle.DownloadedBeeFileLocation);
        }
        public static long MaxDownloadSizeInMBRemote = 4L * 1024 * 1024 * 1024;
        /// <summary>
        /// Loads an avatar locally for a <see cref="BasisLocalPlayer"/>.
        /// Can handle download, addressable load, in-scene instantiation, or fallback.
        /// </summary>
        /// <param name="Player">The local player to assign the avatar to.</param>
        /// <param name="Mode">Load mode: 0=download, 1=addressable, 2=in-scene object.</param>
        /// <param name="BasisLoadableBundle">The bundle containing avatar metadata.</param>
        /// <param name="Position">Spawn position for the avatar.</param>
        /// <param name="Rotation">Spawn rotation for the avatar.</param>
        public static async Task LoadAvatarLocal(BasisLocalPlayer Player, byte Mode, BasisLoadableBundle BasisLoadableBundle, Vector3 Position, Quaternion Rotation)
        {
            var token = ReplacePlayerLoadToken(Player);

            if (string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                BasisDebug.LogError("Avatar Address was empty or null! Falling back to loading avatar.");
                await LoadAvatarAfterError(Player, Position, Rotation); // UNGATED
                ClearPlayerLoadToken(Player, token);
                return;
            }

            // Fallback can happen instantly, no restriction
            RemoveOldAvatarAndLoadFallback(Player, LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation, Position, Rotation);
            try
            {
                GameObject Output = null;

                switch (Mode)
                {
                    case 2: // in-scene is instant, no gate needed
                        Output = BasisLoadableBundle.LoadableGameobject.InSceneItem;
                        Output.transform.SetPositionAndRotation(Position, Rotation);
                        break;

                    case 0:
                    case 1:
                    default:
                        // Gate ONLY the actual load (download/addressables), NOT fallback.
                        await _loadGate.WaitAsync(token);
                        try
                        {
                            token.ThrowIfCancellationRequested();

                            if (Mode == 0)
                            {
                                BasisDebug.Log($"Requested Avatar was a AssetBundle Avatar {BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation}", BasisDebug.LogTag.Avatar);
                                Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player, Position, Rotation, token);
                            }
                            else
                            {
                                BasisDebug.Log($"Requested Avatar was an Addressable Avatar {BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation}", BasisDebug.LogTag.Avatar);
                                InstantiationParameters Para = InstantiationParameters(Player, Position, Rotation);
                                ChecksRequired Required = new ChecksRequired(true, false, true);

                                // If LoadAsGameObjectsAsync doesn't accept a token, we still check before/after.
                                Output = await AddressableResourceProcess.LoadAsGameObjectsAsync(
                                    BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, Para, Required, BundledContentHolder.Selector.Avatar);

                                token.ThrowIfCancellationRequested();
                            }
                        }
                        finally
                        {
                            _loadGate.Release();
                        }
                        break;
                }

                token.ThrowIfCancellationRequested();

                Player.AvatarMetaData = BasisLoadableBundle;
                Player.AvatarLoadMode = Mode;

                InitializePlayerAvatar(Player, Output);
                Player.AvatarSwitched();
            }
            catch (OperationCanceledException)
            {
                // Replaced by a newer request: do NOT load fallback; the newer request will handle visuals.
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Loading avatar failed: {e}");
                // Only fallback if this request is still the current one.
                if (!token.IsCancellationRequested)
                    await LoadAvatarAfterError(Player, Position, Rotation); // UNGATED
            }
            finally
            {
                ClearPlayerLoadToken(Player, token);
            }
        }


        /// <summary>
        /// Loads an avatar for a <see cref="BasisRemotePlayer"/> with similar logic to <see cref="LoadAvatarLocal"/>.
        /// </summary>
        public static async Task LoadAvatarRemote(BasisRemotePlayer Player, byte Mode, BasisLoadableBundle BasisLoadableBundle, Vector3 Position, Quaternion Rotation)
        {
            var token = ReplacePlayerLoadToken(Player);

            if (string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                BasisDebug.LogError("Avatar Address was empty or null! Falling back to loading avatar.");
                await LoadAvatarAfterError(Player, Position, Rotation); // UNGATED
                ClearPlayerLoadToken(Player, token);
                return;
            }

            // Instant fallback, not gated
            RemoveOldAvatarAndLoadFallback(Player, LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation, Position, Rotation);
            try
            {
                GameObject Output = null;

                switch (Mode)
                {
                    case 2:
                        Output = BasisLoadableBundle.LoadableGameobject.InSceneItem;
                        Output.transform.SetPositionAndRotation(Position, Rotation);
                        break;

                    case 0:
                    case 1:
                    default:
                        await _loadGate.WaitAsync(token);
                        try
                        {
                            token.ThrowIfCancellationRequested();

                            if (Mode == 0)
                            {
                                Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player, Position, Rotation, token, MaxDownloadSizeInMBRemote);
                            }
                            else
                            {
                                ChecksRequired Required = new ChecksRequired(false, false, true);
                                InstantiationParameters Para = InstantiationParameters(Player, Position, Rotation);

                                Output = await AddressableResourceProcess.LoadAsGameObjectsAsync(
                                    BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, Para, Required, BundledContentHolder.Selector.Avatar);

                                token.ThrowIfCancellationRequested();
                            }
                        }
                        finally
                        {
                            _loadGate.Release();
                        }
                        break;
                }

                token.ThrowIfCancellationRequested();

                Player.AvatarMetaData = BasisLoadableBundle;
                Player.AvatarLoadMode = Mode;

                InitializePlayerAvatar(Player, Output);
                Player.AvatarSwitched();
            }
            catch (OperationCanceledException)
            {
                // replaced; ignore
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Loading avatar failed: {e}");
                if (!token.IsCancellationRequested)
                    await LoadAvatarAfterError(Player, Position, Rotation); // UNGATED
            }
            finally
            {
                ClearPlayerLoadToken(Player, token);
            }
        }


        /// <summary>
        /// Downloads and instantiates an avatar from a bundle.
        /// </summary>
        /// <param name="BasisLoadableBundle">The bundle containing the avatar data.</param>
        /// <param name="BasisPlayer">The player to assign the avatar to.</param>
        /// <param name="Position">Spawn position for the avatar.</param>
        /// <param name="Rotation">Spawn rotation for the avatar.</param>
        public static async Task<GameObject> DownloadAndLoadAvatar(BasisLoadableBundle BasisLoadableBundle, BasisPlayer BasisPlayer, Vector3 Position, Quaternion Rotation, CancellationToken  Token, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
        {
            string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
            GameObject Output = await BasisLoadHandler.LoadGameObjectBundle(
                BasisLoadableBundle, true, BasisPlayer.ProgressReportAvatarLoad, Token,
                Position, Rotation, Vector3.one, false, BundledContentHolder.Selector.Avatar, BasisPlayer.transform, true,MaxDownloadSizeInMB);

            BasisPlayer.ProgressReportAvatarLoad.ReportProgress(UniqueID, 100, "Setting Position");
            return Output;
        }

        /// <summary>
        /// Loads a fallback avatar if the requested one fails or is invalid.
        /// </summary>
        /// <param name="Player">The player to assign the fallback avatar to.</param>
        /// <param name="LoadingAvatarToUse">The address of the fallback avatar.</param>
        public static void RemoveOldAvatarAndLoadFallback(BasisPlayer Player, string LoadingAvatarToUse, Vector3 Position, Quaternion Rotation)
        {
            var op = Addressables.LoadAssetAsync<GameObject>(LoadingAvatarToUse);
            var loadingAvatar = op.WaitForCompletion();
            var inSceneLoadingAvatar = GameObject.Instantiate(loadingAvatar, Position, Rotation, Player.transform);

            if (inSceneLoadingAvatar.TryGetComponent(out BasisAvatar avatar))
            {
                SetupPlayerAvatar(Player, avatar, inSceneLoadingAvatar, isFallback: true);
            }
            else
            {
                BasisDebug.LogError("Missing Basis Avatar Component on Fallback Avatar");
            }
        }

        /// <summary>
        /// Initializes a player's avatar with the given prefab instance.
        /// </summary>
        private static void InitializePlayerAvatar(BasisPlayer Player, GameObject Output)
        {
            if (Output.TryGetComponent(out BasisAvatar avatar))
            {
                SetupPlayerAvatar(Player, avatar, Output, isFallback: false);
            }
        }

        /// <summary>
        /// Configures a player with a specific avatar.
        /// Handles both local and remote player cases.
        /// </summary>
        private static void SetupPlayerAvatar(BasisPlayer Player, BasisAvatar avatar, GameObject rootObject, bool isFallback)
        {
            DeleteLastAvatar(Player);
            Player.IsConsideredFallBackAvatar = isFallback;
            Player.BasisAvatar = avatar;
            Player.AvatarTransform = avatar.transform;
            Player.AvatarAnimatorTransform = avatar.Animator.transform;
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

        /// <summary>
        /// Attempts to load the fallback avatar after a loading error.
        /// </summary>
        public static async Task LoadAvatarAfterError(BasisPlayer Player, Vector3 Position, Quaternion Rotation)
        {
            try
            {
                ChecksRequired Required = new ChecksRequired(false, false, true);
                InstantiationParameters Para = InstantiationParameters(Player, Position, Rotation);
                GameObject data = await AddressableResourceProcess.LoadAsGameObjectsAsync(
                    LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation, Para, Required, BundledContentHolder.Selector.Avatar);

                InitializePlayerAvatar(Player, data);
                Player.AvatarMetaData = BasisAvatarFactory.LoadingAvatar;
                Player.AvatarLoadMode = 1;
                Player.AvatarSwitched();
            }
            catch (Exception Exception)
            {
                BasisDebug.LogError($"Fallback avatar loading failed: {Exception}");
            }
        }

        /// <summary>
        /// Creates instantiation parameters for spawning an avatar.
        /// </summary>
        public static InstantiationParameters InstantiationParameters(BasisPlayer Player, Vector3 Position, Quaternion Rotation)
        {
            return new InstantiationParameters(Position, Rotation, Player.transform);
        }

        /// <summary>
        /// Deletes the player's previous avatar, releasing bundles or destroying objects as needed.
        /// </summary>
        /// <remarks>
        /// This method is async void: cleanup is triggered instantly, but actual unloading may be delayed.
        /// </remarks>
        public static async void DeleteLastAvatar(BasisPlayer Player)
        {
            if (Player.BasisAvatar != null)
            {
                if (Player.IsConsideredFallBackAvatar)
                {
                    AddressableResourceProcess.ReleaseGameobject(Player.BasisAvatar.gameObject);
                }
                else
                {
                    GameObject.Destroy(Player.BasisAvatar.gameObject);
                    if (Player.AvatarLoadMode == 1 || Player.AvatarLoadMode == 0)
                    {
                        await BasisLoadHandler.RequestDeIncrementOfBundle(Player.AvatarMetaData);
                    }
                    else
                    {
                        BasisDebug.Log("Skipping remove; DeIncrement not required for load mode " + Player.AvatarLoadMode);
                    }
                }
            }
        }

        /// <summary>
        /// Configures remote player avatars after instantiation.
        /// </summary>
        public static void SetupRemoteAvatar(BasisRemotePlayer Player)
        {
            Player.RemoteAvatarDriver.RemoteCalibration(Player);
            Player.BasisAvatar.OnAvatarReady?.Invoke(false);
            Player.RemoteAvatarDriver.CalibrationComplete?.Invoke();
        }

        /// <summary>
        /// Configures local player avatars after instantiation.
        /// </summary>
        public static void SetupLocalAvatar(BasisLocalPlayer Player)
        {
            Player.LocalAvatarDriver.InitialLocalCalibration(Player);
            Player.BasisAvatar.OnAvatarReady?.Invoke(true);
            BasisLocalAvatarDriver.CalibrationComplete?.Invoke();
        }

        private const int MaxConcurrentAvatarLoads = 50;
        private static readonly SemaphoreSlim _loadGate = new(MaxConcurrentAvatarLoads, MaxConcurrentAvatarLoads);

        // Tracks the latest in-flight request per player (local/remote share this).
        private static readonly ConcurrentDictionary<int, CancellationTokenSource> _playerLoadCts = new();

        private static CancellationToken ReplacePlayerLoadToken(BasisPlayer player)
        {
            int key = player.GetInstanceID();

            // Cancel & dispose previous request (if any)
            if (_playerLoadCts.TryRemove(key, out var old))
            {
                try { old.Cancel(); } catch { /* ignore */ }
                old.Dispose();
            }

            var cts = new CancellationTokenSource();
            _playerLoadCts[key] = cts;
            return cts.Token;
        }

        private static void ClearPlayerLoadToken(BasisPlayer player, CancellationToken token)
        {
            int key = player.GetInstanceID();
            if (_playerLoadCts.TryGetValue(key, out var cts) && cts.Token == token)
            {
                _playerLoadCts.TryRemove(key, out _);
                cts.Dispose();
            }
        }
    }
}
