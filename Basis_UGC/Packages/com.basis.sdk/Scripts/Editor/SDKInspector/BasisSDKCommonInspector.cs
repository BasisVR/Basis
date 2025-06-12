using System;
using System.Reflection;
using UnityEditor;
using UnityEngine.UIElements;

public static class BasisSDKCommonInspector
{
    public static void CreateBuildOptionsDropdown(VisualElement parent)
    {
        BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
        Foldout foldout = new Foldout
        {
            text = "Build AssetBundle Options",
            value = false
        };
        parent.Add(foldout);

        foreach (BuildAssetBundleOptions option in Enum.GetValues(typeof(BuildAssetBundleOptions)))
        {
            if (option == 0)
            {
                continue; // Skip "None"
            }
            // Check if the enum field has the Obsolete attribute
            FieldInfo fieldInfo = typeof(BuildAssetBundleOptions).GetField(option.ToString());
            if (fieldInfo != null && Attribute.IsDefined(fieldInfo, typeof(ObsoleteAttribute)))
            {
                continue; // Skip obsolete options from being shown

            }
            Toggle toggle = new Toggle(option.ToString())
            {
                value = assetBundleObject.BuildAssetBundleOptions.HasFlag(option)
            };

            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                {
                    assetBundleObject.BuildAssetBundleOptions |= option;
                }
                else
                {
                    assetBundleObject.BuildAssetBundleOptions &= ~option;
                }
            });

            foldout.Add(toggle);
        }
    }
    public static void CreateBuildTargetOptions(VisualElement parent)
    {
        BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
        // Multi-select dropdown (Foldout with Toggles)
        Foldout buildTargetFoldout = new Foldout { text = "Select Build Targets", value = false }; // Expanded by default
        parent.Add(buildTargetFoldout);

        foreach (var target in BasisSDKConstants.allowedTargets)
        {
            // Check if the target is already selected
            bool isSelected = assetBundleObject.selectedTargets.Contains(target);

            Toggle toggle = new Toggle(BasisSDKConstants.targetDisplayNames[target])
            {
                value = isSelected // Set the toggle based on whether the target is in the selected list
            };

            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                    assetBundleObject.selectedTargets.Add(target);
                else
                    assetBundleObject.selectedTargets.Remove(target);
            });

            buildTargetFoldout.Add(toggle);
        }
    }
}
