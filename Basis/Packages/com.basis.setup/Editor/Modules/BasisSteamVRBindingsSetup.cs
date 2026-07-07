using System.IO;

namespace Basis.Setup.Modules
{
    public sealed partial class BasisSteamVRBindingsSetup : BasisSetupModuleBase
    {
        private const string Folder = "Assets/StreamingAssets/SteamVR";

        public override string Key => "steamvr.bindings";
        public override string DisplayName => "SteamVR Action & Binding Files";
        public override string Category => "SteamVR";
        public override int Version => 1;

        protected override bool Exists()
        {
            return File.Exists(BasisSetupIO.ToFullPath(Folder + "/actions.json"));
        }

        protected override bool Build(BasisSetupMode mode)
        {
            BasisSetupIO.EnsureAssetFolder(Folder);

            bool changed = false;
            for (int i = 0; i < Files.Length; i++)
            {
                changed |= BasisSetupIO.WriteTextAsset(Folder + "/" + Files[i].Name, Files[i].Content, mode);
            }

            return changed;
        }
    }
}
