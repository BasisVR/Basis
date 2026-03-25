namespace Basis.Scripts.UI
{
    /// <summary>
    /// Pointer/cursor types modeled after CSS cursor values.
    /// Used to dynamically change the reticle appearance based on UI context.
    /// </summary>
    public enum BasisCursorType
    {
        Default,
        Pointer,
        Text,
        Grab,
        Grabbing,
        NotAllowed,
        Move,
        Custom,
    }
}
