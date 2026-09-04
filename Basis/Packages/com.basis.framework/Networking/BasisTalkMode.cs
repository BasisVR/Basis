namespace Basis.Scripts.Networking
{
    public enum BasisTalkMode : byte
    {
        Normal = 0,
        Private = 1,
        Direct = 2,
        ThisPerson = 3,
        Announce = 4,
        NoOne = 5,
        Shout = 6,
    }

    /// <summary>
    /// Shout is the proximity counterpart to <see cref="BasisTalkMode.Announce"/>: it stays on the
    /// normal spatialized voice path (channel + per-player 3D AudioSource) and simply reaches
    /// <see cref="RangeMultiplier"/> times as far, at <see cref="Gain"/> the level, instead of
    /// being lifted out of the world into a global 2D source the way announce is.
    /// </summary>
    public static class BasisShout
    {
        public const float RangeMultiplier = 2f;
        public const float RangeMultiplierSquared = RangeMultiplier * RangeMultiplier;
        public const float Gain = 1.75f;
    }
}
