using System.Runtime.InteropServices;

namespace OpusSharp.Core
{
    internal static class OpusRuntime
    {
        public static bool ShouldUseStaticImports(bool? useStatic)
        {
            if (useStatic.HasValue)
            {
                return useStatic.Value;
            }

            return IsStaticallyLinkedPlatform();
        }

        private static bool IsStaticallyLinkedPlatform()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS"));
        }
    }
}
