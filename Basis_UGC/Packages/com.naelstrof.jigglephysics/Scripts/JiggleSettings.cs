using System;
using UnityEngine;
using UnityEngine.Serialization;
namespace JigglePhysics
{
    public class JiggleSettings : JiggleSettingsBase
    {
        [SerializeField]
        [Range(0f, 2f)]
        [Tooltip("How much gravity to apply to the simulation, it is a multiplier of the Physics.gravity setting.")]
        private float gravityMultiplier = 1f;
        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("How much mechanical friction to apply, this is specifically how quickly oscillations come to rest.")]
        private float friction = 0.4f;
        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("How much angular force is applied to bring it to the target shape.")]
        private float angleElasticity = 0.4f;
        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("How much of the simulation should be expressed. A value of 0 would make the jiggle have zero effect. A value of 1 gives the full movement as intended. 0.5 would ")]
        private float blend = 1f;
        [FormerlySerializedAs("airFriction")]
        [HideInInspector]
        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("How much jiggled objects should get dragged behind by moving through the air. Or how \"thick\" the air is.")]
        private float airDrag = 0.1f;
        [HideInInspector]
        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("How rigidly the rig holds its length. Low values cause lots of squash and stretch!")]
        private float lengthElasticity = 0.8f;
        [HideInInspector]
        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("How much to allow free bone motion before engaging elasticity.")]
        private float elasticitySoften = 0f;
        [HideInInspector]
        [SerializeField]
        [Tooltip("How much radius points have, only used for collisions. Set to 0 to disable collisions")]
        private float radiusMultiplier = 0f;
        [HideInInspector]
        [SerializeField]
        [Tooltip("How the radius is expressed as a curve along the bone chain from root to child.")]
        private AnimationCurve radiusCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0f));

        public override JiggleSettingsData GetData()
        {
            return new JiggleSettingsData
            {
                gravityMultiplier = gravityMultiplier,
                friction = friction,
                airDrag = airDrag,
                blend = blend,
                angleElasticity = angleElasticity,
                elasticitySoften = elasticitySoften,
                lengthElasticity = lengthElasticity,
                radiusMultiplier = radiusMultiplier,
            };
        }
        public void SetData(JiggleSettingsData data)
        {
            gravityMultiplier = data.gravityMultiplier;
            friction = data.friction;
            angleElasticity = data.angleElasticity;
            blend = data.blend;
            airDrag = data.airDrag;
            lengthElasticity = data.lengthElasticity;
            elasticitySoften = data.elasticitySoften;
            radiusMultiplier = data.radiusMultiplier;
        }
        public override float GetRadius(float normalizedIndex)
        {
            return radiusMultiplier * radiusCurve.Evaluate(normalizedIndex);
        }
        public void SetRadiusCurve(AnimationCurve curve)
        {
            radiusCurve = curve;
        }
    }
}