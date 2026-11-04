namespace Basis.Scripts.Constraints
{
    public enum BasisOverrideSpace : byte
    {
        /// <summary>The override is a world pose.</summary>
        World = 0,
        /// <summary>The override is a local pose, replacing the target's own.</summary>
        Local = 1,
        /// <summary>The override composes onto the target's current local pose.</summary>
        Pivot = 2,
    }
}
