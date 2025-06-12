using BattlePhaze.SaveSystem;
using System;
using System.Collections.Generic;
using System.Linq;
namespace BattlePhaze.SettingsManager
{
    public class SMCommandLineLoader : SMSaveModuleBase
    {
        public override string ModuleName()
        {
            return "CommandLine";
        }
        public override bool Save(SettingsManager Manager, SettingsManagerSaveSystem Save)
        {
            return false;
        }
        Dictionary<string, List<string>> arguments = new Dictionary<string, List<string>>();
        public override bool Load(SettingsManager Manager, SettingsManagerSaveSystem Save)
        {
            List<string> cmdArgs = new List<string>(Environment.GetCommandLineArgs());
            string Checker = "-SM";
            for (int Index = 1; Index < cmdArgs.Count; Index++)
            {
                if (cmdArgs[Index].StartsWith(Checker))
                {
                    string Sanitised = cmdArgs[Index].Substring(Checker.Length);
                    arguments.Add(Sanitised.Replace("-", " "), new List<string>());
                }
                else if (arguments.Count > 0)
                {
                    arguments.Last().Value.Add(cmdArgs[Index]);
                }
            }

            foreach (KeyValuePair<string, List<string>> pair in arguments)
            {
                SettingsMenuInput input = Manager.Options.FirstOrDefault(x => x.Name == pair.Key);
                if (input != null)
                {
                    if (pair.Value.Count >= 2)
                    {
                        Save.Set(pair.Key, pair.Value[0], pair.Value[1]);
                    }
                    else
                    if (pair.Value.Count >= 1)
                    {
                        Save.Set(pair.Key, pair.Value[0], string.Empty);
                    }
                }
            }

            return true;
        }
        public override bool Delete(SettingsManager Manager, SettingsManagerSaveSystem Save)
        {
            return false;
        }
        public override string Location(SettingsManager Manager, SettingsManagerSaveSystem Save)
        {
            return string.Empty;
        }

        public override SaveSystemType Type()
        {
            return SaveSystemType.LoaderOnly;
        }
    }
}