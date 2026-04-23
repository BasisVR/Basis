using System;
using Basis.Scripts.BasisSdk;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/OSC Acquisition")]
    public class OSCAcquisition : MonoBehaviour
    {
        private const string FakeWakeUpMessage = "avtr_00000000-89b1-4313-aa2d-000000000000";

        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [HideInInspector] [SerializeField] private AcquisitionService acquisitionService;

        private OSCAcquisitionServer _acquisitionServer;
        private bool _alreadyInitialized;
        private FaceTrackingActivityRelay _activityRelay;
        private EntityId _oscOwnerId;

        private void Awake()
        {
            if (avatar == null) avatar = HVRCommsUtil.GetAvatar(this);
            if (acquisitionService == null) acquisitionService = AcquisitionService.SceneInstance;
            _activityRelay = FaceTrackingActivityRelay.GetOrCreate(avatar);
            _oscOwnerId = gameObject.GetEntityId();

            avatar.OnAvatarReady -= OnAvatarReady;
            avatar.OnAvatarReady += OnAvatarReady;
        }

        internal void OnAvatarReady(bool isWearer)
        {
            if (!isWearer) return;

            if (_alreadyInitialized) return;
            _alreadyInitialized = true;

            _acquisitionServer = OSCAcquisitionServer.SceneInstance;
            _acquisitionServer.SendWakeUpMessage(FakeWakeUpMessage);
            BasisOscService.RegisterAddressReceiver(_oscOwnerId, OnOscAddressUpdated);
            BasisOscService.UpdateSubscriptions(_oscOwnerId, true, Array.Empty<string>(), Array.Empty<string>());
        }

        private void OnDestroy()
        {
            avatar.OnAvatarReady -= OnAvatarReady;
            if (avatar != null)
            {
                avatar.OnAvatarReady -= OnAvatarReady;
            }

            BasisOscService.UnregisterAddressReceiver(_oscOwnerId);
            BasisOscService.ClearSubscriptions(_oscOwnerId);
        }

        private void OnOscAddressUpdated(int address, float value)
        {
            if (!isActiveAndEnabled) return;

            _activityRelay?.NotifySourceSample();
            acquisitionService.Submit(address, value);
        }
    }
}
