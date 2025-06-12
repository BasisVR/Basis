using UnityEngine;

namespace JigglePhysics
{
    public class JiggleRigSimpleLOD : JiggleRigLOD
    {

        [Tooltip("Distance to disable the jiggle rig.")]
        [SerializeField] float distance = 40f;
        [Tooltip("Distance past distance from which it blends out rather than instantly disabling.")]
        [SerializeField] float blend = 10f;

        public float cameraDistance;
        float maxBlendDistance;
        public void Start()
        {
            maxBlendDistance = distance + blend;
        }
        protected override bool CheckActive()
        {
            float currentBlend = (cameraDistance - maxBlendDistance) / blend;
            currentBlend = Mathf.Clamp01(1f - currentBlend);
            for (int Index = 0; Index < JiggleCount; Index++)
            {
                jiggles[Index].blend = currentBlend;
            }
            return cameraDistance < distance;
        }

    }
}
