using Basis.Editor.Localization;
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
        errorPanel = BasisValidatorUI.CreateErrorPanel(rootElement, out errorMessageLabel, out errorButtonContainer);
    }

    public void CreatePassedPanel(VisualElement rootElement)
    {
        passedPanel = BasisValidatorUI.CreatePassedPanel(rootElement, out passedMessageLabel);
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
            errors.Add(new BasisValidationIssue(BasisEditorLocalization.Get("sdk.avatarValidator.avatarMissing"), ValidationCategory.Configuration, null));
            return false;
        }
        passes.Add(BasisEditorLocalization.Get("sdk.avatarValidator.avatarAssigned"));

        Transform[] children = Avatar.gameObject.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in children)
        {
            int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject);
            if (count > 0)
            {
                warnings.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.missingScripts", child.gameObject), ValidationCategory.MissingReference,
                () => RemoveMissingScripts(Avatar.gameObject),
                BasisEditorLocalization.Get("sdk.avatarValidator.missingScripts.fix"),
                child.gameObject
                ));
            }
        }

        if (Avatar.Animator != null)
        {
            passes.Add(BasisEditorLocalization.Get("sdk.avatarValidator.animator.assigned"));

            if (Avatar.Animator.runtimeAnimatorController != null)
            {
                warnings.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.avatarValidator.animator.controllerWarning"), ValidationCategory.Configuration,
                    null
                ));
            }

            if (Avatar.Animator.avatar == null)
            {
                errors.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.avatarValidator.animator.noAvatar"), ValidationCategory.Configuration,
                    FixTryCreateHumanoidAvatarOnSourceModels,
                    BasisEditorLocalization.Get("sdk.avatarValidator.animator.noAvatar.fix")
                ));
            }
            else
            {
                ValidateHumanoidRig(ref errors, ref warnings, ref passes);
            }
        }
        else
        {
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.animator.missing"), ValidationCategory.MissingReference,
                FixAddOrAssignAnimator,
                BasisEditorLocalization.Get("sdk.avatarValidator.animator.missing.fix")
            ));
        }

        if (Avatar.BlinkViseme != null && Avatar.BlinkViseme.Length > 0)
            passes.Add(BasisEditorLocalization.Get("sdk.avatarValidator.blinkViseme.assigned"));
        else
            errors.Add(new BasisValidationIssue(BasisEditorLocalization.Get("sdk.avatarValidator.blinkViseme.missing"), ValidationCategory.MissingReference, null));

        if (Avatar.FaceVisemeMovement != null && Avatar.FaceVisemeMovement.Length > 0)
            passes.Add(BasisEditorLocalization.Get("sdk.avatarValidator.faceVisemeMovement.assigned"));
        else
            errors.Add(new BasisValidationIssue(BasisEditorLocalization.Get("sdk.avatarValidator.faceVisemeMovement.missing"), ValidationCategory.MissingReference, null));

        if (Avatar.FaceBlinkMesh != null)
            passes.Add(BasisEditorLocalization.Get("sdk.avatarValidator.faceBlinkMesh.assigned"));
        else
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.faceBlinkMesh.missing"), ValidationCategory.MissingReference,
                FixAssignFaceMeshesFromChildren,
                BasisEditorLocalization.Get("sdk.avatarValidator.faceMeshes.fix")
            ));

        if (Avatar.FaceVisemeMesh != null)
            passes.Add(BasisEditorLocalization.Get("sdk.avatarValidator.faceVisemeMesh.assigned"));
        else
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.faceVisemeMesh.missing"), ValidationCategory.MissingReference,
                FixAssignFaceMeshesFromChildren,
                BasisEditorLocalization.Get("sdk.avatarValidator.faceMeshes.fix")
            ));

        if (Avatar.AvatarEyePosition != Vector2.zero)
            passes.Add(BasisEditorLocalization.Get("sdk.avatarValidator.eyePosition.set"));
        else
            errors.Add(new BasisValidationIssue(BasisEditorLocalization.Get("sdk.avatarValidator.eyePosition.notSet"), ValidationCategory.Configuration, null));

        if (Avatar.AvatarMouthPosition != Vector2.zero)
            passes.Add(BasisEditorLocalization.Get("sdk.avatarValidator.mouthPosition.set"));
        else
            errors.Add(new BasisValidationIssue(BasisEditorLocalization.Get("sdk.avatarValidator.mouthPosition.notSet"), ValidationCategory.Configuration, null));

        if (string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleName))
        {
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.bundleName.empty"), ValidationCategory.Configuration,
                FixSetDefaultBundleName,
                BasisEditorLocalization.Get("sdk.avatarValidator.bundleName.fix")
            ));
        }

        if (string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleDescription))
        {
            warnings.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.bundleDescription.empty"), ValidationCategory.Configuration,
                FixSetDefaultDescription,
                BasisEditorLocalization.Get("sdk.avatarValidator.bundleDescription.fix")
            ));
        }
        BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
        if (assetBundleObject != null)
        {
            if(assetBundleObject.UseCustomPassword && (assetBundleObject.UserSelectedPassword == null ||  assetBundleObject.UserSelectedPassword == ""))
            {
                errors.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.avatarValidator.password.empty"),
                    ValidationCategory.Security,
                    null));
            }
        }

        if (ReportIfNoIll2CPP())
        {
            warnings.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.il2cpp.warning"), ValidationCategory.None,
                null
            ));
        }

        ValidateTranslationDof(ref warnings, ref passes);

        var allNonEditorOnlyTransformsInAvatar = CollectAllNonEditorOnlyTransforms(Avatar);

        Renderer[] renderers = GetComponentsInTransforms<Renderer>(allNonEditorOnlyTransformsInAvatar);
        foreach (Renderer r in renderers)
        {
            CheckTextures(r, ref warnings);
            CheckShaders(r, ref errors, ref warnings);
        }

        SkinnedMeshRenderer[] smrs = GetComponentsInTransforms<SkinnedMeshRenderer>(allNonEditorOnlyTransformsInAvatar);
        foreach (SkinnedMeshRenderer smr in smrs)
            CheckMesh(smr, ref errors, ref warnings);

        Transform[] transforms = GetComponentsInTransforms<Transform>(allNonEditorOnlyTransformsInAvatar);
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
                    BasisEditorLocalization.Get("sdk.avatarValidator.duplicateNames.error", entry.Key, entry.Value), ValidationCategory.Configuration,
                    FixDisableDoNotAutoRenameBones,
                    BasisEditorLocalization.Get("sdk.avatarValidator.duplicateNames.fix")
                ));
            }
            else
            {
                warnings.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.avatarValidator.duplicateNames.warning", entry.Key, entry.Value), ValidationCategory.GameObject,
                    null
                ));
            }
        }

        return errors.Count == 0;
    }

    private List<Transform> CollectAllNonEditorOnlyTransforms(BasisAvatar avatar)
    {
        var results = new List<Transform> { avatar.transform };
        CollectNonEditorOnlyTransformsRecursive(avatar.transform, results);
        return results;
    }

    private void CollectNonEditorOnlyTransformsRecursive(Transform rootTransform, List<Transform> results)
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

    private T[] GetComponentsInTransforms<T>(List<Transform> transforms)
    {
        return transforms
            .SelectMany(t => t.GetComponents<T>())
            .ToArray();
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

    private static readonly HumanBodyBones[] RequiredHumanoidBones =
    {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head,
        HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
        HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
        HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot
    };

    private static readonly HumanBodyBones[] RecommendedHumanoidBones =
    {
        HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder,
        HumanBodyBones.LeftEye, HumanBodyBones.RightEye
    };

    private void ValidateHumanoidRig(ref List<BasisValidationIssue> errors, ref List<BasisValidationIssue> warnings, ref List<string> passes)
    {
        UnityEngine.Avatar rig = Avatar.Animator.avatar;
        if (!rig.isValid || !rig.isHuman)
        {
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.rig.notHumanoid"), ValidationCategory.Configuration,
                FixTryCreateHumanoidAvatarOnSourceModels,
                BasisEditorLocalization.Get("sdk.avatarValidator.animator.noAvatar.fix")
            ));
            return;
        }

        List<string> missingRequired = new List<string>();
        foreach (HumanBodyBones bone in RequiredHumanoidBones)
        {
            if (Avatar.Animator.GetBoneTransform(bone) == null)
                missingRequired.Add(bone.ToString());
        }

        List<string> missingRecommended = new List<string>();
        foreach (HumanBodyBones bone in RecommendedHumanoidBones)
        {
            if (Avatar.Animator.GetBoneTransform(bone) == null)
                missingRecommended.Add(bone.ToString());
        }

        if (missingRequired.Count == 0)
            passes.Add(BasisEditorLocalization.Get("sdk.avatarValidator.rig.bonesMapped"));
        else
            errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.rig.missingBones", string.Join(", ", missingRequired)),
                ValidationCategory.Configuration, null
            ));

        if (missingRecommended.Count > 0)
            warnings.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.rig.missingOptionalBones", string.Join(", ", missingRecommended)),
                ValidationCategory.Configuration, null, "", Avatar.Animator
            ));
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
                BasisEditorLocalization.Get("sdk.avatarValidator.translationDof.warning"),
                ValidationCategory.GameObject,
                FixTryCreateHumanoidAvatarOnSourceModels,
                BasisEditorLocalization.Get("sdk.avatarValidator.translationDof.fix")
            ));
        }
        else
        {
            passes.Add(BasisEditorLocalization.Get("sdk.avatarValidator.translationDof.passed"));
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
                    BasisEditorLocalization.Get("sdk.avatarValidator.shader.error", mat.name, renderer.gameObject.name),
                    ValidationCategory.GameObject,
                    () => FixMaterialShaderFallback(mat),
                    BasisEditorLocalization.Get("sdk.avatarValidator.shader.fix")
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
                    BasisEditorLocalization.Get("sdk.avatarValidator.texture.noMipMaps", tex.name),
                    ValidationCategory.Performance,
                    () => { texImporter.mipmapEnabled = true; texImporter.streamingMipmaps = true; texImporter.SaveAndReimport(); },
                    BasisEditorLocalization.Get("sdk.avatarValidator.texture.noMipMaps.fix", tex.name)
                ));
            }
            if (texImporter.mipmapEnabled && !texImporter.streamingMipmaps)
            {
                warnings.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.avatarValidator.texture.noStreamingMips", tex.name),
                    ValidationCategory.Performance,
                    () => { texImporter.streamingMipmaps = true; texImporter.SaveAndReimport(); },
                    BasisEditorLocalization.Get("sdk.avatarValidator.texture.noStreamingMips.fix", tex.name)
                ));
            }
            if (texImporter.maxTextureSize > MaxTextureSizeBeforeWarning)
            {
                warnings.Add(new BasisValidationIssue(
                    BasisEditorLocalization.Get("sdk.avatarValidator.texture.tooLarge", tex.name, texImporter.maxTextureSize, MaxTextureSizeBeforeWarning),
                    ValidationCategory.Performance,
                    () => { texImporter.maxTextureSize = MaxTextureSizeBeforeWarning; texImporter.SaveAndReimport(); },
                    BasisEditorLocalization.Get("sdk.avatarValidator.texture.tooLarge.fix", tex.name, MaxTextureSizeBeforeWarning)
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
                BasisEditorLocalization.Get("sdk.avatarValidator.mesh.noMesh", skinnedMeshRenderer.gameObject.name),
                ValidationCategory.GameObject, null));
            return;
        }
        var mesh = skinnedMeshRenderer.sharedMesh;
        if (mesh.triangles != null && mesh.triangles.Length / 3 > MaxTrianglesBeforeWarning)
            Warnings.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.mesh.tooManyTriangles", skinnedMeshRenderer.gameObject.name, MaxTrianglesBeforeWarning),
                ValidationCategory.Performance, null));
        if (mesh.vertices != null && mesh.vertices.Length > MeshVertices)
            Warnings.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.mesh.tooManyVertices", skinnedMeshRenderer.gameObject.name, MeshVertices),
                ValidationCategory.Performance, null));
        if (mesh.blendShapeCount != 0)
        {
            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(assetPath))
            {
                ModelImporter modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
                if (modelImporter != null && !ModelImporterExtensions.IsLegacyBlendShapeNormalsEnabled(modelImporter))
                    Warnings.Add(new BasisValidationIssue(
                        BasisEditorLocalization.Get("sdk.avatarValidator.mesh.legacyBlendshapes", assetPath),
                        ValidationCategory.GameObject, null));
            }
        }
        if (skinnedMeshRenderer.allowOcclusionWhenDynamic == false)
            Errors.Add(new BasisValidationIssue(
                BasisEditorLocalization.Get("sdk.avatarValidator.mesh.dynamicOcclusion", skinnedMeshRenderer.gameObject.name),
                ValidationCategory.GameObject, FixEnableDynamicOcclusionAllSMR,
                BasisEditorLocalization.Get("sdk.avatarValidator.mesh.dynamicOcclusion.fix")));
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
                BasisValidatorUI.AutoFixButton(errorButtonContainer, issue.Fix, actionTitle, true);
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

        Label header = new Label(BasisEditorLocalization.Get("sdk.validator.warnings.header", category));
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
                    textLines.Add(BasisEditorLocalization.Get("sdk.validator.warnings.more", remaining, category));
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
                    string actionTitle = string.IsNullOrWhiteSpace(issue.FixLabel) ? BasisEditorLocalization.Get("sdk.validator.fix.default") : issue.FixLabel;
                    BasisValidatorUI.AutoFixButton(buttonContainer, issue.Fix, actionTitle, false);
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

}
