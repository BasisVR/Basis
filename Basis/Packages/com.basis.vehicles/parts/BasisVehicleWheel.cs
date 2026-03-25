using Basis.Scripts.Common;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    [RequireComponent(typeof(WheelCollider))]
    public class BasisVehicleWheel : BasisVehiclePart
    {
        [Header("Steering")]
        /// <summary>
        /// The maximum angle in radians that the wheel can steer.
        /// </summary>
        [Range(0.0f, 90.0f)]
        [Tooltip("Realistic values are less than 45.0 degrees.")]
        public float MaxSteeringAngleDegrees = 0.0f;

        /// <summary>
        /// The speed at which the wheel steering angle changes, in degrees per second. If negative, the angle changes instantly.
        /// </summary>
        [Tooltip("Negative means instant change.")]
        public float SteeringDegreesPerSecond = 60.0f;

        /// <summary>
        /// The ratio of the maximum steering angle the wheel is rotated to.
        /// </summary>
        [Range(-1.0f, 1.0f)]
        [Tooltip("Negative values mean turn left. Set at runtime by the vehicle.")]
        public float TargetSteeringRatio = 0.0f;

        /// <summary>
        /// The current steering angle in degrees, tending towards CurrentSteeringRatio * MaxSteeringAngleDegrees.
        /// If SteeringDegreesPerSecond is negative, this will equal the target value.
        /// </summary>
        private float _currentSteeringAngleDegrees = 0.0f;

        [Header("Force")]
        /// <summary>
        /// The maximum force in Newtons (kg⋅m/s²) that the wheel can provide for propulsion.
        /// </summary>
        [Tooltip("N (kg\u22C5m/s\u00B2)")]
        public float MaxPropulsionForce = 0.0f;

        /// <summary>
        /// The braking force in Newtons (kg⋅m/s²) that the wheel applies when the vehicle is trying to stop.
        /// If negative, the wheel uses propulsion force as braking instead.
        /// </summary>
        [Tooltip("Negative means use propulsion force as braking.")]
        public float BrakingForce = -1.0f;

        /// <summary>
        /// The speed at which the wheel propulsion force changes, in Newtons per second. If negative, the force changes instantly.
        /// </summary>
        [Tooltip("Negative means instant change.")]
        public float PropulsionForceChangePerSecond = -1.0f;

        /// <summary>
        /// The ratio of the maximum force the wheel is using for propulsion.
        /// </summary>
        [Range(-1.0f, 1.0f)]
        [Tooltip("Negative values mean reverse. Set at runtime by the vehicle.")]
        public float TargetPropulsionForceRatio = 0.0f;

        /// <summary>
        /// The current force being applied by the wheel, tending towards CurrentForceRatio * MaxForce.
        /// If ForceChangePerSecond is negative, this will equal the target value.
        /// </summary>
        private float _currentForce = 0.0f;

        private WheelCollider _wheelCollider = null;
        private Dictionary<Transform, BasisCalibratedCoords> _childTransforms = new();
        private bool _negateSteering = false;

        protected override void OnEnable()
        {
            base.OnEnable();
            if (transform.localPosition.z < 0.0f)
            {
                _negateSteering = true;
            }
            _wheelCollider = GetComponent<WheelCollider>();
            // Find all child transforms to rotate with the wheel.
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (_childTransforms.ContainsKey(child) == false)
                {
                    BasisCalibratedCoords coords = new BasisCalibratedCoords(child.localPosition, child.localRotation);
                    _childTransforms.Add(child, coords);
                }
            }
        }
        private void FixedUpdate()
        {
            if (!Active)
            {
                _currentForce = 0.0f;
                _wheelCollider.brakeTorque = 0.0f;
                _wheelCollider.motorTorque = 0.0f;
                if (_particles != null)
                {
                    _particleEmission.rateOverTime = 0.0f;
                }
                return;
            }
            // Move the wheel collider's steering angle towards the target.
            float steerTarget = TargetSteeringRatio * MaxSteeringAngleDegrees;
            if (SteeringDegreesPerSecond < 0.0f)
            {
                _currentSteeringAngleDegrees = steerTarget;
            }
            else
            {
                float steerChange = SteeringDegreesPerSecond * Time.fixedDeltaTime;
                _currentSteeringAngleDegrees = Mathf.MoveTowards(_currentSteeringAngleDegrees, steerTarget, steerChange);
            }
            _wheelCollider.steerAngle = _currentSteeringAngleDegrees;
            // Figure out the target force the wheel is moving towards.
            float forceTarget = 0.0f;
            bool shouldWheelsBrake = false;
            if (_parentVehicleBody != null)
            {
                forceTarget = TargetPropulsionForceRatio * MaxPropulsionForce;
                shouldWheelsBrake = _parentVehicleBody.AngularDampeners && _parentVehicleBody.LinearDampeners && _parentVehicleBody.AngularActivation == Vector3.zero && _parentVehicleBody.LinearActivation == Vector3.zero;
            }
            if (shouldWheelsBrake)
            {
                float brakeForce = BrakingForce < 0.0f ? forceTarget : BrakingForce;
                // Note: Unity's brakeTorque MUST be positive regardless of braking direction.
                _wheelCollider.brakeTorque = Mathf.Abs(brakeForce * _wheelCollider.radius);
                forceTarget = 0.0f; // Ramp down the motor torque to zero while braking.
            }
            else
            {
                _wheelCollider.brakeTorque = 0.0f;
            }
            // Move the wheel collider's motor torque towards the target.
            if (PropulsionForceChangePerSecond < 0.0f)
            {
                _currentForce = forceTarget;
            }
            else
            {
                float forceChange = PropulsionForceChangePerSecond * Time.fixedDeltaTime;
                _currentForce = Mathf.MoveTowards(_currentForce, forceTarget, forceChange);
            }
            if (_particles != null)
            {
                _particleEmission.rateOverTime = 100.0f * Mathf.Abs(_currentForce) / MaxPropulsionForce;
            }
            // Note: Unity's motorTorque uses Newton-meters (N⋅m or kg⋅m²/s²) but forceAmount is in Newtons (N or kg⋅m/s²).
            _wheelCollider.motorTorque = _currentForce * _wheelCollider.radius;
            // Transform all child objects to match the wheel collider's rotation and position.
            _wheelCollider.GetWorldPose(out Vector3 wheelPosition, out Quaternion wheelRotation);
            foreach (KeyValuePair<Transform, BasisCalibratedCoords> entry in _childTransforms)
            {
                Transform child = entry.Key;
                BasisCalibratedCoords coords = entry.Value;
                child.position = wheelPosition + (wheelRotation * coords.position);
                child.rotation = wheelRotation * coords.rotation;
            }
        }

        /// <summary>
        /// Sets the wheel's steering and thrust based on vehicle input.
        /// </summary>
        /// <param name="angularInput">The vehicle's angular input on a range of -1.0 to 1.0.</param>
        /// <param name="linearInput">The vehicle's linear input on a range of -1.0 to 1.0.</param>
        public override void SetFromVehicleInput(Vector3 angularInput, Vector3 linearInput)
        {
            // NOTE: This code only supports wheels where 0 steering means forward.
            // Ideally we should allow for wheels in other rotations but that would be more complicated.
            // Set steering, prioritizing linear input when it's stronger, otherwise using angular input.
            float source = (Mathf.Abs(linearInput.x) * 2.0f > Mathf.Abs(angularInput.y)) ? linearInput.x : angularInput.y;
            float steerRatio = source * source;
            if ((source < 0.0f) != _negateSteering)
            {
                steerRatio = -steerRatio;
            }
            TargetSteeringRatio = Mathf.Clamp(steerRatio, -1.0f, 1.0f);
            // Set thrust from vehicle linear input.
            float forceRatio = linearInput.z * Mathf.Cos(_currentSteeringAngleDegrees * Mathf.Deg2Rad);
            TargetPropulsionForceRatio = Mathf.Clamp(forceRatio, -1.0f, 1.0f);
        }
    }
}
