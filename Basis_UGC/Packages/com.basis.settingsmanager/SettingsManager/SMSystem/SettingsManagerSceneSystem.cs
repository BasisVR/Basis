using UnityEngine.SceneManagement;

namespace BattlePhaze.SettingsManager
{
    public static class SettingsManagerSceneSystem
    {
        public static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SettingsManager.Instance.Initalize(true);
        }
    }
}