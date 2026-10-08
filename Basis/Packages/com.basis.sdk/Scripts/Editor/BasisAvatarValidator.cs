using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Everything the SDK checks about an avatar before it can be uploaded.
///
/// <para>Checks are declared as groups on <see cref="BasisValidationRunner"/>, which decides when
/// they run — see that class for why they no longer run on every editor frame. A group is a unit of
/// change detection: the panels are only rebuilt when a group's findings differ from last time.</para>
/// </summary>
public class BasisAvatarValidator : BasisValidationRunner
{
    private readonly BasisAvatar Avatar;

    private VisualElement errorPanel;
    private Label errorMessageLabel;

    private Dictionary<ValidationCategory, VisualElement> warningPanels = new Dictionary<ValidationCategory, VisualElement>();

    private VisualElement passedPanel;
    private Label passedMessageLabel;

    public const int MaxTrianglesBeforeWarning = 150000;
    public const int MeshVertices = 65535;
    public const int MaxTextureSizeBeforeWarning = 4096;
    public const int MaxTextureSizeWithoutMipMaps = 256;
    private static readonly string[] TextureImporterPlatforms = { "Standalone", "Android", "iPhone" };

    private VisualElement errorButtonContainer;

    private HashSet<Label> _registeredWarningLabels = new HashSet<Label>();
    private Dictionary<Label, UnityEngine.Object[]> _warningLabelObjects = new Dictionary<Label, UnityEngine.Object[]>();

    private readonly BasisValidationHierarchyScan _scan = new BasisValidationHierarchyScan();
    private readonly Dictionary<string, int> _nameCounts = new Dictionary<string, int>(256);
    private readonly HashSet<EntityId> _seenMaterials = new HashSet<EntityId>();
    private readonly HashSet<EntityId> _seenTextures = new HashSet<EntityId>();
    private readonly HashSet<EntityId> _seenMeshes = new HashSet<EntityId>();
    private readonly HashSet<string> _seenModelPaths = new HashSet<string>();
    private readonly List<Material> _materialScratch = new List<Material>(8);

    public BasisAvatarValidator(BasisAvatar avatar, VisualElement root)
    {
        Avatar = avatar;

        CreateErrorPanel(root);
        //CreateWarningPanel(root);
        CreatePassedPanel(root);

        BeginValidation(root,
            ValidateConfiguration,
            ValidateFace,
            ValidateRig,
            ValidateHierarchy,
            ValidateMeshes,
            ValidateMaterials,
            ValidateTextures);
    }

    protected override void RefreshScan()
    {
        _scan.Rebuild(Avatar != null ? Avatar.transform : null);
    }

    protected override void Refresh(BasisValidationBucket results)
    {
        if (results.Errors.Count == 0)
        {
            HideErrorPanel();
        }
        else
        {
            ShowErrorPanel(results.Errors);
        }

        if (results.Warnings.Count > 0)
        {
            ShowWarningPanel(Root, results.Warnings);
        }
        else
        {
            HideWarningPanel();
        }
    }

    public void CreateErrorPanel(VisualElement rootElement)
    {
        errorPanel = BasisValidatorUI.CreateErrorPanel(rootElement, out errorMessageLabel, out errorButtonContainer);
    }

    public void CreatePassedPanel(VisualElement rootElement)
    {
        passedPanel = BasisValidatorUI.CreatePassedPanel(rootElement, out passedMessageLabel);
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

    /// <summary>
    /// Runs the complete suite — every group — and returns fresh lists.
    ///
    /// <para>This is what the build button calls. Uploads never read the panels: they get a full
    /// pass taken on the spot, so nothing can ship on a stale or partial result.</para>
    /// </summary>
    public bool ValidateAvatar(out List<BasisValidationIssue> errors, out List<BasisValidationIssue> warnings, out List<string> passes)
    {
        BasisValidationBucket results = RunAllGroups();
        errors = new List<BasisValidationIssue>(results.Errors);
        warnings = new List<BasisValidationIssue>(results.Warnings);
        passes = new List<string>(results.Passes);
        return errors.Count == 0;
    }

    private void ValidateConfiguration(BasisValidationBucket bucket)
    {
        if (Avatar == null)
        {
            bucket.Error(BasisEditorLocalization.Get("sdk.avatarValidator.avatarMissing"), ValidationCategory.Configuration);
            return;
        }
        bucket.Pass(BasisEditorLocalization.Get("sdk.avatarValidator.avatarAssigned"));

        if (Avatar.AvatarEyePosition != Vector2.zero)
            bucket.Pass(BasisEditorLocalization.Get("sdk.avatarValidator.eyePosition.set"));
        else
            bucket.Error(BasisEditorLocalization.Get("sdk.avatarValidator.eyePosition.notSet"), ValidationCategory.Configuration);

        if (Avatar.AvatarMouthPosition != Vector2.zero)
            bucket.Pass(BasisEditorLocalization.Get("sdk.avatarValidator.mouthPosition.set"));
        else
            bucket.Error(BasisEditorLocalization.Get("sdk.avatarValidator.mouthPosition.notSet"), ValidationCategory.Configuration);

        if (string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleName))
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.avatarValidator.bundleName.empty"), ValidationCategory.Configuration,
                FixSetDefaultBundleName,
                BasisEditorLocalization.Get("sdk.avatarValidator.bundleName.fix"));
        }

        if (string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleDescription))
        {
            bucket.Warn(
                BasisEditorLocalization.Get("sdk.avatarValidator.bundleDescription.empty"), ValidationCategory.Configuration,
                FixSetDefaultDescription,
                BasisEditorLocalization.Get("sdk.avatarValidator.bundleDescription.fix"));
        }

        if (Avatar.UseCustomPassword && string.IsNullOrEmpty(Avatar.UserSelectedPassword))
        {
            bucket.Error(BasisEditorLocalization.Get("sdk.avatarValidator.password.empty"), ValidationCategory.Security);
        }

        if (BasisValidationAssetCache.Il2CppMissing)
        {
            bucket.Warn(BasisEditorLocalization.Get("sdk.avatarValidator.il2cpp.warning"), ValidationCategory.None);
        }
    }

    private void ValidateFace(BasisValidationBucket bucket)
    {
        if (Avatar == null) return;

        if (Avatar.BlinkViseme != null && Avatar.BlinkViseme.Length > 0)
            bucket.Pass(BasisEditorLocalization.Get("sdk.avatarValidator.blinkViseme.assigned"));
        else
            bucket.Error(BasisEditorLocalization.Get("sdk.avatarValidator.blinkViseme.missing"), ValidationCategory.MissingReference);

        if (Avatar.FaceVisemeMovement != null && Avatar.FaceVisemeMovement.Length > 0)
        {
            bool anyVisemeMapped = false;
            int visemeCount = Avatar.FaceVisemeMovement.Length;
            for (int Index = 0; Index < visemeCount; Index++)
            {
                if (Avatar.FaceVisemeMovement[Index] != -1)
                {
                    anyVisemeMapped = true;
                    break;
                }
            }

            if (anyVisemeMapped)
                bucket.Pass(BasisEditorLocalization.Get("sdk.avatarValidator.faceVisemeMovement.assigned"));
            else
                bucket.Warn(BasisEditorLocalization.Get("sdk.avatarValidator.faceVisemeMovement.allUnmapped"), ValidationCategory.Configuration);
        }
        else
        {
            bucket.Error(BasisEditorLocalization.Get("sdk.avatarValidator.faceVisemeMovement.missing"), ValidationCategory.MissingReference);
        }

        if (Avatar.FaceVisemeProfiles != null && Avatar.FaceVisemeProfiles.Length > 0)
        {
            if (Avatar.FaceVisemeMovement != null && Avatar.FaceVisemeProfiles.Length != Avatar.FaceVisemeMovement.Length)
            {
                bucket.Warn(BasisEditorLocalization.Get("sdk.avatarValidator.visemeProfiles.lengthMismatch"), ValidationCategory.Configuration);
            }

            int profileCount = Avatar.FaceVisemeProfiles.Length;
            for (int Index = 0; Index < profileCount; Index++)
            {
                BasisVisemeProfile Profile = Avatar.FaceVisemeProfiles[Index];
                if (Profile.OutMax <= Profile.OutMin && Avatar.FaceVisemeMovement != null && Index < Avatar.FaceVisemeMovement.Length && Avatar.FaceVisemeMovement[Index] != -1)
                {
                    bucket.Warn(BasisEditorLocalization.Get("sdk.avatarValidator.visemeProfiles.inertRange"), ValidationCategory.Configuration);
                    break;
                }
            }
        }

        if (Avatar.FaceBlinkMesh != null)
            bucket.Pass(BasisEditorLocalization.Get("sdk.avatarValidator.faceBlinkMesh.assigned"));
        else
            bucket.Error(
                BasisEditorLocalization.Get("sdk.avatarValidator.faceBlinkMesh.missing"), ValidationCategory.MissingReference,
                FixAssignFaceMeshesFromChildren,
                BasisEditorLocalization.Get("sdk.avatarValidator.faceMeshes.fix"));

        if (Avatar.FaceVisemeMesh != null)
            bucket.Pass(BasisEditorLocalization.Get("sdk.avatarValidator.faceVisemeMesh.assigned"));
        else
            bucket.Error(
                BasisEditorLocalization.Get("sdk.avatarValidator.faceVisemeMesh.missing"), ValidationCategory.MissingReference,
                FixAssignFaceMeshesFromChildren,
                BasisEditorLocalization.Get("sdk.avatarValidator.faceMeshes.fix"));
    }

    private void ValidateRig(BasisValidationBucket bucket)
    {
        if (Avatar == null) return;

        if (Avatar.Animator == null)
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.avatarValidator.animator.missing"), ValidationCategory.MissingReference,
                FixAddOrAssignAnimator,
                BasisEditorLocalization.Get("sdk.avatarValidator.animator.missing.fix"));
            return;
        }

        bucket.Pass(BasisEditorLocalization.Get("sdk.avatarValidator.animator.assigned"));

        if (Avatar.Animator.runtimeAnimatorController != null)
        {
            bucket.Warn(BasisEditorLocalization.Get("sdk.avatarValidator.animator.controllerWarning"), ValidationCategory.Configuration);
        }

        if (Avatar.Animator.avatar == null)
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.avatarValidator.animator.noAvatar"), ValidationCategory.Configuration,
                FixTryCreateHumanoidAvatarOnSourceModels,
                BasisEditorLocalization.Get("sdk.avatarValidator.animator.noAvatar.fix"));
        }
        else
        {
            ValidateHumanoidRig(bucket);
        }

        ValidateTranslationDof(bucket);
    }

    private void ValidateHierarchy(BasisValidationBucket bucket)
    {
        if (Avatar == null) return;

        List<Transform> all = _scan.All;
        int transformCount = all.Count;
        for (int Index = 0; Index < transformCount; Index++)
        {
            Transform child = all[Index];
            if (child == null) continue;
            if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) <= 0) continue;

            bucket.Warn(
                BasisEditorLocalization.Get("sdk.avatarValidator.missingScripts", child.gameObject), ValidationCategory.MissingReference,
                () => RemoveMissingScripts(Avatar.gameObject),
                BasisEditorLocalization.Get("sdk.avatarValidator.missingScripts.fix"),
                child.gameObject);
        }

        // Duplicate names are only a problem for what actually ships, so this walks the same
        // EditorOnly-filtered list the mesh and texture checks do.
        _nameCounts.Clear();
        List<Transform> active = _scan.Active;
        int activeCount = active.Count;
        for (int Index = 0; Index < activeCount; Index++)
        {
            Transform transform = active[Index];
            if (transform == null) continue;
            string name = transform.name;
            _nameCounts.TryGetValue(name, out int seen);
            _nameCounts[name] = seen + 1;
        }

        bool doNotAutoRename = Avatar.ProcessingAvatarOptions != null && Avatar.ProcessingAvatarOptions.doNotAutoRenameBones;
        foreach (KeyValuePair<string, int> entry in _nameCounts)
        {
            if (entry.Value <= 1) continue;

            if (doNotAutoRename)
            {
                bucket.Error(
                    BasisEditorLocalization.Get("sdk.avatarValidator.duplicateNames.error", entry.Key, entry.Value), ValidationCategory.Configuration,
                    FixDisableDoNotAutoRenameBones,
                    BasisEditorLocalization.Get("sdk.avatarValidator.duplicateNames.fix"));
            }
            else
            {
                bucket.Warn(
                    BasisEditorLocalization.Get("sdk.avatarValidator.duplicateNames.warning", entry.Key, entry.Value), ValidationCategory.GameObject);
            }
        }
    }

    private void ValidateMeshes(BasisValidationBucket bucket)
    {
        if (Avatar == null) return;

        _seenMeshes.Clear();
        List<SkinnedMeshRenderer> skinnedMeshes = _scan.SkinnedMeshes;
        int count = skinnedMeshes.Count;
        for (int Index = 0; Index < count; Index++)
        {
            CheckMesh(skinnedMeshes[Index], bucket);
        }
    }

    private void ValidateMaterials(BasisValidationBucket bucket)
    {
        if (Avatar == null) return;

        _seenMaterials.Clear();
        List<Renderer> renderers = _scan.Renderers;
        int rendererCount = renderers.Count;
        for (int Index = 0; Index < rendererCount; Index++)
        {
            Renderer renderer = renderers[Index];
            if (renderer == null) continue;

            renderer.GetSharedMaterials(_materialScratch);
            int materialCount = _materialScratch.Count;
            for (int MaterialIndex = 0; MaterialIndex < materialCount; MaterialIndex++)
            {
                Material material = _materialScratch[MaterialIndex];
                if (material == null) continue;
                if (!_seenMaterials.Add(material.GetEntityId())) continue;

                Shader shader = material.shader;
                if (shader != null && shader.isSupported && shader.name != "Hidden/InternalErrorShader") continue;

                bucket.Error(
                    BasisEditorLocalization.Get("sdk.avatarValidator.shader.error", material.name, renderer.gameObject.name),
                    ValidationCategory.GameObject,
                    () => FixMaterialShaderFallback(material),
                    BasisEditorLocalization.Get("sdk.avatarValidator.shader.fix"));
            }
        }
    }

    private void ValidateTextures(BasisValidationBucket bucket)
    {
        if (Avatar == null) return;

        _seenMaterials.Clear();
        _seenTextures.Clear();

        List<Renderer> renderers = _scan.Renderers;
        int rendererCount = renderers.Count;
        for (int Index = 0; Index < rendererCount; Index++)
        {
            Renderer renderer = renderers[Index];
            if (renderer == null) continue;

            renderer.GetSharedMaterials(_materialScratch);
            int materialCount = _materialScratch.Count;
            for (int MaterialIndex = 0; MaterialIndex < materialCount; MaterialIndex++)
            {
                Material material = _materialScratch[MaterialIndex];
                if (material == null) continue;

                // Materials are shared far more often than they are unique, and a texture reached
                // through two of them is still one texture: both are deduplicated so each importer
                // is opened once per pass instead of once per renderer that happens to use it.
                if (!_seenMaterials.Add(material.GetEntityId())) continue;

                Shader shader = material.shader;
                if (shader == null) continue;

                int[] textureProperties = BasisValidationAssetCache.TexturePropertyIds(shader);
                for (int PropertyIndex = 0; PropertyIndex < textureProperties.Length; PropertyIndex++)
                {
                    int propertyId = textureProperties[PropertyIndex];
                    if (!material.HasProperty(propertyId)) continue;

                    Texture texture = material.GetTexture(propertyId);
                    if (texture == null) continue;
                    if (!_seenTextures.Add(texture.GetEntityId())) continue;

                    CheckTexture(texture, bucket);
                }
            }
        }
    }

    private void FixAddOrAssignAnimator()
    {
        if (Avatar == null) return;
        if (!Avatar.TryGetComponent(out Animator anim)) anim = Avatar.gameObject.AddComponent<Animator>();
        Avatar.Animator = anim;
        EditorUtility.SetDirty(Avatar);
        EditorUtility.SetDirty(anim);
    }

    private void FixSetDefaultBundleName()
    {
        if (Avatar == null) return;
        Undo.RecordObject(Avatar, "Set Default Bundle Name");
        string name = BasisContentDefaults.ResolveName(Avatar.gameObject, BasisEditorLocalization.Get("sdk.avatarValidator.bundleName.default"));
        Avatar.BasisBundleDescription.AssetBundleName = name;
        EditorUtility.SetDirty(Avatar);
        BasisContentDefaults.SyncField(Root, BasisSDKConstants.AvatarName, name);
    }

    private void FixSetDefaultDescription()
    {
        if (Avatar == null) return;
        Undo.RecordObject(Avatar, "Set Default Description");
        string name = string.IsNullOrEmpty(Avatar.BasisBundleDescription.AssetBundleName)
            ? BasisContentDefaults.ResolveName(Avatar.gameObject, BasisEditorLocalization.Get("sdk.avatarValidator.bundleName.default"))
            : Avatar.BasisBundleDescription.AssetBundleName;
        string description = BasisEditorLocalization.Get("sdk.avatarValidator.bundleDescription.default", name);
        Avatar.BasisBundleDescription.AssetBundleDescription = description;
        EditorUtility.SetDirty(Avatar);
        BasisContentDefaults.SyncField(Root, BasisSDKConstants.AvatarDescription, description);
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

    /// <summary>
    /// Distinct model importers behind the avatar's skinned meshes, from the scan taken for this
    /// pass. Kept as paths so callers can hold onto one safely — an importer instance does not
    /// survive the reimport a fix triggers.
    /// </summary>
    private void CollectModelImporterPaths(HashSet<string> results)
    {
        results.Clear();
        if (Avatar == null) return;

        List<SkinnedMeshRenderer> skinnedMeshes = _scan.SkinnedMeshes;
        int count = skinnedMeshes.Count;
        for (int Index = 0; Index < count; Index++)
        {
            SkinnedMeshRenderer smr = skinnedMeshes[Index];
            if (smr == null || smr.sharedMesh == null) continue;

            string path = BasisValidationAssetCache.PathOf(smr.sharedMesh);
            if (string.IsNullOrEmpty(path)) continue;
            if (BasisValidationAssetCache.ImporterAt<ModelImporter>(path) == null) continue;
            results.Add(path);
        }
    }

    private void FixTryCreateHumanoidAvatarOnSourceModels()
    {
        if (Avatar == null) return;

        // Re-walked here rather than reusing the pass scan: this runs on a button click, which can
        // land long after the pass that put the button there.
        HashSet<string> paths = new HashSet<string>();
        SkinnedMeshRenderer[] smrs = Avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (SkinnedMeshRenderer smr in smrs)
        {
            if (smr == null || smr.sharedMesh == null) continue;
            string path = AssetDatabase.GetAssetPath(smr.sharedMesh);
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }

        foreach (string path in paths)
        {
            if (AssetImporter.GetAtPath(path) is not ModelImporter modelImporter) continue;

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

    private readonly List<string> _missingRequiredBones = new List<string>(RequiredHumanoidBones.Length);
    private readonly List<string> _missingRecommendedBones = new List<string>(RecommendedHumanoidBones.Length);

    private void ValidateHumanoidRig(BasisValidationBucket bucket)
    {
        UnityEngine.Avatar rig = Avatar.Animator.avatar;
        if (!rig.isValid || !rig.isHuman)
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.avatarValidator.rig.notHumanoid"), ValidationCategory.Configuration,
                FixTryCreateHumanoidAvatarOnSourceModels,
                BasisEditorLocalization.Get("sdk.avatarValidator.animator.noAvatar.fix"));
            return;
        }

        _missingRequiredBones.Clear();
        for (int Index = 0; Index < RequiredHumanoidBones.Length; Index++)
        {
            if (Avatar.Animator.GetBoneTransform(RequiredHumanoidBones[Index]) == null)
                _missingRequiredBones.Add(RequiredHumanoidBones[Index].ToString());
        }

        _missingRecommendedBones.Clear();
        for (int Index = 0; Index < RecommendedHumanoidBones.Length; Index++)
        {
            if (Avatar.Animator.GetBoneTransform(RecommendedHumanoidBones[Index]) == null)
                _missingRecommendedBones.Add(RecommendedHumanoidBones[Index].ToString());
        }

        if (_missingRequiredBones.Count == 0)
            bucket.Pass(BasisEditorLocalization.Get("sdk.avatarValidator.rig.bonesMapped"));
        else
            bucket.Error(
                BasisEditorLocalization.Get("sdk.avatarValidator.rig.missingBones", string.Join(", ", _missingRequiredBones)),
                ValidationCategory.Configuration);

        if (_missingRecommendedBones.Count > 0)
            bucket.Warn(
                BasisEditorLocalization.Get("sdk.avatarValidator.rig.missingOptionalBones", string.Join(", ", _missingRecommendedBones)),
                ValidationCategory.Configuration, null, "", Avatar.Animator);
    }

    private void ValidateTranslationDof(BasisValidationBucket bucket)
    {
        CollectModelImporterPaths(_seenModelPaths);
        if (_seenModelPaths.Count == 0) return;

        bool anyDisabled = false;
        foreach (string path in _seenModelPaths)
        {
            ModelImporter importer = BasisValidationAssetCache.ImporterAt<ModelImporter>(path);
            if (importer != null && !importer.humanDescription.hasTranslationDoF)
            {
                anyDisabled = true;
                break;
            }
        }

        if (!anyDisabled)
        {
            bucket.Warn(
                BasisEditorLocalization.Get("sdk.avatarValidator.translationDof.warning"),
                ValidationCategory.GameObject,
                FixTryCreateHumanoidAvatarOnSourceModels,
                BasisEditorLocalization.Get("sdk.avatarValidator.translationDof.fix"));
        }
        else
        {
            bucket.Pass(BasisEditorLocalization.Get("sdk.avatarValidator.translationDof.passed"));
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

    private void CheckTexture(Texture tex, BasisValidationBucket bucket)
    {
        TextureImporter texImporter = BasisValidationAssetCache.ImporterFor<TextureImporter>(tex);
        if (texImporter == null) return;

        // Fixes capture the path, never the importer: they run on a click, and the importer they
        // were made from is invalidated by the first reimport any of them triggers.
        string texturePath = texImporter.assetPath;
        string textureName = tex.name;

        if (texImporter.maxTextureSize > MaxTextureSizeWithoutMipMaps && !texImporter.mipmapEnabled)
        {
            bucket.Warn(
                BasisEditorLocalization.Get("sdk.avatarValidator.texture.noMipMaps", textureName),
                ValidationCategory.Performance,
                () => FixEnableMipMaps(texturePath),
                BasisEditorLocalization.Get("sdk.avatarValidator.texture.noMipMaps.fix", textureName));
        }
        if (texImporter.mipmapEnabled && !texImporter.streamingMipmaps)
        {
            bucket.Warn(
                BasisEditorLocalization.Get("sdk.avatarValidator.texture.noStreamingMips", textureName),
                ValidationCategory.Performance,
                () => FixEnableStreamingMipMaps(texturePath),
                BasisEditorLocalization.Get("sdk.avatarValidator.texture.noStreamingMips.fix", textureName));
        }
        if (texImporter.maxTextureSize > MaxTextureSizeBeforeWarning)
        {
            bucket.Warn(
                BasisEditorLocalization.Get("sdk.avatarValidator.texture.tooLarge", textureName, texImporter.maxTextureSize, MaxTextureSizeBeforeWarning),
                ValidationCategory.Performance,
                () => FixClampTextureSize(texturePath),
                BasisEditorLocalization.Get("sdk.avatarValidator.texture.tooLarge.fix", textureName, MaxTextureSizeBeforeWarning));
        }
        if (IsCrunchCompressed(tex, texImporter, out string crunchScope))
        {
            bucket.Warn(
                BasisEditorLocalization.Get("sdk.avatarValidator.texture.crunched", textureName, crunchScope),
                ValidationCategory.Performance,
                () => DisableCrunchCompression(BasisValidationAssetCache.ImporterAt<TextureImporter>(texturePath)),
                BasisEditorLocalization.Get("sdk.avatarValidator.texture.crunched.fix", textureName));
        }
    }

    private static void FixEnableMipMaps(string texturePath)
    {
        TextureImporter importer = BasisValidationAssetCache.ImporterAt<TextureImporter>(texturePath);
        if (importer == null) return;
        importer.mipmapEnabled = true;
        importer.streamingMipmaps = true;
        importer.SaveAndReimport();
    }

    private static void FixEnableStreamingMipMaps(string texturePath)
    {
        TextureImporter importer = BasisValidationAssetCache.ImporterAt<TextureImporter>(texturePath);
        if (importer == null) return;
        importer.streamingMipmaps = true;
        importer.SaveAndReimport();
    }

    private static void FixClampTextureSize(string texturePath)
    {
        TextureImporter importer = BasisValidationAssetCache.ImporterAt<TextureImporter>(texturePath);
        if (importer == null) return;
        importer.maxTextureSize = MaxTextureSizeBeforeWarning;
        importer.SaveAndReimport();
    }

    private static bool IsCrunchCompressed(Texture tex, TextureImporter texImporter, out string scope)
    {
        if (tex is Texture2D texture2D && IsCrunchedFormat(texture2D.format))
        {
            scope = texture2D.format.ToString();
            return true;
        }
        for (int Index = 0; Index < TextureImporterPlatforms.Length; Index++)
        {
            TextureImporterPlatformSettings platformSettings = texImporter.GetPlatformTextureSettings(TextureImporterPlatforms[Index]);
            if (platformSettings == null || !platformSettings.overridden) continue;
            if (!platformSettings.crunchedCompression && !IsCrunchedImporterFormat(platformSettings.format)) continue;
            if (!CanCrunchFormat(platformSettings.format)) continue;
            scope = TextureImporterPlatforms[Index];
            return true;
        }
        scope = null;
        return false;
    }

    private static bool IsCrunchedFormat(TextureFormat format)
    {
        switch (format)
        {
            case TextureFormat.DXT1Crunched:
            case TextureFormat.DXT5Crunched:
            case TextureFormat.ETC_RGB4Crunched:
            case TextureFormat.ETC2_RGBA8Crunched:
                return true;
            default:
                return false;
        }
    }

    private static bool IsCrunchedImporterFormat(TextureImporterFormat format)
    {
        switch (format)
        {
            case TextureImporterFormat.DXT1Crunched:
            case TextureImporterFormat.DXT5Crunched:
            case TextureImporterFormat.ETC_RGB4Crunched:
            case TextureImporterFormat.ETC2_RGBA8Crunched:
                return true;
            default:
                return false;
        }
    }

    private static bool CanCrunchFormat(TextureImporterFormat format)
    {
        switch (format)
        {
            case TextureImporterFormat.Automatic:
            case TextureImporterFormat.DXT1:
            case TextureImporterFormat.DXT5:
            case TextureImporterFormat.ETC_RGB4:
            case TextureImporterFormat.ETC2_RGBA8:
                return true;
            default:
                return IsCrunchedImporterFormat(format);
        }
    }

    private static void DisableCrunchCompression(TextureImporter texImporter)
    {
        if (texImporter == null) return;
        texImporter.crunchedCompression = false;
        for (int Index = 0; Index < TextureImporterPlatforms.Length; Index++)
        {
            TextureImporterPlatformSettings platformSettings = texImporter.GetPlatformTextureSettings(TextureImporterPlatforms[Index]);
            if (platformSettings == null || !platformSettings.overridden) continue;
            if (!platformSettings.crunchedCompression && !IsCrunchedImporterFormat(platformSettings.format)) continue;
            platformSettings.crunchedCompression = false;
            platformSettings.format = UncrunchedImporterFormat(platformSettings.format);
            texImporter.SetPlatformTextureSettings(platformSettings);
        }
        texImporter.SaveAndReimport();
    }

    private static TextureImporterFormat UncrunchedImporterFormat(TextureImporterFormat format)
    {
        switch (format)
        {
            case TextureImporterFormat.DXT1Crunched:
                return TextureImporterFormat.DXT1;
            case TextureImporterFormat.DXT5Crunched:
                return TextureImporterFormat.DXT5;
            case TextureImporterFormat.ETC_RGB4Crunched:
                return TextureImporterFormat.ETC_RGB4;
            case TextureImporterFormat.ETC2_RGBA8Crunched:
                return TextureImporterFormat.ETC2_RGBA8;
            default:
                return format;
        }
    }

    private void CheckMesh(SkinnedMeshRenderer skinnedMeshRenderer, BasisValidationBucket bucket)
    {
        if (skinnedMeshRenderer == null) return;
        if (skinnedMeshRenderer.sharedMesh == null)
        {
            bucket.Error(
                BasisEditorLocalization.Get("sdk.avatarValidator.mesh.noMesh", skinnedMeshRenderer.gameObject.name),
                ValidationCategory.GameObject);
            return;
        }

        Mesh mesh = skinnedMeshRenderer.sharedMesh;

        // mesh.triangles and mesh.vertices each marshal a full copy of the buffer out of the mesh
        // just to read a length. The counts are available without touching the data at all.
        if (CountTriangles(mesh) > MaxTrianglesBeforeWarning)
            bucket.Warn(
                BasisEditorLocalization.Get("sdk.avatarValidator.mesh.tooManyTriangles", skinnedMeshRenderer.gameObject.name, MaxTrianglesBeforeWarning),
                ValidationCategory.Performance);

        if (mesh.vertexCount > MeshVertices)
            bucket.Warn(
                BasisEditorLocalization.Get("sdk.avatarValidator.mesh.tooManyVertices", skinnedMeshRenderer.gameObject.name, MeshVertices),
                ValidationCategory.Performance);

        // One warning per source model, not per renderer that happens to point at it.
        if (mesh.blendShapeCount != 0 && _seenMeshes.Add(mesh.GetEntityId()))
        {
            string assetPath = BasisValidationAssetCache.PathOf(mesh);
            if (!string.IsNullOrEmpty(assetPath))
            {
                ModelImporter modelImporter = BasisValidationAssetCache.ImporterAt<ModelImporter>(assetPath);
                if (modelImporter != null && !ModelImporterExtensions.IsLegacyBlendShapeNormalsEnabled(modelImporter))
                    bucket.Warn(
                        BasisEditorLocalization.Get("sdk.avatarValidator.mesh.legacyBlendshapes", assetPath),
                        ValidationCategory.GameObject);
            }
        }

        if (skinnedMeshRenderer.allowOcclusionWhenDynamic == false)
            bucket.Error(
                BasisEditorLocalization.Get("sdk.avatarValidator.mesh.dynamicOcclusion", skinnedMeshRenderer.gameObject.name),
                ValidationCategory.GameObject, FixEnableDynamicOcclusionAllSMR,
                BasisEditorLocalization.Get("sdk.avatarValidator.mesh.dynamicOcclusion.fix"));
    }

    /// <summary>
    /// Triangle count without reading the index buffer. Only triangle submeshes are counted, which
    /// is what <c>Mesh.triangles</c> would have returned.
    /// </summary>
    private static uint CountTriangles(Mesh mesh)
    {
        uint indices = 0;
        int subMeshCount = mesh.subMeshCount;
        for (int Index = 0; Index < subMeshCount; Index++)
        {
            if (mesh.GetTopology(Index) == MeshTopology.Triangles)
            {
                indices += mesh.GetIndexCount(Index);
            }
        }
        return indices / 3;
    }

    public static bool ReportIfNoIll2CPP()
    {
        return BasisValidationAssetCache.Il2CppMissing;
    }

    private void ShowErrorPanel(List<BasisValidationIssue> errors)
    {
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
    }

    private VisualElement CreateCategoryPanel(ValidationCategory category)
    {
        VisualElement panel = new VisualElement();
        panel.style.backgroundColor = new StyleColor(BasisEditorUI.Light
            ? new Color(0.98f, 0.92f, 0.70f, 0.95f)
            : new Color(0.65098f, 0.63137f, 0.05098f, 0.5f));
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
        header.style.color = new StyleColor(BasisEditorUI.Light ? new Color(0.10f, 0.10f, 0.10f) : Color.white);
        panel.Add(header);
        panel.Add(label);

        return panel;
    }

    private readonly Dictionary<ValidationCategory, List<BasisValidationIssue>> _warningsByCategory =
        new Dictionary<ValidationCategory, List<BasisValidationIssue>>();
    private readonly List<string> _warningTextLines = new List<string>();

    private void ShowWarningPanel(VisualElement Root, List<BasisValidationIssue> warnings)
    {
        foreach (var panel in warningPanels.Values)
            panel.style.display = DisplayStyle.None;

        const int maxDisplayCount = 3;

        foreach (List<BasisValidationIssue> grouped in _warningsByCategory.Values)
            grouped.Clear();

        int warningCount = warnings.Count;
        for (int Index = 0; Index < warningCount; Index++)
        {
            BasisValidationIssue issue = warnings[Index];
            if (!_warningsByCategory.TryGetValue(issue.Category, out List<BasisValidationIssue> grouped))
            {
                grouped = new List<BasisValidationIssue>();
                _warningsByCategory.Add(issue.Category, grouped);
            }
            grouped.Add(issue);
        }

        foreach (KeyValuePair<ValidationCategory, List<BasisValidationIssue>> group in _warningsByCategory)
        {
            ValidationCategory category = group.Key;
            List<BasisValidationIssue> issues = group.Value;
            if (issues.Count == 0) continue;

            if (!warningPanels.ContainsKey(category))
            {
                VisualElement newPanel = CreateCategoryPanel(category);
                Root.Add(newPanel);
                warningPanels.Add(category, newPanel);
            }

            VisualElement currentPanel = warningPanels[category];
            currentPanel.style.display = DisplayStyle.Flex;

            Label messageLabel = currentPanel.Q<Label>("MessageLabel");

            _warningTextLines.Clear();
            for (int Index = 0; Index < issues.Count; Index++)
            {
                if (Index >= maxDisplayCount && issues.Count > maxDisplayCount)
                {
                    _warningTextLines.Add(BasisEditorLocalization.Get("sdk.validator.warnings.more", issues.Count - Index, category));
                    break;
                }
                _warningTextLines.Add($"- {issues[Index].Message}");
            }
            messageLabel.text = string.Join("\n", _warningTextLines);

            List<UnityEngine.Object> related = new List<UnityEngine.Object>();
            for (int Index = 0; Index < issues.Count; Index++)
            {
                if (issues[Index].RelatedObject != null) related.Add(issues[Index].RelatedObject);
            }
            _warningLabelObjects[messageLabel] = related.ToArray();

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

            for (int Index = 0; Index < issues.Count; Index++)
            {
                BasisValidationIssue issue = issues[Index];
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
