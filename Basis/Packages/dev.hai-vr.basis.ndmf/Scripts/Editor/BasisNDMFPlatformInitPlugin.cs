using HVR.Basis.NDMF;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(BasisNDMFPlatformInitPlugin))]
namespace HVR.Basis.NDMF
{
    [RunsOnPlatforms("org.basisvr.basis-framework")]
    public class BasisNDMFPlatformInitPlugin : Plugin<BasisNDMFPlatformInitPlugin>
    {
        protected override void Configure()
        {
            InPhase(BuildPhase.PlatformInit).Run("Log", _ =>
            {
                BasisDebug.Log("Executing NDMF for the Basis Framework", BasisDebug.LogTag.Avatar);
            });
        }
    }
}
