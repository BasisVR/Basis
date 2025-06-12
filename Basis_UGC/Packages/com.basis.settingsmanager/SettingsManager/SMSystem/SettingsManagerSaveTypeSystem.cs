using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerSaveTypeSystem
    {
        public static string GetSaveType(SettingsManager manager)
        {
            foreach (var platformSaveDefault in manager.PlatformSaveDefault)
            {
                if (platformSaveDefault.Platform == Application.platform)
                {
                    return platformSaveDefault.SaveType;
                }
            }
            return manager.DefaultSaveType.SaveType;
        }
    }
}