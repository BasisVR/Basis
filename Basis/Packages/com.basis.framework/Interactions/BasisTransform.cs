using UnityEngine;
namespace Basis.Scripts.BasisSdk.Interactions
{
    public class BasisTransform
    {
        /// <summary>
        /// Helper function, scales the object uniformly in all axes, clamped to min/max scale.
        /// </summary>
        public static void scaleObjectWithClamp(
            Transform transform,
            float scaleDirection,
            float minScale,
            float maxScale
        )
        {
            Vector3 vector = Vector3.one;
            vector *= scaleDirection / 200f;

            Vector3 newScale = transform.localScale;
            newScale += vector;
            newScale.x = Mathf.Clamp(newScale.x, minScale, maxScale);
            newScale.y = Mathf.Clamp(newScale.y, minScale, maxScale);
            newScale.z = Mathf.Clamp(newScale.z, minScale, maxScale);

            transform.localScale = newScale;
        }
    }
}
