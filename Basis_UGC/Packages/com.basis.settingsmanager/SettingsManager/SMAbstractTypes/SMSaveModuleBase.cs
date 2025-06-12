using BattlePhaze.SaveSystem;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    public abstract class SMSaveModuleBase : MonoBehaviour
    {
        public abstract SaveSystemType Type();
        public abstract string ModuleName();
        public abstract bool Save(SettingsManager Manager, SettingsManagerSaveSystem Save);
        public abstract bool Load(SettingsManager Manager, SettingsManagerSaveSystem Save);
        public abstract bool Delete(SettingsManager Manager, SettingsManagerSaveSystem Save);
        public abstract string Location(SettingsManager Manager, SettingsManagerSaveSystem Save);
        public enum SaveSystemType
        {
            Normal,// Saves and loads Only When Selected per Platform
            LoaderOnly,//Always Runs if in Save List Only Loads Settings
            WriterOnly//Always Runs if in Save List Only Writes Settings
        }
    }
}