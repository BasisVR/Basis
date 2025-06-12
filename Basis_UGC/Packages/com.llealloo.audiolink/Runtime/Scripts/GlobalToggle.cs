#if UDONSHARP


using UnityEngine.UI;



namespace AudioLink
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GlobalToggle : MonoBehaviour
    {
        [UdonSynced]
        private bool syncedValue;
        private bool deserializing;
        private Toggle toggle;
        private BasisPlayer localPlayer;

        private void Start()
        {
            toggle = transform.GetComponent<Toggle>();
            localPlayer = Networking.LocalPlayer;
            syncedValue = toggle.isOn;
            deserializing = false;

            if (Networking.IsOwner(gameObject))
                RequestSerialization();
        }

        public override void OnDeserialization()
        {
            if (!enabled) return;

            deserializing = true;
            toggle.isOn = syncedValue;
            deserializing = false;
        }

        public void ToggleUpdate()
        {
            if (!enabled) return;
            
            if (toggle == null)
                return;
            if (deserializing)
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(localPlayer, gameObject);

            syncedValue = toggle.isOn;
            RequestSerialization();
        }
    }
}
#endif
