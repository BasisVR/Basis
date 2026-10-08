using Basis.Scripts.Rendering;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Basis.Scripts.Editor
{
    public sealed class BasisLightingSolutionsBuildActivation : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platformGroup == BuildTargetGroup.Standalone && report.summary.GetSubtarget<StandaloneBuildSubtarget>() == StandaloneBuildSubtarget.Server) { return; }
            BasisLightingSolutions.ActivateForBuild(report.summary.platform != BuildTarget.WebGL);
        }

        public void OnPostprocessBuild(BuildReport report) => BasisLightingSolutions.RestoreAfterBuild();
    }
}
