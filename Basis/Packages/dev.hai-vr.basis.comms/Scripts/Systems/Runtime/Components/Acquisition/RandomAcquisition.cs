using System.Collections.Generic;
using UnityEngine;

namespace HVR.Basis.Comms
{
    /// This is a developer class designed to flood face tracking data in order to test complex networking scenarios.
    [AddComponentMenu("HVR.Basis/Comms/Assist/Random Acquisition")]
    public class RandomAcquisition : MonoBehaviour
    {
        public float updatesPerSecond = 30;

        private AcquisitionService _acquisiton;
        private Dictionary<int, AcquisitionForAddress> _dict;
        private float _time;

        private void Awake()
        {
            _acquisiton = AcquisitionService.SceneInstance;
            var field = typeof(AcquisitionService).GetField("_addressUpdated", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            _dict = (Dictionary<int, AcquisitionForAddress>)field.GetValue(_acquisiton);

            _time = float.MinValue;
        }

        private void Update()
        {
            if (Time.time - _time < 1 / updatesPerSecond) return;
            _time = Time.time;

            foreach (var address in _dict.Keys)
            {
                _acquisiton.Submit(address, Random.Range(0f, 1f));
            }
        }
    }
}
