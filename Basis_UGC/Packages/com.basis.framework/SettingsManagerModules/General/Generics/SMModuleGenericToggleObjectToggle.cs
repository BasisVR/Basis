using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    [System.Serializable]
    public class SMModuleGenericToggleObjectToggle
    {
        public GameObject[] ToggleOff;
        public GameObject[] ToggleOn;
        public MonoBehaviour[] ComponentOn;
        public MonoBehaviour[] ComponentOff;
        public string LookingForQualityLevelValue;
    }
}