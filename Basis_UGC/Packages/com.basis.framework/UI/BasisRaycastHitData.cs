using UnityEngine;
using UnityEngine.UI;

namespace Basis.Scripts.UI
{
    [System.Serializable]
    public struct BasisRaycastHitData
    {
        public BasisRaycastHitData(Graphic graphic, Vector3 worldHitPosition, Vector2 screenPosition, float distance, int displayIndex)
        {
            this.graphic = graphic;
            this.worldHitPosition = worldHitPosition;
            this.screenPosition = screenPosition;
            this.distance = distance;
            this.displayIndex = displayIndex;
        }
        [SerializeField]
        public Graphic graphic;
        [SerializeField]
        public Vector3 worldHitPosition;
        [SerializeField]
        public Vector2 screenPosition;
        [SerializeField]
        public float distance;
        [SerializeField]
        public int displayIndex;
    }
}
