using UnityEngine;

namespace AudioLink
{
#if UDONSHARP
    using UdonSharp;

    [RequireComponent(typeof(SkinnedMeshRenderer))]
    public class AudioReactiveBlendshapes : UdonSharpBehaviour
#else
    [RequireComponent(typeof(SkinnedMeshRenderer))]
    public class AudioReactiveBlendshapes : MonoBehaviour
#endif
    {
        public AudioLink audioLink;
        public int band;
        [Range(0, 127)]
        public int delay;
        public int[] blendshapeIDs;
        public float[] blendshapeWeights;
        public float[] blendshapeMinWeights;

        private SkinnedMeshRenderer _skinnedMeshRenderer;
        private int _maxBlendshapes;

        void Start()
        {
            _skinnedMeshRenderer = transform.GetComponent<SkinnedMeshRenderer>();
            int[] maxBlendshapes = new int[3] { blendshapeIDs.Length, blendshapeWeights.Length, blendshapeMinWeights.Length };
            _maxBlendshapes = Mathf.Min(maxBlendshapes);

        }

        void Update()
        {
            if (audioLink.AudioDataIsAvailable())
            {
                float amplitude = audioLink.GetDataAtPixel(delay, band).x * 100f;
                for (int indx = 0; indx < _maxBlendshapes; indx++)
                    _skinnedMeshRenderer.SetBlendShapeWeight(blendshapeIDs[indx], Mathf.LerpUnclamped(blendshapeMinWeights[indx], blendshapeWeights[indx], amplitude));
            }
        }

    }
}
