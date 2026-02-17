using Basis.Scripts.Addressable_Driver.Resource;
using Basis.Scripts.Avatar;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.UI.NamePlate;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using static SerializableBasis;

namespace Basis.Scripts.BasisSdk.Players
{
    /// <summary>
    /// Remote (non-local) player representation used by the Basis SDK.
    /// Handles avatar creation/loading, remote eye/bone driving, network pose consumption,
    /// mesh LOD adjustments, and remote name plate lifecycle.
    /// </summary>
    /// <remarks>
    /// This class owns a number of runtime-only components and addressable resources.
    /// Call <see cref="OnDestroy"/> to dispose drivers and release addressable instances
    /// created during <see cref="RemoteInitialize(ClientAvatarChangeMessage, ClientMetaDataMessage, string)"/>.
    /// </remarks>
    [System.Serializable]
    public class BasisRemotePlayer : BasisPlayer
    {
        #region Drivers & Receivers
        /// <summary>
        /// Driver responsible for avatar-specific remote updates (e.g., bone jobs hookup).
        /// </summary>
        [Header("Avatar Driver")]
        [SerializeField]
        public BasisRemoteAvatarDriver RemoteAvatarDriver = new BasisRemoteAvatarDriver();

        /// <summary>
        /// Network receiver that provides pose/animation buffers and messages for this player.
        /// </summary>
        [Header("Network Receiver")]
        [SerializeField]
        public BasisNetworkReceiver NetworkReceiver;

        /// <summary>
        /// Network Face Driver that provides eye and blink support
        /// </summary>
        [Header("Face Driver")]
        [SerializeField]
        public BasisRemoteFaceDriver RemoteFaceDriver;
        #endregion

        #region UI / Name Plate

        /// <summary>
        /// Instance of the remote player's name plate UI, if present.
        /// </summary>
        [Header("Name Plate")]
        [SerializeField]
        public BasisRemoteNamePlate RemoteNamePlate = null;

        /// <summary>
        /// A cached prefab instance for name plates loaded via Addressables.
        /// </summary>
        /// <remarks>
        /// This static cache is never unloaded in the current implementation (intentional memoization),
        /// which means memory is retained for the lifetime of the process.
        /// </remarks>
        public static GameObject NamePlate;

        #endregion

        #region State / Data

        /// <summary>
        /// Whether this remote player is currently considered out of interaction range
        /// from the local player (used by higher-level systems to gate updates or rendering).
        /// </summary>
        public bool OutOfRangeFromLocal = false;

        /// <summary>
        /// The most recent avatar change message received for this player.
        /// </summary>
        public ClientAvatarChangeMessage CACM;

        /// <summary>
        /// Whether the remote player is within the range where avatar rendering is allowed.
        /// </summary>
        public bool InAvatarRange = true;

        /// <summary>
        /// The "always-requested" load mode for the avatar.
        /// <list type="bullet">
        /// <item><description><c>0</c> – Downloading/remote mode</description></item>
        /// <item><description><c>1</c> – Local mode</description></item>
        /// </list>
        /// </summary>
        public byte AlwaysRequestedMode; // 0 downloading, 1 local

        /// <summary>
        /// The last bundle requested for this player (used by <see cref="ReloadAvatar"/>).
        /// </summary>
        [HideInInspector]
        public BasisLoadableBundle AlwaysRequestedAvatar;

        /// <summary>
        /// Index into a remote player data array managed elsewhere (for external systems).
        /// </summary>
        public int RemotePlayerDataIndex;

        /// <summary>
        /// Optional transform indicating the mouth position, used by lip sync or VFX.
        /// </summary>
        public Transform MouthTransform;

        #endregion

        #region Initialization / Addressables

        /// <summary>
        /// Loads (and caches) a name plate prefab from Addressables and returns the cached instance.
        /// </summary>
        /// <param name="LoadableNamePlatename">The Addressables key or path for the name plate prefab.</param>
        /// <returns>The cached name plate <see cref="GameObject"/> instance.</returns>
        /// <remarks>
        /// This method uses a static cache and does not release the loaded asset.
        /// As noted in the code comment, this currently leaks memory for the lifetime of the process.
        /// </remarks>
        public static GameObject LoadFromHandle(string LoadableNamePlatename)
        {
            if (NamePlate == null)
            {
                UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op =
                    Addressables.LoadAssetAsync<GameObject>(LoadableNamePlatename);
                NamePlate = op.WaitForCompletion();
            }
            return NamePlate;
        }

        /// <summary>
        /// Initializes this remote player with network-transported identity and UI state,
        /// creating and attaching a name plate instance.
        /// </summary>
        /// <param name="cACM">Initial avatar change message for this player.</param>
        /// <param name="PlayerMetaDataMessage">Player metadata containing display name and UUID.</param>
        /// <param name="LoadableNamePlatename">
        /// Optional Addressables key/path for the name plate prefab.
        /// Defaults to <c>"Assets/UI/Prefabs/NamePlate.prefab"</c>.
        /// </param>
        public void RemoteInitialize(
            ClientAvatarChangeMessage cACM,
            ClientMetaDataMessage PlayerMetaDataMessage,
            string LoadableNamePlatename = "Assets/UI/Prefabs/NamePlate.prefab")
        {
            CACM = cACM;
            DisplayName = PlayerMetaDataMessage.playerDisplayName;
            SetSafeDisplayname();
            this.name = DisplayName;
            UUID = PlayerMetaDataMessage.playerUUID;
            IsLocal = false;

            GameObject data = GameObject.Instantiate(LoadFromHandle(LoadableNamePlatename), transform);
            if (data.TryGetComponent(out RemoteNamePlate))
            {
                if (this == null)
                {
                    AddressableResourceProcess.ReleaseGameobject(data);
                    return;
                }
                RemoteNamePlate.Initalize(this);
            }
        }

        #endregion

        #region Avatar Loading

        /// <summary>
        /// Loads the avatar from an initial <see cref="ClientAvatarChangeMessage"/> if no avatar exists yet.
        /// </summary>
        /// <param name="CACM">The message containing the initial avatar payload/bytes.</param>
        /// <remarks>
        /// This is an async-void method intended to be fire-and-forget on the main thread.
        /// Prefer <see cref="CreateAvatar(byte, BasisLoadableBundle)"/> for awaited flows.
        /// </remarks>
        public void LoadAvatarFromInitial(ClientAvatarChangeMessage CACM)
        {
            if (BasisAvatar == null)
            {
                this.CACM = CACM;
                BasisLoadableBundle BasisLoadedBundle = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(CACM.byteArray);

                InAvatarRange = false;

                if (BasisLoadedBundle != null)
                {
                    AlwaysRequestedAvatar = BasisLoadedBundle;
                    AlwaysRequestedMode = CACM.loadMode;

                    BasisAvatarFactory.RemoveOldAvatarAndLoadFallback(this,
                    BasisAvatarFactory.LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation,
                    Vector3.zero, Quaternion.identity);
                }
                else
                {
                    BasisDebug.LogError("Invalid Inital Data");
                }
            }
        }

        /// <summary>
        /// Re-creates the avatar using the last requested mode and bundle,
        /// if available (used after settings or visibility changes).
        /// </summary>
        /// <remarks>
        /// This is an async-void method intended for fire-and-forget usage.
        /// </remarks>
        public async void ReloadAvatar()
        {
            if (AlwaysRequestedAvatar != null)
            {
                await CreateAvatar(AlwaysRequestedMode, AlwaysRequestedAvatar);
            }
        }
        public bool IsLoadingAnAvatar = false;
        /// <summary>
        /// Creates or replaces the current avatar using the provided load mode and bundle.
        /// Applies user visibility settings and distance gating before loading,
        /// and falls back to the loading avatar if not visible/in range.
        /// </summary>
        /// <param name="Mode">Avatar load mode (e.g., 0 = remote/downloading, 1 = local).</param>
        /// <param name="BasisLoadableBundle">The bundle describing the avatar to load.</param>
        /// <returns>A task that completes when the avatar is loaded or a fallback is applied.</returns>
        public async Task CreateAvatar(byte Mode, BasisLoadableBundle BasisLoadableBundle)
        {
            if (IsLoadingAnAvatar)
            {
                BasisDebug.LogWarning("We Loaded a Avatar While a Existing Avatar was loading!!", BasisDebug.LogTag.Remote);
            }
            IsLoadingAnAvatar = true;
            if (BasisLoadableBundle == null || string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                BasisDebug.LogError("trying to create Avatar with empty Bundle", BasisDebug.LogTag.Remote);
                BasisLoadableBundle = BasisAvatarFactory.LoadingAvatar;
                Mode = 0;
            }

            // Fetch per-player visibility settings.
            BasisPlayerSettingsData BasisPlayerSettingsData = await BasisPlayerSettingsManager.RequestPlayerSettings(UUID);

            // Remember last requested avatar and mode for potential reloads.
            AlwaysRequestedAvatar = BasisLoadableBundle;
            AlwaysRequestedMode = Mode;

            if (BasisPlayerSettingsData.AvatarVisible && InAvatarRange)
            {
                await BasisAvatarFactory.LoadAvatarRemote(this, Mode, BasisLoadableBundle, Vector3.zero, Quaternion.identity);
            }
            else
            {
                BasisAvatarFactory.RemoveOldAvatarAndLoadFallback(this,
                    BasisAvatarFactory.LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation,
                    Vector3.zero, Quaternion.identity);
            }
            IsLoadingAnAvatar = false;
        }

        #endregion

        #region Teardown

        /// <summary>
        /// Disposes owned drivers and releases addressable instances (name plate, bone jobs).
        /// </summary>
        public void OnDestroy()
        {
            if (RemoteFaceDriver != null)
            {
                RemoteFaceDriver.OnDestroy();
            }
            if (RemoteNamePlate != null)
            {
                RemoteNamePlate.DeInitalize();
                AddressableResourceProcess.ReleaseGameobject(RemoteNamePlate.gameObject);
            }
            if (RemoteAvatarDriver.InBoneDriver)
            {
                RemoteBoneJobSystem.RemoveRemotePlayer(NetworkReceiver.playerId);
                RemoteAvatarDriver.InBoneDriver = false;
            }
        }

        #endregion

        #region LOD

        /// <summary>
        /// Computes and applies a mesh LOD level for all avatar renderers based on the
        /// distance to the local player and a reduction multiplier.
        /// </summary>
        /// Multiplier applied to the distance before mapping to LOD levels.
        /// Higher values cause LODs to drop off sooner.
        /// </param>
        public void ChangeMeshLOD(short grid)
        {
            if (BasisAvatar != null && BasisAvatar.Renders != null)
            {
                int length = BasisAvatar.Renders.Length;
                for (int Index = 0; Index < length; Index++)
                {
                    Renderer renderer = BasisAvatar.Renders[Index];
                    if (renderer != null)
                    {
                        renderer.forceMeshLod = grid;
                    }
                }
            }
        }

        #endregion
    }
}
