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
    [System.Serializable]
    public class BasisRemotePlayer : BasisPlayer
    {
        [Header("Eye Driver")]
        [SerializeField]
        public BasisRemoteEyeDriver RemoteEyeDriver = new BasisRemoteEyeDriver();
        [Header("Bone Driver")]
       // [SerializeField]
       // public BasisRemoteBoneDriver RemoteBoneDriver = new BasisRemoteBoneDriver();
        [Header("Avatar Driver")]
        [SerializeField]
        public BasisRemoteAvatarDriver RemoteAvatarDriver = new BasisRemoteAvatarDriver();
        [Header("Receiver")]
        [SerializeField]
        public BasisNetworkReceiver NetworkReceiver;
        [Header("Name Plate")]
        [SerializeField]
        public BasisRemoteNamePlate RemoteNamePlate = null;
        public bool OutOfRangeFromLocal = false;
        public ClientAvatarChangeMessage CACM;
        public bool InAvatarRange = true;
        public byte AlwaysRequestedMode;//0 downloading 1 local
        [HideInInspector]
        public BasisLoadableBundle AlwaysRequestedAvatar;
        public static GameObject NamePlate;
        public int RemotePlayerDataIndex;
        public Transform MouthTransform;
        /// <summary>
        /// we are leaking this memory atm!
        /// </summary>
        /// <param name="LoadableNamePlatename"></param>
        /// <returns></returns>
        public static GameObject LoadFromHandle(string LoadableNamePlatename)
        {
            if (NamePlate == null)
            {
                UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op = Addressables.LoadAssetAsync<GameObject>(LoadableNamePlatename);
                NamePlate = op.WaitForCompletion();
            }
            return NamePlate;
        }
        public void RemoteInitialize(ClientAvatarChangeMessage cACM, ClientMetaDataMessage PlayerMetaDataMessage, string LoadableNamePlatename = "Assets/UI/Prefabs/NamePlate.prefab")
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
        public async void LoadAvatarFromInitial(ClientAvatarChangeMessage CACM)
        {
            if (BasisAvatar == null)
            {
                this.CACM = CACM;
                BasisLoadableBundle BasisLoadedBundle = BasisBundleConversionNetwork.ConvertNetworkBytesToBasisLoadableBundle(CACM.byteArray);
                if (BasisLoadedBundle != null)
                {
                    await CreateAvatar(CACM.loadMode, BasisLoadedBundle);
                }
                else
                {
                    BasisDebug.LogError("Invalid Inital Data");
                }
            }
        }
        public async void ReloadAvatar()
        {
            if (AlwaysRequestedAvatar != null)
            {
                await CreateAvatar(AlwaysRequestedMode, AlwaysRequestedAvatar);
            }
        }
        public async Task CreateAvatar(byte Mode, BasisLoadableBundle BasisLoadableBundle)
        {
            if (BasisLoadableBundle == null || string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.RemoteBeeFileLocation))
            {
                BasisDebug.LogError("trying to create Avatar with empty Bundle", BasisDebug.LogTag.Remote);
                BasisLoadableBundle = BasisAvatarFactory.LoadingAvatar;
                Mode = 0;
            }
            //BasisDebug.Log("Remote Player Create Avatar Request");
            BasisPlayerSettingsData BasisPlayerSettingsData = await BasisPlayerSettingsManager.RequestPlayerSettings(UUID);

            AlwaysRequestedAvatar = BasisLoadableBundle;
            AlwaysRequestedMode = Mode;

            if (BasisPlayerSettingsData.AvatarVisible && InAvatarRange)
            {
                //    BasisDebug.Log("loading avatar from " + BasisLoadableBundle.BasisRemoteBundleEncrypted.CombinedURL + " with net mode " + Mode);
                await BasisAvatarFactory.LoadAvatarRemote(this, Mode, BasisLoadableBundle, Vector3.zero, Quaternion.identity);
            }
            else
            {
                // BasisDebug.Log("Going to load Loading Avatar Instead of requested Avatar");
                BasisAvatarFactory.RemoveOldAvatarAndLoadFallback(this, BasisAvatarFactory.LoadingAvatar.BasisLocalEncryptedBundle.DownloadedBeeFileLocation, Vector3.zero, Quaternion.identity);
            }
            if (NetworkReceiver != null)
            {
                if (NetworkReceiver.PoseHandler == null)
                {
                    return;
                }
                if (NetworkReceiver.BufferHolder.HasFirst)
                {
                    return;
                }
                if (NetworkReceiver.BufferHolder.HasLast)
                {
                    return;
                }
            }
            LastComputedMeshLod = -1;
        }
        public void OnDestroy()
        {
            if (FacialBlinkDriver != null)
            {
                FacialBlinkDriver.OnDestroy();
            }
            if (RemoteEyeDriver != null)
            {
                RemoteEyeDriver.OnDestroy();
            }
            if (RemoteNamePlate != null)
            {
                RemoteNamePlate.DeInitalize();
                AddressableResourceProcess.ReleaseGameobject(RemoteNamePlate.gameObject);
            }
            if (RemoteAvatarDriver.hasDatainBoneDriver)
            {
                RemoteBoneJobSystem.RemoveRemotePlayer(NetworkReceiver.playerId);
                RemoteAvatarDriver.hasDatainBoneDriver = false;
            }
        }
        public short LastComputedMeshLod = -1;
        public void ChangeMeshLOD(float DistanceToPlayer, float ReductionMultiplier)
        {
            if (BasisAvatar != null && BasisAvatar.Renders != null)
            {
                // Normalize distance into [0,1]
                float normalized = DistanceToPlayer * ReductionMultiplier;

                // Map evenly to 0–3 LOD (4 levels total)
                short grid = (short)Mathf.Clamp(Mathf.FloorToInt(normalized * 4f), 0, 3);

                if (LastComputedMeshLod != grid)
                {
                    LastComputedMeshLod = grid;
                    foreach (Renderer renderer in BasisAvatar.Renders)
                    {
                        if (renderer != null)
                        {
                            renderer.forceMeshLod = grid;          // Correct property, not "forceMeshLod"
                        }
                    }
                }
            }
        }
    }
}
