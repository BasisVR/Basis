using System;
namespace Basis.Scripts.Constraints
{
    [Flags]
    public enum BasisConstraintAxis : byte
    {
        None = 0,
        X = 1,
        Y = 2,
        Z = 4,
        All = X | Y | Z,
    }
}
