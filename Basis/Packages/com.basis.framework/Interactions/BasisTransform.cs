using UnityEngine;
namespace Basis.Scripts.BasisSdk.Interactions
{

    public class BasisTransform
    {
        public enum Direction
        {
            Embiggen = 0,
            Ensmallen = 1
        }

        public static void ScaleObjectBetween(
            Transform transform,
            BasisTransform.Direction scaleDirection,
            float stepSize,
            float minScale,
            float maxScale
        )
        {
            Vector3 scaleVector = Vector3.one;
            scaleVector *= (scaleDirection == Direction.Embiggen ? 1 : -1) * stepSize;

            BasisDebug.Log(scaleDirection == Direction.Embiggen ? "Embiggening" : "Ensmallening");
            BasisDebug.Log("Min Scale: " + minScale + " | Max Scale: " + maxScale);
            BasisDebug.Log(scaleVector.ToString());
            Vector3 newScale = transform.localScale;

            newScale += scaleVector;
            newScale.x = Mathf.Clamp(newScale.x, minScale, maxScale);
            newScale.y = Mathf.Clamp(newScale.y, minScale, maxScale);
            newScale.z = Mathf.Clamp(newScale.z, minScale, maxScale);

            transform.localScale = newScale;
        }
    }
}
