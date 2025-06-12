using UnityEditor;
using System.Reflection;

public static class ModelImporterExtensions
{
    /// <summary>
    /// Checks if the internal Legacy Blend Shape Normals flag is enabled on the given ModelImporter.
    /// </summary>
    /// <param name="importer">The ModelImporter to check.</param>
    /// <returns>True if enabled; otherwise, false.</returns>
    public static bool IsLegacyBlendShapeNormalsEnabled(ModelImporter importer)
    {
        if (importer == null)
        {
            throw new System.ArgumentNullException(nameof(importer));
        }

        // Use reflection to get the internal property.
        PropertyInfo legacyProperty = typeof(ModelImporter).GetProperty(
            "legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (legacyProperty != null)
        {
            // Retrieve the property's value.
            object value = legacyProperty.GetValue(importer, null);
            if (value is bool isEnabled)
            {
                return isEnabled;
            }
        }

        // If the property is not found or value cannot be determined, assume false.
        return false;
    }
}
