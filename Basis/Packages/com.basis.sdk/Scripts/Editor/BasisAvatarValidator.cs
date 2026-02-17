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

    // Track fix buttons created this frame
    public List<Button> FixMeButtons = new List<Button>();

    public BasisAvatarValidator(BasisAvatar avatar, VisualElement root)
    {
        Avatar = avatar;
        Root = root;

        CreateErrorPanel(root);
        //CreateWarningPanel(root);
        CreatePassedPanel(root);

        EditorApplication.update += UpdateValidation; // Run per frame
    }

    public void OnDestroy()
    {
        EditorApplication.update -= UpdateValidation; // Stop updating on destroy
    }

    private void UpdateValidation()
    {
        // Clear fix buttons each frame so they match current validation results
        //ClearFixButtons(Root);

        if (ValidateAvatar(out List<BasisValidationIssue> errors, out List<BasisValidationIssue> warnings, out List<string> passes))
            HideErrorPanel();
        else
            ShowErrorPanel(Root, errors);

        if (warnings.Count > 0)
            ShowWarningPanel(Root, warnings);
        else
            HideWarningPanel();

        // Optional: show passes
        // if (passes.Count > 0) ShowPassedPanel(passes); else HidePassedPanel();
    }

    public void CreateErrorPanel(VisualElement rootElement)
    {
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
        errorMessageLabel.style.whiteSpace = WhiteSpace.Normal;
        errorPanel.Add(errorMessageLabel);

        errorPanel.style.display = DisplayStyle.None;
        rootElement.Add(errorPanel);
    }
    /*
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

        warningMessageLabel = new Label("No Warnings");
        warningMessageLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        warningMessageLabel.style.whiteSpace = WhiteSpace.Normal;
        warningPanel.Add(warningMessageLabel);

        warningPanel.style.display = DisplayStyle.None;
        rootElement.Add(warningPanel);
    }
    */

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

    public enum ValidationCategory
    {
        None,
        Configuration,
        GameObject,
        Perfomance,
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
        BasisDebug.Log($"Removed a total of {removedCount} missing scripts.", BasisDebug.LogTag.Editor);
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

        // Animator
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
                    "Set source model(s) to Humanoid + Create Avatar"
                ));
            }
        }
        else
        {
            errors.Add(new BasisValidationIssue(
                "Animator is missing.", ValidationCategory.MissingReference,
                FixAddOrAssignAnimator,
                "Add/Assign Animator"
            ));
        }

        // Blink/Viseme metadata
        if (Avatar.BlinkViseme != null && Avatar.BlinkViseme.Length > 0)
            passes.Add("BlinkViseme Meta Data is assigned.");
        else
            errors.Add(new BasisValidationIssue("BlinkViseme Meta Data is missing.", ValidationCategory.MissingReference, null));

        if (Avatar.FaceVisemeMovement != null && Avatar.FaceVisemeMovement.Length > 0)
            passes.Add("FaceVisemeMovement Meta Data is assigned.");
        else
            errors.Add(new BasisValidationIssue("FaceVisemeMovement Meta Data is missing.", ValidationCategory.MissingReference, null));

        // Face meshes
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

        // Eye/Mouth position
        if (Avatar.AvatarEyePosition != Vector2.zero)
            passes.Add("Avatar Eye Position is set.");
        else
            errors.Add(new BasisValidationIssue("Avatar Eye Position is not set.", ValidationCategory.Configuration, null));

        if (Avatar.AvatarMouthPosition != Vector2.zero)
            passes.Add("Avatar Mouth Position is set.");
        else
            errors.Add(new BasisValidationIssue("Avatar Mouth Position is not set.", ValidationCategory.Configuration, null));

        // Bundle name/description
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
                    "Can not have custom password be empty!",
                    ValidationCategory.Security,
                    null));
            }
        }
        /*
        // Processing options
        if (Avatar.ProcessingAvatarOptions != null && Avatar.ProcessingAvatarOptions.RemoveUnusedBlendshapes == false && Avatar.transform.Find("HVR.Networking") == null)
        {
            warnings.Add(new BasisValidationIssue(
                "Recommend turning on RemoveUnusedBlendshapes in Processing Options! Leave off for face/eye tracking.", ValidationCategory.Perfomance,
                FixRemoveUnusedBlendShape,
                "Turn on RemoveUnusedBlendshapes (dont if your using Face/Eye Tracking!!!"
            ));
        }
        */

        // IL2CPP
        if (ReportIfNoIll2CPP())
        {
            warnings.Add(new BasisValidationIssue(
                "IL2CPP may be missing. Check Unity Hub modules (Linux/Windows/Android IL2CPP commonly needed).", ValidationCategory.None,
                null
            ));
        }

        // Translation DoF + humanoid avatar generation (best-effort, fixable)
        ValidateTranslationDof(ref warnings, ref passes);

        // Renderers checks: textures + shader health + instancing
        Renderer[] renderers = Avatar.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer r in renderers)
        {
            CheckTextures(r, ref warnings);
            CheckShaders(r, ref errors, ref warnings);
        }

        // Mesh checks
        SkinnedMeshRenderer[] smrs = Avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (SkinnedMeshRenderer smr in smrs)
            CheckMesh(smr, ref errors, ref warnings);

        // Duplicate names
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
                    "Allow auto-rename bones"
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
    /*
    public void FixRemoveUnusedBlendShape()
    {
        if (Avatar?.ProcessingAvatarOptions == null) return;
        Avatar.ProcessingAvatarOptions.RemoveUnusedBlendshapes = true;
        EditorUtility.SetDirty(Avatar);
    }
    */
    // -----------------------------
    // NEW: Fix helpers
    // -----------------------------

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

            // Prefer meshes with blendshapes (likely face)
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

    /// <summary>
    /// Best-effort fix: set source model(s) to Humanoid, create avatar from model, enable Translation DoF.
    /// Some models won't auto-map; Unity will still require manual mapping in Rig tab.
    /// </summary>
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
            if (!hd.hasTranslationDoF)
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
                "Translation DoF is Eabled on one or more source models (Humanoid). This can cause retargeting issues.",
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

    // -----------------------------
    // Shader checks (pink/error + instancing)
    // -----------------------------

    // Minimal "pink shader" detection:
    // - shader is null OR shader not supported OR material renders with InternalErrorShader
    // Fix:
    // - cannot auto-fix missing shader safely (depends on your pipeline), but we can:
    //   1) prompt user, and
    //   2) offer auto-switch to URP Lit / Standard as a *best-effort* option when available.
    //
    // GPU instancing:
    // - if shader supports instancing and material.enableInstancing is false -> fix to enable
    // - if shader does NOT support instancing -> warning; no safe auto-fix except changing shader
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

            bool isErrorShader = (shader == null)
                                 || !shader.isSupported
                                 || shader.name == "Hidden/InternalErrorShader";

            if (isErrorShader)
            {
                // Offer best-effort fallback: URP Lit if present else Standard if present
                errors.Add(new BasisValidationIssue(
                    $"Material \"{mat.name}\" on \"{renderer.gameObject.name}\" is using an error/unsupported shader (pink).",
                    ValidationCategory.GameObject,
                    () => FixMaterialShaderFallback(mat),
                    "Set shader fallback (URP Lit / Standard)"
                ));

                // If shader is broken, instancing doesn't matter; skip instancing check
                continue;
            }
        }
    }
    private void FixMaterialShaderFallback(Material mat)
    {
        if (mat == null) return;

        // Prefer URP Lit if available
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit != null)
        {
            mat.shader = urpLit;
            EditorUtility.SetDirty(mat);
            return;
        }

        // Otherwise fallback to Built-in Standard if available
        Shader standard = Shader.Find("Standard");
        if (standard != null)
        {
            mat.shader = standard;
            EditorUtility.SetDirty(mat);
            return;
        }

        // If neither exists, we can't safely fix automatically.
        Debug.LogWarning($"[BasisAvatarValidator] No fallback shader found (URP Lit / Standard) for material '{mat.name}'.");
    }

    // -----------------------------
    // Textures
    // -----------------------------
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
                    ValidationCategory.Perfomance,
                    () =>
                    {
                        texImporter.mipmapEnabled = true;
                        texImporter.streamingMipmaps = true; // all mip maps should be streamed
                        texImporter.SaveAndReimport();
                    },
                    $"Enable Mip Maps on \"{tex.name}\""
                ));
            }

            if (texImporter.mipmapEnabled && !texImporter.streamingMipmaps)
            {
                warnings.Add(new BasisValidationIssue(
                    $"Texture \"{tex.name}\" does not have Streaming Mip Maps enabled. This will negatively affect its performance ranking.",
                    ValidationCategory.Perfomance,
                    () =>
                    {
                        texImporter.streamingMipmaps = true;
                        texImporter.SaveAndReimport();
                    },
                    $"Enable Streaming Mip Maps on \"{tex.name}\""
                ));
            }

            if (texImporter.maxTextureSize > MaxTextureSizeBeforeWarning)
            {
                warnings.Add(new BasisValidationIssue(

                    $"Texture \"{tex.name}\" is {texImporter.maxTextureSize} (should be <= {MaxTextureSizeBeforeWarning}). This will negatively affect its performance ranking.",
                    ValidationCategory.Perfomance,
                    () =>
                    {
                        texImporter.maxTextureSize = MaxTextureSizeBeforeWarning;
                        texImporter.SaveAndReimport();
                    },
                    $"Resize \"{tex.name}\" to {MaxTextureSizeBeforeWarning}"
                ));
            }
        }
    }

    // -----------------------------
    // Mesh checks
    // -----------------------------
    public void CheckMesh(SkinnedMeshRenderer skinnedMeshRenderer, ref List<BasisValidationIssue> Errors, ref List<BasisValidationIssue> Warnings)
    {
        if (skinnedMeshRenderer == null)
            return;

        if (skinnedMeshRenderer.sharedMesh == null)
        {
            Errors.Add(new BasisValidationIssue(
                $"{skinnedMeshRenderer.gameObject.name} does not have a mesh assigned to its SkinnedMeshRenderer!",
                ValidationCategory.GameObject,
                null
            ));
            return;
        }

        var mesh = skinnedMeshRenderer.sharedMesh;

        // Triangles
        if (mesh.triangles != null)
        {
            if (mesh.triangles.Length / 3 > MaxTrianglesBeforeWarning)
            {
                Warnings.Add(new BasisValidationIssue(
                    $"{skinnedMeshRenderer.gameObject.name} has more than {MaxTrianglesBeforeWarning} triangles. This will cause performance issues.",
                    ValidationCategory.Perfomance,
                    null
                ));
            }
        }

        // Vertices
        if (mesh.vertices != null)
        {
            if (mesh.vertices.Length > MeshVertices)
            {
                Warnings.Add(new BasisValidationIssue(
                    $"{skinnedMeshRenderer.gameObject.name} has more vertices than can be properly rendered ({MeshVertices}). This will cause performance issues.",
                    ValidationCategory.Perfomance,
                    null
                ));
            }
        }

        // Legacy blendshape normals warning (no safe auto fix here without your extension details)
        if (mesh.blendShapeCount != 0)
        {
            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(assetPath))
            {
                ModelImporter modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                if (modelImporter != null && !ModelImporterExtensions.IsLegacyBlendShapeNormalsEnabled(modelImporter))
                {
                    Warnings.Add(new BasisValidationIssue(
                        $"{assetPath} does not have legacy blendshapes enabled, which may increase file size.",
                        ValidationCategory.GameObject,
                        null
                    ));
                }
            }
        }

        // Dynamic occlusion
        if (skinnedMeshRenderer.allowOcclusionWhenDynamic == false)
        {
            Errors.Add(new BasisValidationIssue(
                "Dynamic Occlusion disabled on SkinnedMeshRenderer: " + skinnedMeshRenderer.gameObject.name,
                ValidationCategory.GameObject,
                FixEnableDynamicOcclusionAllSMR,
                "Enable Dynamic Occlusion on all SMRs"
            ));
        }
    }

    // -----------------------------
    // IL2CPP check
    // -----------------------------
    public static bool ReportIfNoIll2CPP()
    {
        string unityPath = EditorApplication.applicationPath;
        string unityFolder = Path.GetDirectoryName(unityPath);

        string il2cppPath = Path.Combine(unityFolder, "Data", "il2cpp");
        bool il2cppExists = Directory.Exists(il2cppPath);
        return !il2cppExists;
    }

    // -----------------------------
    // UI panels
    // -----------------------------
    private void ShowErrorPanel(VisualElement Root, List<BasisValidationIssue> errors)
    {
        List<string> issueList = new List<string>();

        for (int i = 0; i < errors.Count; i++)
        {
            var issue = errors[i];
            if (issue.Fix != null)
            {
                string actionTitle = string.IsNullOrWhiteSpace(issue.FixLabel) ? issue.Message : issue.FixLabel;
                AutoFixButton(Root, issue.Fix, actionTitle, true);
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
    }

    private VisualElement CreateCategoryPanel(ValidationCategory category)
    {
        VisualElement panel = new VisualElement();

        // Style
        panel.style.backgroundColor = new StyleColor(new Color(0.65098f, 0.63137f, 0.05098f, 0.5f));
        panel.style.marginBottom = 10;
        panel.style.paddingTop = 5;
        panel.style.paddingBottom = 5;
        panel.style.borderLeftWidth = 2;
        panel.style.borderRightWidth = 2;
        panel.style.borderTopWidth = 2;
        panel.style.borderBottomWidth = 2;
        panel.style.borderBottomColor = new StyleColor(Color.yellow);
        // ... add your radius styling here ...

        // Create Label
        Label label = new Label();
        label.name = "MessageLabel";
        label.style.unityFontStyleAndWeight = FontStyle.Bold;
        label.style.whiteSpace = WhiteSpace.Normal;

        // Optional: Add a header to show which category this is
        Label header = new Label($"{category} Warnings");
        header.style.unityFontStyleAndWeight = FontStyle.Bold;
        header.style.color = new StyleColor(Color.white); // Make header distinct
        panel.Add(header);

        panel.Add(label);

        return panel;
    }
    private string _lastWarningSignature = "";
    private void ShowWarningPanel(VisualElement Root, List<BasisValidationIssue> warnings)
    {
        // 1. GENERATE SIGNATURE
        // Create a simple string representing the current warnings (e.g. "Category:Message|Category:Message")
        // If the list is huge, we can use a hash, but string join is usually fine for UI lists.
        string currentSignature = string.Join("|", warnings.Select(w => $"{w.Category}:{w.Message}"));

        // 2. DIRTY CHECK
        // If the data is exactly the same as the last frame, DO NOTHING.
        if (currentSignature == _lastWarningSignature) return;

        // Data changed, save the new signature
        _lastWarningSignature = currentSignature;

        // 3. REBUILD LOGIC
        // (Optimization: Instead of warningPanel.Clear(), we hide existing ones and re-enable only what we need)

        // Reset all known panels to hidden initially (pool-like behavior)
        foreach (var panel in warningPanels.Values)
        {
            panel.style.display = DisplayStyle.None;
        }

        int maxDisplayCount = 3;
        var groupedIssues = warnings.GroupBy(w => w.Category);

        foreach (var group in groupedIssues)
        {
            ValidationCategory category = group.Key;
            List<BasisValidationIssue> issues = group.ToList();

            // --- LAZY CREATION ---
            // Only create the visual element if we've never seen this category before
            if (!warningPanels.ContainsKey(category))
            {
                VisualElement newPanel = CreateCategoryPanel(category); // Uses your helper method
                Root.Add(newPanel); // Add to the main Root UI
                warningPanels.Add(category, newPanel);
            }

            // Get the cached panel
            VisualElement currentPanel = warningPanels[category];
            currentPanel.style.display = DisplayStyle.Flex; // Unhide it

            // --- UPDATE TEXT ---
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
            messageLabel.RegisterCallback<PointerDownEvent>(evt =>
            {
                Selection.objects = uniqueMessages.Select(i => i.RelatedObject).Where(obj => obj != null).ToArray();
                if (Selection.objects.Length > 0)
                {
                    EditorGUIUtility.PingObject(Selection.objects[0]);
                }
            });

            // --- BUTTON HANDLING ---
            // Buttons are tricky because they have callbacks. 
            // Simple approach: Clear ONLY buttons (not the whole panel) and rebuild them.
            // Assuming your CreateCategoryPanel adds a container named "ButtonContainer" or we append to end.

            // Remove old buttons (elements that are NOT the label or header)
            // A cleaner way is to keep a dedicated container for buttons inside the panel
            var buttonContainer = currentPanel.Q<VisualElement>("ButtonContainer");
            if (buttonContainer == null)
            {
                // If you didn't create a container in the helper, create one now dynamically
                buttonContainer = new VisualElement() { name = "ButtonContainer" };
                currentPanel.Add(buttonContainer);
            }
            buttonContainer.Clear(); // Only clear the buttons

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
        {
            panel.style.display = DisplayStyle.None;
        }
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

    public void ClearFixButtons(VisualElement rootElement)
    {
        for (int i = 0; i < FixMeButtons.Count; i++)
            rootElement.Remove(FixMeButtons[i]);

        FixMeButtons.Clear();
    }

    public void AutoFixButton(VisualElement rootElement, Action onClickAction, string fixMe, bool isError = true)
    {
        foreach (Button button in FixMeButtons)
        {
            if (button.text == fixMe)
                return;
        }

        Button fixMeButton = new Button();

        fixMeButton.clicked += delegate
        {
            onClickAction?.Invoke();
            ClearFixButtons(Root);
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
        FixMeButtons.Add(fixMeButton);
    }
}
