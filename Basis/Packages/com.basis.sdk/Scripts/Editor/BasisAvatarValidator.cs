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
    public const int MaxTrianglesBeforeWarning = 150000;
    public const int MeshVertices = 65535;
    public VisualElement Root;
    public BasisAvatarValidator(BasisAvatar avatar, VisualElement root)
    {
        Avatar = avatar;
        Root = root;
        CreateErrorPanel(root);
        CreateWarningPanel(root);
        CreatePassedPanel(root);
        EditorApplication.update += UpdateValidation; // Run per frame
    }

    public void OnDestroy()
    {
        EditorApplication.update -= UpdateValidation; // Stop updating on destroy
    }

    private void UpdateValidation()
    {
        if (ValidateAvatar(out List<BasisValidationIssue> errors, out List<BasisValidationIssue> warnings, out List<string> passes))
        {
            HideErrorPanel();
        }
        else
        {
            ShowErrorPanel(Root,errors);
        }

        if (warnings.Count > 0)
        {
            ShowWarningPanel(Root, warnings);
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
    public class BasisValidationIssue
    {
        public string Message { get; }
        public Action Fix { get; }
        public BasisValidationIssue(string message, Action fix = null)
        {
            Message = message;
            Fix = fix;
        }
    }
    private static void RemoveMissingScripts(GameObject MissingScriptParent)
    {
        int removedCount = 0;
        BasisDebug.Log("Evaluating RemoveMissingScripts");
        Transform[] children = MissingScriptParent.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in children)
        {
            int count = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
            if (count > 0)
            {
                BasisDebug.LogWarning($"Removed {count} missing script(s) from GameObject: {child.name}", BasisDebug.LogTag.Editor);
                removedCount += count;

                // Mark the object as dirty so Unity knows it was changed
                EditorUtility.SetDirty(child.gameObject);
            }
        }
        BasisDebug.Log($"Removed a total of {removedCount} missing scripts.", BasisDebug.LogTag.Editor);
    }
    public bool ValidateAvatar(out List<BasisValidationIssue> errors,out List<BasisValidationIssue> warnings, out List<string> passes)
    {
        errors = new List<BasisValidationIssue>();
        warnings = new List<BasisValidationIssue>();
        passes = new List<string>();

        if (Avatar == null)
        {
            errors.Add(new BasisValidationIssue("Avatar is missing.",null));
            return false;
        }
        else
        {
            passes.Add("Avatar is assigned.");
        }

        int missingCount = 0;
        Component[] components = Avatar.GetComponentsInChildren<Component>(true);
        for (int Index = 0; Index < components.Length; Index++)
        {
            if (components[Index] == null)
            {
                missingCount++;
            }
        }
        if (missingCount == 0)
        {
            passes.Add("No missing scripts found in the scene.");
        }
        else
        {
            void action()
            {
                RemoveMissingScripts(Avatar.gameObject);
            }
            warnings.Add(new BasisValidationIssue( $"Press to remove missing scripts automatically", action));
        }

        if (Avatar.Animator != null)
        {
            passes.Add("Animator is assigned.");

            if(Avatar.Animator.runtimeAnimatorController  != null)
            {
                warnings.Add(new BasisValidationIssue("Animator Controller Exists, please check that it supports basis before usage", null));
            }
            if (Avatar.Animator.avatar == null)
            {
                errors.Add(new BasisValidationIssue("Animator Exists but has not Avatar! please check import settings!", null));
            }
        }
        else
        {
            errors.Add(new BasisValidationIssue("Animator is missing.", null));
        }

        if (Avatar.BlinkViseme != null && Avatar.BlinkViseme.Length > 0)
        {
            passes.Add("BlinkViseme Meta Data is assigned.");
        }
        else
        {
            errors.Add(new BasisValidationIssue("BlinkViseme Meta Data is missing.", null));
        }

        if (Avatar.FaceVisemeMovement != null && Avatar.FaceVisemeMovement.Length > 0)
        {
            passes.Add("FaceVisemeMovement Meta Data is assigned.");
        }
        else
        {
            errors.Add(new BasisValidationIssue("FaceVisemeMovement Meta Data is missing.", null));
        }

        if (Avatar.FaceBlinkMesh != null)
        {
            passes.Add("FaceBlinkMesh is assigned.");
        }
        else
        {
            errors.Add(new BasisValidationIssue("FaceBlinkMesh is missing. Assign a skinned mesh.", null));
        }

        if (Avatar.FaceVisemeMesh != null)
        {
            passes.Add("FaceVisemeMesh is assigned.");
        }
        else
        {
            errors.Add(new BasisValidationIssue("FaceVisemeMesh is missing. Assign a skinned mesh.", null));
        }

        if (Avatar.AvatarEyePosition != Vector2.zero)
        {
            passes.Add("Avatar Eye Position is set.");
        }
        else
        {
            errors.Add(new BasisValidationIssue("Avatar Eye Position is not set.", null));
        }

        if (Avatar.AvatarMouthPosition != Vector2.zero)
        {
            passes.Add("Avatar Mouth Position is set.");
        }
        else
        {
            errors.Add(new BasisValidationIssue("Avatar Mouth Position is not set.", null));
        }
        if (string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleName))
        {
            errors.Add(new BasisValidationIssue("Avatar Name Is Empty.", null));
        }

        if (string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleDescription))
        {
            warnings.Add(new BasisValidationIssue("Avatar Description Is empty", null));
        }
        if (ReportIfNoIll2CPP())
        {
            warnings.Add(new BasisValidationIssue("IL2CPP Is Potentially Missing, Check Unity Hub, Normally needed is Linux,Windows,Android Ill2CPP", null));
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
                                errors.Add(new BasisValidationIssue("Avatar Ignored Transform is Missing", null));
                            }
                        }
                    }
                    if (Strain.RootTransform == null)
                    {
                        errors.Add(new BasisValidationIssue("RootTransform of Jiggle is missing!", null));
                    }
                    if (Strain.Colliders != null && Strain.Colliders.Length != 0)
                    {
                        for (int Index = 0; Index < Strain.Colliders.Length; Index++)
                        {
                            if (Strain.Colliders[Index] == null)
                            {
                                errors.Add(new BasisValidationIssue("Avatar Jiggle Collider Is Missing!", null));
                            }
                        }
                    }
                }
                else
                {
                    errors.Add(new BasisValidationIssue("Avatar.JiggleStrains Has a Empty Strain!! at index " + JiggleStrainIndex, null));
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
                errors.Add(new BasisValidationIssue($"Duplicate name found: {entry.Key} ({entry.Value} times)", null));
            }
        }
        return errors.Count == 0;
    }
    public void CheckTextures(Renderer Renderer,ref List<BasisValidationIssue> warnings)
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
                        warnings.Add(new BasisValidationIssue($"Texture \"{tex.name}\" does not have Streaming Mip Maps enabled. this will effect negatively its performance ranking",null));
                    }
                    if(texImporter.maxTextureSize > 4096)
                    {
                        warnings.Add(new BasisValidationIssue($"Texture \"{tex.name}\" is {texImporter.maxTextureSize} this will impact performance negatively", null));
                    }
                }
            }
        }
    }
    public void CheckMesh(SkinnedMeshRenderer skinnedMeshRenderer, ref List<BasisValidationIssue> Errors, ref List<BasisValidationIssue> Warnings)
    {
        if (skinnedMeshRenderer.sharedMesh == null)
        {
            Errors.Add(new BasisValidationIssue($"{skinnedMeshRenderer.gameObject.name} does not have a mesh assigned to its SkinnedMeshRenderer!", null));
            return;
        }
        if (skinnedMeshRenderer.sharedMesh.triangles.Length > MaxTrianglesBeforeWarning)
        {
            Warnings.Add(new BasisValidationIssue($"{skinnedMeshRenderer.gameObject.name} Has More then {MaxTrianglesBeforeWarning} Triangles. This will cause performance issues", null));
        }
        if (skinnedMeshRenderer.sharedMesh.vertices.Length > MeshVertices)
        {
            Warnings.Add(new BasisValidationIssue($"{skinnedMeshRenderer.gameObject.name} Has more vertices then what can be properly renderer ({MeshVertices}). this will cause performance issues", null));
        }
        if (skinnedMeshRenderer.sharedMesh.blendShapeCount != 0)
        {
            string assetPath = AssetDatabase.GetAssetPath(skinnedMeshRenderer?.sharedMesh);
            if (!string.IsNullOrEmpty(assetPath))
            {
                ModelImporter modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                if (modelImporter != null && !ModelImporterExtensions.IsLegacyBlendShapeNormalsEnabled(modelImporter))
                {
                    Warnings.Add(new BasisValidationIssue($"{assetPath} does not have legacy blendshapes enabled, which may increase file size.", null));
                }
            }
        }
        if (skinnedMeshRenderer.allowOcclusionWhenDynamic == false)
        {
            Errors.Add(new BasisValidationIssue("Avatar has Dynamic Occlusion disabled on Skinned Mesh Renderer " + skinnedMeshRenderer.gameObject.name, null));
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
    private void ShowErrorPanel(VisualElement Root, List<BasisValidationIssue> errors)
    {
        string IssueList = string.Empty;
        for (int Index = 0; Index < errors.Count; Index++)
        {
            BasisValidationIssue issue = errors[Index];
            Action ActFix = issue.Fix;
            if (ActFix != null)
            {
                AutoFixButton(Root, ActFix, issue.Message);
            }
            IssueList += issue.Message;
        }
        errorMessageLabel.text = string.Join("\n", IssueList);
        errorPanel.style.display = DisplayStyle.Flex;
    }
    private void HideErrorPanel()
    {
        errorPanel.style.display = DisplayStyle.None;
    }
    private void ShowWarningPanel(VisualElement Root,List<BasisValidationIssue> warnings)
    {
        string warningsList = string.Empty;
        for (int Index = 0; Index < warnings.Count; Index++)
        {
            BasisValidationIssue issue = warnings[Index];
            Action ActFix = issue.Fix;
            if (ActFix != null)
            {
                AutoFixButton(Root, ActFix, issue.Message);
            }
            warningsList += issue.Message;
        }
        warningMessageLabel.text = string.Join("\n", warningsList);
        warningPanel.style.display = DisplayStyle.Flex;
    }
    public void ClearFixButtons(VisualElement rootElement)
    {
        int FixMeButtonsCount = FixMeButtons.Count;
        for (int Index = 0; Index < FixMeButtonsCount; Index++)
        {
            rootElement.Remove(FixMeButtons[Index]);    
        }
        FixMeButtons.Clear();
    }
    public List<Button> FixMeButtons = new List<Button>();
    public void AutoFixButton(VisualElement rootElement, Action onClickAction, string fixMe)
    {
        foreach(Button button in FixMeButtons)
        {
            if(button.text == fixMe)
            {
                return;
            }
        }
        // Create the button
        Button fixMeButton = new Button();

        fixMeButton.clicked += delegate
        {
            onClickAction.Invoke();
            ClearFixButtons(Root);
        };

        fixMeButton.text = fixMe; // Icon + Text

        // Modern slick style
        fixMeButton.style.backgroundColor = new StyleColor(new Color(0.5f, 0.5f, 0.5f, 1f)); // Material Red 500
        fixMeButton.style.color = new StyleColor(Color.white);
        fixMeButton.style.fontSize = 14;
        fixMeButton.style.unityFontStyleAndWeight = FontStyle.Bold;

        // Padding and margin
        fixMeButton.style.paddingTop = 6;
        fixMeButton.style.paddingBottom = 6;
        fixMeButton.style.paddingLeft = 12;
        fixMeButton.style.paddingRight = 12;
        fixMeButton.style.marginBottom = 10;

        // Rounded corners
        fixMeButton.style.borderTopLeftRadius = 8;
        fixMeButton.style.borderTopRightRadius = 8;
        fixMeButton.style.borderBottomLeftRadius = 8;
        fixMeButton.style.borderBottomRightRadius = 8;

        // Border and shadow
        fixMeButton.style.borderLeftWidth = 0;
        fixMeButton.style.borderRightWidth = 0;
        fixMeButton.style.borderTopWidth = 0;
        fixMeButton.style.borderBottomWidth = 3;

        // Shadow-like effect via unityBackgroundImageTintColor or using USS later
        fixMeButton.style.unityTextAlign = TextAnchor.MiddleCenter;
        fixMeButton.style.alignSelf = Align.Auto;

        // Hover effect via C# events (UI Toolkit lacks hover pseudoclass in C# directly)
        fixMeButton.RegisterCallback<MouseEnterEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(new Color(0.9f, 0.2f, 0.2f));
        });
        fixMeButton.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(new Color(0.96f, 0.26f, 0.21f));
        });

        // Add to root and store
        rootElement.Add(fixMeButton);
        FixMeButtons.Add(fixMeButton);
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
