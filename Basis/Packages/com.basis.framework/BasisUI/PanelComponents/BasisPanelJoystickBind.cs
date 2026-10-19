using Unity.Scripting.LifecycleManagement;
using System;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Hands one panel slider to a thumbstick, so a value can be tuned while looking at what it
    /// changes instead of at the menu. The bind is taken from the slider's own options window —
    /// the same hover gesture that offers a reset (<see cref="BasisPanelResetGesture"/>) — and the
    /// hand that opened that window owns the stick: forward raises the value, back lowers it, for
    /// as long as the bind is held.
    ///
    /// <para>
    /// The bind is kept on the slider's <see cref="BasisSettingsBinding{T}"/> rather than the
    /// control, because the control is destroyed the moment the menu closes — which is exactly when
    /// the stick becomes useful. A rebuilt page re-adopts its slider through
    /// <see cref="NotifySliderCreated"/> so the handle tracks the stick again when the menu returns.
    /// </para>
    ///
    /// <para>
    /// A sweep is treated like a drag: the value moves live but only reaches disk when the stick
    /// comes back to centre. See <see cref="ApplyLive"/> and <see cref="Commit"/>.
    /// </para>
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisPanelJoystickBind
    {
        /// <summary>
        /// Deflection below this reads as a stick at rest. Deliberately well past the pointer
        /// deadzone: a bound slider answers a resting hand for as long as the bind lives, so
        /// hardware drift that is harmless for walking would otherwise wander the value all day.
        /// </summary>
        private const float StickDeadzone = 0.2f;

        /// <summary>Shortest sweep honoured, so a tiny sweep time can't make the stick uncontrollable.</summary>
        private const float MinimumSweepSeconds = 0.25f;

        /// <summary>How often a sweep tells the setting's listeners. See <see cref="ApplyLive"/>.</summary>
        private const float LiveApplyInterval = 0.1f;

        private static PanelSlider _slider;
        private static BasisSettingsBinding<float> _binding;
        private static BasisBoneTrackedRole _role;
        private static string _label;
        private static float _min;
        private static float _max;
        private static bool _wholeNumbers;

        private static bool _ticking;
        private static bool _sweeping;
        private static float _liveValue;
        private static float _nextLiveApply;

        /// <summary>True while a stick is driving something.</summary>
        public static bool HasBinding => _binding != null || _slider != null;

        /// <summary>Raised when the bind is taken or dropped, for anything showing its state.</summary>
        public static event Action StateChanged;

        /// <summary>Gives the stick back. A sweep in progress is written out first rather than lost.</summary>
        public static void Clear()
        {
            if (!HasBinding) return;
            if (_sweeping) Commit();
            Drop();
        }

        /// <summary>True when <paramref name="slider"/> is the one being driven.</summary>
        public static bool IsBound(PanelSlider slider) => slider != null && ReferenceEquals(slider, _slider);

        /// <summary>
        /// True while this role's stick belongs to a slider. Read by the locomotion actions, which
        /// drop the forward/back axis for that hand so tuning a value doesn't also walk the player.
        /// </summary>
        public static bool CapturesStick(BasisBoneTrackedRole role) => HasBinding && role == _role;

        /// <summary>
        /// Puts this slider's thumbstick option on its open options window: bind it to the hand that
        /// opened the window, or give the stick back when that hand already has it. Adds nothing
        /// when there is no stick behind the gesture — desktop, or a pointer that isn't a hand — or
        /// when a stick could not drive the slider, so the window never offers what it can't do.
        ///
        /// <para>
        /// The hand is resolved here, while the gesture that opened the window is still known, and
        /// the button remembers it: the window outlives the gesture by however long it is left up.
        /// </para>
        /// </summary>
        public static void AddDialogueOption(PanelSlider slider, BasisMenuDialoguePanel dialogue)
        {
            if (slider == null || dialogue == null) return;

            if (IsBound(slider))
            {
                dialogue.EnableAlternate(BasisLocalization.Get("ui.joystickBind.unbind"), Clear);
                return;
            }

            if (!CanBind(slider)) return;

            BasisInput input = BasisPanelResetGesture.GestureDevice;
            if (input == null || !input.TryGetRole(out BasisBoneTrackedRole role)) return;
            if (role != BasisBoneTrackedRole.LeftHand && role != BasisBoneTrackedRole.RightHand) return;

            // "This" rather than the hand's name: the window was opened by the hand that is about
            // to own the stick, so there is nothing to disambiguate.
            dialogue.EnableAlternate(BasisLocalization.Get("ui.joystickBind.bind"), () => Bind(slider, role));
        }

        /// <summary>
        /// True when a stick could drive <paramref name="slider"/> at all: it has to be usable, and
        /// it has to have a range to move through.
        /// </summary>
        private static bool CanBind(PanelSlider slider) =>
            slider != null && slider.IsInteractable && slider.Settings.SliderMax > slider.Settings.SliderMin;

        /// <summary>
        /// Hands <paramref name="slider"/> to <paramref name="role"/>'s stick, replacing whatever
        /// that stick was driving before — only one slider is bound at a time, and the newer bind is
        /// the one just asked for.
        /// </summary>
        private static void Bind(PanelSlider slider, BasisBoneTrackedRole role)
        {
            if (!CanBind(slider)) return;

            // A sweep still running on the outgoing slider is written out rather than dropped.
            if (_sweeping) Commit();

            Capture(slider, role);

            // Felt on the hand that now owns the stick, which is not necessarily the hand that
            // pressed the button in the window.
            ResolveDevice(role)?.PlayHaptic(0.1f, 0.6f, 0.5f);
        }

        /// <summary>
        /// Re-adopts a slider rebuilt on the same setting the stick is already driving, so a
        /// reopened menu shows the live handle instead of one frozen where the page left it.
        /// </summary>
        public static void NotifySliderCreated(PanelSlider slider)
        {
            if (slider == null || _binding == null) return;
            if (!ReferenceEquals(slider.SettingsBinding, _binding)) return;
            if (ReferenceEquals(slider, _slider)) return;

            _slider = slider;
            ReadRange();
            if (_sweeping) _slider.SetExternalDrive(true);
            StateChanged?.Invoke();
        }

        /// <summary>One line naming what the stick is driving, for the controller settings.</summary>
        public static string StatusText
        {
            get
            {
                if (!HasBinding) return BasisLocalization.Get("settings.controls.joystickBind.status.none");

                string label = string.IsNullOrEmpty(_label)
                    ? BasisLocalization.Get("settings.controls.joystickBind.status.unnamed")
                    : _label;
                return string.Format(BasisLocalization.Get("settings.controls.joystickBind.status.bound"), label, HandName(_role));
            }
        }

        /// <summary>
        /// What a hovered control should add to its tooltip: a reminder of which stick moves it.
        /// Null for anything not on a stick right now — the offer to take one lives in the control's
        /// options window, which the reset hint already points at.
        /// </summary>
        public static string HintFor(PanelComponent component)
        {
            PanelSlider slider = component as PanelSlider;
            if (slider == null || !IsBound(slider)) return null;
            return string.Format(BasisLocalization.Get("ui.joystickBind.hint.bound"), HandName(_role));
        }

        private static string HandName(BasisBoneTrackedRole role) =>
            BasisLocalization.Get(role == BasisBoneTrackedRole.LeftHand ? "ui.hand.left" : "ui.hand.right");

        private static void Capture(PanelSlider slider, BasisBoneTrackedRole role)
        {
            _slider = slider;
            _binding = slider.SettingsBinding;
            _role = role;
            _label = slider.Descriptor != null ? slider.Descriptor.Title : null;
            _sweeping = false;
            ReadRange();

            StartTicking();
            StateChanged?.Invoke();
        }

        private static void Drop()
        {
            _slider = null;
            _binding = null;
            _label = null;
            _sweeping = false;
            StopTicking();
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Takes the range from the live control. Re-read every tick: a bound ceiling can move
        /// while the panel is up (<see cref="BasisAudioRangeSliderLimit"/>), and a slider handed
        /// its settings after its binding would otherwise be driven against the prefab's range.
        /// </summary>
        private static void ReadRange()
        {
            if (_slider == null) return;
            PanelSlider.SliderSettings settings = _slider.Settings;
            if (settings.SliderMax <= settings.SliderMin) return;

            _min = settings.SliderMin;
            _max = settings.SliderMax;
            _wholeNumbers = settings.UseWholeNumbers;
        }

        private static void StartTicking()
        {
            if (_ticking) return;
            _ticking = true;
            BasisFrameClock.OnTick += Tick;
            BasisFrameClock.AddRequest();
        }

        private static void StopTicking()
        {
            if (!_ticking) return;
            _ticking = false;
            BasisFrameClock.OnTick -= Tick;
            BasisFrameClock.RemoveRequest();
        }

        private static void Tick()
        {
            // A destroyed control with no setting behind it leaves nothing to drive. Callback-only
            // sliders die with their menu; bound ones outlive it.
            if (_slider == null && _binding == null)
            {
                Drop();
                return;
            }

            ReadRange();

            BasisInput input = ResolveDevice(_role);
            float axis = input != null ? ReadAxis(input) : 0f;

            if (axis != 0f)
            {
                if (!_sweeping)
                {
                    _sweeping = true;
                    _liveValue = CurrentValue();
                    _nextLiveApply = 0f;

                    // Animate like a drag for as long as the stick is over, which also shows which
                    // slider the stick has hold of when several are on screen.
                    if (_slider != null) _slider.SetExternalDrive(true);
                }

                float sweepSeconds = Mathf.Max(MinimumSweepSeconds, BasisSettingsDefaults.JoystickBindSweepSeconds.RawValue);

                // Squared response: the range is crossed in the sweep time at full deflection,
                // while a nudge off centre creeps, which is where tuning actually happens.
                float step = axis * Mathf.Abs(axis) * ((_max - _min) / sweepSeconds) * Time.unscaledDeltaTime;
                _liveValue = Mathf.Clamp(_liveValue + step, _min, _max);
                ApplyLive();
                return;
            }

            if (_sweeping) Commit();
        }

        /// <summary>Where a sweep starts from — whatever the setting is worth right now.</summary>
        private static float CurrentValue()
        {
            if (_binding != null) return Mathf.Clamp(_binding.RawValue, _min, _max);
            return _slider != null ? Mathf.Clamp(_slider.Value, _min, _max) : _min;
        }

        private static float Quantized(float value) => _wholeNumbers ? Mathf.Round(value) : value;

        /// <summary>
        /// Moves the handle and the value in force without saving. A dragged slider behaves the
        /// same way — the write happens when it is let go — and anything reading
        /// <see cref="BasisSettingsBinding{T}.RawValue"/> per frame picks this up immediately.
        /// </summary>
        private static void ApplyLive()
        {
            float value = Quantized(_liveValue);
            if (_slider != null) _slider.SetValueWithoutNotify(value);
            if (_binding == null) return;

            _binding.SetValueWithoutNotify(value);

            // Settings that apply from OnChanged instead of reading RawValue would otherwise sit
            // still until the stick came back. They are told at a fixed rate rather than every
            // frame: some listeners rebuild real resources, and a stick held over for a second is
            // ninety of those.
            float now = Time.unscaledTime;
            if (now < _nextLiveApply) return;
            _nextLiveApply = now + LiveApplyInterval;
            _binding.OnChanged?.Invoke(value);
        }

        /// <summary>
        /// End of a sweep. Goes through the control so its callback runs and its binding is written
        /// exactly as a released drag would write it — one disk write per sweep, not per frame.
        /// </summary>
        private static void Commit()
        {
            _sweeping = false;
            float value = Quantized(_liveValue);

            if (_slider != null)
            {
                _slider.SetExternalDrive(false);
                _slider.ApplyDrivenValue(value);
                return;
            }
            _binding?.SetValue(value);
        }

        /// <summary>Stick deflection past the deadzone, remapped so it starts from a crawl.</summary>
        private static float ReadAxis(BasisInput input)
        {
            float y = input.CurrentInputState.Primary2DAxisRaw.y;
            float magnitude = Mathf.Abs(y);
            if (magnitude <= StickDeadzone) return 0f;
            return Mathf.Sign(y) * ((magnitude - StickDeadzone) / (1f - StickDeadzone));
        }

        /// <summary>
        /// The device holding the bound role right now. Resolved every tick rather than held: a
        /// controller that sleeps and comes back is a new device for the same hand, and the bind
        /// belongs to the hand.
        /// </summary>
        private static BasisInput ResolveDevice(BasisBoneTrackedRole role)
        {
            BasisDeviceManagement management = BasisDeviceManagement.Instance;
            if (management == null) return null;

            BasisObservableList<BasisInput> devices = management.AllInputDevices;
            int count = devices.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisInput input = devices[Index];
                if (input != null && input.TryGetRole(out BasisBoneTrackedRole found) && found == role)
                {
                    return input;
                }
            }
            return null;
        }
    }
}
