using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Jinxxy.Item.XProp
{
    /// <summary>
    /// Provides default validators for the reserved XProp facets defined in the v0.1.0 specification
    /// </summary>
    public static class XPropFacetValidators
    {

        /// <summary>
        /// Defines a material property with its shader name and valid type hints
        /// </summary>
        private struct XPropMatProperty
        {
            public string InterfaceName { get; set; }
            public string EngineName { get; set; }
            public string[] ValidTypeHints { get; set; }
        }

        public struct XPropProperty
        {
            public string Name;
            public string[] ValidTypeHints;
            public bool AllowAccessors;


            public XPropProperty(string name, string[] validTypeHints, bool allowAccessors = true)
            {
                Name = name;
                ValidTypeHints = validTypeHints;
                AllowAccessors = allowAccessors;
            }

            public XPropProperty(string name) : this(name, Array.Empty<string>(), true) { }
            public XPropProperty(string name, string validType) : this(name, new[] { validType }, true) { }

        }

        /// <summary>
        /// Defines which component accessors are valid for each type
        /// </summary>
        private static readonly Dictionary<string, string[]> TypeAccessors = new()
        {
            { "color", new[] { "r", "g", "b", "a" } },
            { "float2", new[] { "x", "y" } },
            { "float3", new[] { "x", "y", "z", "r", "g", "b" } },  // Allow both xyz and rgb for compatibility
            { "float4", new[] { "x", "y", "z", "w", "r", "g", "b", "a" } },
            { "quat", new[] { "x", "y", "z", "w" } }
        };

        // Valid property names for each reserved facet
        private static readonly XPropProperty[] XformProperties = new XPropProperty[]
        {
            new("position","float3"),
            new("rotation", "quat"),
            new("rotation_euler", "float3"),
            new("scale", "float3"),
            new("scale_uniform", "float"),
            new("world_position", "float3"),
            new("world_rotation", "quat")
        };

        private static readonly XPropProperty[] RenderProperties = new XPropProperty[]
        {
            new("visible", "bool"),
        };
        // 1:1 mapping: InterfaceName -> UnityShaderName
        private static readonly Dictionary<string, string> MatInterfaceToShaderName = new()
        {
            { "unity_standard", "Standard" },
            { "unity_unlit", "Unlit" },
            { "urp_lit", "Universal Render Pipeline/Lit" },
            { "urp_unlit", "Universal Render Pipeline/Unlit" }
        };

        // ShaderInterface -> Properties with type information
        private static readonly Dictionary<string, XPropMatProperty[]> MatShaderInterfacesTyped =
            new()
            {
            {
                "unity_standard", new XPropMatProperty[]
                {
                    new() { InterfaceName = "color", EngineName = "_Color", ValidTypeHints = new[] { "rgba", "float4" } },
                    new() { InterfaceName = "emission", EngineName = "_EmissionColor", ValidTypeHints = new[] { "float3", "rgb" } },
                    new() { InterfaceName = "emissionIntensity", EngineName = "_EmissionIntensity", ValidTypeHints = new[] { "float" } },
                    new() { InterfaceName = "metallic", EngineName = "_Metallic", ValidTypeHints = new[] { "float" } },
                    new() { InterfaceName = "roughness", EngineName = "_Roughness", ValidTypeHints = new[] { "float" } },
                    new() { InterfaceName = "smoothness", EngineName = "_Smoothness", ValidTypeHints = new[] { "float" } },
                    new() { InterfaceName = "opacity", EngineName = "_Alpha", ValidTypeHints = new[] { "float" } },
                    new() { InterfaceName = "cutoff", EngineName = "_Cutoff", ValidTypeHints = new[] { "float" } },
                    new() { InterfaceName = "tiling", EngineName = "_MainTex_ST", ValidTypeHints = new[] { "float2" } },
                    new() { InterfaceName = "offset", EngineName = "_MainTex_ST", ValidTypeHints = new[] { "float2" } }
                }
            },
            {
                "unity_unlit", new XPropMatProperty[]
                {
                    new() { InterfaceName = "color", EngineName = "_Color", ValidTypeHints = new[] { "rgba", "float4" } },
                    new() { InterfaceName = "emission", EngineName = "_EmissionColor", ValidTypeHints = new[] { "float3", "rgb" } },
                    new() { InterfaceName = "tiling", EngineName = "_MainTex_ST", ValidTypeHints = new[] { "float2" } },
                    new() { InterfaceName = "offset", EngineName = "_MainTex_ST", ValidTypeHints = new[] { "float2" } }
                }
            },
            {
                "urp_lit", new XPropMatProperty[]
                {
                    new() { InterfaceName = "color", EngineName = "_BaseColor", ValidTypeHints = new[] { "rgba", "float4" } },
                    new() { InterfaceName = "emission", EngineName = "_EmissionColor", ValidTypeHints = new[] { "float3", "rgb" } },
                    new() { InterfaceName = "emissionIntensity", EngineName = "_EmissionIntensity", ValidTypeHints = new[] { "float" } },
                    new() { InterfaceName = "metallic", EngineName = "_Metallic", ValidTypeHints = new[] { "float" } },
                    new() { InterfaceName = "smoothness", EngineName = "_Smoothness", ValidTypeHints = new[] { "float" } },
                    new() { InterfaceName = "opacity", EngineName = "_BaseColor", ValidTypeHints = new[] { "float" } },  // Alpha channel
                    new() { InterfaceName = "cutoff", EngineName = "_Cutoff", ValidTypeHints = new[] { "float" } },
                    new() { InterfaceName = "tiling", EngineName = "_BaseMap_ST", ValidTypeHints = new[] { "float2" } },
                    new() { InterfaceName = "offset", EngineName = "_BaseMap_ST", ValidTypeHints = new[] { "float2" } }
                }
            },
            {
                "urp_unlit", new XPropMatProperty[]
                {
                    new() { InterfaceName = "color", EngineName = "_BaseColor", ValidTypeHints = new[] { "rgba", "float4" } },
                    new() { InterfaceName = "tiling", EngineName = "_BaseMap_ST", ValidTypeHints = new[] { "float2" } },
                    new() { InterfaceName = "offset", EngineName = "_BaseMap_ST", ValidTypeHints = new[] { "float2" } }
                }
            }
        };

        // ShaderInterface -> StandardProperty -> ActualPropertyName
        private static readonly Dictionary<string, Dictionary<string, string>> MatShaderInterfaces = new();

        // Pre-computed flat set of all standard material properties (for fast validation)
        private static readonly HashSet<string> MatStandardProperties;

        // Pre-computed interface -> property set mapping (for fast containment checks)
        private static readonly Dictionary<string, HashSet<string>> MatInterfacePropertySets;

        // Type hints
        private static readonly string[] TypeFloat = { "float" };
        private static readonly string[] TypeFloat3 = { "float3" };
        private static readonly string[] TypeQuat = { "quat" };
        private static readonly string[] TypeBool = { "bool" };

        private static readonly Dictionary<string, string[]> XformTypeHints = new()
        {
            { "position", TypeFloat3 }, { "position.", TypeFloat },
            { "rotation", TypeQuat }, { "rotation.", TypeFloat },
            { "rotation_euler", TypeFloat3 }, { "rotation_euler.", TypeFloat },
            { "scale", TypeFloat3 }, { "scale.", TypeFloat },
            { "scale_uniform", TypeFloat },
            { "world_position", TypeFloat3 }, { "world_position.", TypeFloat },
            { "world_rotation", TypeQuat }, { "world_rotation.", TypeFloat },
        };

        private static readonly Dictionary<string, string[]> RenderTypeHints = new()
        {
            { "visible", TypeBool },
        };

        // Auto-generated type hints from MatShaderInterfacesTyped (populated in static constructor)
        private static readonly Dictionary<string, string[]> MatTypeHints;

        /// <summary>
        /// Static constructor to pre-compute optimized lookup tables from typed material property definitions
        /// </summary>
        static XPropFacetValidators()
        {
            MatStandardProperties = new HashSet<string>();
            MatInterfacePropertySets = new Dictionary<string, HashSet<string>>();
            MatTypeHints = new Dictionary<string, string[]>();

            foreach (var (interfaceName, properties) in MatShaderInterfacesTyped)
            {
                var propertyMap = new Dictionary<string, string>();
                var propertySet = new HashSet<string>();

                foreach (var prop in properties)
                {
                    var name = prop.InterfaceName;
                    propertyMap[name] = prop.EngineName;
                    propertySet.Add(name);
                    MatStandardProperties.Add(name);

                    // Store type hints directly (no HashSet overhead)
                    if (!MatTypeHints.ContainsKey(name))
                        MatTypeHints[name] = prop.ValidTypeHints;

                    // Generate component accessors from primary type
                    if (TypeAccessors.TryGetValue(prop.ValidTypeHints[0], out var accessors))
                    {
                        foreach (var accessor in accessors)
                        {
                            var fullPath = name + "." + accessor;
                            MatStandardProperties.Add(fullPath);
                            propertySet.Add(fullPath);
                            if (!MatTypeHints.ContainsKey(fullPath))
                                MatTypeHints[fullPath] = TypeFloat;
                        }

                        var pattern = name + ".";
                        if (!MatTypeHints.ContainsKey(pattern))
                            MatTypeHints[pattern] = TypeFloat;
                    }
                }

                MatShaderInterfaces[interfaceName] = propertyMap;
                MatInterfacePropertySets[interfaceName] = propertySet;
            }
        }

        /// <summary>
        /// Validator for the 'xform' facet (transform/spatial properties)
        /// </summary>
        public static bool ValidateXform(string facet, string[] qualifier, string typeHint, string propertyPath)
            => ValidateNoQualifierFacet(qualifier, propertyPath, typeHint, XformProperties, XformTypeHints);

        /// <summary>
        /// Validator for the 'mat' facet (material/surface properties)
        /// - Qualifier: (slot) or (slot,...) where slot is 0-based integer
        /// - Properties: color, emission, metallic, roughness, opacity, tiling, offset, etc.
        /// - Type hints: color, float3, float2, float
        /// </summary>
        public static bool ValidateMat(string facet, string[] qualifier, string typeHint, string propertyPath)
        {
            // mat must have at least one qualifier (material slot index)
            if (qualifier == null || qualifier.Length == 0)
            {
                return false;
            }

            // First qualifier must be a valid non-negative integer (slot index)
            if (!int.TryParse(qualifier[0], out int slot) || slot < 0)
            {
                return false;
            }

            string shaderInterface = qualifier.Length >= 2 ? qualifier[1] : null;

            // Additional qualifier parameters are application-defined (shader hints, etc.)
            // We don't validate them here

            // Fast standard property check using optimized lookup
            bool isStandardProperty = IsValidMatProperty(shaderInterface, propertyPath);

            // If it's a standard property, validate the type hint
            if (isStandardProperty && !string.IsNullOrEmpty(typeHint))
            {
                return ValidateTypeHintForProperty(propertyPath, typeHint, MatTypeHints);
            }

            // Allow shader-specific properties with any type hint
            // This supports "Applications MAY support shader-specific properties via native names"
            return true;
        }

        /// <summary>
        /// Validator for the 'script' facet (component/behavior properties)
        /// - Qualifier: (Type) or (Type,index) where Type is component name, index is optional integer
        /// - Properties: any (arbitrary nested with array indices)
        /// - Type hints: any
        /// </summary>
        public static bool ValidateScript(string facet, string[] qualifier, string typeHint, string propertyPath)
        {
            // script must have at least one qualifier (component type name)
            if (qualifier == null || qualifier.Length == 0)
            {
                return false;
            }

            // First qualifier is the component type name - must not be empty
            if (string.IsNullOrWhiteSpace(qualifier[0]))
            {
                return false;
            }

            // Second qualifier (if present) must be a valid non-negative integer (instance index)
            if (qualifier.Length >= 2)
            {
                if (!int.TryParse(qualifier[1], out int index) || index < 0)
                {
                    return false;
                }
            }

            // More than 2 qualifiers is invalid for script facet
            if (qualifier.Length > 2)
            {
                return false;
            }

            // Property path can be arbitrary for script facet
            // Type hint can be any valid type
            // No further validation needed - applications define their own properties

            return true;
        }

        /// <summary>
        /// Validator for the 'render' facet (visibility and render state)
        /// </summary>
        public static bool ValidateRender(string facet, string[] qualifier, string typeHint, string propertyPath)
            => ValidateNoQualifierFacet(qualifier, propertyPath, typeHint, RenderProperties, RenderTypeHints);

        /// <summary>
        /// Common validation for facets that don't allow qualifiers
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ValidateNoQualifierFacet(string[] qualifier, string propertyPath, string typeHint,
            XPropProperty[] allowedProperties, Dictionary<string, string[]> typeHints)
        {
            if (qualifier is { Length: > 0 }) return false;
            if (!ValidatePropertyInSet(propertyPath, allowedProperties)) return false;
            return string.IsNullOrEmpty(typeHint) || ValidateTypeHintForProperty(propertyPath, typeHint, typeHints);
        }

        /// <summary>
        /// Creates a FacetValidationOptions instance with all reserved facet validators
        /// </summary>
        public static FacetValidationOptions CreateReservedFacetValidators()
        {
            var options = new FacetValidationOptions
            {
                AllowUnknownFacets = true  // Allow extension facets by default
            };

            options.AddFacet("xform", ValidateXform);
            options.AddFacet("mat", ValidateMat);
            options.AddFacet("script", ValidateScript);
            options.AddFacet("render", ValidateRender);

            return options;
        }

        /// <summary>
        /// Creates a FacetValidationOptions instance with only reserved facets (no extensions)
        /// </summary>
        public static FacetValidationOptions CreateStrictReservedFacetValidators()
        {
            var options = new FacetValidationOptions
            {
                AllowUnknownFacets = false  // Only allow reserved facets
            };

            options.AddFacet("xform", ValidateXform);
            options.AddFacet("mat", ValidateMat);
            options.AddFacet("script", ValidateScript);
            options.AddFacet("render", ValidateRender);

            return options;
        }

        /// <summary>
        /// Fast lookup to check if a property is valid for a specific material shader interface
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsValidMatProperty(string shaderInterface, string propertyPath)
        {
            // If no interface specified, check against all standard properties
            if (string.IsNullOrEmpty(shaderInterface))
            {
                return MatStandardProperties.Contains(propertyPath);
            }

            // Check specific interface
            if (MatInterfacePropertySets.TryGetValue(shaderInterface, out var props))
            {
                // Handle component accessors (e.g., "color.r")
                if (props.Contains(propertyPath))
                    return true;

                int dotIdx = propertyPath.IndexOf('.');
                if (dotIdx > 0)
                {
                    string baseProp = propertyPath.Substring(0, dotIdx);
                    return props.Contains(baseProp);
                }
            }

            return false;
        }

        /// <summary>
        /// Fast lookup to get actual shader property name for a standard property
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetActualPropertyName(string shaderInterface, string shaderName, out string actualName)
        {
            actualName = null;

            if (string.IsNullOrEmpty(shaderInterface))
            {
                // No interface specified - try first interface that has this property
                foreach (var kvp in MatShaderInterfaces)
                {
                    if (kvp.Value.TryGetValue(shaderName, out actualName))
                        return true;
                }
                return false;
            }

            // Specific interface
            if (MatShaderInterfaces.TryGetValue(shaderInterface, out var props))
            {
                return props.TryGetValue(shaderName, out actualName);
            }

            return false;
        }

        /// <summary>
        /// Helper method to validate a property path against a set of allowed properties.
        /// Supports multi-level accessors (e.g., "position.x", "rotation.euler.y") and respects
        /// the AllowAccessors flag on each property.
        /// </summary>
        private static bool ValidatePropertyInSet(string propertyPath, XPropProperty[] allowedProperties)
        {
            if (string.IsNullOrEmpty(propertyPath))
            {
                return false;
            }

            // Try exact match first (most common case)
            for (int i = 0; i < allowedProperties.Length; i++)
            {
                if (allowedProperties[i].Name == propertyPath)
                {
                    return true;
                }
            }

            // No dots = not an accessor pattern
            int firstDotIndex = propertyPath.IndexOf('.');
            if (firstDotIndex <= 0)
            {
                return false;
            }

            // Check all possible prefixes from longest to shortest
            // e.g., for "rotation.euler.x", check: "rotation.euler", then "rotation"
            int dotIndex = propertyPath.LastIndexOf('.');
            int depth = 0;
            while (dotIndex > 0 && depth < XPropParser.MAX_PROPERTY_DEPTH)
            {
                string basePath = propertyPath[..dotIndex];

                // Check if this base path matches an allowed property with accessors enabled
                for (int i = 0; i < allowedProperties.Length; i++)
                {
                    if (allowedProperties[i].Name == basePath && allowedProperties[i].AllowAccessors)
                    {
                        return true;
                    }
                }

                // Move to next shorter prefix
                dotIndex = propertyPath.LastIndexOf('.', dotIndex - 1);
                depth++;
            }

            return false;
        }

        /// <summary>
        /// Helper method to validate type hint for a given property
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ValidateTypeHintForProperty(string propertyPath, string typeHint, Dictionary<string, string[]> typeHintMap)
        {
            if (string.IsNullOrEmpty(typeHint)) return true;

            // Try exact match first
            if (typeHintMap.TryGetValue(propertyPath, out var allowed) && ArrayContains(allowed, typeHint))
                return true;

            // Try pattern match for component accessors (e.g., "position.x" -> "position.")
            int lastDot = propertyPath.LastIndexOf('.');
            if (lastDot > 0)
            {
                if (typeHintMap.TryGetValue(propertyPath[..(lastDot + 1)], out allowed) && ArrayContains(allowed, typeHint))
                    return true;
                if (typeHintMap.TryGetValue(propertyPath[..lastDot], out allowed) && ArrayContains(allowed, typeHint))
                    return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ArrayContains(string[] arr, string value)
        {
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] == value) return true;
            return false;
        }

        /// <summary>
        /// Gets a validator function for a specific reserved facet by name
        /// </summary>
        public static FacetValidator GetValidatorForFacet(string facetName)
        {
            return facetName switch
            {
                "xform" => ValidateXform,
                "mat" => ValidateMat,
                "script" => ValidateScript,
                "render" => ValidateRender,
                _ => null
            };
        }

        /// <summary>
        /// Checks if a facet name is a reserved facet
        /// </summary>
        public static bool IsReservedFacet(string facetName)
        {
            return facetName == "xform" || facetName == "mat" ||
                   facetName == "script" || facetName == "render";
        }

        /// <summary>
        /// Gets the Unity shader name for a given material interface name
        /// </summary>
        /// <param name="interfaceName">The interface name (e.g., "standard", "urp")</param>
        /// <param name="shaderName">The Unity shader name (e.g., "Standard", "Universal Render Pipeline/Lit")</param>
        /// <returns>True if the interface name is known</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetShaderName(string interfaceName, out string shaderName)
        {
            return MatInterfaceToShaderName.TryGetValue(interfaceName, out shaderName);
        }

        /// <summary>
        /// Gets all registered material interface names
        /// </summary>
        public static IEnumerable<string> GetRegisteredMaterialInterfaces()
        {
            return MatInterfaceToShaderName.Keys;
        }

        /// <summary>
        /// Checks if a material interface name is registered
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsRegisteredMaterialInterface(string interfaceName)
        {
            return MatInterfaceToShaderName.ContainsKey(interfaceName);
        }

        /// <summary>
        /// Gets valid type hints for a material property. Returns null if property is unknown.
        /// </summary>
        /// <param name="propertyPath">The property path (e.g., "color", "color.r", "tiling")</param>
        /// <returns>Array of valid type hint strings, or null if property is not recognized</returns>
        public static string[] GetValidTypeHintsForMatProperty(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath)) return null;

            if (MatTypeHints.TryGetValue(propertyPath, out var hints))
                return hints;

            int lastDot = propertyPath.LastIndexOf('.');
            if (lastDot > 0 && MatTypeHints.TryGetValue(propertyPath[..(lastDot + 1)], out hints))
                return hints;

            return null;
        }

        /// <summary>
        /// Gets all standard material properties with their valid type hints
        /// </summary>
        public static Dictionary<string, string[]> GetAllMatPropertyTypeHints()
        {
            var result = new Dictionary<string, string[]>();
            foreach (var (key, value) in MatTypeHints)
                if (!key.EndsWith('.')) result[key] = value;
            return result;
        }
    }
}
