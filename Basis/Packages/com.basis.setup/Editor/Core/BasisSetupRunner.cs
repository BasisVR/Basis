using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Basis.Setup
{
    public static class BasisSetupRunner
    {
        public static List<BasisSetupReport> ApplyAll(BasisSetupMode mode)
        {
            var reports = new List<BasisSetupReport>();
            foreach (IBasisSetupModule module in BasisSetupRegistry.Modules)
            {
                reports.Add(module.Apply(mode));
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            LogSummary(reports, mode);
            return reports;
        }

        public static BasisSetupReport ApplyOne(IBasisSetupModule module, BasisSetupMode mode)
        {
            BasisSetupReport report = module.Apply(mode);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return report;
        }

        [MenuItem("Basis/Setup/Create Missing Assets", false, 20)]
        public static void CreateMissingMenu()
        {
            ApplyAll(BasisSetupMode.EnsureExists);
        }

        [MenuItem("Basis/Setup/Update All To Latest", false, 21)]
        public static void UpdateAllMenu()
        {
            bool go = EditorUtility.DisplayDialog(
                "Basis Setup",
                "Regenerate every managed Basis config asset to its latest shipped default?\n\n" +
                "Existing files are copied to BasisSetupBackups/ before being overwritten. " +
                "Additive files (link.xml, Addressables groups) only gain missing entries.",
                "Update All",
                "Cancel");
            if (go)
            {
                ApplyAll(BasisSetupMode.Update);
            }
        }

        private static void LogSummary(List<BasisSetupReport> reports, BasisSetupMode mode)
        {
            int created = 0;
            int updated = 0;
            int errors = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"[BasisSetup] {mode} finished.");
            for (int i = 0; i < reports.Count; i++)
            {
                BasisSetupReport r = reports[i];
                if (r.Status == BasisSetupStatus.Error)
                {
                    errors++;
                }
                else if (r.Changed)
                {
                    if (r.Message == "Created.")
                    {
                        created++;
                    }
                    else
                    {
                        updated++;
                    }
                }

                if (r.Changed || r.Status == BasisSetupStatus.Error)
                {
                    sb.AppendLine($"  {r.Key}: {r.Message}");
                }
            }

            sb.AppendLine($"Created {created}, updated {updated}, errors {errors}.");
            if (errors > 0)
            {
                Debug.LogWarning(sb.ToString());
            }
            else
            {
                Debug.Log(sb.ToString());
            }
        }
    }
}
