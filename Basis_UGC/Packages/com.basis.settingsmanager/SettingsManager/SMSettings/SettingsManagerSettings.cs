using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
namespace BattlePhaze.SettingsManager
{
    [System.Serializable]
    public class SettingsManagerSettings
    {
        public bool SuccessfullyGenerated = false;
        [SerializeField]
        public Object ActiveCanvasLikeObject;
        [SerializeField]
        public int BuildValues;
        [SerializeField]
        public string FileName = "Settings Manager";
        [SerializeField]
        public SettingsManagerEnums.CultureType CType = SettingsManagerEnums.CultureType.InvariantCulture;
        [SerializeField]
        public SettingsManagerEnums.SupportedRenderPipelines CurrentPipeline = SettingsManagerEnums.SupportedRenderPipelines.BuiltIn;
        [SerializeField]
        public SettingsManagerEnums.DestroyOnLoadSettings MarkAsDontDestroyOnLoad;
        [SerializeField]
        public CultureInfo CInfo = CultureInfo.InvariantCulture;
        [SerializeField]
        public Object ExplanationText;
        [SerializeField]
        public List<string> SettingsManagerObjectpaths = new List<string>();
        [SerializeField]
        public List<SettingsManagerObject> SettingsManagerObjects = new List<SettingsManagerObject>();
        [SerializeField]
        public SMSaveModuleBase CurrentlySelectedSaveModule;
        [SerializeField]
        public string CurrentlySelectedSaveModuleName;
    }
}