using System;
using System.Collections;
using Basis.Scripts.BasisSdk;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Object State Actuation")]
    public class ObjectStateActuation : MonoBehaviour, ICommsNetworkable
    {
        public ActivationSource activationSource;
        public string address;

        [SerializeField] private bool isActiveByDefault;
        [SerializeField] private bool isInterpolated;
        [SerializeField] private float interpolationToActiveDuration = 0f;
        [SerializeField] private float interpolationToInactiveDuration = 0f;

        [SerializeField] private Component[] whenActive;
        [SerializeField] private Component[] whenInactive;

        private bool _currentTargetState;
        private InterpolatingState _currentInterpolation;

        private float _lastStateChangeTime = 0f;
        private float _endStateChangeTime = 0f;
        private Coroutine _lastCoroutineNullable;

        [HideInInspector] [SerializeField] private BasisAvatar avatar;
        [HideInInspector] [SerializeField] private FeatureNetworking featureNetworking;
        [HideInInspector] [SerializeField] private AcquisitionService acquisition;

#region NetworkingFields
        private int _guidIndex;
        // Can be null due to:
        // - Application with no network, or
        // - Network late initialization.
        // Nullability is needed for local tests without initialization scene.
        // - Becomes non-null after HVRAvatarComms.OnAvatarNetworkReady is successfully invoked
        private FeatureEvent _featureEvent;
        private byte[] _msg;
        private bool _isWearer;
#endregion

        private void Awake()
        {
            if (avatar == null) avatar = CommsUtil.GetAvatar(this);
            if (featureNetworking == null) featureNetworking = CommsUtil.FeatureNetworkingFromAvatar(avatar);
            if (acquisition == null) acquisition = AcquisitionService.SceneInstance;

            whenActive = CommsUtil.SlowSanitizeEndUserProvidedObjectArray(whenActive);
            whenInactive = CommsUtil.SlowSanitizeEndUserProvidedObjectArray(whenInactive);

            if (!isInterpolated)
            {
                interpolationToActiveDuration = 0f;
                interpolationToInactiveDuration = 0f;
            }

            avatar.OnAvatarReady -= OnAvatarReady;
            avatar.OnAvatarReady += OnAvatarReady;

            _msg = new byte[1];
        }

        private void OnAvatarReady(bool isWearer)
        {
            _isWearer = isWearer;

            _currentTargetState = isActiveByDefault;
            _currentInterpolation = isActiveByDefault ? InterpolatingState.Active : InterpolatingState.Inactive;

            if (isWearer)
            {
                acquisition.RegisterAddresses(new []{ address }, OnAddressUpdated);
            }

            ForceUpdateState();
        }

        private void OnDestroy()
        {
            avatar.OnAvatarReady -= OnAvatarReady;

            if (_isWearer)
            {
                acquisition.UnregisterAddresses(new []{ address }, OnAddressUpdated);
            }
        }

        private void ForceUpdateState()
        {
            Debug.Log($"Target is {_currentTargetState} and interpolation is {_currentInterpolation}");
            // When _currentState is Interpolating, both Active and Inactive objects are visible.
            // This is to enable interpolation effects such as material fading or material dissolving.

            var activeObjectsShouldBecome = _currentInterpolation != InterpolatingState.Inactive;
            foreach (var component in whenActive)
            {
                ProcessObject(component, activeObjectsShouldBecome);
            }

            var inactiveObjectsShouldBecome = _currentInterpolation != InterpolatingState.Active;
            foreach (var component in whenInactive)
            {
                ProcessObject(component, inactiveObjectsShouldBecome);
            }
        }

        private static void ProcessObject(Component component, bool shouldBecome)
        {
            if (component is Transform) component.gameObject.SetActive(shouldBecome);
            else if (component is Behaviour behaviour) behaviour.enabled = shouldBecome;
        }

        public void OnGuidAssigned(int guidIndex, Guid guid)
        {
            _featureEvent = featureNetworking.NewEventDriven(guidIndex, OnEventReceived, OnResyncRequested, OnResyncEveryoneRequested);
        }

        private void OnResyncRequested(ushort[] whoAsked)
        {
            _msg[0] = _currentTargetState ? (byte)1 : (byte)0;
            _featureEvent.Submit(_msg, whoAsked);
        }

        private void OnResyncEveryoneRequested()
        {
            InternalSubmitToEveryone();
        }

        private void OnAddressUpdated(string receivedAddress, float value)
        {
            if (receivedAddress != address) throw new ArgumentException("Unexpected address received");

            var state = value >= 1f;
            if (_currentTargetState != state)
            {
                InternalConfirmedUpdateStateChange(state);

                if (_featureEvent != null)
                {
                    InternalSubmitToEveryone();
                }
            }
        }

        private void InternalSubmitToEveryone()
        {
            _msg[0] = _currentTargetState ? (byte)1 : (byte)0;
            _featureEvent.Submit(_msg);
        }

        private void OnEventReceived(ArraySegment<byte> subBuffer)
        {
            if (subBuffer.Count != 1) { HVRAvatarComms.ProtocolError("Protocol error (in ObjectStateActuation): Unexpected length."); return; }

            var item = subBuffer.get_Item(0);
            if (item > 1) { HVRAvatarComms.ProtocolError("Protocol error (in ObjectStateActuation): Unexpected value."); return; }

            var state = item == 1;
            if (_currentTargetState != state)
            {
                InternalConfirmedUpdateStateChange(state);
            }
        }

        private void InternalConfirmedUpdateStateChange(bool state)
        {
            if (_lastCoroutineNullable != null)
            {
                StopCoroutine(_lastCoroutineNullable);
                _lastCoroutineNullable = null;
            }

            _currentTargetState = state;
            var nextInterpolationDuration = state ? interpolationToActiveDuration : interpolationToInactiveDuration;

            if (isInterpolated && nextInterpolationDuration > 0)
            {
                _currentInterpolation = InterpolatingState.Interpolating;
                _lastStateChangeTime = Time.time;
                _endStateChangeTime = Time.time + nextInterpolationDuration;

                ForceUpdateState();

                _lastCoroutineNullable = StartCoroutine(FinishInterpolation());
            }
            else
            {
                _currentInterpolation = state ? InterpolatingState.Active : InterpolatingState.Inactive;
                _lastStateChangeTime = Time.time;
                _endStateChangeTime = Time.time;

                ForceUpdateState();
            }
        }

        private IEnumerator FinishInterpolation()
        {
            var timeToWait = _endStateChangeTime - Time.time;
            Debug.Log($"Will wait {timeToWait} seconds before switching to {_currentTargetState}");

            yield return new WaitForSeconds(timeToWait);

            _currentInterpolation = _currentTargetState ? InterpolatingState.Active : InterpolatingState.Inactive;
            ForceUpdateState();
        }
    }

    public enum ActivationSource
    {
        Address
    }

    internal enum InterpolatingState
    {
        Inactive, Active, Interpolating
    }
}
