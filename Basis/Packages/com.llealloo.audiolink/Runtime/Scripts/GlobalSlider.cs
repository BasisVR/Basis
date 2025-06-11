#if UDONSHARP


using UnityEngine.UI;



namespace AudioLink
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GlobalSlider : MonoBehaviour
    {
        [UdonSynced]
        private float syncedValue;
        private bool deserializing;
        private Slider slider;
        private BasisPlayer localPlayer;

        private void Start()
        {
            slider = transform.GetComponent<Slider>();
            localPlayer = Networking.LocalPlayer;
            syncedValue = slider.value;
            deserializing = false;

            if (Networking.IsOwner(gameObject))
                RequestSerialization();
        }

        public override void OnDeserialization()
        {
            if (!enabled) return;

            deserializing = true;
            slider.value = syncedValue;
            deserializing = false;
        }

        public void SlideUpdate()
        {
            if (!enabled) return;

            if (slider == null)
                return;
            if (deserializing)
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(localPlayer, gameObject);

            syncedValue = slider.value;
            RequestSerialization();
        }
    }
}
#endif
