using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Vehicles.Main
{
    [RequireComponent(typeof(Rigidbody))]
    public class BasisVehicleBody : MonoBehaviour
    {
        private const float InertiaDampenerRateAngular = 4.0f;
        private const float InertiaDampenerRateLinear = 1.0f;

        /// <summary>
        /// The node to use as the pilot seat / driver seat. A player sitting in this seat will control the vehicle.
        /// </summary>
        [Tooltip("Can be null to set automatically.")]
        public BasisVehiclePilotSeat PilotSeat = null;

        /// <summary>
        /// The input value controlling the ratio of the vehicle's angular forces.
        /// Each axis is on a range of -1.0 to 1.0, the input may be longer than 1.0 overall.
        /// </summary>
        [Tooltip("Each axis is on a range of -1.0 to 1.0.")]
        public Vector3 AngularActivation = Vector3.zero;
        /// <summary>
        /// The input value controlling the ratio of the vehicle's linear forces.
        /// Each axis is on a range of -1.0 to 1.0, the input may be longer than 1.0 overall.
        /// </summary>
        [Tooltip("Each axis is on a range of -1.0 to 1.0.")]
        public Vector3 LinearActivation = Vector3.zero;

        /// <summary>
        /// The gyroscope torque intrinsic to the vehicle, excluding torque from parts, measured in Newton-meters per radian (kg⋅m²/s²/rad).
        /// </summary>
        [Tooltip("N\u22C5m/rad (kg\u22C5m\u00B2/s\u00B2/rad)")]
        public Vector3 GyroscopeTorque = Vector3.zero;

        /// <summary>
        /// If non-negative, the speed in meters per second at which the vehicle should stop driving acceleration further.
        /// If throttle is used, activation is a ratio of this speed if positive, or a ratio of thrust power if negative.
        /// </summary>
        [Tooltip("Negative means no speed limit.")]
        public float MaxSpeed = -1.0f;

        /// <summary>
        /// If true, the vehicle should slow its rotation down when not given angular activation input for a specific rotation.
        /// </summary>
        [Tooltip("Should the vehicle slow its rotation automatically?")]
        public bool AngularDampeners = true;
        /// <summary>
        /// If true, the vehicle should slow itself down when not given linear activation input for a specific direction.
        /// </summary>
        [Tooltip("Should the vehicle slow itself down automatically?")]
        public bool LinearDampeners = true;
        /// <summary>
        /// If true, the vehicle should use a throttle for linear movement. Pilot seat input "sticks around" when let go.
        /// If MaxSpeed is non-negative, the throttle is a ratio of that speed, otherwise it is a ratio of thrust power.
        /// </summary>
        [Tooltip("Persist linear input and use as a ratio of MaxSpeed or thrust power.")]
        public bool UseThrottle = false;

        private List<Parts.BasisVehicleHoverThruster> _hoverThrusters = new List<Parts.BasisVehicleHoverThruster>();
        private List<Parts.BasisVehicleWheel> _wheels = new List<Parts.BasisVehicleWheel>();
        private List<Parts.BasisVehiclePart> _otherParts = new List<Parts.BasisVehiclePart>();
        public Rigidbody rb;
        private void Awake()
        {
            if (PilotSeat != null)
            {
                PilotSeat.PilotedVehicleBody = this;
            }
            rb = GetComponent<Rigidbody>();
        }

        private void FixedUpdate()
        {
            if (rb == null)
            {
                BasisDebug.LogError("BasisVehicleBody: No Rigidbody found on the vehicle body.");
                return;
            }
            Vector3 actualAngular = AngularActivation;
            Vector3 actualLinear = LinearActivation;
            Vector3 localLinearVel = transform.InverseTransformDirection(rb.linearVelocity);
            Vector3 localAngularVel = transform.InverseTransformDirection(rb.angularVelocity);
            Vector3 localUpDirection = -GetLocalGravityDirection();
            // Determine the actual linear values to use based on activation, throttle, and dampeners.
            if (MaxSpeed >= 0.0f)
            {
                // In this case, the throttle should be a ratio of the maximum speed,
                // with the thrust adjusting so that the vehicle meets the target speed.
                Vector3 targetVelocity = MaxSpeed * Vector3.ClampMagnitude(LinearActivation, 1.0f);
                actualLinear = (targetVelocity - localLinearVel) / MaxSpeed;
            }
            else if (!UseThrottle && LinearDampeners)
            {
                if (Mathf.Approximately(actualLinear.x, 0.0f))
                {
                    actualLinear.x = localLinearVel.x * -InertiaDampenerRateLinear;
                }
                if (Mathf.Approximately(actualLinear.y, 0.0f))
                {
                    actualLinear.y = localLinearVel.y * -InertiaDampenerRateLinear;
                }
                if (Mathf.Approximately(actualLinear.z, 0.0f))
                {
                    actualLinear.z = localLinearVel.z * -InertiaDampenerRateLinear;
                }
                if (_hoverThrusters.Count > 0)
                {
                    actualLinear += (LinearActivation != Vector3.zero) ? localUpDirection : localUpDirection * 0.75f;
                }
            }
            // Vehicle wheels should never rotate due to dampeners, because for wheels,
            // pointing straight is a vehicle's best attempt to stop rotating.
            for (int i = 0; i < _wheels.Count; i++)
            {
                _wheels[i].SetFromVehicleInput(actualAngular, actualLinear);
            }
            // Determine the actual angular values to use based on activation and dampeners.
            if (AngularDampeners)
            {
                if (Mathf.Approximately(AngularActivation.x, 0.0f))
                {
                    actualAngular.x = localAngularVel.x * -InertiaDampenerRateAngular;
                }
                if (Mathf.Approximately(AngularActivation.y, 0.0f))
                {
                    actualAngular.y = localAngularVel.y * -InertiaDampenerRateAngular;
                }
                if (Mathf.Approximately(AngularActivation.z, 0.0f))
                {
                    actualAngular.z = localAngularVel.z * -InertiaDampenerRateAngular;
                }
                // Hovercraft, cars, etc should attempt to keep themselves upright.
                if (PilotSeat != null && PilotSeat.DoesPilotSeatWantToKeepUpright())
                {
                    Quaternion toUp = GetRotationToUpright(localUpDirection);
                    Vector3 v = Vector3.ClampMagnitude(new Vector3(toUp.x, 0.0f, toUp.z), 1.0f);
                    // Only apply the upright correction if there's no input for that axis.
                    if (Mathf.Approximately(AngularActivation.x, 0.0f))
                    {
                        actualAngular.x += v.x;
                    }
                    if (Mathf.Approximately(AngularActivation.z, 0.0f))
                    {
                        actualAngular.z += v.z;
                    }
                }
            }
            // Clamp the actual inputs to the range of -1.0 to 1.0 per each axis (can be longer than 1.0 overall).
            // The individual parts (thrusters etc) may clamp these further as needed (such as to a length of 1.0).
            actualAngular = new Vector3(
                Mathf.Clamp(actualAngular.x, -1.0f, 1.0f),
                Mathf.Clamp(actualAngular.y, -1.0f, 1.0f),
                Mathf.Clamp(actualAngular.z, -1.0f, 1.0f)
            );
            actualLinear = new Vector3(
                Mathf.Clamp(actualLinear.x, -1.0f, 1.0f),
                Mathf.Clamp(actualLinear.y, -1.0f, 1.0f),
                Mathf.Clamp(actualLinear.z, -1.0f, 1.0f)
            );
            // Now that we've calculated the actual angular/linear inputs including
            // throttle and dampeners, apply them to everything (except wheels).
            rb.AddTorque(transform.TransformDirection(Vector3.Scale(GyroscopeTorque, actualAngular)), ForceMode.Force);
            for (int i = 0; i < _hoverThrusters.Count; i++)
            {
                _hoverThrusters[i].SetFromVehicleInput(actualAngular, actualLinear);
            }
            for (int i = 0; i < _otherParts.Count; i++)
            {
                _otherParts[i].SetFromVehicleInput(actualAngular, actualLinear);
            }
        }

        public bool HasHoverThrusters()
        {
            return _hoverThrusters.Count > 0;
        }

        public bool HasWheels()
        {
            return _wheels.Count > 0;
        }

        public void RegisterPart(Parts.BasisVehiclePart part)
        {
            if (part is Parts.BasisVehicleHoverThruster hoverThruster)
            {
                _hoverThrusters.Add(hoverThruster);
            }
            else if (part is Parts.BasisVehicleWheel wheel)
            {
                _wheels.Add(wheel);
            }
            else
            {
                _otherParts.Add(part);
            }
        }

        private Quaternion GetRotationToUpright(Vector3 upDirection)
        {
            if (upDirection == Vector3.zero)
            {
                return Quaternion.identity;
            }
            Vector3 x = Vector3.Cross(upDirection, Vector3.forward).normalized;
            Vector3 z = Vector3.Cross(x, upDirection).normalized;
            return Quaternion.LookRotation(z, upDirection);
        }

        private Vector3 GetLocalGravityDirection()
        {
            // TODO: This assumes that gravity is always global, which may change in a future version of Basis.
            return Quaternion.Inverse(transform.rotation) * Physics.gravity.normalized;
        }
    }
}
