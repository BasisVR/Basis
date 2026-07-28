using System.Runtime.InteropServices;

namespace Klak.Syphon.Interop {

public static class PluginCommon
{
    #if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX

    [DllImport("KlakSyphon", EntryPoint = "Plugin_EnableColorSpaceConversion")]
    public static extern void EnableColorSpaceConversion();

    [DllImport("KlakSyphon", EntryPoint = "Plugin_DisableColorSpaceConversion")]
    public static extern void DisableColorSpaceConversion();

    #else

    public static void EnableColorSpaceConversion() {}
    public static void DisableColorSpaceConversion() {}

    #endif
}

} // namespace Klak.Sypho.Interopn
