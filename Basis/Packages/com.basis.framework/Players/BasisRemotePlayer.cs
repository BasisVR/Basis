using Basis.Scripts.Avatar;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.UI.NamePlate;
using System.Threading.Tasks;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.BasisSdk.Players
{
    [System.Serializable]
    public class BasisRemotePlayer : BasisPlayer
    {
        [SerializeField]
        public BasisRemoteEyeFollow EyeFollow = new BasisRemoteEyeFollow();
        [SerializeField]
        public BasisRemoteBoneDriver RemoteBoneDriver = new BasisRemoteBoneDriver();
        [SerializeField]
        public BasisRemoteAvatarDriver RemoteAvatarDriver = new BasisRemoteAvatarDriver();
        [SerializeField]
        public BasisNetworkReceiver NetworkReceiver;
        public bool HasEvents = false;
        public bool OutOfRangeFromLocal = false;
        public ClientAvatarChangeMessage CACM;
        public Transform NetworkedVoice;
        public bool InAvatarRange = true;
        public byte AlwaysRequestedMode;//0 downloading 1 local
        [HideInInspector]
        public BasisLoadableBundle AlwaysRequestedAvatar;
        public async Task RemoteInitialize(ClientAvatarChangeMessage cACM, PlayerMetaDataMessage PlayerMetaDataMessage)
        {
            CACM = cACM;
            DisplayName = PlayerMetaDataMessage.playerDisplayName;
            UUID = PlayerMetaDataMessage.playerUUID;
            IsLocal = false;
            RemoteBoneDriver.CreateInitialArrays(this.transform, false);
            RemoteBoneDriver.InitializeRemote();
            if (HasEvents == false)
            {
                RemoteAvatarDriver.CalibrationComplete += RemoteCalibration;
                HasEvents = true;
            }
            await BasisRemoteNamePlateFactory.LoadRemoteNamePlate(this);
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
            if (BasisLoadableBundle == null || string.IsNullOrEmpty(BasisLoadableBundle.BasisRemoteBundleEncrypted.CombinedURL))
            {
                BasisDebug.LogError("trying to create Avatar with empty Bundle");
                return;
            }
            //BasisDebug.Log("Remote Player Create Avatar Request");
            BasisPlayerSettingsData BasisPlayerSettingsData = await BasisPlayerSettingsManager.RequestPlayerSettings(UUID);

            AlwaysRequestedAvatar = BasisLoadableBundle;
            AlwaysRequestedMode = Mode;

            if (BasisPlayerSettingsData.AvatarVisible && InAvatarRange)
            {
                //    BasisDebug.Log("loading avatar from " + BasisLoadableBundle.BasisRemoteBundleEncrypted.CombinedURL + " with net mode " + Mode);
                await BasisAvatarFactory.LoadAvatarRemote(this, Mode, BasisLoadableBundle);
            }
            else
            {
                // BasisDebug.Log("Going to load Loading Avatar Instead of requested Avatar");
                BasisAvatarFactory.RemoveOldAvatarAndLoadFallback(this, BasisAvatarFactory.LoadingAvatar.BasisLocalEncryptedBundle.LocalConnectorPath);
            }
        }
        public void OnDestroy()
        {
            if (HasEvents)
            {
                if (RemoteAvatarDriver != null)
                {
                    RemoteAvatarDriver.CalibrationComplete -= RemoteCalibration;
                    HasEvents = false;
                }
            }
            if (FacialBlinkDriver != null)
            {
                FacialBlinkDriver.OnDestroy();
            }
            if (EyeFollow != null)
            {
                EyeFollow.OnDestroy();
            }
            RemoteBoneDriver.DeInitializeGizmos();
        }
        public void RemoteCalibration()
        {
            RemoteBoneDriver.OnCalibration(this);
        }
    }
}
