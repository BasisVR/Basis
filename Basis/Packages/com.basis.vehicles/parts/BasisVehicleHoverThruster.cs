using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    /// <summary>
    /// Hover thruster for vehicles, used for hovercraft thrust. This is not realistic, it is sci-fi.
    /// </summary>
    public class BasisVehicleHoverThruster : BasisVehicleGimbalablePart
    {
        /// <summary>
        /// Controls how much the vehicle's angular input should affect the hover ratio.
        /// 0 = none, 1 or more = too much stabilization (overcorrection/bounciness).
        /// </summary>
        private const float TorqueStabilization = 0.5f;

        [Header("Hover Thrust")]
        /// <summary>
        /// The maximum hover energy in Newton-meters (N⋅m or kg⋅m²/s²) that the hover thruster can provide. Must not be negative.
        /// </summary>
        [Tooltip("N\u22C5m (kg\u22C5m\u00B2/s\u00B2)")]
        public float MaxHoverEnergy = 0.0f;

        /// <summary>
        /// The speed at which the hover energy changes, in Newtons-meters per second. If negative, the force changes instantly.
        /// </summary>
        [Tooltip("Negative means instant change.")]
        public float HoverEnergyChangePerSecond = -1.0f;

        /// <summary>
        /// The ratio of the maximum hover energy the hover thruster is using for propulsion.
        /// </summary>
        [Range(0.0f, 1.0f)]
        [Tooltip("Set at runtime by the vehicle.")]
        public float TargetHoverRatio = 0.0f;

        /// <summary>
        /// The current hover energy being applied by the thruster, tending towards CurrentHoverRatio * MaxHoverEnergy.
        /// If HoverEnergyChangePerSecond is negative, this will equal the target value.
        /// </summary>
        private float _currentHoverEnergy = 0.0f;

        protected override void FixedUpdate()
        {
            base.FixedUpdate();
            if (_parentBody == null || !Active)
            {
                _currentHoverEnergy = 0.0f;
                if (_particles != null)
                {
                    _particleEmission.rateOverTime = 0.0f;
                }
                return;
            }
            // Move the current hover energy towards the target value.
            float targetHoverEnergy = Mathf.Clamp01(TargetHoverRatio) * MaxHoverEnergy;
            if (HoverEnergyChangePerSecond < 0.0f)
            {
                _currentHoverEnergy = targetHoverEnergy;
            }
            else
            {
                float hoverEnergyChange = HoverEnergyChangePerSecond * Time.fixedDeltaTime;
                _currentHoverEnergy = Mathf.MoveTowards(_currentHoverEnergy, targetHoverEnergy, hoverEnergyChange);
            }
            if (_particles != null)
            {
                _particleEmission.rateOverTime = 100.0f * Mathf.Abs(_currentHoverEnergy) / MaxHoverEnergy;
            }
            // Perform a raycast to determine how far away the ground is. Hover thrusters should naturally
            // provide more thrust the closer they are to the ground, and less thrust the farther they are.
            Vector3 rayOrigin = transform.position;
            // Unity uses +Z for forward, which maps to glTF +Z object front. The thruster
            // thrusts in the +Z direction, which means the "nozzle" points in -Z.
            Vector3 rayDir = -transform.forward;
            RaycastHit hit;
            if (!Physics.Raycast(rayOrigin, rayDir, out hit, 1000.0f))
            {
                return;
            }
            float hitDistance = Mathf.Max(hit.distance, 0.01f); // Avoid division by zero or near-zero.
            float hoverForce = _currentHoverEnergy / hitDistance; // N = Nm / m
            // Note: Unity's AddForceAtPosition uses global world coordinates for both parameters.
            _parentBody.AddForceAtPosition(transform.forward * hoverForce, transform.position);
        }

        public override void SetFromVehicleInput(Vector3 angularInput, Vector3 linearInput)
        {
            SetGimbalFromVehicleInput(angularInput, linearInput);
            // Set the hover ratio based on angular and linear input.
            Vector3 thrustDir = _restQuaternionToBody * GetGimbalRotationQuaternion() * new Vector3(0, 0, 1);
            float thrustHover = Mathf.Max(Vector3.Dot(linearInput, thrustDir), 0.0f);
            Vector3 torque = Vector3.Cross(_parentTransformToBody * transform.localPosition, thrustDir);
            float torqueHover = Mathf.Max(Vector3.Dot(angularInput, torque) * TorqueStabilization, 0.0f);
            TargetHoverRatio = Mathf.Clamp01(thrustHover + torqueHover);
        }

        public BasisVehicleHoverThruster()
        {
            // Default hover thrusters to having some linear gimbal adjustment.
            LinearGimbalAdjustRatio = 0.5f;
        }
    }
}
