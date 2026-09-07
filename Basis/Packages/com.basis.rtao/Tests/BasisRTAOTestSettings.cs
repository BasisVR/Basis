using UnityEngine;

namespace Basis.Rendering.RTAO.Tests
{
    internal static class BasisRTAOTestSettings
    {
        /// <summary>
        /// The shipping default traces the two avatar layers only, because that is all this system is for.
        /// Tests that put a cube or a floor in front of the camera are about geometry and filtering rather
        /// than about that policy, so they ask for every layer explicitly. A test that quietly depended on
        /// the default being permissive would start passing or failing for reasons it never stated.
        /// </summary>
        public static BasisRTAOSceneSettings EveryLayer
        {
            get
            {
                BasisRTAOSceneSettings settings = BasisRTAOSceneSettings.Default;
                settings.layerMask = ~0;
                return settings;
            }
        }
    }
}
