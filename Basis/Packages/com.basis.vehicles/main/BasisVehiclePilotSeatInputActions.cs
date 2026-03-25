using Basis.Scripts.Common;
using System;
using UnityEngine.InputSystem;

namespace Basis.Scripts.Vehicles.Main
{
    public sealed class BasisVehiclePilotSeatInputActions : IDisposable
    {
        public static BasisVehiclePilotSeatInputActions Instance { get; } = new BasisVehiclePilotSeatInputActions();
        private readonly BasisLocks.LockContext LookRotationLock = BasisLocks.GetContext(BasisLocks.LookRotation);

        // Actions (public so other systems can subscribe: action.performed += ...)
        public readonly InputAction RotateRoll;
        public readonly InputAction ThrottleZero;
        public readonly InputAction ToggleAngularDampeners;
        public readonly InputAction ToggleLinearDampeners;

        private bool _isConnected;

        public BasisVehiclePilotSeatInputActions()
        {
            // Actions (names match the config)
            RotateRoll = new InputAction(
                name: "RotateRoll",
                type: InputActionType.Value,
                binding: null,
                interactions: null,
                processors: null,
                expectedControlType: "Axis"
            );
            ThrottleZero = new InputAction(
                name: "ThrottleZero",
                type: InputActionType.Button
            );
            ToggleAngularDampeners = new InputAction(
                name: "ToggleAngularDampeners",
                type: InputActionType.Button
            );
            ToggleLinearDampeners = new InputAction(
                name: "ToggleLinearDampeners",
                type: InputActionType.Button
            );
        }

        /// <summary>
        /// Adds bindings and composites exactly as described by the config snippet.
        /// Safe to call multiple times; subsequent calls do nothing.
        /// </summary>
        public void Connect()
        {
            if (_isConnected)
            {
                return;
            }
            _isConnected = true;
            // ToggleLinearDampeners: <Keyboard>/z (like Space Engineers), <Keyboard>/p (for "park")
            ToggleLinearDampeners.AddBinding("<Keyboard>/z").WithGroups(";All");
            ToggleLinearDampeners.AddBinding("<Keyboard>/p").WithGroups(";All");
            // ToggleAngularDampeners: <Keyboard>/semicolon (below "P" for "park")
            ToggleAngularDampeners.AddBinding("<Keyboard>/semicolon").WithGroups(";All");
            // ThrottleZero: <Keyboard>/x
            ThrottleZero.AddBinding("<Keyboard>/x").WithGroups(";All");
            // RotateRoll composites:
            // "QE" 1DAxis: positive=<Keyboard>/q, negative=<Keyboard>/e
            RotateRoll.AddCompositeBinding("1DAxis")
                .With("positive", "<Keyboard>/q")
                .With("negative", "<Keyboard>/e");
            // "UO" 1DAxis: positive=<Keyboard>/u, negative=<Keyboard>/o
            RotateRoll.AddCompositeBinding("1DAxis")
                .With("positive", "<Keyboard>/u")
                .With("negative", "<Keyboard>/o");
            // "DPadLR" 1DAxis: positive=<Gamepad>/dpad/left, negative=<Gamepad>/dpad/right
            RotateRoll.AddCompositeBinding("1DAxis")
                .With("positive", "<Gamepad>/dpad/left")
                .With("negative", "<Gamepad>/dpad/right");
        }

        public void EnableAll(bool lockLookRotation)
        {
            Connect(); // Only connects once.
            RotateRoll.Enable();
            ThrottleZero.Enable();
            ToggleAngularDampeners.Enable();
            ToggleLinearDampeners.Enable();
            if (lockLookRotation)
            {
                LookRotationLock.Add(nameof(BasisVehiclePilotSeatInputActions));
            }
        }

        public void DisableAll()
        {
            RotateRoll.Disable();
            ThrottleZero.Disable();
            ToggleAngularDampeners.Disable();
            ToggleLinearDampeners.Disable();
            LookRotationLock.Remove(nameof(BasisVehiclePilotSeatInputActions));
        }

        public void Dispose()
        {
            DisableAll();
            RotateRoll.Dispose();
            ThrottleZero.Dispose();
            ToggleAngularDampeners.Dispose();
            ToggleLinearDampeners.Dispose();
        }

        public bool IsPilotSeatOnlyLockerOfLookRotation()
        {
            return LookRotationLock.ContainsOnly(nameof(BasisVehiclePilotSeatInputActions));
        }
    }
}
