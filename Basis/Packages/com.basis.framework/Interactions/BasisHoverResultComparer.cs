using System.Collections.Generic;
namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// Efficient singleton comparer for sorting HoverResults by distance.
    /// </summary>
    public class BasisHoverResultComparer : IComparer<BasisHoverResult>
    {
        public static readonly BasisHoverResultComparer Instance = new BasisHoverResultComparer();
        public int Compare(BasisHoverResult a, BasisHoverResult b)
        {
            return a.distanceToCenter.CompareTo(b.distanceToCenter);
        }
    }
}
