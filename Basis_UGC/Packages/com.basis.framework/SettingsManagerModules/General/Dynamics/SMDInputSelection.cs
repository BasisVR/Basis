#if BattlePhazeInputSystem
namespace BattlePhaze.SettingsManager.Intergrations
{
    using BattlePhaze.SettingsManager;
    using UnityEngine;
    using UnityEngine.InputSystem;
    public class SMDInputSelection : MonoBehaviour
    {
        public SettingsManagerRebind Rebinds;
        public InputActionAsset inputAsset;
        [System.Serializable]
        public class SettingsManagerRebind
        {
            public string SaveName;
            public string CurrentValues;
        }
        private void SaveRebinds()
        {
            Rebinds.CurrentValues = InputActionRebindingExtensions.SaveBindingOverridesAsJson(inputAsset);
            SettingsManager.Instance.SaveSystem.Set(Rebinds.SaveName, Rebinds.CurrentValues, "Rebinds");
        }
        public void GetCurrentBindings()
        {
            Rebinds.CurrentValues = SettingsManager.Instance.SaveSystem.Get(Rebinds.SaveName, string.Empty);
            if (Rebinds.CurrentValues == string.Empty)
            {
                SaveRebinds();
            }
            InputActionRebindingExtensions.LoadBindingOverridesFromJson(inputAsset, Rebinds.CurrentValues);
        }
        public void Start()
        {
            GetCurrentBindings();
        }
    }
}
#endif