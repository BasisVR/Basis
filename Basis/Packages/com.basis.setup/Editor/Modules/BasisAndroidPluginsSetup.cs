using System.IO;

namespace Basis.Setup.Modules
{
    public sealed partial class BasisAndroidPluginsSetup : BasisSetupModuleBase
    {
        private const string Folder = "Assets/Plugins/Android";
        private const string ManifestPath = Folder + "/AndroidManifest.xml";

        public override string Key => "android.plugins";
        public override string DisplayName => "Android Manifest & Gradle Templates";
        public override string Category => "Android";
        public override int Version => 1;

        protected override bool Exists()
        {
            return File.Exists(BasisSetupIO.ToFullPath(ManifestPath));
        }

        protected override bool Build(BasisSetupMode mode)
        {
            BasisSetupIO.EnsureAssetFolder(Folder);

            bool changed = false;
            changed |= BasisSetupIO.WriteTextAsset(ManifestPath, ActiveManifest, mode);
            changed |= BasisSetupIO.WriteTextAsset(Folder + "/AndroidManifest.xml.DISABLED", TemplateManifest, mode);
            changed |= BasisSetupIO.WriteTextAsset(Folder + "/LauncherManifest.xml.DISABLED", LauncherManifest, mode);
            changed |= BasisSetupIO.WriteTextAsset(Folder + "/baseProjectTemplate.gradle.DISABLED", BaseProjectTemplate, mode);
            changed |= BasisSetupIO.WriteTextAsset(Folder + "/gradleTemplate.properties.DISABLED", GradleProperties, mode);
            changed |= BasisSetupIO.WriteTextAsset(Folder + "/launcherTemplate.gradle.DISABLED", LauncherTemplate, mode);
            changed |= BasisSetupIO.WriteTextAsset(Folder + "/mainTemplate.gradle.DISABLED", MainTemplate, mode);
            changed |= BasisSetupIO.WriteTextAsset(Folder + "/settingsTemplate.gradle.DISABLED", SettingsTemplate, mode);
            changed |= BasisSetupIO.WriteTextAsset(Folder + "/proguard-user.txt.DISABLED", string.Empty, mode);
            return changed;
        }
    }
}
