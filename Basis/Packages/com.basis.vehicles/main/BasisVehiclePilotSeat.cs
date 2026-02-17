using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.Scripts.Vehicles.Main
{
    /// <summary>
    /// A BasisSeat designed for its occupant to pilot a BasisVehicleBody node.
    /// </summary>
    public class BasisVehiclePilotSeat : BasisSdk.Interactions.BasisSeat
    {
        /// <summary>
        /// The control schemes supported by the pilot seat. Each member's summary refers to keyboard/mouse
        /// for simplicity, but see the Basis input mapping for the actual bound controls on various devices.
        /// </summary>
        public enum ControlScheme
        {
            /// <summary>
            /// Automatically determine the control scheme based on the vehicle's components.
            /// Throttled vehicles default to Navball, else wheeled vehicles default to Car,
            /// else hover thruster vehicles default to SixDofHorizontal, else SixDof.
            /// </summary>
            Auto,
            /// <summary>
            /// No controls; the vehicle will ignore pilot input.
            /// </summary>
            None,
            /// <summary>
            /// Uses WS for forward/back movement and AD for steering, like most driving games.
            /// Also, use RF for pitch, and QEUO for roll, if the car has such capabilities.
            /// </summary>
            Car,
            /// <summary>
            /// Uses WASDRF for linear movement, QE roll, mouse pitch/yaw, or IJKLUO rotation, like Space Engineers.
            /// </summary>
            SixDof,
            /// <summary>
            /// Like SixDof but flattens the horizontal WASDRF input, like Minecraft creative mode.
            /// This is good for Empyrion-style hovercraft control, but you may want to use Car instead.
            /// </summary>
            SixDofHorizontal,
            /// <summary>
            /// Uses WASDQE for rotation, with W as up and S as down. The pitch is inverted compared to flight sims.
            /// Also, RF are either forward/backward or throttle up/down.
            /// </summary>
            Navball,
            /// <summary>
            /// Uses WASDQE for rotation, with W as down and S as up, like Kerbal Space Program and flight sims.
            /// Also, RF are either forward/backward or throttle up/down.
            /// </summary>
            NavballInverted,
        }

        private const float ThrottleRate = 0.5f;

        [Header("Pilot Seat Settings")]
        /// <summary>
        /// The control scheme to use. More can be added by editing BasisVehiclePilotSeat.cs.
        /// </summary>
        [Tooltip("Auto considers the vehicle's parts.")]
        public ControlScheme controlScheme = ControlScheme.Auto;

        /// <summary>
        /// Will be set automatically when <see cref="EnterPilotSeat"/> is called.
        /// Can also be overridden for custom use cases.
        /// </summary>
        [Tooltip("Set when the local player enters the seat.")]
        public bool UseLocalControls = false;

        private BasisVehicleBody _pilotedVehicleBody = null;
        /// <summary>
        /// The vehicle body being piloted.
        /// </summary>
        public BasisVehicleBody PilotedVehicleBody
        {
            get { return _pilotedVehicleBody; }
            set
            {
                _pilotedVehicleBody = value;
                if (value != null && value.PilotSeat != this)
                {
                    value.PilotSeat = this;
                }
            }
        }

        /// <summary>
        /// Should be set at runtime when a player enters the pilot seat via <see cref="EnterPilotSeat"/>
        /// </summary>
        [Tooltip("Leave blank, this is set at runtime.")]
        public BasisPlayer PilotingPlayer = null;
        public override void Awake()
        {
            ResetPitchOnEntry = true;
            base.Awake();
            if (_pilotedVehicleBody == null)
            {
                Transform parent = transform.parent;
                if (parent != null)
                {
                    _pilotedVehicleBody = parent.GetComponent<BasisVehicleBody>();
                }
            }
            if (_pilotedVehicleBody != null)
            {
                _pilotedVehicleBody.PilotSeat = this;
            }
            else
            {
                BasisDebug.LogError("BasisVehiclePilotSeat should be a direct child of a GameObject with a BasisVehicleBody component.");
            }
            OnLocalPlayerEnterSeat += EnterPilotSeat;
            OnLocalPlayerExitSeat += ExitPilotSeat;
        }

        private void FixedUpdate()
        {
            if (!UseLocalControls || _pilotedVehicleBody == null)
            {
                return;
            }
            ControlScheme actualControlScheme = GetActualControlScheme();
            Vector3 angularInput = GetAngularInput(actualControlScheme);
            Vector3 linearInput = GetLinearInput(actualControlScheme);
            _pilotedVehicleBody.AngularActivation = angularInput;
            bool throttleZero = BasisVehiclePilotSeatInputActions.Instance.ThrottleZero.ReadValue<bool>();
            if (throttleZero)
            {
                _pilotedVehicleBody.LinearActivation = Vector3.zero;
            }
            else if (_pilotedVehicleBody.UseThrottle)
            {
                Vector3 change = ThrottleRate * linearInput * Time.fixedDeltaTime;
                _pilotedVehicleBody.LinearActivation = Vector3.ClampMagnitude(_pilotedVehicleBody.LinearActivation + change, 1.0f);
            }
            else
            {
                _pilotedVehicleBody.LinearActivation = linearInput;
            }
        }

        public void EnterPilotSeat(BasisPlayer player)
        {
            Debug.Assert(player != null);
            PilotingPlayer = player;
            UseLocalControls = player.IsLocal;
            if (UseLocalControls)
            {
                var vehicleInput = BasisVehiclePilotSeatInputActions.Instance;
                vehicleInput.EnableAll(DoesPilotSeatNeedMouseInput());

                vehicleInput.ThrottleZero.performed += OnThrottleZero;
                vehicleInput.ToggleAngularDampeners.performed += OnToggleAngular;
                vehicleInput.ToggleLinearDampeners.performed += OnToggleLinear;
            }
        }
        private void OnThrottleZero(UnityEngine.InputSystem.InputAction.CallbackContext ctx) => SetThrottleToZero();
        private void OnToggleAngular(UnityEngine.InputSystem.InputAction.CallbackContext ctx) => ToggleAngularDampeners();
        private void OnToggleLinear(UnityEngine.InputSystem.InputAction.CallbackContext ctx) => TogggleLinearDampeners();

        public void ExitPilotSeat(BasisPlayer player)
        {
            if (PilotingPlayer != player)
            {
                return;
            }
            PilotingPlayer = null;
            if (UseLocalControls)
            {
                var vehicleInput = BasisVehiclePilotSeatInputActions.Instance;

                vehicleInput.ThrottleZero.performed -= OnThrottleZero;
                vehicleInput.ToggleAngularDampeners.performed -= OnToggleAngular;
                vehicleInput.ToggleLinearDampeners.performed -= OnToggleLinear;

                vehicleInput.DisableAll();
            }
            UseLocalControls = false;
            // Zero out activations on exit (to avoid cars with the gas pedal stuck down).
            if (_pilotedVehicleBody != null)
            {
                _pilotedVehicleBody.AngularActivation = Vector3.zero;
                _pilotedVehicleBody.LinearActivation = Vector3.zero;
            }
        }

        public void SetThrottleToZero()
        {
            if (_pilotedVehicleBody != null)
            {
                _pilotedVehicleBody.AngularActivation = Vector3.zero;
                _pilotedVehicleBody.LinearActivation = Vector3.zero;
            }
        }

        public void ToggleAngularDampeners()
        {
            if (_pilotedVehicleBody != null)
            {
                _pilotedVehicleBody.AngularDampeners = !_pilotedVehicleBody.AngularDampeners;
            }
        }

        public void TogggleLinearDampeners()
        {
            if (_pilotedVehicleBody != null)
            {
                _pilotedVehicleBody.LinearDampeners = !_pilotedVehicleBody.LinearDampeners;
            }
        }

        public bool DoesPilotSeatNeedMouseInput()
        {
            // 6DoF control schemes need a desktop user's mouse input for pitch/yaw.
            ControlScheme actualControlScheme = GetActualControlScheme();
            return actualControlScheme == ControlScheme.SixDof || actualControlScheme == ControlScheme.SixDofHorizontal;
        }

        public bool DoesPilotSeatWantToKeepUpright()
        {
            ControlScheme actualControlScheme = GetActualControlScheme();
            return actualControlScheme == ControlScheme.Car || actualControlScheme == ControlScheme.SixDofHorizontal;
        }

        /// <summary>
        /// Throttled vehicles default to Navball, else wheeled vehicles default to Car,
        /// else hover thruster vehicles default to SixDofHorizontal, else SixDof.
        /// </summary>
        /// <returns>The actual control scheme to use (anything but Auto).</returns>
        private ControlScheme GetActualControlScheme()
        {
            if (controlScheme != ControlScheme.Auto)
            {
                return controlScheme;
            }
            if (_pilotedVehicleBody.UseThrottle)
            {
                return ControlScheme.Navball;
            }
            if (_pilotedVehicleBody.HasWheels())
            {
                return ControlScheme.Car;
            }
            if (_pilotedVehicleBody.HasHoverThrusters())
            {
                return ControlScheme.SixDofHorizontal;
            }
            return ControlScheme.SixDof;
        }

        /// <summary>
        /// Gets the angular input based on the current control scheme.
        /// The details of this function are Basis-specific, considered unstable, and subject to change.
        /// </summary>
        /// <param name="actualControlScheme">The actual control scheme being used.</param>
        /// <returns>The angular input vector.</returns>
        private Vector3 GetAngularInput(ControlScheme actualControlScheme)
        {
            if (actualControlScheme == ControlScheme.None)
            {
                return Vector3.zero;
            }
            Vector2 rawCharRot = BasisLocalPlayer.Instance.LocalCharacterDriver.Rotation;
            if (DoesPilotSeatNeedMouseInput() && BasisVehiclePilotSeatInputActions.Instance.IsPilotSeatOnlyLockerOfLookRotation())
            {
                rawCharRot += Device_Management.Devices.Desktop.BasisDesktopEye.Instance.LookRotationVector * 2.0f;
            }
            Vector2 charRot = StretchToSquare(Vector2.ClampMagnitude(rawCharRot, 1.0f));
            Vector2 charMove = StretchToSquare(BasisLocalPlayer.Instance.LocalCharacterDriver.MovementVector);
            float roll = BasisVehiclePilotSeatInputActions.Instance.RotateRoll.ReadValue<float>();
            switch (actualControlScheme)
            {
                case ControlScheme.Navball:
                    return new Vector3(-charMove.y, charMove.x, roll);
                case ControlScheme.NavballInverted:
                    return new Vector3(charMove.y, charMove.x, roll);
                default: break;
            }
            return new Vector3(-charRot.y, charRot.x, roll);
        }

        /// <summary>
        /// Gets the linear input based on the current control scheme.
        /// The details of this function are Basis-specific, considered unstable, and subject to change.
        /// </summary>
        /// <param name="actualControlScheme">The actual control scheme being used.</param>
        /// <returns>The linear input vector.</returns>
        private Vector3 GetLinearInput(ControlScheme actualControlScheme)
        {
            if (actualControlScheme == ControlScheme.None)
            {
                return Vector3.zero;
            }
            Vector2 charMove = StretchToSquare(BasisLocalPlayer.Instance.LocalCharacterDriver.MovementVector);
            float vert = BasisLocalPlayer.Instance.LocalCharacterDriver.GetVerticalMovement();
            switch (actualControlScheme)
            {
                case ControlScheme.Navball:
                    return new Vector3(0.0f, 0.0f, vert);
                case ControlScheme.NavballInverted:
                    return new Vector3(0.0f, 0.0f, vert);
                case ControlScheme.SixDofHorizontal:
                    {
                        Quaternion vehicleRotation = _pilotedVehicleBody.transform.rotation;
                        Vector3 euler = vehicleRotation.eulerAngles;
                        Quaternion flatten = Quaternion.Euler(-euler.x, 0.0f, -euler.z);
                        Vector3 input = new Vector3(charMove.x, vert, charMove.y);
                        return flatten * input;
                    }
                default: break;
            }
            return new Vector3(charMove.x, vert, charMove.y);
        }

        /// <summary>
        /// Stretches a Vector2 that was limited to a circle into a square in a continuous way.
        /// </summary>
        /// <param name="vector">The circular-limited vector.</param>
        /// <returns>The square-limited vector.</returns>
        private static Vector2 StretchToSquare(Vector2 vector)
        {
            if (vector == Vector2.zero)
            {
                return vector;
            }
            // scale = 1.0 / max(abs(x), abs(y))
            float scale = 1.0f / Mathf.Max(Mathf.Abs(vector.x), Mathf.Abs(vector.y));
            return vector * (scale * vector.magnitude);
        }
    }
}
