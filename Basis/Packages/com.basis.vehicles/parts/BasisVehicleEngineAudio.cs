// ============================
// BasisVehicleEngineAudio.cs
// (updated: supports network revs)
// ============================
using UnityEngine;

namespace Basis.Scripts.Vehicles.Parts
{
    /// <summary>
    /// Simple engine audio controller:
    /// - Uses vehicle Rigidbody speed + BasisVehicleBody LinearActivation.z as "throttle" (local)
    /// - OR uses NetworkRevs01 when UseNetworkRevs is true (remote)
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class BasisVehicleEngineAudio : MonoBehaviour
    {
        [Header("Source (optional)")]
        [Tooltip("If null, will auto-find a parent BasisVehicleBody.")]
        public Basis.Scripts.Vehicles.Main.BasisVehicleBody VehicleBody;

        [Tooltip("If null, will auto-find a parent Rigidbody.")]
        public Rigidbody VehicleRigidbody;

        [Header("Network Override (Remote)")]
        public bool UseNetworkRevs = false;

        [Range(0f, 1f)]
        public float NetworkRevs01 = 0f;

        [Header("Speed / Throttle")]
        [Tooltip("Speed (m/s) considered 'max revs'. If <= 0, uses VehicleBody.MaxSpeed if available, else defaults to 30.")]
        public float SpeedForMaxRevs = 0f;

        [Tooltip("How much throttle affects revs at low speed.")]
        [Range(0f, 2f)]
        public float ThrottleInfluence = 1.0f;

        [Tooltip("When no throttle, keep a little idle rev.")]
        [Range(0f, 1f)]
        public float IdleRevs = 0.12f;

        [Header("Audio")]
        [Tooltip("Base pitch at idle.")]
        public float MinPitch = 0.9f;

        [Tooltip("Pitch at full revs.")]
        public float MaxPitch = 2.0f;

        [Tooltip("Volume at idle.")]
        [Range(0f, 1f)]
        public float MinVolume = 0.15f;

        [Tooltip("Volume at full revs.")]
        [Range(0f, 1f)]
        public float MaxVolume = 0.9f;

        [Header("Smoothing")]
        [Tooltip("How quickly pitch/volume follow target. Higher = snappier.")]
        public float Response = 8f;

        [Header("Flavor (optional)")]
        [Tooltip("Adds subtle pitch wobble (like engine vibration). Set 0 to disable.")]
        [Range(0f, 0.15f)]
        public float PitchWobble = 0.03f;

        [Tooltip("How fast the wobble oscillates.")]
        public float WobbleHz = 12f;

        [Tooltip("Quantize revs to fake gears. 0 = off. Try 4-6.")]
        [Range(0, 8)]
        public int FakeGears = 0;

        private AudioSource _audio;
        private float _currentRevs;
        private float _wobblePhase;

        private void Awake()
        {
            _audio = GetComponent<AudioSource>();
            _audio.loop = true;

            AutoWireIfNeeded();

            if (_audio.clip != null && !_audio.isPlaying)
                _audio.Play();
        }

        private void OnEnable()
        {
            AutoWireIfNeeded();
            if (_audio != null && _audio.clip != null && !_audio.isPlaying)
                _audio.Play();
        }

        private void AutoWireIfNeeded()
        {
            if (VehicleBody == null)
                VehicleBody = GetComponentInParent<Basis.Scripts.Vehicles.Main.BasisVehicleBody>();

            if (VehicleRigidbody == null)
                VehicleRigidbody = GetComponentInParent<Rigidbody>();
        }

        private void Update()
        {
            if (_audio == null)
                return;

            float targetRevs;

            if (UseNetworkRevs)
            {
                targetRevs = Mathf.Clamp01(NetworkRevs01);
            }
            else
            {
                if (VehicleRigidbody == null)
                {
                    AutoWireIfNeeded();
                    if (VehicleRigidbody == null) return;
                }

                float speed = VehicleRigidbody.linearVelocity.magnitude; // m/s
                float throttle = 0f;

                if (VehicleBody != null)
                {
                    throttle = Mathf.Clamp(VehicleBody.LinearActivation.z, -1f, 1f);
                    throttle = Mathf.Abs(throttle);
                }

                float maxSpeed = ResolveSpeedForMaxRevs();
                float speedRatio = (maxSpeed > 0.001f) ? Mathf.Clamp01(speed / maxSpeed) : 0f;

                targetRevs =
                    Mathf.Max(IdleRevs,
                              Mathf.Clamp01(speedRatio + throttle * ThrottleInfluence * (1f - speedRatio)));
            }

            if (FakeGears > 0)
            {
                float steps = FakeGears;
                targetRevs = Mathf.Round(targetRevs * steps) / steps;
                targetRevs = Mathf.Clamp01(targetRevs);
            }

            float lerp = 1f - Mathf.Exp(-Response * Time.deltaTime);
            _currentRevs = Mathf.Lerp(_currentRevs, targetRevs, lerp);

            float pitch = Mathf.Lerp(MinPitch, MaxPitch, _currentRevs);
            float volume = Mathf.Lerp(MinVolume, MaxVolume, _currentRevs);

            if (PitchWobble > 0f)
            {
                _wobblePhase += Time.deltaTime * Mathf.Max(0.1f, WobbleHz) * Mathf.PI * 2f;
                pitch *= 1f + Mathf.Sin(_wobblePhase) * PitchWobble;
            }

            _audio.pitch = pitch;
            _audio.volume = volume;
        }

        private float ResolveSpeedForMaxRevs()
        {
            if (SpeedForMaxRevs > 0.001f)
                return SpeedForMaxRevs;

            if (VehicleBody != null && VehicleBody.MaxSpeed > 0.001f)
                return VehicleBody.MaxSpeed;

            return 30f;
        }
    }
}
