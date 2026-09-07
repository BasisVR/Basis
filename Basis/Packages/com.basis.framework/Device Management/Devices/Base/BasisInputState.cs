using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices
{
    /// <summary>
    /// Represents the full state for a single input device (buttons, triggers, 2D axes).
    /// Exposes events that fire when properties change, enabling reactive input handling.
    /// </summary>
    [Serializable]
    public class BasisInputState
    {
        /// <summary>Raised when <see cref="GripButton"/> changes.</summary>
        public event Action OnGripButtonChanged;

        /// <summary>Raised when <see cref="SystemOrMenuButton"/> changes.</summary>
        public event Action OnMenuButtonChanged;

        /// <summary>Raised when <see cref="PrimaryButtonGetState"/> changes.</summary>
        public event Action OnPrimaryButtonGetStateChanged;

        /// <summary>Raised when <see cref="SecondaryButtonGetState"/> changes.</summary>
        public event Action OnSecondaryButtonGetStateChanged;

        /// <summary>Raised when <see cref="Secondary2DAxisClick"/> changes.</summary>
        public event Action OnSecondary2DAxisClickChanged;

        /// <summary>Raised when <see cref="Primary2DAxisClick"/> changes.</summary>
        public event Action OnPrimary2DAxisClickChanged;

        /// <summary>Raised when <see cref="Trigger"/> changes.</summary>
        public event Action OnTriggerChanged;

        /// <summary>Raised when <see cref="SecondaryTrigger"/> changes.</summary>
        public event Action OnSecondaryTriggerChanged;

        /// <summary>Raised when <see cref="Primary2DAxisDeadZoned"/> changes (after deadzone).</summary>
        public event Action OnPrimary2DAxisChanged;

        /// <summary>Raised when <see cref="Secondary2DAxisDeadZoned"/> changes (after deadzone).</summary>
        public event Action OnSecondary2DAxisChanged;

        public event Action OnPrimaryButtonTouchChanged;
        public event Action OnSecondaryButtonTouchChanged;
        public event Action OnPrimary2DAxisTouchChanged;
        public event Action OnSecondary2DAxisTouchChanged;
        public event Action OnTriggerTouchChanged;
        public event Action OnThumbrestTouchChanged;

        [SerializeField] private bool gripButton;
        [SerializeField] private bool menuButton;
        [SerializeField] private bool primaryButtonGetState;
        [SerializeField] private bool secondaryButtonGetState;
        [SerializeField] private bool secondary2DAxisClick; // e.g., desktop scroll wheel click
        [SerializeField] private bool primary2DAxisClick;
        [SerializeField] private float trigger;
        [SerializeField] private float secondaryTrigger;
        [SerializeField] private Vector2 primary2DAxisRaw;
        [SerializeField] private Vector2 secondary2DAxisRaw;
        [SerializeField] private bool primaryButtonTouch, secondaryButtonTouch, primary2DAxisTouch, secondary2DAxisTouch, triggerTouch, thumbrestTouch;
        /// <summary>
        /// Grip (often controller side-button). True while held.
        /// </summary>
        public bool GripButton
        {
            get => gripButton;
            set
            {
                if (gripButton != value)
                {
                    gripButton = value;
                    OnGripButtonChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// System/Menu button state. True while held.
        /// </summary>
        public bool SystemOrMenuButton
        {
            get => menuButton;
            set
            {
                if (menuButton != value)
                {
                    menuButton = value;
                    OnMenuButtonChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Primary face button (e.g., A/X). True while held.
        /// </summary>
        public bool PrimaryButtonGetState
        {
            get => primaryButtonGetState;
            set
            {
                if (primaryButtonGetState != value)
                {
                    primaryButtonGetState = value;
                    OnPrimaryButtonGetStateChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Secondary face button (e.g., B/Y). True while held.
        /// </summary>
        public bool SecondaryButtonGetState
        {
            get => secondaryButtonGetState;
            set
            {
                if (secondaryButtonGetState != value)
                {
                    secondaryButtonGetState = value;
                    OnSecondaryButtonGetStateChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Secondary 2D axis click (e.g., scroll-wheel click). True on press.
        /// </summary>
        public bool Secondary2DAxisClick
        {
            get => secondary2DAxisClick;
            set
            {
                if (secondary2DAxisClick != value)
                {
                    secondary2DAxisClick = value;
                    OnSecondary2DAxisClickChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Primary 2D axis click (e.g., joystick press). True on press.
        /// </summary>
        public bool Primary2DAxisClick
        {
            get => primary2DAxisClick;
            set
            {
                if (primary2DAxisClick != value)
                {
                    primary2DAxisClick = value;
                    OnPrimary2DAxisClickChanged?.Invoke();
                }
            }
        }

        public bool PrimaryButtonTouch
        {
            get => primaryButtonTouch;
            set
            {
                if (primaryButtonTouch != value)
                {
                    primaryButtonTouch = value;
                    OnPrimaryButtonTouchChanged?.Invoke();
                }
            }
        }

        public bool SecondaryButtonTouch
        {
            get => secondaryButtonTouch;
            set
            {
                if (secondaryButtonTouch != value)
                {
                    secondaryButtonTouch = value;
                    OnSecondaryButtonTouchChanged?.Invoke();
                }
            }
        }

        public bool Primary2DAxisTouch
        {
            get => primary2DAxisTouch;
            set
            {
                if (primary2DAxisTouch != value)
                {
                    primary2DAxisTouch = value;
                    OnPrimary2DAxisTouchChanged?.Invoke();
                }
            }
        }

        public bool Secondary2DAxisTouch
        {
            get => secondary2DAxisTouch;
            set
            {
                if (secondary2DAxisTouch != value)
                {
                    secondary2DAxisTouch = value;
                    OnSecondary2DAxisTouchChanged?.Invoke();
                }
            }
        }

        public bool TriggerTouch
        {
            get => triggerTouch;
            set
            {
                if (triggerTouch != value)
                {
                    triggerTouch = value;
                    OnTriggerTouchChanged?.Invoke();
                }
            }
        }

        public bool ThumbrestTouch
        {
            get => thumbrestTouch;
            set
            {
                if (thumbrestTouch != value)
                {
                    thumbrestTouch = value;
                    OnThumbrestTouchChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Primary analog trigger value in [0..1].
        /// </summary>
        public float Trigger
        {
            get => trigger;
            set
            {
                if (Math.Abs(trigger - value) > 0.0001f)
                {
                    trigger = value;
                    OnTriggerChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Secondary analog trigger value in [0..1].
        /// </summary>
        public float SecondaryTrigger
        {
            get => secondaryTrigger;
            set
            {
                if (Math.Abs(secondaryTrigger - value) > 0.0001f)
                {
                    secondaryTrigger = value;
                    OnSecondaryTriggerChanged?.Invoke();
                }
            }
        }
        public Vector2 Primary2DAxisRaw
        {
            get => primary2DAxisRaw;
            set
            {
                if (primary2DAxisRaw != value)
                {
                    primary2DAxisRaw = value;
                    OnPrimary2DAxisChanged?.Invoke();
                }
            }
        }

        public Vector2 Secondary2DAxisRaw
        {
            get => secondary2DAxisRaw;
            set
            {
                if (secondary2DAxisRaw != value)
                {
                    secondary2DAxisRaw = value;
                    OnSecondary2DAxisChanged?.Invoke();
                }
            }
        }
        public Vector2 Primary2DAxisDeadZoned
        {
            get =>
                ApplyNormalDeadzone(
                    primary2DAxisRaw,
                    SMModuleControllerSettings.JoyStickDeadZone
                );
        }

        public Vector2 Secondary2DAxisDeadZoned
        {
            get =>
                ApplyNormalDeadzone(
                    secondary2DAxisRaw,
                    SMModuleControllerSettings.JoyStickDeadZone
                );
        }
        public Vector2 Primary2DAxisButterfly
        {
            get =>
                ButterflyGate(
                    primary2DAxisRaw,
                    SMModuleControllerSettings.baseXDeadzone,
                    SMModuleControllerSettings.extraXDeadzoneAtFullY,
                    SMModuleControllerSettings.yDeadzone,
                    SMModuleControllerSettings.wingExponent
                );
        }

        public Vector2 Secondary2DAxisButterfly
        {
            get =>
                ButterflyGate(
                    secondary2DAxisRaw,
                    SMModuleControllerSettings.baseXDeadzone,
                    SMModuleControllerSettings.extraXDeadzoneAtFullY,
                    SMModuleControllerSettings.yDeadzone,
                    SMModuleControllerSettings.wingExponent
                );
        }
        // Apply a deadzone to a single axis and remap to [0..1] outside the deadzone.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ApplyDeadzone(float v, float dz)
        {
            float av = Mathf.Abs(v);
            if (av <= dz) return 0f;
            float sign = Mathf.Sign(v);
            // Remap so output hits 1 at the edge
            return sign * ((av - dz) / (1f - dz));
        }

        /// <summary>
        /// "Butterfly wings" gating: as |Y| increases, X deadzone increases.
        /// Keeps horizontal look precise even when stick is pushed forward.
        /// </summary>
        public static Vector2 ButterflyGate(Vector2 stick,
                                            float baseXDeadzone = 0.08f,
                                            float extraXDeadzoneAtFullY = 0.35f,
                                            float yDeadzone = 0.10f,
                                            float wingExponent = 1.6f)
        {
            // Optional: deadzone Y normally (useful if you also use Y for something)
            float y = ApplyDeadzone(stick.y, yDeadzone);

            // Grow X deadzone as you push on Y (|y| in [0..1])
            float yInfluence = Mathf.Pow(Mathf.Clamp01(Mathf.Abs(y)), wingExponent);
            float xDeadzone = Mathf.Clamp01(baseXDeadzone + extraXDeadzoneAtFullY * yInfluence);

            float x = ApplyDeadzone(stick.x, xDeadzone);

            return new Vector2(x, y);
        }
        /// <summary>
        /// Applies a circular deadzone to a 2D input vector.
        /// </summary>
        /// <param name="input">Raw input vector.</param>
        /// <param name="deadzoneThreshold">Magnitude below which input is treated as zero.</param>
        /// <returns>Zeroed vector if under threshold; original vector otherwise.</returns>
        public Vector2 ApplyNormalDeadzone(Vector2 input, float deadzoneThreshold)
        {
            if (input.magnitude < deadzoneThreshold)
            {
                return Vector2.zero;
            }
            return input;
        }

        /// <summary>
        /// Copies this state into <paramref name="target"/> and triggers appropriate change events in the target.
        /// </summary>
        /// <param name="target">Destination state.</param>
        public void CopyTo(BasisInputState target)
        {
            target.GripButton = this.GripButton;
            target.SystemOrMenuButton = this.SystemOrMenuButton;
            target.PrimaryButtonGetState = this.PrimaryButtonGetState;
            target.SecondaryButtonGetState = this.SecondaryButtonGetState;
            target.Secondary2DAxisClick = this.Secondary2DAxisClick;
            target.Primary2DAxisClick = this.Primary2DAxisClick;
            target.PrimaryButtonTouch = this.PrimaryButtonTouch;
            target.SecondaryButtonTouch = this.SecondaryButtonTouch;
            target.Primary2DAxisTouch = this.Primary2DAxisTouch;
            target.Secondary2DAxisTouch = this.Secondary2DAxisTouch;
            target.TriggerTouch = this.TriggerTouch;
            target.ThumbrestTouch = this.ThumbrestTouch;
            target.Trigger = this.Trigger;
            target.SecondaryTrigger = this.SecondaryTrigger;
            target.primary2DAxisRaw = this.primary2DAxisRaw;
            target.Secondary2DAxisRaw = this.Secondary2DAxisRaw;
        }

        /// <summary>
        /// OR/max-merges another device's state into this one. Used to combine multiple devices
        /// that share a hand role into a single aggregated dispatch, so an edge action fires once
        /// for the role instead of once per device. Booleans are OR'd, triggers take the max, and
        /// each 2D axis takes the larger-magnitude of the two so an idle second device does not
        /// cancel out the active one.
        /// </summary>
        /// <param name="other">The state to merge into this one.</param>
        public void MergeFrom(BasisInputState other)
        {
            GripButton |= other.gripButton;
            SystemOrMenuButton |= other.menuButton;
            PrimaryButtonGetState |= other.primaryButtonGetState;
            SecondaryButtonGetState |= other.secondaryButtonGetState;
            Secondary2DAxisClick |= other.secondary2DAxisClick;
            Primary2DAxisClick |= other.primary2DAxisClick;
            PrimaryButtonTouch |= other.primaryButtonTouch;
            SecondaryButtonTouch |= other.secondaryButtonTouch;
            Primary2DAxisTouch |= other.primary2DAxisTouch;
            Secondary2DAxisTouch |= other.secondary2DAxisTouch;
            TriggerTouch |= other.triggerTouch;
            ThumbrestTouch |= other.thumbrestTouch;
            Trigger = Mathf.Max(trigger, other.trigger);
            SecondaryTrigger = Mathf.Max(secondaryTrigger, other.secondaryTrigger);
            if (other.primary2DAxisRaw.sqrMagnitude > primary2DAxisRaw.sqrMagnitude)
            {
                primary2DAxisRaw = other.primary2DAxisRaw;
            }
            if (other.secondary2DAxisRaw.sqrMagnitude > secondary2DAxisRaw.sqrMagnitude)
            {
                secondary2DAxisRaw = other.secondary2DAxisRaw;
            }
        }
    }
}
