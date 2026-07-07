using System.IO;
using UnityEditor;
using UnityEngine.InputSystem;

namespace Basis.Setup.Modules
{
    public sealed partial class BasisInputActionsSetup : BasisSetupModuleBase
    {
        private const string Folder = "Assets/Basis/Settings/InputActions";
        private const string ActionsPath = Folder + "/InputAction.inputactions";
        private const string SettingsPath = Folder + "/InputSystem.inputsettings.asset";

        private const string ActionsGuid = "5e4c3601c6a155f43af19da3bdd35a64";
        private const string SettingsGuid = "cf469a9a31a25dc449c2537cb0b17ad6";

        private const string SettingsConfigKey = "com.unity.input.settings";
        private const string ActionsConfigKey = "com.unity.input.settings.actions";

        public override string Key => "input.actions";
        public override string DisplayName => "Input Actions & Settings";
        public override string Category => "Input";
        public override int Version => 1;

        protected override bool Exists()
        {
            return File.Exists(BasisSetupIO.ToFullPath(ActionsPath))
                && File.Exists(BasisSetupIO.ToFullPath(SettingsPath));
        }

        protected override bool Build(BasisSetupMode mode)
        {
            BasisSetupIO.EnsureAssetFolder(Folder);

            bool changed = BasisSetupIO.WriteTextAsset(ActionsPath, ActionsJson, mode);
            BasisSetupIO.ForceGuid(ActionsPath, ActionsGuid);

            InputSettings settings = BasisSetupIO.CreateOrUpdateAsset<InputSettings>(
                SettingsPath, mode, s => s.updateMode = InputSettings.UpdateMode.ProcessEventsManually,
                out bool settingsChanged);
            changed |= settingsChanged;
            BasisSetupIO.ForceGuid(SettingsPath, SettingsGuid);

            EditorBuildSettings.AddConfigObject(SettingsConfigKey, settings, true);

            InputActionAsset actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(ActionsPath);
            if (actions != null)
            {
                EditorBuildSettings.AddConfigObject(ActionsConfigKey, actions, true);
            }

            return changed;
        }
    }
}
