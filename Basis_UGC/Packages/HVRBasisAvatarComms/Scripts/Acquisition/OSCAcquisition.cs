using Basis.Scripts.BasisSdk;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/OSC Acquisition")]
    public class OSCAcquisition : MonoBehaviour
    {
        private const string FakeWakeUpMessage = "avtr_00000000-d7dc-4a90-ab09-000000000000";
        
        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [HideInInspector] [SerializeField] private AcquisitionService acquisitionService;
        
        private OSCAcquisitionServer _acquisitionServer;
        private bool _alreadyInitialized;

        private void Awake()
        {
            if (avatar == null) avatar = CommsUtil.GetAvatar(this);
            if (acquisitionService == null) acquisitionService = AcquisitionService.SceneInstance;
            
            avatar.OnAvatarReady -= OnAvatarReady;
            avatar.OnAvatarReady += OnAvatarReady;
        }

        private void OnAvatarReady(bool isWearer)
        {
            if (!isWearer) return;
            
            if (_alreadyInitialized) return;
            _alreadyInitialized = true;

            _acquisitionServer = OSCAcquisitionServer.SceneInstance;
            _acquisitionServer.SendWakeUpMessage(FakeWakeUpMessage);

            _acquisitionServer.OnAddressUpdated -= OnAddressUpdated;
            _acquisitionServer.OnAddressUpdated += OnAddressUpdated;
        }

        private void OnDestroy()
        {
            avatar.OnAvatarReady -= OnAvatarReady;

            if (_acquisitionServer != null)
            {
                _acquisitionServer.OnAddressUpdated -= OnAddressUpdated;
            }
            if (avatar != null)
            {
                avatar.OnAvatarReady -= OnAvatarReady;
            }
        }

        private void OnAddressUpdated(string address, float value)
        {
            if (!isActiveAndEnabled) return;
            
            acquisitionService.Submit(address, value);
        }
    }
}