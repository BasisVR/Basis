// ============================
// BasisVehicleSteeringWheel.cs
// (updated: supports network steer ratio)
// ============================
using Basis.Scripts.Common;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    public class BasisVehicleSteeringWheel : MonoBehaviour
    {
        [Header("Source (optional)")]
        [Tooltip("If null, will auto-find a parent BasisVehicleBody and its steering wheels.")]
        public Basis.Scripts.Vehicles.Main.BasisVehicleBody VehicleBody;

        [Tooltip("If empty, will auto-find BasisVehicleWheel parts under VehicleBody and use the ones that can steer (MaxSteeringAngleDegrees > 0).")]
        public List<Basis.Scripts.Vehicles.Parts.BasisVehicleWheel> SteeringWheels = new();

        [Header("Network Override (Remote)")]
        public bool UseNetworkSteerRatio = false;

        [Range(-1f, 1f)]
        public float NetworkSteerRatio = 0f;

        [Header("Steering Wheel Visual")]
        [Tooltip("The visual transform to rotate. If null, uses this transform.")]
        public Transform SteeringWheelTransform;

        [Tooltip("Axis of rotation in the steering wheel's LOCAL space. (0,1,0)=local Y, (1,0,0)=local X, (0,0,1)=local Z.")]
        public Vector3 LocalRotationAxis = Vector3.up;

        [Tooltip("How many degrees the steering wheel rotates at full lock (ratio = 1). Typical cars: 360–900.")]
        public float MaxSteeringWheelRotationDegrees = 540f;

        [Tooltip("Invert steering wheel direction.")]
        public bool Invert = false;

        [Tooltip("If > 0, smooths rotation (degrees/second). If <= 0, snaps instantly.")]
        public float RotationDegreesPerSecond = 720f;

        [Header("Behavior")]
        [Tooltip("Use WheelCollider.steerAngle (actual) instead of TargetSteeringRatio (desired). Usually looks better.")]
        public bool UseActualWheelAngle = true;

        private Quaternion _initialLocalRotation;
        private float _currentVisualAngleDeg;

        private void Awake()
        {
            if (SteeringWheelTransform == null)
                SteeringWheelTransform = transform;

            _initialLocalRotation = SteeringWheelTransform.localRotation;
        }

        private void LateUpdate()
        {
            float steerRatio = UseNetworkSteerRatio ? NetworkSteerRatio : ComputeSteerRatio();
            if (Invert) steerRatio = -steerRatio;

            float targetVisualAngleDeg = steerRatio * MaxSteeringWheelRotationDegrees;

            if (RotationDegreesPerSecond <= 0f)
            {
                _currentVisualAngleDeg = targetVisualAngleDeg;
            }
            else
            {
                float maxStep = RotationDegreesPerSecond * Time.deltaTime;
                _currentVisualAngleDeg = Mathf.MoveTowards(_currentVisualAngleDeg, targetVisualAngleDeg, maxStep);
            }

            Vector3 axis = LocalRotationAxis;
            if (axis.sqrMagnitude < 0.000001f)
                axis = Vector3.up;

            axis.Normalize();

            SteeringWheelTransform.localRotation =
                _initialLocalRotation * Quaternion.AngleAxis(_currentVisualAngleDeg, axis);
        }

        private float ComputeSteerRatio()
        {
            float sum = 0f;
            int count = 0;

            for (int i = 0; i < SteeringWheels.Count; i++)
            {
                var wheel = SteeringWheels[i];
                if (wheel == null || wheel.MaxSteeringAngleDegrees <= 0.0f)
                    continue;

                float ratio;

                if (UseActualWheelAngle)
                {
                    var wc = wheel.GetComponent<WheelCollider>();
                    if (wc == null)
                        continue;

                    ratio = wc.steerAngle / wheel.MaxSteeringAngleDegrees;
                }
                else
                {
                    ratio = wheel.TargetSteeringRatio;
                }

                sum += Mathf.Clamp(ratio, -1f, 1f);
                count++;
            }

            return (count == 0) ? 0f : (sum / count);
        }
    }
}
