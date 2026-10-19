using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Drivers;

namespace Basis.Shims
{
    /// <summary>
    /// Whether the local player is sitting, as one static read, cheap enough to poll every frame.
    /// CurrentSeat (worlds only) is the seat itself, or null.
    /// </summary>
    public static class BasisSeatShim
    {
        public static bool IsSeated => BasisLocalSeatDriver.ActiveSeat != null;
        public static BasisSeat CurrentSeat => BasisLocalSeatDriver.ActiveSeat;
    }
}
