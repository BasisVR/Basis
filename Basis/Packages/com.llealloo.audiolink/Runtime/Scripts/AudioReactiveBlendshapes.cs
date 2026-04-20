using UnityEngine;

namespace AudioLink
{
#if UDONSHARP
    using UdonSharp;
#endif

    [RequireComponent(typeof(SkinnedMeshRenderer))]
    public class AudioReactiveBlendshapes : AudioReactive
    {
        public AudioLink audioLink;
        [Header("AudioLink Settings")]
        public AudioLinkBand band;
        public bool smooth;
        [Range(0, 127)]
        public int delay;
        
        [Header("Reactivity Settings")]
        public int[] blendshapeIDs;
        public float[] blendshapeFromWeights;
        public float[] blendshapeToWeights;

        private SkinnedMeshRenderer _skinnedMeshRenderer;
        private int _maxBlendshapes;

        void Start()
        {
            _skinnedMeshRenderer = transform.GetComponent<SkinnedMeshRenderer>();
            int[] maxBlendshapes = new int[3] { blendshapeIDs.Length, blendshapeFromWeights.Length, blendshapeToWeights.Length};
            _maxBlendshapes = Mathf.Min(maxBlendshapes);

        }

        void Update()
        {
            if (audioLink.AudioDataIsAvailable())
            {
                float amplitude = AudioLink.ToGrayscale(audioLink.GetBandAsSmooth(band, delay, smooth));
                for (int indx = 0; indx < _maxBlendshapes; indx++)
                    _skinnedMeshRenderer.SetBlendShapeWeight(blendshapeIDs[indx], Mathf.LerpUnclamped(blendshapeFromWeights[indx], blendshapeToWeights[indx], amplitude) * 100f);
            }
        }

    }
}
