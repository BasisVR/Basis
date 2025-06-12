using System;
namespace BattlePhaze.SettingsManager.Information
{
    public static class SettingsManagerInformationConverter
    {
        /// <summary>
        /// Post-processes a value based on the specified parameters.
        /// </summary>
        /// <param name="round">Indicates whether to round the value.</param>
        /// <param name="percentage">Indicates whether the value represents a percentage.</param>
        /// <param name="roundTo">The number of decimal places to round the value to.</param>
        /// <param name="currentValue">The value to post-process.</param>
        /// <param name="maxValue">The maximum value that the current value can reach.</param>
        /// <param name="maxPercentage">The maximum percentage that the current value can represent.</param>
        /// <param name="minPercentageOffset">The minimum percentage offset.</param>
        /// <returns>The post-processed value as a string.</returns>
        public static string PostProcessValue(bool round, bool percentage, int roundTo, float currentValue, float maxValue, float maxPercentage = 100, float minPercentageOffset = 0)
        {
            if (percentage)
            {
                // Convert the value to a percentage based on the maximum percentage and the minimum percentage offset
                currentValue = (maxPercentage * currentValue - minPercentageOffset) / maxValue;

                if (round)
                {
                    // Round the percentage value
                    currentValue = (float)Math.Round(currentValue, roundTo);
                }
            }
            else
            {
                if (round)
                {
                    // Round the value
                    currentValue = (float)Math.Round(currentValue, roundTo);
                }
            }

            // Convert the value to a string using the culture info specified in the settings manager instance
            string returnable = currentValue.ToString(SettingsManager.Instance.ManagerSettings.CInfo);

            if (percentage)
            {
                // Append the percentage symbol if the value is a percentage
                returnable += "%";
            }

            return returnable;
        }
    }
}