using UnityEngine;

namespace AudioLink
{
#if UDONSHARP
    using UdonSharp;
#endif

    public class AudioReactiveObject : AudioReactive
    {
        public AudioLink audioLink;
        [Header("AudioLink Settings")]
        public AudioLinkBand band;
        public bool smooth;
        [Range(0, 127)]
        public int delay;
        
        [Header("Reactivity Settings")]
        public Vector3 position;
        public Vector3 rotation;
        public Vector3 scale = new Vector3(1f, 1f, 1f);

        private Vector3 _initialPosition;
        private Vector3 _initialRotation;
        private Vector3 _initialScale;

        void Start()
        {
            _initialPosition = transform.localPosition;
            _initialRotation = transform.localEulerAngles;
            _initialScale = transform.localScale;

        }

        void Update()
        {
            if (audioLink.AudioDataIsAvailable()) // Check for AudioLink initialization
            {
                float amplitude = AudioLink.ToGrayscale(audioLink.GetBandAsSmooth(band, delay, smooth));

                transform.localPosition = _initialPosition + (position * amplitude);
                transform.localEulerAngles = _initialRotation + (rotation * amplitude);

                transform.localScale = new Vector3(_initialScale.x * Mathf.Lerp(1f, scale.x, amplitude), _initialScale.y * Mathf.Lerp(1f, scale.y, amplitude), _initialScale.z * Mathf.Lerp(1f, scale.z, amplitude));
            }
        }

    }
}
