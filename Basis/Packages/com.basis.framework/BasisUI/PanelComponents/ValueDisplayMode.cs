namespace Basis.BasisUI
{
    public enum ValueDisplayMode
    {
        Percentage,
        Raw,
        Meters,
        Degrees,
        percentageFromZero,
        MemorySize,
        /// <summary>
        /// SI-style short form: 1k / 10k / 32.5k / 1.2M. Decimals are only shown
        /// when the scaled value isn't a whole number, so clean round values stay
        /// clean. Intended for large integer sliders like triangle / bone counts
        /// where "2000000" is unreadable but "2M" is obvious at a glance.
        /// </summary>
        Compact,
        Hz,
        PercentageFromZeroTwoDecimals
    }
}
