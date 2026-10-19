using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEngine;

namespace HVR.Vixxy.Editor
{
    public static class HVR_EditorHelpers
    {
        public const string CrossSymbol = "×";
        public const string PlusSymbol = "+";
        public const string ArrowRightSymbol = "→";
        public const string ArrowUpSymbol = "↑";
        public const string ArrowDownSymbol = "↓";
        public const string GroupBoxStyle = "GroupBox";

        public const float SwapElementWidth = 15;
        public const float PlusButtonWidth = 25;
        public const float DeleteButtonWidth = 20;

        public static List<string> ListAllBlendshapes(SkinnedMeshRenderer renderer)
        {
            var results = new List<string>();
            var mesh = renderer.sharedMesh;
            if (mesh != null)
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

        public static List<Transform> CollectAllNonEditorOnlyTransforms(BasisAvatar avatar)
        {
            return CollectAllNonEditorOnlyTransforms(avatar.transform);
        }

        public static List<Transform> CollectAllNonEditorOnlyTransforms(Transform rootTransform)
        {
            var results = new List<Transform> { rootTransform };
            CollectNonEditorOnlyTransformsRecursive(rootTransform, results);
            return results;
        }

        private static void CollectNonEditorOnlyTransformsRecursive(Transform rootTransform, List<Transform> results)
        {
            foreach (Transform child in rootTransform)
            {
                if (!child.gameObject.CompareTag("EditorOnly"))
                {
                    results.Add(child);
                    CollectNonEditorOnlyTransformsRecursive(child, results);
                }
            }
        }

        public static Type FindEditorOnlyTypeOrNull()
        {
            var assemblies = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.FullName == "nadena.dev.ndmf.INDMFEditorOnly")
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        public static bool TryCaptureProperty(GameObject targetObject, object vixxyProperty, SerializedProperty propertySp, out object result)
        {
            result = null;
            if (targetObject == null) return false;

            var fullClassName = propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyBase.fullClassName)).stringValue;
            if (!HVR_ComponentDictionary.TryGetComponentType(fullClassName, out var componentType)) return false;

            if (!targetObject.TryGetComponent(componentType, out var component)) return false;

            var variant = (HVRVixxyPropertyVariant)propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyBase.variant)).intValue;
            var propertyName = propertySp.FindPropertyRelative(nameof(HVRVixxyPropertyBase.propertyName)).stringValue;
            if (variant == HVRVixxyPropertyVariant.BlendShape)
            {
                if (component is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    var indexOfBlendshape = ListAllBlendshapes(smr).IndexOf(propertyName);
                    if (indexOfBlendshape != -1)
                    {
                        result = smr.GetBlendShapeWeight(indexOfBlendshape);
                        return true;
                    }
                }
            }
            else if (variant == HVRVixxyPropertyVariant.MaterialProperty)
            {
                if (component is Renderer renderer)
                {
                    foreach (var sharedMaterial in renderer.sharedMaterials)
                    {
                        if (sharedMaterial == null) continue;
                        if (sharedMaterial.HasProperty(propertyName))
                        {
                            if (vixxyProperty is HVRVixxyPropertyColor) { result = sharedMaterial.GetColor(propertyName); return true; }
                            if (vixxyProperty is HVRVixxyPropertyColor32) { result = sharedMaterial.GetColor(propertyName); return true; }
                            if (vixxyProperty is HVRVixxyPropertyColorHDR) { result = sharedMaterial.GetColor(propertyName); return true; }
                            if (vixxyProperty is HVRVixxyPropertyFloat) { result = sharedMaterial.GetFloat(propertyName); return true; }
                            if (vixxyProperty is HVRVixxyPropertyInt) { result = sharedMaterial.GetInt(propertyName); return true; }
                            if (vixxyProperty is HVRVixxyPropertyVector4) { result = sharedMaterial.GetVector(propertyName); return true; }
                            if (vixxyProperty is HVRVixxyPropertyVector3) { result = sharedMaterial.GetVector(propertyName); return true; }
                            if (vixxyProperty is HVRVixxyPropertyTexture) { result = sharedMaterial.GetTexture(propertyName); return true; }
                        }
                    }
                }
            }
            else
            {
                if (componentType == typeof(Transform))
                {
                    if (propertyName == "localRotation")
                    {
                        result = TransformUtils.GetInspectorRotation((Transform)component);
                        return true;
                    }
                }

                var fieldInfo = HVRVixxyControl.GetFieldInfoOrNull(componentType, propertyName);
                if (fieldInfo != null)
                {
                    result = fieldInfo.GetValue(component);
                    return true;
                }

                var propertyInfo = HVRVixxyControl.GetPropertyInfoOrNull(componentType, propertyName);
                if (propertyInfo != null)
                {
                    result = propertyInfo.GetValue(component);
                    return true;
                }
            }

            return false;
        }
    }
}
