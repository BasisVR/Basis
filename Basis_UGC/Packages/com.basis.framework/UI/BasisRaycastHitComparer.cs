using System.Collections.Generic;

namespace Basis.Scripts.UI
{
    /// </summary>
    sealed class BasisRaycastHitComparer : IComparer<BasisRaycastHitData>
    {
        public int Compare(BasisRaycastHitData a, BasisRaycastHitData b)
            => b.graphic.depth.CompareTo(a.graphic.depth);
    }
}
