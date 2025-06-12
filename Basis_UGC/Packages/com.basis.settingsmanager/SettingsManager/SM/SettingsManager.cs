namespace BattlePhaze.SettingsManager
{
    using BattlePhaze.SaveSystem;
    using BattlePhaze.SettingsManager.DebugSystem;
    using System;
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.Events;
    using UnityEngine.SceneManagement;
    [AddComponentMenu("BattlePhaze/SettingsManager/Settings Manager")]
    [System.Serializable]
    public class SettingsManager : MonoBehaviour
    {
        public static SettingsManager Instance { get; set; }
        public SettingsManagerSaveSystem SaveSystem;
        public SettingsManagerSettings ManagerSettings = new SettingsManagerSettings();
        public List<SettingsManagerOption> SettingsManagerOptions = new List<SettingsManagerOption>();
        public List<SettingsMenuInput> Options = new List<SettingsMenuInput>();
        public List<SMSaveModuleBase> SaveModules = new List<SMSaveModuleBase>();
        public List<SettingsManagerIndexing> SettingsManagerSave = new List<SettingsManagerIndexing>();
        public List<Types.SettingsManagerAbstractTypeDropdown> SettingsManagerAbstractTypeDropdown = new List<Types.SettingsManagerAbstractTypeDropdown>();
        public List<Types.SettingsManagerAbstractTypeToggle> SettingsManagerAbstractTypeToggle = new List<Types.SettingsManagerAbstractTypeToggle>();
        public List<Types.SettingsManagerAbstractTypeSlider> SettingsManagerAbstractTypeSlider = new List<Types.SettingsManagerAbstractTypeSlider>();
        public List<Types.SettingsManagerAbstractTypeManagement> SettingsManagerAbstractTypeManagement = new List<Types.SettingsManagerAbstractTypeManagement>();
        public List<Types.SettingsManagerAbstractTypeText> SettingsManagerAbstractTypeText = new List<Types.SettingsManagerAbstractTypeText>();
        public List<SMPlatFormDefaultSave> PlatformSaveDefault = new List<SMPlatFormDefaultSave>();
        public List<SMWorkAround> WorkArounds = new List<SMWorkAround>();
        public SMPlatFormDefaultSave DefaultSaveType;
        public Action OnSettingsSaving;
        public Action OnSettingsSaved;
        public static Action OnSettingsSavingStatic;
        public static Action OnSettingsSavedStatic;
        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                if (ManagerSettings.MarkAsDontDestroyOnLoad == SettingsManagerEnums.DestroyOnLoadSettings.DontDestroy)
                {
                    DontDestroyOnLoad(gameObject);
                }
                CheckSupportedPlatforms();
               // Initalize(true);
                SceneManager.sceneLoaded += SettingsManagerSceneSystem.OnSceneLoaded;
            }
            else
            {
                Destroy(gameObject);
            }
        }
        public void InitalizeStoredData()
        {
            WorkArounds.Clear();
            for (int WorkAroundIndex = 0; WorkAroundIndex < Options.Count; WorkAroundIndex++)
            {
                SMWorkAround WorkAround = new SMWorkAround
                {
                    Name = Options[WorkAroundIndex].Name,
                    Index = WorkAroundIndex,
                    SelectedValue = Options[WorkAroundIndex].SelectedValue,
                    DefaultValue = Options[WorkAroundIndex].SelectedValueDefault,
                    SelectableValueList = Options[WorkAroundIndex].SelectableValueList
                };

                WorkArounds.Add(WorkAround);
            }
        }
        public int FindOrAddOption(int Index)
        {
            if (WorkArounds.Count != Options.Count)
            {
                InitalizeStoredData();
            }
            return Index;
        }
        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SettingsManagerSceneSystem.OnSceneLoaded;
        }
        private void CheckSupportedPlatforms()
        {
            for (int Index = 0; Index < Options.Count; Index++)
            {
                SettingsMenuInput option = Options[Index];
                bool foundPipeline = false;
                if (option.SupportedRenderPipeline.Count == 0)
                {
                    SettingsManagerDebug.Log("No Supported render Pipeline Found" + option.Name + " will not evaluate");
                    foundPipeline = true;
                }
                foreach (var platform in option.SupportedRenderPipeline)
                {
                    if (platform == ManagerSettings.CurrentPipeline)
                    {
                        foundPipeline = true;
                        break;
                    }
                }
                if (!foundPipeline)
                {
                    SettingsManagerDebug.Log("Disabling Option " + option.Name + " Render Pipeline did not match");
                    option.Type = SettingsManagerEnums.IsType.Disabled;
                }
            }
        }
        private SMSaveModuleBase AssignActiveSaveModule(string currentSaveType)
        {
            for (int Index = 0; Index < SaveModules.Count; Index++)
            {
                SMSaveModuleBase saveModule = SaveModules[Index];
                if (saveModule == null)
                {
                    SettingsManagerDebug.Log("Please assign an active save module with the name " + currentSaveType);
                    continue;
                }
                if (saveModule.Type() == SMSaveModuleBase.SaveSystemType.Normal)
                {
                    if (string.Equals(saveModule.ModuleName(), currentSaveType, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return saveModule;
                    }
                }
            }

            SettingsManagerDebug.LogError("Please assign an active save module with the name " + currentSaveType);
            return null;
        }
        private void InitializeSaveSystem()
        {
            ManagerSettings.CurrentlySelectedSaveModuleName = SettingsManagerSaveTypeSystem.GetSaveType(this);
            ManagerSettings.CurrentlySelectedSaveModule = AssignActiveSaveModule(ManagerSettings.CurrentlySelectedSaveModuleName);
            if (ManagerSettings.CurrentlySelectedSaveModule == null)
            {
                SettingsManagerDebug.LogError("Please Assign an Active Save Module!! Tried looking for " + ManagerSettings.CurrentlySelectedSaveModuleName);
                return;
            }
            SaveSystem ??= new SettingsManagerSaveSystem();
        }
        public void Initalize(bool readFromFile)
        {
            InitializeSaveSystem();
            RemoveNullOptions();
            SettingsManagerExclusionSystem.ExcludeFromPlatform(this);
            for (int Index = 0; Index < Options.Count; Index++)
            {
                SettingsMenuInput option = Options[Index];
                SettingsManagerOptionVisiblitySystem.SetOptionVisible(option.OptionIndex, true, this);
                SettingsManagerStorageManagement.Read(this, option.OptionIndex, readFromFile);
                InitializeOption(option);
                SettingsManagerDescriptionSystem.TxtDescriptionSetText(this, option.OptionIndex);
                SendOption(option);
            }
            SettingsManagerDescriptionSystem.ExplanationSetup(this);
            SettingsManagerStorageManagement.Save(this);
        }
        private void RemoveNullOptions()
        {
            Options.RemoveAll(option => option == null);
            SettingsManagerOptions.RemoveAll(option => option == null);
        }
        private void InitializeOption(SettingsMenuInput option)
        {
            switch (option.Type)
            {
                case SettingsManagerEnums.IsType.Disabled:
                    SettingsManagerOptionVisiblitySystem.SetOptionVisible(option.OptionIndex, false, this);
                    break;
                case SettingsManagerEnums.IsType.DropDown:
                    SettingsManagerDropDown.InitializeDropDown(this, option.OptionIndex);
                    break;
                case SettingsManagerEnums.IsType.Slider:
                    SettingsManagerSlider.InitializeSlider(this, option.OptionIndex);
                    break;
                case SettingsManagerEnums.IsType.Toggle:
                    SettingsManagerToggle.InitializeToggle(this, option.OptionIndex);
                    break;
                case SettingsManagerEnums.IsType.Dynamic:
                    SettingsManagerDropDown.InitializeDropDown(this, option.OptionIndex);
                    break;
            }
        }
        public void SendOption(SettingsMenuInput Option)
        {
            SettingsManagerDebug.Log($"Sent update: {Option.Name}: {Option.SelectedValue}");
            for (int Index = 0; Index < SettingsManagerOptions.Count; Index++)
            {
                try
                {
                    SettingsManagerOptions[Index]?.ReceiveOption(Option, this);
                }
                catch (Exception e)
                {
                    SettingsManagerDebug.LogError(e.StackTrace);
                }
            }
        }
        public static bool TypeCompare(object type, System.Type typeAgainst)
        {
            return type != null && type.GetType() == typeAgainst;
        }
    }
}
