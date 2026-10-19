using System.Runtime.InteropServices;

namespace OpusSharp.Core
{
    internal static class OpusRuntime
    {
        public static bool ShouldUseStaticImports(bool? useStatic)
        {
            return useStatic ?? IsStaticallyLinkedPlatform();
        }

        private static bool IsStaticallyLinkedPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return true;
#else
            return RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS"));
#endif
        }
    }
}
