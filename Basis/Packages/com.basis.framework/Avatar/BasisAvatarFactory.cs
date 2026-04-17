using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using GatorDragonGames.JigglePhysics;
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
        /// Cached prefab for the loading/fallback avatar. Loaded once, instantiated many times.
        /// </summary>
        private static GameObject CachedLoadingAvatarPrefab;

        public static void Initalize()
        {
            var op = Addressables.LoadAssetAsync<GameObject>(LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation);
            CachedLoadingAvatarPrefab = op.WaitForCompletion();
        }
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
            if (Player == null)
            {
                return;
            }

            var token = ReplacePlayerLoadToken(Player);

            if (string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                BasisDebug.LogError("Avatar Address was empty or null! Falling back to loading avatar.");
                LoadAvatarAfterError(Player, Position, Rotation); // UNGATED
                ClearPlayerLoadToken(Player, token);
                return;
            }

            // Fallback can happen instantly, no restriction
            RemoveOldAvatarAndLoadFallback(Player, Position, Rotation);
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
                        // ResolveGate picks between download / disc-load / addressable so
                        // slow network downloads can't starve fast cached or in-memory loads.
                        SemaphoreSlim gate = ResolveGate(Mode, BasisLoadableBundle);
                        await gate.WaitAsync(token);
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
                                ChecksRequired Required = new ChecksRequired(true, false, false,true);

                                // If LoadAsGameObjectsAsync doesn't accept a token, we still check before/after.
                                Output = await AddressableResourceProcess.LoadAsGameObjectsAsync(BasisDeviceManagement.Instance.CreationGameobject,
                                    BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, Para, Required, BundledContentHolder.Selector.Avatar);

                                token.ThrowIfCancellationRequested();
                            }
                        }
                        finally
                        {
                            gate.Release();
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
                    LoadAvatarAfterError(Player, Position, Rotation); // UNGATED
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
            // Caller may have been destroyed between scheduling this load and us running
            // (e.g. disconnect during an earlier async step). Unity's overloaded == catches
            // destroyed-but-not-yet-GCd objects.
            if (Player == null)
            {
                return;
            }

            var token = ReplacePlayerLoadToken(Player);

            if (string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                Player.AvatarLoadErrorMessage = "Avatar address was empty or null";
                BasisDebug.LogError("Avatar Address was empty or null! Falling back to loading avatar.");
                MarkRemoteLoadFailed(Player);
                LoadAvatarAfterError(Player, Position, Rotation); // UNGATED
                ClearPlayerLoadToken(Player, token);
                return;
            }

            // Instant fallback while real avatar loads, skip if already on fallback
            if (!Player.IsConsideredFallBackAvatar)
            {
                RemoveOldAvatarAndLoadFallback(Player, Position, Rotation);
            }
            GameObject Output = null;
            try
            {
                switch (Mode)
                {
                    case 2:
                        Output = BasisLoadableBundle.LoadableGameobject.InSceneItem;
                        Output.transform.SetPositionAndRotation(Position, Rotation);
                        break;

                    case 0:
                    case 1:
                    default:
                        SemaphoreSlim gate = ResolveGate(Mode, BasisLoadableBundle);
                        await gate.WaitAsync(token);
                        try
                        {
                            token.ThrowIfCancellationRequested();

                            // Player may have been destroyed while we were queued on the gate.
                            // Treat that as a cancellation so the existing OCE branch handles
                            // cleanup instead of letting Player.transform throw downstream.
                            if (Player == null)
                            {
                                throw new OperationCanceledException(token);
                            }

                            if (Mode == 0)
                            {
                                Output = await DownloadAndLoadAvatar(BasisLoadableBundle, Player, Position, Rotation, token, MaxDownloadSizeInMBRemote);
                            }
                            else
                            {
                                ChecksRequired Required = new ChecksRequired(false, false, false,true);
                                InstantiationParameters Para = InstantiationParameters(Player, Position, Rotation);

                                Output = await AddressableResourceProcess.LoadAsGameObjectsAsync(BasisDeviceManagement.Instance.CreationGameobject,
                                    BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation, Para, Required, BundledContentHolder.Selector.Avatar);

                                token.ThrowIfCancellationRequested();
                            }
                        }
                        finally
                        {
                            gate.Release();
                        }
                        break;
                }

                token.ThrowIfCancellationRequested();

                Player.AvatarMetaData = BasisLoadableBundle;
                Player.AvatarLoadMode = Mode;

                InitializePlayerAvatar(Player, Output);
                Player.AvatarLoadErrorMessage = null;
                Player.AvatarSwitched();
            }
            catch (OperationCanceledException)
            {
                // Load was cancelled (e.g. player disconnected). Destroy any already-instantiated
                // avatar GameObject to prevent it from being orphaned at spawn.
                if (Output != null)
                {
                    GameObject.Destroy(Output);
                }
            }
            catch (Exception e)
            {
                Player.AvatarLoadErrorMessage = $"Loading avatar failed: {e.Message}";
                BasisDebug.LogError($"Loading avatar failed: {e}");
                if (!token.IsCancellationRequested)
                {
                    MarkRemoteLoadFailed(Player);
                    LoadAvatarAfterError(Player, Position, Rotation); // UNGATED
                }
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
        public static async Task<GameObject> DownloadAndLoadAvatar(BasisLoadableBundle BasisLoadableBundle, BasisPlayer BasisPlayer, Vector3 Position, Quaternion Rotation, CancellationToken Token, long MaxDownloadSizeInMB = 4L * 1024 * 1024 * 1024)
        {
            string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
            GameObject Output = await BasisLoadHandler.LoadGameObjectBundle(BasisDeviceManagement.Instance.CreationGameobject,
                BasisLoadableBundle, true, BasisPlayer.ProgressReportAvatarLoad, Token,
                Position, Rotation, Vector3.one, false, BundledContentHolder.Selector.Avatar, BasisPlayer.transform, false, true, MaxDownloadSizeInMB);

            BasisPlayer.ProgressReportAvatarLoad.ReportProgress(UniqueID, 100, "Setting Position");
            return Output;
        }

        /// <summary>
        /// Loads a fallback avatar if the requested one fails or is invalid.
        /// </summary>
        /// <param name="Player">The player to assign the fallback avatar to.</param>
        /// <param name="LoadingAvatarToUse">The address of the fallback avatar.</param>
        public static void RemoveOldAvatarAndLoadFallback(BasisPlayer Player, Vector3 Position, Quaternion Rotation)
        {
            var inSceneLoadingAvatar = GameObject.Instantiate(CachedLoadingAvatarPrefab, Position, Rotation, Player.transform);

            if (inSceneLoadingAvatar.TryGetComponent(out BasisAvatar avatar))
            {
                SetupPlayerAvatar(Player, avatar, isFallback: true);
            }
            else
            {
                BasisDebug.LogError("Missing Basis Avatar Component on Fallback Avatar");
            }
        }

        /// <summary>
        /// Initializes a player's avatar with the given prefab instance. For non-local
        /// avatars, runs the performance-limit trim pass before setup so excess
        /// components are gone before the avatar driver caches renderer/component
        /// references. The resulting trim counts are stashed on the remote player so
        /// the individual-player menu can show exactly what got removed.
        /// </summary>
        private static void InitializePlayerAvatar(BasisPlayer Player, GameObject Output)
        {
            if (Output.TryGetComponent(out BasisAvatar avatar))
            {
                if (!Player.IsLocal && Player is BasisRemotePlayer remote)
                {
                    // Per-player session bypass short-circuits the trim entirely,
                    // mirroring the Evaluate skip in BasisRemotePlayer.CreateAvatar.
                    // Leaving LastPerformanceInfo at its freshly-reset default lets
                    // the UI show a clean "no filter applied" state for this player.
                    var trimInfo = remote.BypassPerformanceLimits
                        ? default(BasisAvatarPerformanceLimits.PerformanceInfo)
                        : BasisAvatarPerformanceLimits.TrimExcessComponents(Output);

                    // Only the success path (non-blocked, non-fallback) reaches here,
                    // so CreateAvatar's freshly-reset LastPerformanceInfo has Blocked
                    // = false and all counts = 0. Overwrite with the trim result; the
                    // jiggle rig count is written later by RemoteCalibration.
                    remote.LastPerformanceInfo = trimInfo;
                }
                SetupPlayerAvatar(Player, avatar, isFallback: false);
            }
        }

        /// <summary>
        /// Configures a player with a specific avatar.
        /// Handles both local and remote player cases.
        /// </summary>
        private static void SetupPlayerAvatar(BasisPlayer Player, BasisAvatar avatar, bool isFallback)
        {
            // Explicitly unregister old JiggleRigs synchronously. DeleteLastAvatar is async void
            // and GameObject.Destroy only fires OnDisable at end-of-frame, which races with the
            // new avatar's JiggleRig registration below. Doing it here keeps tree state consistent.
            if (Player.BasisAvatar != null)
            {
                var oldRigs = Player.BasisAvatar.GetComponentsInChildren<JiggleRig>(true);
                for (int i = 0; i < oldRigs.Length; i++)
                {
                    oldRigs[i].OnRemove();
                }
            }
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
        /// Marks a remote player as permanently failed for this session's avatar load,
        /// preventing range-change retries, and pushes the red failure color onto the nameplate.
        /// Cleared only via the Hide/Show Avatar toggle in the individual player menu.
        /// </summary>
        public static void MarkRemoteLoadFailed(BasisRemotePlayer Player)
        {
            if (Player == null) return;
            Player.HasFailedAvatarLoadGlobally = true;
            if (Player.RemoteNamePlate != null)
            {
                Player.RemoteNamePlate.RefreshFailedStateColor();
            }
        }

        /// <summary>
        /// Attempts to load the fallback avatar after a loading error.
        /// </summary>
        public static void LoadAvatarAfterError(BasisPlayer Player, Vector3 Position, Quaternion Rotation)
        {
            // Error paths reach here after an await — the player may already be destroyed.
            // Accessing Player.transform on a destroyed object would throw a second
            // MissingReferenceException while we're already handling the first one.
            if (Player == null)
            {
                return;
            }

            try
            {
                GameObject data = GameObject.Instantiate(CachedLoadingAvatarPrefab, Position, Rotation, Player.transform);

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
                    GameObject.Destroy(Player.BasisAvatar.gameObject);
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
            Player.BasisAvatar.NotifyAvatarReady(false);
        }

        /// <summary>
        /// Configures local player avatars after instantiation.
        /// </summary>
        public static void SetupLocalAvatar(BasisLocalPlayer Player)
        {
            Player.LocalAvatarDriver.InitialLocalCalibration(Player);
            Player.BasisAvatar.NotifyAvatarReady(true);
            BasisLocalAvatarDriver.CalibrationComplete?.Invoke();
        }

        // Avatar load concurrency gates. Three separate semaphores because each path has
        // a completely different bottleneck:
        //
        //   _downloadGate    — bundle downloads from the network. Bandwidth-bound. Small
        //                      default so each transfer runs at full speed instead of
        //                      splitting bandwidth across N simultaneous loads.
        //   _discGate        — bundle loads from the local disc cache. I/O + decryption +
        //                      AssetBundle decompression. No network, so the gate can be
        //                      wider, but not unlimited because the Unity AssetBundle API
        //                      serialises heavily under the hood.
        //   _addressableGate — built-in addressable instantiations. In-memory operations,
        //                      the widest of the three.
        //
        // Defaults live in BasisSettingsDefaults and are pushed in via
        // SMModuleAvatarLoadGates. Gates are field-swapped (not resized) when settings
        // change, so in-flight loads continue on whichever semaphore they captured.
        private static SemaphoreSlim _downloadGate = new(5, int.MaxValue);
        private static SemaphoreSlim _discGate = new(15, int.MaxValue);
        private static SemaphoreSlim _addressableGate = new(25, int.MaxValue);

        /// <summary>
        /// Replace the network-download gate with a new semaphore sized to
        /// <paramref name="capacity"/>. In-flight loads that already captured the previous
        /// semaphore will continue to use it and drain normally.
        /// </summary>
        public static void SetDownloadGateCapacity(int capacity)
        {
            if (capacity < 1) capacity = 1;
            _downloadGate = new SemaphoreSlim(capacity, int.MaxValue);
        }

        /// <summary>
        /// Replace the disc-load gate with a new semaphore sized to <paramref name="capacity"/>.
        /// </summary>
        public static void SetDiscGateCapacity(int capacity)
        {
            if (capacity < 1) capacity = 1;
            _discGate = new SemaphoreSlim(capacity, int.MaxValue);
        }

        /// <summary>
        /// Replace the addressable instantiation gate with a new semaphore sized to
        /// <paramref name="capacity"/>.
        /// </summary>
        public static void SetAddressableGateCapacity(int capacity)
        {
            if (capacity < 1) capacity = 1;
            _addressableGate = new SemaphoreSlim(capacity, int.MaxValue);
        }

        /// <summary>
        /// Picks the appropriate semaphore for a given load mode. For Mode 0 (asset bundle)
        /// we check <see cref="BasisLoadHandler.IsMetaDataOnDisc"/> so already-cached loads
        /// use the disc gate instead of sitting behind slow network downloads.
        /// </summary>
        private static SemaphoreSlim ResolveGate(byte Mode, BasisLoadableBundle bundle)
        {
            if (Mode == 0)
            {
                bool cached = BasisLoadHandler.IsMetaDataOnDisc(
                    bundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation,
                    out _);
                return cached ? _discGate : _downloadGate;
            }
            return _addressableGate;
        }

        // Tracks the latest in-flight request per player (local/remote share this).
        private static readonly ConcurrentDictionary<int, CancellationTokenSource> _playerLoadCts = new();

        private static CancellationToken ReplacePlayerLoadToken(BasisPlayer player)
        {
            int key = player.GetEntityId();

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
            int key = player.GetEntityId();
            if (_playerLoadCts.TryGetValue(key, out var cts) && cts.Token == token)
            {
                _playerLoadCts.TryRemove(key, out _);
                cts.Dispose();
            }
        }

        /// <summary>
        /// Cancels any in-flight avatar load for the given player.
        /// Call this before destroying a player to prevent orphaned avatar GameObjects.
        /// </summary>
        public static void CancelPlayerLoad(BasisPlayer player)
        {
            if (player == null) return;
            int key = player.GetEntityId();
            if (_playerLoadCts.TryRemove(key, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
                cts.Dispose();
            }
        }
    }
}
