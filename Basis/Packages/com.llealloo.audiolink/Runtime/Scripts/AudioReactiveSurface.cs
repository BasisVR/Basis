using UnityEngine;

namespace AudioLink
{
#if UDONSHARP
    using UdonSharp;
    using static VRC.SDKBase.VRCShader;
#else
    using static Shader;
#endif

    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class AudioReactiveSurface : AudioReactive
    {
        [Header("AudioLink Settings")]
        public AudioLinkBand band;
        public bool smooth;
        [Range(0, 127)]
        public int delay;

        [Header("Reactivity Settings")]
        [Tooltip("Emission driven by amplitude")]
        [ColorUsage(true, true)]
        public Color color = Color.white;
        [Tooltip("Emission multiplier")]
        public float intensity = 1f;
        [Tooltip("Hue shift driven by amplitude")]
        public float hueShift = 0f;
        [Tooltip("Pulse length")]
        public float pulseLength = 0f;
        [Tooltip("Pulse rotation")]
        public float pulseRotation = 0f;

        #region PropertyIDs

        // ReSharper disable InconsistentNaming
        private int _Delay;
        private int _Band;
        private int _HueShift;
        private int _EmissionColor;
        private int _Emission;
        private int _Pulse;
        private int _PulseRotation;
        // ReSharper restore InconsistentNaming

        private void InitIDs()
        {
            _Delay = PropertyToID("_Delay");
            _Band = PropertyToID("_Band");
            _HueShift = PropertyToID("_HueShift");
            _EmissionColor = PropertyToID("_EmissionColor");
            _Emission = PropertyToID("_Emission");
            _Pulse = PropertyToID("_Pulse");
            _PulseRotation = PropertyToID("_PulseRotation");
        }

        #endregion
        void Start()
        {
            InitIDs();
            UpdateMaterial();
        }

        public void UpdateMaterial()
        {
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            MeshRenderer mesh = transform.GetComponent<MeshRenderer>();
            int bandInt = (int)band;
            int delayTmp = delay;
            if (smooth)
            {
                bandInt += Mathf.FloorToInt(AudioLink.GetALPassFilteredAudioLink().y);
                delayTmp = 15 - delayTmp;
            }
            
            block.SetFloat(_Delay, (float)delayTmp / 128f);
            block.SetFloat(_Band, (float)bandInt);
            block.SetFloat(_HueShift, hueShift);
            block.SetColor(_EmissionColor, color);
            block.SetFloat(_Emission, intensity);
            block.SetFloat(_Pulse, pulseLength);
            block.SetFloat(_PulseRotation, pulseRotation);
            mesh.SetPropertyBlock(block);
        }
    }
}
