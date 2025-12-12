using System.Collections.Generic;

namespace Jinxxy.Item.XProp
{
    /// <summary>
    /// Maps XProp standard property names to Unity-specific shader property names
    /// and provides reverse mapping for serialization
    /// </summary>
    public static class XPropPropertyMapping
    {
        /// <summary>
        /// XProp standard material property names to common Unity shader property names
        /// </summary>
        private static readonly Dictionary<string, string[]> StandardToUnityMaterial = new Dictionary<string, string[]>
        {
            // Color properties
            { "color", new[] { "_Color", "_BaseColor", "_MainColor", "_Main_Color" } },

            // Emission properties
            { "emission", new[] { "_EmissionColor", "_Emission" } },
            { "emissionIntensity", new[] { "_EmissionIntensity", "_EmissionStrength" } },

            // PBR properties
            { "metallic", new[] { "_Metallic", "_MetallicGlossMap" } },
            { "roughness", new[] { "_Roughness" } },
            { "smoothness", new[] { "_Smoothness", "_Glossiness" } },

            // Transparency
            { "opacity", new[] { "_Alpha", "_Opacity" } },
            { "cutoff", new[] { "_Cutoff", "_AlphaCutoff" } },

            // UV properties
            { "tiling", new[] { "_MainTex_ST", "_BaseMap_ST" } },  // Note: ST = Scale/Tiling
            { "offset", new[] { "_MainTex_ST", "_BaseMap_ST" } },  // Note: ST contains both
        };

        /// <summary>
        /// Unity shader property names to XProp standard names
        /// </summary>
        private static readonly Dictionary<string, string> UnityToStandardMaterial;

        static XPropPropertyMapping()
        {
            // Build reverse mapping
            UnityToStandardMaterial = new Dictionary<string, string>();
            foreach (var kvp in StandardToUnityMaterial)
            {
                foreach (var unityName in kvp.Value)
                {
                    if (!UnityToStandardMaterial.ContainsKey(unityName))
                    {
                        UnityToStandardMaterial[unityName] = kvp.Key;
                    }
                }
            }
        }

        /// <summary>
        /// Tries to resolve an XProp standard property name to a Unity shader property name
        /// by checking if the material has any of the known Unity equivalents
        /// </summary>
        public static bool TryResolveToUnityProperty(string xpropPropertyName, UnityEngine.Material material, out string unityPropertyName)
        {
            unityPropertyName = null;

            // Direct match first
            if (material.HasProperty(xpropPropertyName))
            {
                unityPropertyName = xpropPropertyName;
                return true;
            }

            // Try standard mappings
            if (StandardToUnityMaterial.TryGetValue(xpropPropertyName, out var candidates))
            {
                foreach (var candidate in candidates)
                {
                    if (material.HasProperty(candidate))
                    {
                        unityPropertyName = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Tries to convert a Unity shader property name to XProp standard name
        /// </summary>
        public static bool TryConvertToStandardProperty(string unityPropertyName, out string xpropPropertyName)
        {
            // Check if it's already a standard name
            if (StandardToUnityMaterial.ContainsKey(unityPropertyName))
            {
                xpropPropertyName = unityPropertyName;
                return true;
            }

            // Try reverse mapping
            if (UnityToStandardMaterial.TryGetValue(unityPropertyName, out xpropPropertyName))
            {
                return true;
            }

            // Not a standard property, keep Unity name
            xpropPropertyName = unityPropertyName;
            return false;
        }

        /// <summary>
        /// Gets the appropriate property name for serialization.
        /// Prefers XProp standard names when available, otherwise uses Unity-specific names.
        /// </summary>
        public static string GetSerializedPropertyName(string unityPropertyName)
        {
            TryConvertToStandardProperty(unityPropertyName, out string result);
            return result;
        }

        /// <summary>
        /// Checks if a property name is an XProp standard property
        /// </summary>
        public static bool IsStandardProperty(string propertyName)
        {
            return StandardToUnityMaterial.ContainsKey(propertyName);
        }

        /// <summary>
        /// Maps XProp component accessors to their indices
        /// </summary>
        public static bool TryParseComponentAccessor(string accessor, out int componentIndex, out bool isColorChannel)
        {
            componentIndex = -1;
            isColorChannel = false;

            switch (accessor.ToLower())
            {
                case ".x":
                case ".r":
                    componentIndex = 0;
                    isColorChannel = accessor == ".r";
                    return true;
                case ".y":
                case ".g":
                    componentIndex = 1;
                    isColorChannel = accessor == ".g";
                    return true;
                case ".z":
                case ".b":
                    componentIndex = 2;
                    isColorChannel = accessor == ".b";
                    return true;
                case ".w":
                case ".a":
                    componentIndex = 3;
                    isColorChannel = accessor == ".a";
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Gets the component accessor string from index
        /// </summary>
        public static string GetComponentAccessor(int componentIndex, bool isColorChannel)
        {
            if (componentIndex < 0 || componentIndex > 3)
                return string.Empty;

            if (isColorChannel)
            {
                return new[] { ".r", ".g", ".b", ".a" }[componentIndex];
            }
            else
            {
                return new[] { ".x", ".y", ".z", ".w" }[componentIndex];
            }
        }
    }
}
