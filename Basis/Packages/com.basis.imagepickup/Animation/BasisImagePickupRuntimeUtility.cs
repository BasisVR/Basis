using UnityEngine;

namespace Basis.ImagePickup
{
    internal static class BasisImagePickupRuntimeUtility
    {
        public const int RequiredAnimationCompositorPassCount = 4;

        public static bool CanUseAnimationCompositorShader(Shader shader)
        {
            return shader != null
                && shader.isSupported
                && shader.passCount >= RequiredAnimationCompositorPassCount;
        }
    }
}
