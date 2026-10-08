using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;
using static SerializableBasis;

namespace Basis.Scripts.Player
{
    /// <summary>
    /// Factory for creating local and remote player instances.
    /// Wraps Addressables loading, prefab instantiation, and component initialization.
    /// </summary>
    /// <remarks>
    /// Call <see cref="Initialize"/> once at startup to load the local/remote player prefabs.
    /// When shutting down or changing scenes, call <see cref="DeInitialize"/> to release Addressables handles.
    /// </remarks>
    [AutoStaticsCleanup]
    public static partial class BasisPlayerFactory
    {
        /// <summary>
        /// Prefab asset for the local player, loaded via Addressables by <see cref="Initialize"/>.
        /// </summary>
        public static GameObject LocalPlayerReadyToSpawn;

        /// <summary>
        /// Addressables key for the local player prefab.
        /// </summary>
        public static string LocalPlayerId = "LocalPlayer";

        /// <summary>
        /// Addressables handle used to manage the local player prefab's lifetime.
        /// </summary>
        public static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> LocalHandle;

        /// <summary>
        /// Tpose Handle
        /// </summary>
        public static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<RuntimeAnimatorController> TposeHandle;

        public static RuntimeAnimatorController TposeController;

        /// <summary>Addressable path to the T-Pose animator controller asset.</summary>
        public const string TPose = "Assets/Animator/Animated TPose.controller";
        /// <summary>
        /// Loads the local and remote player prefabs from Addressables and caches them for instantiation.
        /// </summary>
        /// <remarks>
        /// This method blocks using <c>WaitForCompletion()</c>. If you need a non-blocking flow,
        /// adapt this to await the async operations and expose a Task-returning version.
        /// </remarks>
        public static void Initialize()
        {
            LocalHandle = Addressables.LoadAssetAsync<GameObject>(LocalPlayerId);
            LocalPlayerReadyToSpawn = LocalHandle.WaitForCompletion();

            TposeHandle = Addressables.LoadAssetAsync<RuntimeAnimatorController>(TPose);
            TposeController = TposeHandle.WaitForCompletion();
        }

        /// <summary>
        /// Releases Addressables handles acquired by <see cref="Initialize"/>.
        /// </summary>
        /// <remarks>
        /// After calling this, <see cref="LocalPlayerReadyToSpawn"/>
        /// should no longer be used. Re-run <see cref="Initialize"/> to load it again.
        /// </remarks>
        public static void DeInitialize()
        {
            if (LocalHandle.IsValid())
            {
                Addressables.Release(LocalHandle);
                LocalHandle = default;
            }
            if (TposeHandle.IsValid())
            {
                Addressables.Release(TposeHandle);
                TposeHandle = default;
            }
            LocalPlayerReadyToSpawn = null;
            TposeController = null;
        }

        /// <summary>
        /// Instantiates and initializes the local player from the cached prefab.
        /// </summary>
        /// <param name="InstantiationParameters">
        /// Position/rotation/parent parameters used by Addressables-style instantiation.
        /// </param>
        /// <returns>
        /// The created and initialized <see cref="BasisLocalPlayer"/> instance.
        /// </returns>
        /// <remarks>
        /// Calls <see cref="BasisLocalPlayer.LocalInitialize"/> and awaits completion before returning.
        /// </remarks>
        public static async Task<BasisLocalPlayer> CreateLocalPlayer(InstantiationParameters InstantiationParameters)
        {
            GameObject gameObject = GameObject.Instantiate(
                LocalPlayerReadyToSpawn,
                InstantiationParameters.Position,
                InstantiationParameters.Rotation,
                InstantiationParameters.Parent);

            if (gameObject.TryGetComponent<BasisLocalPlayer>(out BasisLocalPlayer CreatedLocalPlayer))
            {
                // found component
            }
            else
            {
                BasisDebug.LogError("Missing LocalPlayer");
            }

            CreatedLocalPlayer.PlayerSelf = CreatedLocalPlayer.transform;
            await CreatedLocalPlayer.LocalInitialize();
            return CreatedLocalPlayer;
        }

        /// <summary>
        /// Instantiates and initializes a remote player from the cached prefab.
        /// </summary>
        /// <param name="InstantiationParameters">
        /// Position/rotation/parent parameters used by Addressables-style instantiation.
        /// </param>
        /// <param name="AvatarURL">
        /// Initial avatar change message (network payload) used to configure the remote avatar.
        /// </param>
        /// <param name="PlayerMetaDataMessage">
        /// Player metadata containing display name and UUID for <see cref="BasisRemotePlayer.RemoteInitialize"/>.
        /// </param>
        /// <returns>The created <see cref="BasisRemotePlayer"/> instance.</returns>
        /// <remarks>
        /// This method calls <see cref="BasisRemotePlayer.RemoteInitialize(SerializableBasis.ClientAvatarChangeMessage, SerializableBasis.ClientMetaDataMessage, string)"/>.
        /// </remarks>
        public static BasisRemotePlayer CreateRemotePlayer(
            InstantiationParameters InstantiationParameters,
            ClientAvatarChangeMessage AvatarURL,
            ClientMetaDataMessage PlayerMetaDataMessage,
            string PreparedSafeDisplayName = null)
        {
            // The remote player is a plain managed object with no dedicated root GameObject.
            // RemoteInitialize creates its own scene-root marker transforms (mouth, nameplate),
            // and the avatar loads as its own scene root — each a distinct Transform.root so
            // the bone jobs spread across worker threads.
            BasisRemotePlayer CreatedRemotePlayer = new BasisRemotePlayer();
            CreatedRemotePlayer.RemoteInitialize(AvatarURL, PlayerMetaDataMessage,
                PreparedSafeDisplayName: PreparedSafeDisplayName);
            return CreatedRemotePlayer;
        }
    }
}
