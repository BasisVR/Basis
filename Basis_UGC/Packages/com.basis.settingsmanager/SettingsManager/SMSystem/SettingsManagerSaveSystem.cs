namespace BattlePhaze.SaveSystem
{
    using BattlePhaze.SettingsManager;
    using BattlePhaze.SettingsManager.DebugSystem;
    using System.Collections.Generic;
    using UnityEngine;
    public class SettingsManagerSaveSystem
    {
        /// <summary>
        /// Gets the current group.
        /// </summary>
        /// <value>The current group.</value>
        public string CurrentGroup;
        [SerializeField]
        public Dictionary<string, SMOptionInformation> OptionMapping = new Dictionary<string, SMOptionInformation>();
        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsManagerSaveSystem"/> class and load from file.
        /// </summary>
        public SettingsManagerSaveSystem()
        {
            OptionMapping = new Dictionary<string, SMOptionInformation>();
            CurrentGroup = string.Empty;
            Load();
        }
        #region Set functions
        /// <summary>
        /// Set the value and comment of an option. Creates a new option if it does not exist.
        /// </summary>
        /// <param name="key">The name of the option.</param>
        /// <param name="value">The value of the option.</param>
        /// <param name="comment">The comment for the option.</param>
        public void Set(string key, string value, string comment)
        {
            if (string.IsNullOrEmpty(key) || key.Contains("="))
            {
                return;
            }

            string computedKey = CurrentGroup + key;

            if (OptionMapping.TryGetValue(computedKey, out SMOptionInformation option))
            {
                option.value = value;
                option.comment = comment;
            }
            else
            {
                OptionMapping.Add(computedKey, new SMOptionInformation(computedKey, value, comment));
            }
        }
        #endregion
        #region Get functions
        /// <summary>
        /// Returns the value of the specified property.
        /// </summary>
        /// <param name="key">The name of the property.</param>
        /// <param name="defaultValue">The default value to return if the property is absent.</param>
        /// <returns>The value of the property, or the default value if the property is absent.</returns>
        public string Get(string key, string defaultValue)
        {
            string computedKey = CurrentGroup + key;
            if (key.Contains("="))
            {
                // Invalid property name.
                return defaultValue;
            }
            else if (OptionMapping.TryGetValue(computedKey, out SMOptionInformation option))
            {
                // Property found.
                return option.value;
            }
            else
            {
                // Property not found.
                return defaultValue;
            }
        }
        #endregion
        /// <summary>
        /// Remove all properties.
        /// </summary>
        public void Clear()
        {
            OptionMapping.Clear();
            CurrentGroup = string.Empty;
        }
        /// <summary>
        /// Save properties to file.
        /// </summary>
        public void Save()
        {
            if (SettingsManager.Instance.ManagerSettings.CurrentlySelectedSaveModule != null)
            {
                SettingsManagerDebug.Log("Saving Data");
                SettingsManager.Instance.ManagerSettings.CurrentlySelectedSaveModule.Save(SettingsManager.Instance, this);
            }
            else
            {
                SettingsManagerDebug.LogError("Missing Save Module Unable To Load");
            }
            foreach (SMSaveModuleBase Module in SettingsManager.Instance.SaveModules)
            {
                if (Module != null)
                {
                    if (Module.Type() == SMSaveModuleBase.SaveSystemType.WriterOnly)
                    {
                        Module.Save(SettingsManager.Instance, this);
                    }
                }
            }
        }
        /// <summary>
        /// Load properties from file.
        /// </summary>
        public void Load()
        {
            Clear();
            if (SettingsManager.Instance.ManagerSettings.CurrentlySelectedSaveModule != null)
            {
                SettingsManagerDebug.Log("Loading Saved Data ");
                SettingsManager.Instance.ManagerSettings.CurrentlySelectedSaveModule.Load(SettingsManager.Instance, this);
            }
            else
            {
                SettingsManagerDebug.LogError("Missing Save Module Unable To Save");
            }
            foreach (SMSaveModuleBase Module in SettingsManager.Instance.SaveModules)
            {
                if (Module != null)
                {
                    if (Module.Type() == SMSaveModuleBase.SaveSystemType.LoaderOnly)
                    {
                        Module.Load(SettingsManager.Instance, this);
                    }
                }
            }
        }
    }
}