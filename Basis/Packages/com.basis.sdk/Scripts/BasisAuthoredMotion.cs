using System;
using UnityEngine;

/// <summary>
/// Data-only avatar component declaring authored, deterministic dynamic motion on transforms
/// the humanoid rig and IK don't drive (tail/ear chains, accessories, etc.). It holds pure
/// serialized configuration and runs no per-instance per-frame <c>Update</c> — all runtime
/// evaluation happens in the batched <c>BasisAuthoredMotionSystem</c> job, which reads this
/// component at calibration. The config model mirrors <see cref="BasisParameterDriver"/>'s
/// <c>Operation[]</c> shape (an enum kind + per-kind fields).
///
/// Allow it onto an avatar by adding its type to the Content Police
/// (<c>ContentPoliceSelector.selectedTypes</c> in <c>AvatarContentPoliceSelector.asset</c>).
/// Group movements that toggle together into one component; an avatar may carry several.
/// </summary>
public class BasisAuthoredMotion : MonoBehaviour
{
    /// <summary>
    /// Raised on enable/disable so a registered motion system can flip this component's slice
    /// of its valid-mask without a per-frame poll. The system subscribes at registration; the
    /// component holds no reference to any toggle package, so any actuator that flips
    /// <see cref="Behaviour.enabled"/> (e.g. an HVR.Vixxy activation) drives it unchanged.
    /// </summary>
    public event Action<BasisAuthoredMotion, bool> EnabledStateChanged;

    public Movement[] movements = Array.Empty<Movement>();

    private void OnEnable() => EnabledStateChanged?.Invoke(this, true);
    private void OnDisable() => EnabledStateChanged?.Invoke(this, false);

    [Serializable]
    public class Movement
    {
        // Open, extensible set — new kinds slot in without disturbing registration / scheduling / toggles.
        public enum Kind { Oscillate, Rotate, Orbit, RandomSelect, Sequence, Noise }
        public enum Channel { Rotation, Position, Scale }   // what Oscillate / Noise drive
        public enum Waveform { Sine, Triangle, Square, Pulse }

        public Kind kind = Kind.Oscillate;
        public string label;              // author-facing identifier only
        public bool enabled = true;       // author default; runtime toggle rides the component's own enabled
        public Vector3 axis = Vector3.up; // local axis the movement acts about

        // Oscillate — periodic motion on `channel`; a chain makes a travelling wave (1 entry = simple sway).
        public Channel channel = Channel.Rotation; // amplitude unit: deg | metres | scale-factor
        public Waveform waveform = Waveform.Sine;
        public float pulseWidth = 0.5f;   // square/pulse duty cycle (0–1)
        public Transform[] chain;
        public float amplitude = 15f;
        public float frequencyHz = 0.5f;
        public float phase = 0f;
        public float chainPhaseStep = 0f; // phase delay per element down the chain
        public float chainFalloff = 1f;   // amplitude scale per element down the chain

        // Rotate — constant angular velocity about `axis`, in place.
        public Transform target;
        public float speedDeg = 36f;      // deg/sec

        // Orbit — revolve `target` around `pivot` at `radius` (not a spin-in-place).
        public Transform pivot;
        public float radius = 0.1f;
        public float orbitSpeedDeg = 90f; // deg/sec around the pivot

        // RandomSelect — every `intervalRange.x` seconds, deterministically pick one weighted option (or idle)
        // and ease the target in/out. Each Option may set its own `target`, else falls back to `selectTarget`.
        public Transform selectTarget;   // default target for options that leave their own target null
        public Option[] options = Array.Empty<Option>();
        public float idleWeight = 0f;     // relative weight of the "pose nothing" outcome
        public Vector2 intervalRange = new Vector2(2f, 6f);  // x = fixed period (seconds between picks)
        public float attack = 0.06f, release = 0.25f;        // ease in / out seconds
        public bool preventRepeats = true;
        public uint seed = 0;             // 0 = derive from registration index

        // Sequence — authored timeline, loop or one-shot. A baked clip drives many bones via paths under
        // `sequenceRoot`; inline keyframes (deferred) use `sequenceTarget`.
        public Transform sequenceTarget; // single-bone inline-keyframe target (inline path; deferred)
        public Transform sequenceRoot;   // baked-clip paths resolve under this (defaults to the avatar root)
        public Keyframe[] keyframes = Array.Empty<Keyframe>();
        public BasisMotionClip bakedClip; // shared baked curves; null when using inline keyframes
        public bool loop = true;
        public float sequenceSpeed = 1f; // playback rate multiplier (1 = authored speed); negative plays in reverse

        // Noise — simplex drift on `channel` about `axis`; reuses amplitude / chain / chainFalloff / seed; `noiseSpeed` = sample rate.
        public float noiseSpeed = 0.5f;
    }

    [Serializable]
    public class Option
    {
        [Tooltip("Transform this option poses. Falls back to the movement's Select Target when null.")]
        public Transform target;
        [Tooltip("Local axis to rotate about.")]
        public Vector3 axis = Vector3.up;
        [Tooltip("Rotation applied about Axis when this option is selected, in degrees.")]
        public float angleDeg;
        [Tooltip("Relative likelihood this option is picked.")]
        public float weight = 1f;
    }

    [Serializable]
    public class Keyframe
    {
        [Tooltip("Time of this key, in seconds from the start of the sequence.")]
        public float time;
        [Tooltip("Rotation delta from rest at this key, in euler degrees.")]
        public Vector3 eulerDelta;
        [Tooltip("Local position delta from rest at this key.")]
        public Vector3 positionDelta;
        [Tooltip("Local scale delta from rest at this key.")]
        public Vector3 scaleDelta;
    }
}
