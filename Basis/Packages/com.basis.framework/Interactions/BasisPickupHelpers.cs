using UnityEngine;
        using UnityEngine.XR;

        public class BasisPickupHelpers
        {
            public static float GetDistanceBetweenHands()
            {
                var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand).TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 leftPosition) ? leftPosition : Vector3.zero;
                var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand).TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 rightPosition) ? rightPosition : Vector3.zero;
                return (left - right).sqrMagnitude;
            }

            public static float GetNormalizedDistanceBetweenHands(float min = 0.02f, float max = 0.5f)
            {
                var distance = GetDistanceBetweenHands();
                return Mathf.InverseLerp(min, max, distance);
            }
        }
