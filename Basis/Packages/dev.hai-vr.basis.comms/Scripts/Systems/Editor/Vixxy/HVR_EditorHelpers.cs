using System.Collections.Generic;
using UnityEngine;

namespace HVR.Vixxy.Editor
{
    public static class HVR_EditorHelpers
    {
        public const string CrossSymbol = "×";
        public const string PlusSymbol = "+";
        public const string SwapSymbol = "⇅";
        public const string GroupBoxStyle = "GroupBox";

        public static List<string> ListAllBlendshapes(SkinnedMeshRenderer renderer)
        {
            var results = new List<string>();
            if (renderer.sharedMesh is { } mesh)
            {
                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    results.Add(mesh.GetBlendShapeName(i));
                }
            }
            return results;
        }

        /// Returns a dictionary of Float, Int, Vector, Texture properties.
        public static Dictionary<MaterialPropertyType, List<string>> ListMostMaterialProperties(Renderer renderer)
        {
            var results = new Dictionary<MaterialPropertyType, List<string>>();

            // We don't want to store property names that already exist
            var existingStrings = new HashSet<string>();

            foreach (var propertyType in new[] { MaterialPropertyType.Float, MaterialPropertyType.Int, MaterialPropertyType.Vector, MaterialPropertyType.Texture })
            {
                var propertyNamesOfThisType = new List<string>();
                results.Add(propertyType, propertyNamesOfThisType);

                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;

                    foreach (var propertyName in material.GetPropertyNames(propertyType))
                    {
                        if (!existingStrings.Contains(propertyName))
                        {
                            existingStrings.Add(propertyName);
                            propertyNamesOfThisType.Add(propertyName);
                        }
                    }
                }
            }

            return results;
        }
    }
}
