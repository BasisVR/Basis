using System;
using System.Collections.Generic;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [AddComponentMenu("HVR.Basis/Comms/Assist/Acquisition Assist")]
    public class AcquisitionAssist : MonoBehaviour
    {
        public BlendshapeActuationDefinitionFile definitionFile;
        [HideInInspector] public AcquisitionService acquisitionService;
        [NonSerialized] public Dictionary<string, float> memory = new Dictionary<string, float>();

        public string[] toggles;

        private void Awake()
        {
            if (acquisitionService == null) acquisitionService = AcquisitionService.SceneInstance;
        }

        private void OnAddressUpdated(string address, float value)
        {
            if (!isActiveAndEnabled) return;
            
            acquisitionService.Submit(address, value);
        }
    }
}
