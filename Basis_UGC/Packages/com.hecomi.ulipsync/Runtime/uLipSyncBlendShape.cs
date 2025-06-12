using UnityEngine;
using System.Collections.Generic;

namespace uLipSync
{
    [ExecuteAlways]
    public class uLipSyncBlendShape : MonoBehaviour
    {
        public SkinnedMeshRenderer skinnedMeshRenderer;
        public List<BlendShapeInfo> CachedblendShapes = new List<BlendShapeInfo>();
        public BlendShapeInfo[] BlendShapeInfos;

        [Range(0f, 0.3f)]
        public float smoothness = 0.05f;
        public float maxBlendShapeValue = 100f;
        public float minVolume = -2.5f;
        public float maxVolume = -1.5f;
        public bool usePhonemeBlend = false;
        public float _volume = 0f;
        public float _openCloseVelocity = 0f;

       public float SmoothDamp(float value, float target, ref float velocity)
        {
            return Mathf.SmoothDamp(value, target, ref velocity, smoothness);
        }

        public BlendShapeInfo GetBlendShapeInfo(string phoneme)
        {
            return CachedblendShapes.Find(info => info.phoneme == phoneme);
        }

        public void AddBlendShape(string phoneme, int blendShape)
        {
            var bs = GetBlendShapeInfo(phoneme);
            if (bs == null)
            {
                bs = new BlendShapeInfo { phoneme = phoneme };
                CachedblendShapes.Add(bs);
            }

            if (skinnedMeshRenderer != null)
            {
                bs.index = blendShape;
            }
        }
    }
}
