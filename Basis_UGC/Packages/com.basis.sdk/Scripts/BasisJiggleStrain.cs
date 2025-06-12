using UnityEngine;

namespace Basis.Scripts.BasisSdk
{
    [System.Serializable]
    public class BasisJiggleStrain
    {
        [Range(0f, 2f)]
        [Tooltip("How much gravity to apply to the simulation, it is a multiplier of the Physics.gravity setting.")]
        public float GravityMultiplier = 0.1f;
        [Tooltip("How much mechanical friction to apply, this is specifically how quickly oscillations come to rest.")]
        [Range(0f, 1f)]
        public float Friction = 0.45f;
        [Tooltip("How much angular force is applied to bring it to the target shape.")]
        [Range(0f, 1f)]
        public float AngleElasticity = 0.6f;
        [Tooltip("How much of the simulation should be expressed. A value of 0 would make the jiggle have zero effect. A value of 1 gives the full movement as intended. 0.5 would ")]
        [Range(0f, 1f)]
        public float Blend = 1f;
        [Tooltip("How much jiggled objects should get dragged behind by moving through the air. Or how \"thick\" the air is.")]
        [Range(0f, 1f)]
        public float AirDrag = 0.01f;
        [Tooltip("How rigidly the rig holds its length. Low values cause lots of squash and stretch!")]
        [Range(0f, 1f)]
        public float LengthElasticity = 0.4f;
        [Tooltip("How much to allow free bone motion before engaging elasticity.")]
        [Range(0f, 1f)]
        public float ElasticitySoften = 0.2f;
        [Tooltip("How much radius points have, only used for collisions. Set to 0 to disable collisions")]
        public float RadiusMultiplier = 0.01f;
        [SerializeField]
        public Transform RootTransform;
        [SerializeField]
        public Transform[] IgnoredTransforms;
        [SerializeField]
        public Collider[] Colliders;
    }
}
