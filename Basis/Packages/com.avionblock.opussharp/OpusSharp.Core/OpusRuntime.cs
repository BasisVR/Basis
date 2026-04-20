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
            return RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS"));
        }
    }
}
