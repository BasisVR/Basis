using Basis.Scripts.Device_Management.EyeTracking;
using Unity.Mathematics;
using UnityEngine;

namespace HVR.Basis.Comms
{
    internal class HvrOscEyeProvider : IBasisEyeTrackingProvider
    {
        private float2 _leftAngles;
        private float2 _rightAngles;
        private bool _active;

        public BasisEyeSource Source => BasisEyeSource.Osc;
        public string DisplayName => "OSC";
        public bool IsActive => _active;

        public void SetActive(bool active) => _active = active;

        public void SetAngles(float xLeft, float xRight, float y, float multiplyX, float multiplyY)
        {
            float yawLeft = Clamp(Mathf.Asin(Mathf.Clamp(xLeft, -1f, 1f)) * multiplyX);
            float yawRight = Clamp(Mathf.Asin(Mathf.Clamp(xRight, -1f, 1f)) * multiplyX);
            float pitch = Clamp(Mathf.Asin(Mathf.Clamp(y, -1f, 1f)) * multiplyY);

            _leftAngles = new float2(yawLeft, pitch);
            _rightAngles = new float2(yawRight, pitch);
        }

        public bool TryGetEyeData(ref BasisEyeTrackingData data)
        {
            if (!_active)
            {
                return false;
            }

            data.LeftAngles = _leftAngles;
            data.RightAngles = _rightAngles;
            data.HasPerEyeAngles = true;

            data.LeftOpenness = 1f;
            data.RightOpenness = 1f;
            data.HasOpenness = true;

            return true;
        }

        private static float Clamp(float value) => Mathf.Clamp(value, -Mathf.PI / 2f, Mathf.PI / 2f);
    }
}
