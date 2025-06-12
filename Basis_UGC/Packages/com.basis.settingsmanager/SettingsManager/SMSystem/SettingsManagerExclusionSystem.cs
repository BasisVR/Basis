using System.Linq;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerExclusionSystem
    {
        public static void ExcludeFromPlatform(SettingsManager Manager)
        {
            var currentPlatform = Application.platform;

            foreach (var option in Manager.Options)
            {
                var isExcluded = option.ExcludeFromThesePlatforms.Any(exclusion => exclusion.Platform == currentPlatform);
                if (isExcluded)
                {
                    option.Type = SettingsManagerEnums.IsType.Disabled;
                }
            }
        }
    }
}