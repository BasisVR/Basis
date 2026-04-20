using Basis.Scripts.BasisSdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

public class BasisAvatarValidator
{
    private readonly BasisAvatar Avatar;

    private VisualElement errorPanel;
    private Label errorMessageLabel;

    private Dictionary<ValidationCategory, VisualElement> warningPanels = new Dictionary<ValidationCategory, VisualElement>();
    private Label warningMessageLabel;

    private VisualElement passedPanel;
    private Label passedMessageLabel;

    public const int MaxTrianglesBeforeWarning = 150000;
    public const int MeshVertices = 65535;
    public const int MaxTextureSizeBeforeWarning = 4096;
    public const int MaxTextureSizeWithoutMipMaps = 256;

    public VisualElement Root;

    private VisualElement errorButtonContainer;
    private string _lastErrorSignature = "";
    private string _lastWarningSignature = "";

    private HashSet<Label> _registeredWarningLabels = new HashSet<Label>();
    private Dictionary<Label, UnityEngine.Object[]> _warningLabelObjects = new Dictionary<Label, UnityEngine.Object[]>();

    public BasisAvatarValidator(BasisAvatar avatar, VisualElement root)
    {
        Avatar = avatar;
        Root = root;

        CreateErrorPanel(root);
        //CreateWarningPanel(root);
        CreatePassedPanel(root);

        EditorApplication.update += UpdateValidation;
    }

    public void OnDestroy()
    {
        EditorApplication.update -= UpdateValidation;
    }

    private void UpdateValidation()
    {
        if (ValidateAvatar(out List<BasisValidationIssue> errors, out List<BasisValidationIssue> warnings, out List<string> passes))
            HideErrorPanel();
        else
            ShowErrorPanel(errors);

        if (warnings.Count > 0)
            ShowWarningPanel(Root, warnings);
        else
            HideWarningPanel();
    }

    public void CreateErrorPanel(VisualElement rootElement)
    {
        errorPanel = new VisualElement();
        errorPanel.style.backgroundColor = new StyleColor(new Color(1, 0.5f, 0.5f, 0.5f));
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
        errorMessageLabel.style.whiteSpace = WhiteSpace.Normal;
        errorPanel.Add(errorMessageLabel);

        errorButtonContainer = new VisualElement() { name = "ErrorButtonContainer" };
        errorPanel.Add(errorButtonContainer);

        errorPanel.style.display = DisplayStyle.None;
        rootElement.Add(errorPanel);
    }

    public void CreatePassedPanel(VisualElement rootElement)
    {
        passedPanel = new VisualElement();
        passedPanel.style.backgroundColor = new StyleColor(new Color(0.5f, 1f, 0.5f, 0.5f));
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

    public enum ValidationCategory
    {
        None,
        Configuration,
        GameObject,
        Performance,
        Security,
        MissingReference
    }

    public class BasisValidationIssue
    {
        public ValidationCategory Category { get; }
        public string Message { get; }
        public string FixLabel { get; }
        public Action Fix { get; }
        public UnityEngine.Object RelatedObject { get; }

        public BasisValidationIssue(string message, ValidationCategory category = ValidationCategory.None,
                                Action fix = null, string fixLabel = "", UnityEngine.Object relatedObject = null)
        {
            Category = category;
            Message = message;
            Fix = fix;
            FixLabel = fixLabel;
            RelatedObject = relatedObject;
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
                EditorUtility.SetDirty(child.gameObject);
            }
        }
        BasisDebug.Log($"Removed a total of {removedCount} missing script(s).", BasisDebug.LogTag.Editor);
    }

    public bool ValidateAvatar(out List<BasisValidationIssue> errors, out List<BasisValidationIssue> warnings, out List<string> passes)
    {
        errors = new List<BasisValidationIssue>();
        warnings = new List<BasisValidationIssue>();
        passes = new List<string>();

        if (Avatar == null)
        {
            errors.Add(new BasisValidationIssue("Avatar is missing.", ValidationCategory.Configuration, null));
            return false;
        }
        passes.Add("Avatar is assigned.");

        Transform[] children = Avatar.gameObject.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in children)
        {
            int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject);
            if (count > 0)
            {
                warnings.Add(new BasisValidationIssue(
                $"Missing script references found on {child.gameObject}. Click here to locate it.", ValidationCategory.MissingReference,
                () => RemoveMissingScripts(Avatar.gameObject),
                "Remove missing scripts",
                child.gameObject
                ));
            }
        }

        if (Avatar.Animator != null)
        {
            passes.Add("Animator is assigned.");

            if (Avatar.Animator.runtimeAnimatorController != null)
            {
                warnings.Add(new BasisValidationIssue(
                    "Animator Controller exists. Verify it supports Basis before usage.", ValidationCategory.Configuration,
                    null
                ));
            }

            if (Avatar.Animator.avatar == null)
            {
                errors.Add(new BasisValidationIssue(
                    "Animator exists but has no Avatar (Humanoid avatar not generated).", ValidationCategory.Configuration,
                    FixTryCreateHumanoidAvatarOnSourceModels,
                    "Set source model(s) to Humanoid and Create Avatar"
                ));
            }
        }
        else
        {
            errors.Add(new BasisValidationIssue(
                "Animator is missing.", ValidationCategory.MissingReference,
                FixAddOrAssignAnimator,
                "Add or assign Animator"
            ));
        }

        if (Avatar.BlinkViseme != null && Avatar.BlinkViseme.Length > 0)
            passes.Add("BlinkViseme Meta Data is assigned.");
        else
            errors.Add(new BasisValidationIssue("BlinkViseme Meta Data is missing.", ValidationCategory.MissingReference, null));

        if (Avatar.FaceVisemeMovement != null && Avatar.FaceVisemeMovement.Length > 0)
            passes.Add("FaceVisemeMovement Meta Data is assigned.");
        else
            errors.Add(new BasisValidationIssue("FaceVisemeMovement Meta Data is missing.", ValidationCategory.MissingReference, null));

        if (Avatar.FaceBlinkMesh != null)
            passes.Add("FaceBlinkMesh is assigned.");
        else
            errors.Add(new BasisValidationIssue(
                "FaceBlinkMesh is missing. Assign a skinned mesh.", ValidationCategory.MissingReference,
                FixAssignFaceMeshesFromChildren,
                "Auto-assign Face meshes"
            ));

        if (Avatar.FaceVisemeMesh != null)
            passes.Add("FaceVisemeMesh is assigned.");
        else
            errors.Add(new BasisValidationIssue(
                "FaceVisemeMesh is missing. Assign a skinned mesh.", ValidationCategory.MissingReference,
                FixAssignFaceMeshesFromChildren,
                "Auto-assign Face meshes"
            ));

        if (Avatar.AvatarEyePosition != Vector2.zero)
            passes.Add("Avatar Eye Position is set.");
        else
            errors.Add(new BasisValidationIssue("Avatar Eye Position is not set.", ValidationCategory.Configuration, null));

        if (Avatar.AvatarMouthPosition != Vector2.zero)
            passes.Add("Avatar Mouth Position is set.");
        else
            errors.Add(new BasisValidationIssue("Avatar Mouth Position is not set.", ValidationCategory.Configuration, null));

        if (string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleName))
        {
            errors.Add(new BasisValidationIssue(
                "Avatar Name is empty.", ValidationCategory.Configuration,
                FixSetDefaultBundleName,
                "Set name from GameObject"
            ));
        }

        if (string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleDescription))
        {
            warnings.Add(new BasisValidationIssue(
                "Avatar Description is empty.", ValidationCategory.Configuration,
                FixSetDefaultDescription,
                "Set default description"
            ));
        }
        BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
        if (assetBundleObject != null)
        {
            if(assetBundleObject.UseCustomPassword && (assetBundleObject.UserSelectedPassword == null ||  assetBundleObject.UserSelectedPassword == ""))
            {
                errors.Add(new BasisValidationIssue(
                    "The custom password is not allowed to be empty.",
                    ValidationCategory.Security,
                    null));
            }
        }

        if (ReportIfNoIll2CPP())
        {
            warnings.Add(new BasisValidationIssue(
                "IL2CPP may be missing. Check Unity Hub modules (Linux/Windows/Android IL2CPP commonly needed).", ValidationCategory.None,
                null
            ));
        }

        ValidateTranslationDof(ref warnings, ref passes);

        Renderer[] renderers = Avatar.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer r in renderers)
        {
            CheckTextures(r, ref warnings);
            CheckShaders(r, ref errors, ref warnings);
        }

        SkinnedMeshRenderer[] smrs = Avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (SkinnedMeshRenderer smr in smrs)
            CheckMesh(smr, ref errors, ref warnings);

        Transform[] transforms = Avatar.GetComponentsInChildren<Transform>(true);
        Dictionary<string, int> nameCounts = new Dictionary<string, int>();
        foreach (Transform t in transforms)
        {
            if (nameCounts.ContainsKey(t.name)) nameCounts[t.name]++;
            else nameCounts[t.name] = 1;
        }

        foreach (var entry in nameCounts)
        {
            if (entry.Value <= 1) continue;

            if (Avatar.ProcessingAvatarOptions != null && Avatar.ProcessingAvatarOptions.doNotAutoRenameBones)
            {
                errors.Add(new BasisValidationIssue(
                    $"Duplicate name found: {entry.Key} ({entry.Value} times)", ValidationCategory.Configuration,
                    FixDisableDoNotAutoRenameBones,
                    "Allow auto-renaming bones"
                ));
            }
            else
            {
                warnings.Add(new BasisValidationIssue(
                    $"Duplicate name found; it will be renamed automatically: {entry.Key} ({entry.Value} times)", ValidationCategory.GameObject,
                    null
                ));
            }
        }

        return errors.Count == 0;
    }

    private void FixAddOrAssignAnimator()
    {
        if (Avatar == null) return;
        var anim = Avatar.GetComponent<Animator>();
        if (anim == null) anim = Avatar.gameObject.AddComponent<Animator>();
        Avatar.Animator = anim;
        EditorUtility.SetDirty(Avatar);
        EditorUtility.SetDirty(anim);
    }

    private void FixSetDefaultBundleName()
    {
        if (Avatar == null) return;
        string name = Avatar.gameObject.name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        Avatar.BasisBundleDescription.AssetBundleName = name;
        EditorUtility.SetDirty(Avatar);
    }

    private void FixSetDefaultDescription()
    {
        if (Avatar == null) return;
        Avatar.BasisBundleDescription.AssetBundleDescription =
            $"Avatar \"{Avatar.gameObject.name}\"";
        EditorUtility.SetDirty(Avatar);
    }

    private void FixDisableDoNotAutoRenameBones()
    {
        if (Avatar?.ProcessingAvatarOptions == null) return;
        Avatar.ProcessingAvatarOptions.doNotAutoRenameBones = false;
        EditorUtility.SetDirty(Avatar);
    }

    private void FixAssignFaceMeshesFromChildren()
    {
        if (Avatar == null) return;
        var smrs = Avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        SkinnedMeshRenderer best = null;
        foreach (var smr in smrs)
        {
            if (smr == null || smr.sharedMesh == null) continue;
            if (smr.sharedMesh.blendShapeCount > 0)
            {
                best = smr;
                break;
            }
            if (best == null) best = smr;
        }
        if (best == null) return;
        if (Avatar.FaceBlinkMesh == null) Avatar.FaceBlinkMesh = best;
        if (Avatar.FaceVisemeMesh == null) Avatar.FaceVisemeMesh = best;
        EditorUtility.SetDirty(Avatar);
    }

    private void FixEnableDynamicOcclusionAllSMR()
    {
        if (Avatar == null) return;
        var smrs = Avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var smr in smrs)
        {
            if (smr == null) continue;
            if (!smr.allowOcclusionWhenDynamic)
            {
                smr.allowOcclusionWhenDynamic = true;
                EditorUtility.SetDirty(smr);
            }
        }
    }

    private static IEnumerable<ModelImporter> GetModelImportersFromAvatarMeshes(BasisAvatar avatar)
    {
        if (avatar == null) yield break;
        var smrs = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        HashSet<string> seen = new HashSet<string>();
        foreach (var smr in smrs)
        {
            if (smr == null || smr.sharedMesh == null) continue;
            string path = AssetDatabase.GetAssetPath(smr.sharedMesh);
            if (string.IsNullOrEmpty(path)) continue;
            if (!seen.Add(path)) continue;
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer != null) yield return importer;
        }
    }

    private void FixTryCreateHumanoidAvatarOnSourceModels()
    {
        if (Avatar == null) return;
        foreach (var modelImporter in GetModelImportersFromAvatarMeshes(Avatar))
        {
            bool changed = false;
            if (modelImporter.animationType != ModelImporterAnimationType.Human)
            {
                modelImporter.animationType = ModelImporterAnimationType.Human;
                changed = true;
            }
            if (modelImporter.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                modelImporter.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                changed = true;
            }
            var hd = modelImporter.humanDescription;
            if (hd.hasTranslationDoF)
            {
                hd.hasTranslationDoF = false;
                modelImporter.humanDescription = hd;
                changed = true;
            }
            if (changed)
            {
                try { modelImporter.SaveAndReimport(); }
                catch (Exception e)
                {
                    Debug.LogError($"[BasisAvatarValidator] Reimport failed for '{modelImporter.assetPath}': {e.Message}");
                }
            }
        }
        EditorUtility.SetDirty(Avatar);
    }

    private void ValidateTranslationDof(ref List<BasisValidationIssue> warnings, ref List<string> passes)
    {
        bool foundAny = false;
        bool anyDisabled = false;
        foreach (var importer in GetModelImportersFromAvatarMeshes(Avatar))
        {
            foundAny = true;
            if (!importer.humanDescription.hasTranslationDoF) anyDisabled = true;
        }
        if (!foundAny) return;
        if (!anyDisabled)
        {
            warnings.Add(new BasisValidationIssue(
                "Translation DoF is Enabled on one or more source models (Humanoid). This can cause retargeting issues.",
                ValidationCategory.GameObject,
                FixTryCreateHumanoidAvatarOnSourceModels,
                "Disable Translation DoF + Humanoid Avatar on source models"
            ));
        }
        else
        {
            passes.Add("Translation DoF Disabled on source models.");
        }
    }

    public void CheckShaders(Renderer renderer, ref List<BasisValidationIssue> errors, ref List<BasisValidationIssue> warnings)
    {
        if (renderer == null) return;
        var mats = renderer.sharedMaterials;
        if (mats == null) return;
        for (int i = 0; i < mats.Length; i++)
        {
            var mat = mats[i];
            if (mat == null) continue;
            Shader shader = mat.shader;
            bool isErrorShader = (shader == null) || !shader.isSupported || shader.name == "Hidden/InternalErrorShader";
            if (isErrorShader)
            {
                errors.Add(new BasisValidationIssue(
                    $"Material \"{mat.name}\" on \"{renderer.gameObject.name}\" is using an error/unsupported shader (pink).",
                    ValidationCategory.GameObject,
                    () => FixMaterialShaderFallback(mat),
                    "Set shader fallback (URP Lit / Standard)"
                ));
                continue;
            }
        }
    }

    private void FixMaterialShaderFallback(Material mat)
    {
        if (mat == null) return;
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit != null) { mat.shader = urpLit; EditorUtility.SetDirty(mat); return; }
        Shader standard = Shader.Find("Standard");
        if (standard != null) { mat.shader = standard; EditorUtility.SetDirty(mat); return; }
        Debug.LogWarning($"[BasisAvatarValidator] No fallback shader found (URP Lit / Standard) for material '{mat.name}'.");
    }

    public void CheckTextures(Renderer Renderer, ref List<BasisValidationIssue> warnings)
    {
        if (Renderer == null) return;
        List<Texture> texturesToCheck = new List<Texture>();
        foreach (Material mat in Renderer.sharedMaterials)
        {
            if (mat == null) continue;
            Shader shader = mat.shader;
            if (shader == null) continue;
            int propertyCount = shader.GetPropertyCount();
            for (int Index = 0; Index < propertyCount; Index++)
            {
                if (shader.GetPropertyType(Index) == ShaderPropertyType.Texture)
                {
                    string propName = shader.GetPropertyName(Index);
                    if (mat.HasProperty(propName))
                    {
                        Texture tex = mat.GetTexture(propName);
                        if (tex != null && !texturesToCheck.Contains(tex))
                            texturesToCheck.Add(tex);
                    }
                }
            }
        }
        foreach (Texture tex in texturesToCheck)
        {
            string texPath = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(texPath)) continue;
            TextureImporter texImporter = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (texImporter == null) continue;
            if (texImporter.maxTextureSize > MaxTextureSizeWithoutMipMaps && !texImporter.mipmapEnabled)
            {
                warnings.Add(new BasisValidationIssue(
                    $"Texture \"{tex.name}\" does not have Mip Maps enabled. This will negatively affect its performance ranking.",
                    ValidationCategory.Performance,
                    () => { texImporter.mipmapEnabled = true; texImporter.streamingMipmaps = true; texImporter.SaveAndReimport(); },
                    $"Enable Mip Maps on \"{tex.name}\""
                ));
            }
            if (texImporter.mipmapEnabled && !texImporter.streamingMipmaps)
            {
                warnings.Add(new BasisValidationIssue(
                    $"Texture \"{tex.name}\" does not have Streaming Mip Maps enabled. This will negatively affect its performance ranking.",
                    ValidationCategory.Performance,
                    () => { texImporter.streamingMipmaps = true; texImporter.SaveAndReimport(); },
                    $"Enable Streaming Mip Maps on \"{tex.name}\""
                ));
            }
            if (texImporter.maxTextureSize > MaxTextureSizeBeforeWarning)
            {
                warnings.Add(new BasisValidationIssue(
                    $"Texture \"{tex.name}\" is {texImporter.maxTextureSize} (should be <= {MaxTextureSizeBeforeWarning}). This will negatively affect its performance ranking.",
                    ValidationCategory.Performance,
                    () => { texImporter.maxTextureSize = MaxTextureSizeBeforeWarning; texImporter.SaveAndReimport(); },
                    $"Resize \"{tex.name}\" to {MaxTextureSizeBeforeWarning}"
                ));
            }
        }
    }

    public void CheckMesh(SkinnedMeshRenderer skinnedMeshRenderer, ref List<BasisValidationIssue> Errors, ref List<BasisValidationIssue> Warnings)
    {
        if (skinnedMeshRenderer == null) return;
        if (skinnedMeshRenderer.sharedMesh == null)
        {
            Errors.Add(new BasisValidationIssue(
                $"{skinnedMeshRenderer.gameObject.name} does not have a mesh assigned to its SkinnedMeshRenderer!",
                ValidationCategory.GameObject, null));
            return;
        }
        var mesh = skinnedMeshRenderer.sharedMesh;
        if (mesh.triangles != null && mesh.triangles.Length / 3 > MaxTrianglesBeforeWarning)
            Warnings.Add(new BasisValidationIssue(
                $"{skinnedMeshRenderer.gameObject.name} has more than {MaxTrianglesBeforeWarning} triangles. This will cause performance issues.",
                ValidationCategory.Performance, null));
        if (mesh.vertices != null && mesh.vertices.Length > MeshVertices)
            Warnings.Add(new BasisValidationIssue(
                $"{skinnedMeshRenderer.gameObject.name} has more vertices than can be properly rendered ({MeshVertices}). This will cause performance issues.",
                ValidationCategory.Performance, null));
        if (mesh.blendShapeCount != 0)
        {
            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(assetPath))
            {
                ModelImporter modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                if (modelImporter != null && !ModelImporterExtensions.IsLegacyBlendShapeNormalsEnabled(modelImporter))
                    Warnings.Add(new BasisValidationIssue(
                        $"{assetPath} does not have legacy blendshapes enabled, which may increase file size.",
                        ValidationCategory.GameObject, null));
            }
        }
        if (skinnedMeshRenderer.allowOcclusionWhenDynamic == false)
            Errors.Add(new BasisValidationIssue(
                "Dynamic Occlusion disabled on SkinnedMeshRenderer: " + skinnedMeshRenderer.gameObject.name,
                ValidationCategory.GameObject, FixEnableDynamicOcclusionAllSMR,
                "Enable Dynamic Occlusion on all SMRs"));
    }

    public static bool ReportIfNoIll2CPP()
    {
        string unityPath = EditorApplication.applicationPath;
        string unityFolder = Path.GetDirectoryName(unityPath);
        string il2cppPath = Path.Combine(unityFolder, "Data", "il2cpp");
        return !Directory.Exists(il2cppPath);
    }

    private void ShowErrorPanel(List<BasisValidationIssue> errors)
    {
        string currentSignature = string.Join("|", errors.Select(e => $"{e.Category}:{e.Message}"));
        if (currentSignature == _lastErrorSignature)
        {
            errorPanel.style.display = DisplayStyle.Flex;
            return;
        }
        _lastErrorSignature = currentSignature;

        List<string> issueList = new List<string>();
        errorButtonContainer.Clear();

        for (int i = 0; i < errors.Count; i++)
        {
            var issue = errors[i];
            if (issue.Fix != null)
            {
                string actionTitle = string.IsNullOrWhiteSpace(issue.FixLabel) ? issue.Message : issue.FixLabel;
                AutoFixButton(errorButtonContainer, issue.Fix, actionTitle, true);
            }
            if (!issueList.Contains(issue.Message))
                issueList.Add(issue.Message);
        }

        errorMessageLabel.text = string.Join("\n", issueList.ToArray());
        errorPanel.style.display = DisplayStyle.Flex;
    }

    private void HideErrorPanel()
    {
        errorPanel.style.display = DisplayStyle.None;
        _lastErrorSignature = "";
    }

    private VisualElement CreateCategoryPanel(ValidationCategory category)
    {
        VisualElement panel = new VisualElement();
        panel.style.backgroundColor = new StyleColor(new Color(0.65098f, 0.63137f, 0.05098f, 0.5f));
        panel.style.marginBottom = 10;
        panel.style.paddingTop = 5;
        panel.style.paddingBottom = 5;
        panel.style.borderLeftWidth = 2;
        panel.style.borderRightWidth = 2;
        panel.style.borderTopWidth = 2;
        panel.style.borderBottomWidth = 2;
        panel.style.borderBottomColor = new StyleColor(Color.yellow);

        Label label = new Label();
        label.name = "MessageLabel";
        label.style.unityFontStyleAndWeight = FontStyle.Bold;
        label.style.whiteSpace = WhiteSpace.Normal;

        Label header = new Label($"{category} Warnings");
        header.style.unityFontStyleAndWeight = FontStyle.Bold;
        header.style.color = new StyleColor(Color.white);
        panel.Add(header);
        panel.Add(label);

        return panel;
    }

    private void ShowWarningPanel(VisualElement Root, List<BasisValidationIssue> warnings)
    {
        string currentSignature = string.Join("|", warnings.Select(w => $"{w.Category}:{w.Message}"));
        if (currentSignature == _lastWarningSignature) return;
        _lastWarningSignature = currentSignature;

        foreach (var panel in warningPanels.Values)
            panel.style.display = DisplayStyle.None;

        int maxDisplayCount = 3;
        var groupedIssues = warnings.GroupBy(w => w.Category);

        foreach (var group in groupedIssues)
        {
            ValidationCategory category = group.Key;
            List<BasisValidationIssue> issues = group.ToList();

            if (!warningPanels.ContainsKey(category))
            {
                VisualElement newPanel = CreateCategoryPanel(category);
                Root.Add(newPanel);
                warningPanels.Add(category, newPanel);
            }

            VisualElement currentPanel = warningPanels[category];
            currentPanel.style.display = DisplayStyle.Flex;

            Label messageLabel = currentPanel.Q<Label>("MessageLabel");

            List<BasisValidationIssue> uniqueMessages = issues.Distinct().ToList();
            List<string> textLines = new List<string>();

            for (int i = 0; i < uniqueMessages.Count; i++)
            {
                if (i >= maxDisplayCount && uniqueMessages.Count > maxDisplayCount)
                {
                    int remaining = uniqueMessages.Count - i;
                    textLines.Add($"\n... + {remaining} more {category} warnings");
                    break;
                }
                textLines.Add($"- {uniqueMessages[i].Message}");
            }
            messageLabel.text = string.Join("\n", textLines);

            _warningLabelObjects[messageLabel] = uniqueMessages
                .Select(i => i.RelatedObject)
                .Where(obj => obj != null)
                .ToArray();

            if (_registeredWarningLabels.Add(messageLabel))
            {
                messageLabel.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (_warningLabelObjects.TryGetValue(messageLabel, out var objects) && objects.Length > 0)
                    {
                        EditorGUIUtility.PingObject(objects[0]);
                    }
                });
            }

            var buttonContainer = currentPanel.Q<VisualElement>("ButtonContainer");
            if (buttonContainer == null)
            {
                buttonContainer = new VisualElement() { name = "ButtonContainer" };
                currentPanel.Add(buttonContainer);
            }
            buttonContainer.Clear();

            foreach (var issue in issues)
            {
                if (issue.Fix != null)
                {
                    string actionTitle = string.IsNullOrWhiteSpace(issue.FixLabel) ? "Fix" : issue.FixLabel;
                    AutoFixButton(buttonContainer, issue.Fix, actionTitle, false);
                }
            }
        }
    }

    private void HideWarningPanel()
    {
        foreach (var panel in warningPanels.Values)
            panel.style.display = DisplayStyle.None;
        _lastWarningSignature = "";
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

    public void AutoFixButton(VisualElement rootElement, Action onClickAction, string fixMe, bool isError = true)
    {
        foreach (var child in rootElement.Children())
        {
            if (child is Button existing && existing.text == fixMe)
                return;
        }

        Button fixMeButton = new Button();

        fixMeButton.clicked += delegate
        {
            onClickAction?.Invoke();
            fixMeButton.RemoveFromHierarchy();
        };

        fixMeButton.text = fixMe;

        Color errBackground = new Color(0.96f, 0.26f, 0.21f);
        Color errHover = new Color(0.9f, 0.2f, 0.2f);

        Color warnBackground = new Color(1f, 0.63f, 0f);
        Color warnHover = new Color(1f, 0.7f, 0f);

        fixMeButton.style.backgroundColor = new StyleColor(isError ? errBackground : warnBackground);
        fixMeButton.style.color = new StyleColor(Color.white);
        fixMeButton.style.fontSize = 14;
        fixMeButton.style.unityFontStyleAndWeight = FontStyle.Bold;
        fixMeButton.style.whiteSpace = WhiteSpace.Normal;
        fixMeButton.style.flexShrink = 0;

        fixMeButton.style.paddingTop = 6;
        fixMeButton.style.paddingBottom = 6;
        fixMeButton.style.paddingLeft = 12;
        fixMeButton.style.paddingRight = 12;
        fixMeButton.style.marginBottom = 10;

        fixMeButton.style.borderTopLeftRadius = 8;
        fixMeButton.style.borderTopRightRadius = 8;
        fixMeButton.style.borderBottomLeftRadius = 8;
        fixMeButton.style.borderBottomRightRadius = 8;

        fixMeButton.style.borderLeftWidth = 0;
        fixMeButton.style.borderRightWidth = 0;
        fixMeButton.style.borderTopWidth = 0;
        fixMeButton.style.borderBottomWidth = 3;

        fixMeButton.style.unityTextAlign = TextAnchor.MiddleCenter;
        fixMeButton.style.alignSelf = Align.Auto;

        fixMeButton.RegisterCallback<MouseEnterEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(isError ? errHover : warnHover);
        });
        fixMeButton.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            fixMeButton.style.backgroundColor = new StyleColor(isError ? errBackground : warnBackground);
        });

        rootElement.Add(fixMeButton);
    }
}
