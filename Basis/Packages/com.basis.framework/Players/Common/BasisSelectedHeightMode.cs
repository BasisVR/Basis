namespace Basis.Scripts.BasisSdk.Players
{
    /// <summary>
    /// Defines the available modes for determining a player's selected height
    /// within the Basis SDK. These modes control how height is measured or 
    /// specified when adapting to different player setups.
    /// </summary>
    public enum BasisSelectedHeightMode
    {
        /// <summary>
        /// Height is estimated based on the player's arm span. 
        /// This is often used when direct height data is unavailable 
        /// but arm span can be measured or inferred.
        /// </summary>
        ArmSpan,

        /// <summary>
        /// Height is set according to the player's eye level. 
        /// This mode is commonly applied in VR setups where 
        /// eye position is tracked directly.
        /// </summary>
        EyeHeight,
    }
}
