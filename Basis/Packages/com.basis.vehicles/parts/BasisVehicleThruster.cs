using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    /// <summary>
    /// General-purpose thruster for vehicles. Has a gimbal and emits a force.
    /// BasisVehicleThruster can be used to create rocket engines, jet engines, control thrusters, or any other kind of thruster.
    /// </summary>
    public class BasisVehicleThruster : BasisVehicleGimbalablePart
    {
        [Header("Thrust")]
        /// <summary>
        /// The maximum thrust force in Newtons (kg⋅m/s²) that the thruster can provide. Must not be negative.
        /// </summary>
        [Tooltip("N (kg\u22C5m/s\u00B2)")]
        public float MaxForce = 0.0f;

        /// <summary>
        /// The speed at which the thruster force changes, in Newtons per second. If negative, the force changes instantly.
        /// </summary>
        [Tooltip("Negative means instant change.")]
        public float ForceChangePerSecond = -1.0f;

        /// <summary>
        /// The ratio of the maximum thrust force the thruster is currently using for propulsion.
        /// </summary>
        [Range(0.0f, 1.0f)]
        [Tooltip("Set at runtime by the vehicle.")]
        public float TargetForceRatio = 0.0f;

        /// <summary>
        /// The current thrust force being applied by the thruster, tending towards CurrentForceRatio * MaxForce.
        /// If ForceChangePerSecond is negative, this will equal the target value.
        /// </summary>
        private float _currentForce = 0.0f;

        protected override void FixedUpdate()
        {
            base.FixedUpdate();
            if (_parentBody == null || MaxForce == 0.0f || !Active)
            {
                _currentForce = 0.0f;
                if (_particles != null)
                {
                    _particleEmission.rateOverTime = 0.0f;
                }
                return;
            }
            // Move the current thrust force towards the target value.
            float targetForce = TargetForceRatio * MaxForce;
            if (ForceChangePerSecond < 0.0f)
            {
                _currentForce = targetForce;
            }
            else
            {
                float forceChange = ForceChangePerSecond * Time.fixedDeltaTime;
                _currentForce = Mathf.MoveTowards(_currentForce, targetForce, forceChange);
            }
            if (_particles != null)
            {
                _particleEmission.rateOverTime = 100.0f * Mathf.Abs(_currentForce / MaxForce);
            }
            if (_parentBody == null)
            {
                return;
            }
            // Note: Unity's AddForceAtPosition uses global world coordinates for both parameters.
            _parentBody.AddForceAtPosition(transform.forward * _currentForce, transform.position);
        }

        /// <summary>
        /// Sets the wheel's steering and thrust based on vehicle input.
        /// </summary>
        /// <param name="angularInput">The vehicle's angular input on a range of -1.0 to 1.0.</param>
        /// <param name="linearInput">The vehicle's linear input on a range of -1.0 to 1.0.</param>
        public override void SetFromVehicleInput(Vector3 angularInput, Vector3 linearInput)
        {
            // Set gimbal from vehicle angular input.
            SetGimbalFromVehicleInput(angularInput, linearInput);
            // Set thrust from vehicle linear input.
            Quaternion rotationToBody = _restQuaternionToBody * GetGimbalRotationQuaternion();
            Vector3 thrustDirection = rotationToBody * new Vector3(0.0f, 0.0f, 1.0f);
            TargetForceRatio = Mathf.Clamp01(Vector3.Dot(linearInput, thrustDirection));
        }
    }
}
