using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Internal/Acquisition Service")]
    public class AcquisitionService : MonoBehaviour
    {
        public static AcquisitionService SceneInstance => HVRCommsUtil.GetOrCreateSceneInstance(ref _sceneInstance);
        private static AcquisitionService _sceneInstance;

        public readonly HVRVariableStore VariableStore = new();

        // Note: All the logic of this service has been moved to HVRVariableStore.
        // This is because we want components inside an avatar or prop to subscribe to the HVRVariableStore assigned to
        // that specific avatar or prop, which will be different if we're the wearer of the avatar.

        public void Submit(int addressId, float value) => VariableStore.Submit(addressId, value);
        public void RegisterAddresses(int[] addressIds, HVRVariableStore.AddressUpdated onAddressUpdated) => VariableStore.RegisterAddresses(addressIds, onAddressUpdated);
        public void UnregisterAddresses(int[] addressIds, HVRVariableStore.AddressUpdated onAddressUpdated) => VariableStore.UnregisterAddresses(addressIds, onAddressUpdated);
    }
}
