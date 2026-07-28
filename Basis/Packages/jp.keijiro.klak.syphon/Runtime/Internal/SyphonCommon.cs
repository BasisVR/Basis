using UnityEngine;
using Plugin = Klak.Syphon.Interop.PluginCommon;

namespace Klak.Syphon {

static class InternalCommon
{
    // Apply the current color space setting.
    // Actually this is needed only once, but we do every time for simplicity.
    public static void ApplyCurrentColorSpace()
    {
        if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            Plugin.EnableColorSpaceConversion();
        else
            Plugin.DisableColorSpaceConversion();
    }
}

} // namespace Klak.Syphon
