using Basis.Scripts.BasisSdk;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class BasisAvatarValidator
{
    private readonly BasisAvatar Avatar;
    private VisualElement errorPanel;
    private Label errorMessageLabel;
    private VisualElement warningPanel;
    private Label warningMessageLabel;
    private VisualElement passedPanel;
    private Label passedMessageLabel;

    public BasisAvatarValidator(BasisAvatar avatar, VisualElement Root)
    {
        Avatar = avatar;

        CreateErrorPanel(Root);
        CreateWarningPanel(Root);
        CreatePassedPanel(Root);
        EditorApplication.update += UpdateValidation; // Run per frame
    }

    public void OnDestroy()
    {
        EditorApplication.update -= UpdateValidation; // Stop updating on destroy
    }

    private void UpdateValidation()
    {
        if (ValidateAvatar(out List<string> errors, out List<string> warnings, out List<string> passes))
        {
            HideErrorPanel();
        }
        else
        {
            ShowErrorPanel(errors);
        }

        if (warnings.Count > 0)
        {
            ShowWarningPanel(warnings);
        }
        else
        {
            HideWarningPanel();
        }

        if (passes.Count > 0)
        {
          //  ShowPassedPanel(passes);
        }
        else
        {
          //  HidePassedPanel();
        }
    }

    public void CreateErrorPanel(VisualElement rootElement)
    {
        // Create error panel
        errorPanel = new VisualElement();
        errorPanel.style.backgroundColor = new StyleColor(new Color(1, 0.5f, 0.5f, 0.5f)); // Light red
        errorPanel.style.paddingTop = 5;

        errorPanel.style.flexGrow = 1;

        errorPanel.style.paddingBottom = 5;
        errorPanel.style.marginBottom = 10;
        errorPanel.style.borderTopLeftRadius = 5;
        errorPanel.style.borderTopRightRadius = 5;
        errorPanel.style.borderBottomLeftRadius = 5;
        errorPanel.style.borderBottomRightRadius = 5;
        errorPanel.style.borderLeftWidth = 2;
        errorPanel.style.borderRightWidth = 2;
        errorPanel.style.borderTopWidth = 2;
        errorPanel.style.borderBottomWidth = 2;
        errorPanel.style.borderBottomColor = new StyleColor(Color.red);

        errorMessageLabel = new Label("No Errors");
        errorMessageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        errorPanel.Add(errorMessageLabel);

        errorPanel.style.display = DisplayStyle.None;

        rootElement.Add(errorPanel);
    }
    public void CreateWarningPanel(VisualElement rootElement)
    {
        warningPanel = new VisualElement();
        warningPanel.style.backgroundColor = new StyleColor(new Color(0.65098f, 0.63137f, 0.05098f, 0.5f));
        warningPanel.style.paddingTop = 5;

        warningPanel.style.flexGrow = 1;

        warningPanel.style.paddingBottom = 5;
        warningPanel.style.marginBottom = 10;
        warningPanel.style.borderTopLeftRadius = 5;
        warningPanel.style.borderTopRightRadius = 5;
        warningPanel.style.borderBottomLeftRadius = 5;
        warningPanel.style.borderBottomRightRadius = 5;
        warningPanel.style.borderLeftWidth = 2;
        warningPanel.style.borderRightWidth = 2;
        warningPanel.style.borderTopWidth = 2;
        warningPanel.style.borderBottomWidth = 2;
        warningPanel.style.borderBottomColor = new StyleColor(Color.yellow);

        warningMessageLabel = new Label("No Errors");
        warningMessageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        warningPanel.Add(warningMessageLabel);

        warningPanel.style.display = DisplayStyle.None;
        rootElement.Add(warningPanel);
    }
    public void CreatePassedPanel(VisualElement rootElement)
    {
        passedPanel = new VisualElement();
        passedPanel.style.backgroundColor = new StyleColor(new Color(0.5f, 1f, 0.5f, 0.5f)); // Light green
        passedPanel.style.paddingTop = 5;

        passedPanel.style.flexGrow = 1;

        passedPanel.style.paddingBottom = 5;
        passedPanel.style.marginBottom = 10;
        passedPanel.style.borderTopLeftRadius = 5;
        passedPanel.style.borderTopRightRadius = 5;
        passedPanel.style.borderBottomLeftRadius = 5;
        passedPanel.style.borderBottomRightRadius = 5;
        passedPanel.style.borderLeftWidth = 2;
        passedPanel.style.borderRightWidth = 2;
        passedPanel.style.borderTopWidth = 2;
        passedPanel.style.borderBottomWidth = 2;
        passedPanel.style.borderBottomColor = new StyleColor(Color.green);

        passedMessageLabel = new Label("No Passed Checks");
        passedMessageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        passedPanel.Add(passedMessageLabel);

        passedPanel.style.display = DisplayStyle.None;
        rootElement.Add(passedPanel);
    }

    public bool ValidateAvatar(out List<string> errors, out List<string> warnings, out List<string> passes)
    {
        errors = new List<string>();
        warnings = new List<string>();
        passes = new List<string>();

        if (Avatar == null)
        {
            errors.Add("Avatar is missing.");
            return false;
        }
        else
        {
            passes.Add("Avatar is assigned.");
        }

        if (Avatar.Animator != null)
        {
            passes.Add("Animator is assigned.");

            if(Avatar.Animator.runtimeAnimatorController  != null)
            {
                warnings.Add("Animator Controller Exists, please check that it supports basis before usage");
            }
            if (Avatar.Animator.avatar == null)
            {
                errors.Add("Animator Exists but has not Avatar! please check import settings!");
            }
        }
        else
        {
            errors.Add("Animator is missing.");
        }

        if (Avatar.BlinkViseme != null && Avatar.BlinkViseme.Length > 0)
        {
            passes.Add("BlinkViseme Meta Data is assigned.");
        }
        else
        {
            errors.Add("BlinkViseme Meta Data is missing.");
        }

        if (Avatar.FaceVisemeMovement != null && Avatar.FaceVisemeMovement.Length > 0)
        {
            passes.Add("FaceVisemeMovement Meta Data is assigned.");
        }
        else
        {
            errors.Add("FaceVisemeMovement Meta Data is missing.");
        }

        if (Avatar.FaceBlinkMesh != null)
        {
            passes.Add("FaceBlinkMesh is assigned.");
        }
        else
        {
            errors.Add("FaceBlinkMesh is missing. Assign a skinned mesh.");
        }

        if (Avatar.FaceVisemeMesh != null)
        {
            passes.Add("FaceVisemeMesh is assigned.");
        }
        else
        {
            errors.Add("FaceVisemeMesh is missing. Assign a skinned mesh.");
        }

        if (Avatar.AvatarEyePosition != Vector2.zero)
        {
            passes.Add("Avatar Eye Position is set.");
        }
        else
        {
            errors.Add("Avatar Eye Position is not set.");
        }

        if (Avatar.AvatarMouthPosition != Vector2.zero)
        {
            passes.Add("Avatar Mouth Position is set.");
        }
        else
        {
            errors.Add("Avatar Mouth Position is not set.");
        }
        if (string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleName))
        {
            errors.Add("Avatar Name Is Empty.");
        }

        if (string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleDescription))
        {
            warnings.Add("Avatar Description Is empty");
        }
        if (ReportIfNoIll2CPP())
        {
            warnings.Add("IL2CPP Is Potentially Missing, Check Unity Hub, Normally needed is Linux,Windows,Android Ill2CPP");
        }
        Renderer[] renderers = Avatar.GetComponentsInChildren<Renderer>();
        SkinnedMeshRenderer[] SMRS = Avatar.GetComponentsInChildren<SkinnedMeshRenderer>();
        foreach (Renderer renderer in renderers)
        {
            CheckTextures(renderer, ref warnings);
        }
        foreach (SkinnedMeshRenderer SMR in SMRS)
        {
            CheckMesh(SMR, ref errors,ref warnings);

        }
        if (Avatar.JiggleStrains != null && Avatar.JiggleStrains.Length != 0)
        {
            for (int JiggleStrainIndex = 0; JiggleStrainIndex < Avatar.JiggleStrains.Length; JiggleStrainIndex++)
            {
                BasisJiggleStrain Strain = Avatar.JiggleStrains[JiggleStrainIndex];
                if (Strain != null)
                {
                    if (Strain.IgnoredTransforms != null && Strain.IgnoredTransforms.Length != 0)
                    {
                        for (int Index = 0; Index < Strain.IgnoredTransforms.Length; Index++)
                        {
                            if (Strain.IgnoredTransforms[Index] == null)
                            {
                                errors.Add("Avatar Ignored Transform is Missing");
                            }
                        }
                    }
                    if (Strain.RootTransform == null)
                    {
                        errors.Add("RootTransform of Jiggle is missing!");
                    }
                    if (Strain.Colliders != null && Strain.Colliders.Length != 0)
                    {
                        for (int Index = 0; Index < Strain.Colliders.Length; Index++)
                        {
                            if (Strain.Colliders[Index] == null)
                            {
                                errors.Add("Avatar Jiggle Collider Is Missing!");
                            }
                        }
                    }
                }
                else
                {
                    errors.Add("Avatar.JiggleStrains Has a Empty Strain!! at index " + JiggleStrainIndex);
                }
            }
        }
        Transform[] transforms = Avatar.GetComponentsInChildren<Transform>();
        Dictionary<string, int> nameCounts = new Dictionary<string, int>();

        foreach (Transform trans in transforms)
        {
            if (nameCounts.ContainsKey(trans.name))
            {
                nameCounts[trans.name]++;
            }
            else
            {
                nameCounts[trans.name] = 1;
            }
        }

        foreach (var entry in nameCounts)
        {
            if (entry.Value > 1)
            {
                errors.Add($"Duplicate name found: {entry.Key} ({entry.Value} times)");
            }
        }
        return errors.Count == 0;
    }
    public void CheckTextures(Renderer Renderer,ref List<string> warnings)
    {
        // Check for texture streaming
        List<Texture> texturesToCheck = new List<Texture>();
        foreach (Material mat in Renderer.sharedMaterials)
        {
            if (mat == null)
            {
                continue;
            }

            Shader shader = mat.shader;
            int propertyCount = ShaderUtil.GetPropertyCount(shader);
            for (int Index = 0; Index < propertyCount; Index++)
            {
                if (ShaderUtil.GetPropertyType(shader, Index) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    string propName = ShaderUtil.GetPropertyName(shader, Index);
                    if (mat.HasProperty(propName))
                    {
                        Texture tex = mat.GetTexture(propName);
                        if (tex != null && !texturesToCheck.Contains(tex))
                        {
                            texturesToCheck.Add(tex);
                        }
                    }
                }
            }
        }

        foreach (Texture tex in texturesToCheck)
        {
            string texPath = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(texPath))
            {
                TextureImporter texImporter = AssetImporter.GetAtPath(texPath) as TextureImporter;
                if (texImporter != null)
                {
                    if (!texImporter.streamingMipmaps)
                    {
                        warnings.Add($"Texture \"{tex.name}\" does not have Streaming Mip Maps enabled. this will effect negatively its performance ranking");
                    }
                    if(texImporter.maxTextureSize > 4096)
                    {
                        warnings.Add($"Texture \"{tex.name}\" is {texImporter.maxTextureSize} this will impact performance negatively");
                    }
                }
            }
        }
    }
    public const int MaxTrianglesBeforeWarning = 150000;
    public const int MeshVertices = 65535;
    public void CheckMesh(SkinnedMeshRenderer skinnedMeshRenderer, ref List<string> Errors, ref List<string> Warnings)
    {
        if (skinnedMeshRenderer.sharedMesh == null)
        {
            Errors.Add($"{skinnedMeshRenderer.gameObject.name} does not have a mesh assigned to its SkinnedMeshRenderer!");
            return;
        }
        if (skinnedMeshRenderer.sharedMesh.triangles.Length > MaxTrianglesBeforeWarning)
        {
            Warnings.Add($"{skinnedMeshRenderer.gameObject.name} Has More then {MaxTrianglesBeforeWarning} Triangles. This will cause performance issues");
        }
        if (skinnedMeshRenderer.sharedMesh.vertices.Length > MeshVertices)
        {
            Warnings.Add($"{skinnedMeshRenderer.gameObject.name} Has more vertices then what can be properly renderer ({MeshVertices}). this will cause performance issues");
        }
        if (skinnedMeshRenderer.sharedMesh.blendShapeCount != 0)
        {
            string assetPath = AssetDatabase.GetAssetPath(skinnedMeshRenderer?.sharedMesh);
            if (!string.IsNullOrEmpty(assetPath))
            {
                ModelImporter modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                if (modelImporter != null && !ModelImporterExtensions.IsLegacyBlendShapeNormalsEnabled(modelImporter))
                {
                    Warnings.Add($"{assetPath} does not have legacy blendshapes enabled, which may increase file size.");
                }
            }
        }
        if (skinnedMeshRenderer.allowOcclusionWhenDynamic == false)
        {
            Errors.Add("Avatar has Dynamic Occlusion disabled on Skinned Mesh Renderer " + skinnedMeshRenderer.gameObject.name);
        }
    }
    public static bool ReportIfNoIll2CPP()
    {
        string unityPath = EditorApplication.applicationPath;
        string unityFolder = Path.GetDirectoryName(unityPath);

        // Check IL2CPP existence in Unity installation
        string il2cppPath = Path.Combine(unityFolder, "Data", "il2cpp");
        bool il2cppExists = Directory.Exists(il2cppPath);
        return !il2cppExists;
    }
    private void ShowErrorPanel(List<string> errors)
    {
        errorMessageLabel.text = string.Join("\n", errors);
        errorPanel.style.display = DisplayStyle.Flex;
    }
    private void HideErrorPanel()
    {
        errorPanel.style.display = DisplayStyle.None;
    }
    private void ShowWarningPanel(List<string> warnings)
    {
        warningMessageLabel.text = string.Join("\n", warnings);
        warningPanel.style.display = DisplayStyle.Flex;
    }
    private void HideWarningPanel()
    {
        warningPanel.style.display = DisplayStyle.None;
    }
    private void ShowPassedPanel(List<string> passes)
    {
        passedMessageLabel.text = string.Join("\n", passes);
        passedPanel.style.display = DisplayStyle.Flex;
    }
    private void HidePassedPanel()
    {
        passedPanel.style.display = DisplayStyle.None;
    }
}
