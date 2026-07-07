using UnityEditor;

namespace Basis.Setup
{
    internal sealed class BasisSetupPostprocessor : AssetPostprocessor
    {
        private static bool _running;

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (_running || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                return;
            }

            bool any = false;
            foreach (IBasisSetupModule module in BasisSetupRegistry.Modules)
            {
                if (module.AutoApplyOnImport && module.IsAvailable)
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                return;
            }

            _running = true;
            try
            {
                foreach (IBasisSetupModule module in BasisSetupRegistry.Modules)
                {
                    if (module.AutoApplyOnImport && module.IsAvailable)
                    {
                        module.Apply(BasisSetupMode.EnsureExists);
                    }
                }
            }
            finally
            {
                _running = false;
            }
        }
    }
}
