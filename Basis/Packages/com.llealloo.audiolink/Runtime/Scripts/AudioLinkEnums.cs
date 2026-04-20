using UnityEngine;

namespace AudioLink
{
    public enum AudioLinkBand
    {
        [InspectorName("Bass")] BASS = 0,
        [InspectorName("Low Mid")] LOWMID = 1,
        [InspectorName("High Mid")] HIGHMID = 2,
        [InspectorName("Treble")] TREBLE = 3
    }

    public enum AudioReactiveLightColorMode
    {
        [InspectorName("Static Color")] STATIC,
        [InspectorName("Theme Color 0")] THEME0,
        [InspectorName("Theme Color 1")] THEME1,
        [InspectorName("Theme Color 2")] THEME2,
        [InspectorName("Theme Color 3")] THEME3
    }
}
