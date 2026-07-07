using System.Collections.Generic;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace Basis.Setup.Modules
{
    public sealed class BasisAddressablesSetup : BasisSetupModuleBase
    {
        private const string DefaultGroupName = "Basis Foundation Assets";
        private const string LanguageLabel = "language";

        private static readonly (string Name, BundledAssetGroupSchema.BundlePackingMode Packing)[] Groups =
        {
            ("Basis Fonts", BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            ("Basis Foundation Assets", BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            ("Basis Gizmos", BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            ("Basis Localization", BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            ("Basis MediaPipe Models", BundledAssetGroupSchema.BundlePackingMode.PackSeparately),
            ("Basis OpenLipSync", BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            ("Basis Shared", BundledAssetGroupSchema.BundlePackingMode.PackTogether),
            ("Basis UI Assets", BundledAssetGroupSchema.BundlePackingMode.PackTogether),
        };

        public override string Key => "addressables.settings";
        public override string DisplayName => "Addressables Settings & Groups";
        public override string Category => "Addressables";
        public override int Version => 1;

        protected override bool Exists()
        {
            return AddressableAssetSettingsDefaultObject.SettingsExists;
        }

        protected override bool Build(BasisSetupMode mode)
        {
            bool existedBefore = AddressableAssetSettingsDefaultObject.SettingsExists;
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            if (settings == null)
            {
                return false;
            }

            bool changed = !existedBefore;

            for (int i = 0; i < Groups.Length; i++)
            {
                changed |= GetOrCreate(settings, Groups[i].Name, Groups[i].Packing);
            }

            AddressableAssetGroup defaultGroup = settings.FindGroup(DefaultGroupName);
            if (defaultGroup != null && settings.DefaultGroup != defaultGroup)
            {
                settings.DefaultGroup = defaultGroup;
                changed = true;
            }

            List<string> labels = settings.GetLabels();
            if (labels == null || !labels.Contains(LanguageLabel))
            {
                settings.AddLabel(LanguageLabel, false);
                changed = true;
            }

            settings.NonRecursiveBuilding = true;
            settings.ContiguousBundles = true;
            settings.UniqueBundleIds = true;
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer;

            if (changed)
            {
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
            }

            return changed;
        }

        private static bool GetOrCreate(AddressableAssetSettings settings, string name,
            BundledAssetGroupSchema.BundlePackingMode packing)
        {
            AddressableAssetGroup group = settings.FindGroup(name);
            bool created = group == null;
            if (group == null)
            {
                group = settings.CreateGroup(name, false, false, false, null,
                    typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            }

            BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema == null)
            {
                schema = group.AddSchema<BundledAssetGroupSchema>();
            }

            if (group.GetSchema<ContentUpdateGroupSchema>() == null)
            {
                group.AddSchema<ContentUpdateGroupSchema>();
            }

            schema.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            schema.BundleMode = packing;
            schema.IncludeInBuild = true;
            schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            return created;
        }
    }
}
