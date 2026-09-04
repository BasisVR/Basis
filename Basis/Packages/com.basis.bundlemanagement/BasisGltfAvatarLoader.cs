using System;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using GLTFast;
using UnityEngine;

/// <summary>
/// Runtime loader for the Generic (glTF) avatar section. Decrypts the section bytes, imports
/// the GLB with glTFast, rebuilds the humanoid rig from the connector's
/// <see cref="BasisGenericAvatarData"/>, wires a fresh <see cref="BasisAvatar"/>, and parks the
/// result as an inactive template on the wrapper — the glTF equivalent of a loaded AssetBundle,
/// which the normal instantiate/ContentPolice path then clones per player.
/// </summary>
public static class BasisGltfAvatarLoader
{
    public static async Task<bool> LoadTemplate(BasisTrackedBundleWrapper wrapper, BasisBundleGenerated generated, BasisBundleSection encryptedSection, BasisProgressReport report)
    {
        if (wrapper?.LoadableBundle == null || generated == null || !encryptedSection.HasPayload)
        {
            BasisDebug.LogError("Generic (glTF) load: missing wrapper, section entry, or section bytes.");
            return false;
        }

        BasisGenericAvatarData avatarData = await Task.Run(() => BasisGenericAvatarData.FromJson(generated.GenericAvatarDataJson));
        if (avatarData == null)
        {
            BasisDebug.LogError("Generic (glTF) load: section carries no BasisGenericAvatarData; cannot rebuild a humanoid avatar.");
            return false;
        }

        var basisPassword = new BasisEncryptionWrapper.BasisPassword { VP = wrapper.LoadableBundle.UnlockPassword };
        string uniqueID = BasisGenerateUniqueID.GenerateUniqueID();
        var decrypted = await BasisEncryptionToData.DecryptSection(uniqueID, basisPassword, encryptedSection, report);
        if (!decrypted.Success || decrypted.Data == null || decrypted.Data.Length == 0)
        {
            BasisDebug.LogError($"Generic (glTF) load: decrypt failed. {decrypted.Error} | {decrypted.Message}");
            return false;
        }

        // The section is the GLB followed by an optional Basis blendshape sidecar (glTFast
        // exports no morph targets, so driver shapes ride separately and are rebuilt below).
        byte[] sectionBytes = decrypted.Data;
        byte[] glbBytes = sectionBytes;
        BasisGenericBlendshapeSidecar blendshapeSidecar = null;
        int glbLength = BasisGenericBlendshapeSidecar.GetGlbLength(sectionBytes);
        if (glbLength > 0 && glbLength < sectionBytes.Length)
        {
            (blendshapeSidecar, glbBytes) = await Task.Run(() =>
            {
                BasisGenericBlendshapeSidecar sidecar = BasisGenericBlendshapeSidecar.TryParse(sectionBytes, glbLength);
                byte[] glb = new byte[glbLength];
                Buffer.BlockCopy(sectionBytes, 0, glb, 0, glbLength);
                return (sidecar, glb);
            });
        }

        GltfImport gltf = new GltfImport();
        GameObject holder = null;
        Avatar humanoidAvatar = null;
        try
        {
            ImportSettings importSettings = new ImportSettings
            {
                NodeNameMethod = NameImportMethod.Original,
                AnimationMethod = AnimationMethod.None,
                GenerateMipMaps = true,
            };
            bool loaded = await gltf.Load(glbBytes, null, importSettings);
            if (!loaded)
            {
                BasisDebug.LogError("Generic (glTF) load: glb parse/load failed for " + generated.AssetToLoadName);
                return false;
            }

            // The template lives outside every scene and never renders; clones come from
            // Instantiate exactly like prefab loads from an AssetBundle.
            holder = new GameObject("GenericAvatarTemplate " + (wrapper.LoadableBundle.BasisBundleConnector?.UniqueVersion ?? generated.AssetToLoadName));
            UnityEngine.Object.DontDestroyOnLoad(holder);
            holder.SetActive(false);

            bool instantiated = await gltf.InstantiateMainSceneAsync(holder.transform);
            if (!instantiated)
            {
                BasisDebug.LogError("Generic (glTF) load: scene instantiation failed for " + generated.AssetToLoadName);
                return false;
            }

            GameObject avatarRoot = LocateAvatarRoot(holder.transform, avatarData.RootNodeName);
            if (avatarRoot == null)
            {
                BasisDebug.LogError($"Generic (glTF) load: could not locate avatar root node '{avatarData.RootNodeName}'.");
                return false;
            }
            avatarRoot.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

            if (!avatarRoot.TryGetComponent(out Animator animator))
            {
                animator = avatarRoot.AddComponent<Animator>();
            }

            humanoidAvatar = BuildHumanoidAvatar(holder, avatarRoot, avatarData);
            if (humanoidAvatar == null)
            {
                return false;
            }
            animator.avatar = humanoidAvatar;

            // glTF imports everything active/enabled; restore the authored off-state after
            // the humanoid build so the build saw the full exported hierarchy.
            avatarData.ApplyNodeState(avatarRoot.transform);

            // Rebuild driver blendshapes on the imported meshes before wiring so the viseme
            // and blink indices can be re-resolved against what actually exists now.
            blendshapeSidecar?.ApplyTo(avatarRoot.transform);

            BasisAvatar basisAvatar = avatarRoot.AddComponent<BasisAvatar>();
            basisAvatar.Animator = animator;
            avatarData.ApplyWiring(basisAvatar, avatarRoot.transform);
            avatarData.RemapShapeIndicesByName(basisAvatar);

            // Rebuild replicated components (jiggle, face tracking, colliders, ...) on the
            // still-inactive template. The avatar ContentPolice selector gates every type
            // BEFORE construction (fail closed when unavailable), and clones additionally
            // pass the full ContentPolice walk — the client's removal safeties stay in force.
            ContentPoliceSelector avatarSelector = null;
            if (BundledContentHolder.Instance != null)
            {
                BundledContentHolder.Instance.GetSelector(BundledContentHolder.Selector.Avatar, out avatarSelector);
            }
            BasisGenericComponentReplicator.Apply(avatarData.ComponentsJson, avatarRoot.transform, avatarSelector);
            basisAvatar.Renders = avatarRoot.GetComponentsInChildren<Renderer>(true);
            basisAvatar.BasisBundleDescription = wrapper.LoadableBundle.BasisBundleConnector?.BasisBundleDescription;
            basisAvatar.TransformStorage = BasisAvatarTransformStorage.CaptureFrom(animator);
            basisAvatar.HumanScale = animator.humanScale;

            wrapper.GltfImport = gltf;
            wrapper.GltfTemplateHolder = holder;
            wrapper.GltfBuiltAvatar = humanoidAvatar;
            wrapper.GltfTemplateAvatarRoot = avatarRoot;
            BasisDebug.Log($"Generic (glTF) avatar template ready: {generated.AssetToLoadName}", BasisDebug.LogTag.Event);
            return true;
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Generic (glTF) load threw: {ex}");
            return false;
        }
        finally
        {
            // Anything short of full success tears the partial state down; on success the
            // wrapper owns all of it.
            if (wrapper.GltfTemplateAvatarRoot == null)
            {
                if (holder != null)
                {
                    UnityEngine.Object.Destroy(holder);
                }
                if (humanoidAvatar != null)
                {
                    UnityEngine.Object.Destroy(humanoidAvatar);
                }
                gltf.Dispose();
            }
        }
    }

    /// <summary>
    /// glTFast wraps exported root nodes in a scene GameObject, so the avatar root is found by
    /// the node name the exporter recorded, falling back to the first child chain for payloads
    /// built before the name was stored.
    /// </summary>
    private static GameObject LocateAvatarRoot(Transform holder, string rootNodeName)
    {
        if (!string.IsNullOrEmpty(rootNodeName))
        {
            Transform named = FindChildByNameRecursive(holder, rootNodeName);
            if (named != null)
            {
                return named.gameObject;
            }
        }
        Transform cursor = holder;
        while (cursor.childCount == 1)
        {
            cursor = cursor.GetChild(0);
            // A node with renderers or more than one child is the actual content root.
            if (cursor.childCount != 1 || cursor.GetComponent<Renderer>() != null)
            {
                break;
            }
        }
        return cursor != holder ? cursor.gameObject : (holder.childCount > 0 ? holder.GetChild(0).gameObject : null);
    }

    private static Transform FindChildByNameRecursive(Transform parent, string name)
    {
        int childCount = parent.childCount;
        for (int Index = 0; Index < childCount; Index++)
        {
            Transform child = parent.GetChild(Index);
            if (child.name == name)
            {
                return child;
            }
            Transform nested = FindChildByNameRecursive(child, name);
            if (nested != null)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>
    /// AvatarBuilder.BuildHumanAvatar requires an active hierarchy, so the (inactive, hidden)
    /// holder is activated far below the world for the duration of the synchronous build —
    /// nothing renders because the frame ends with the holder inactive again.
    /// </summary>
    private static Avatar BuildHumanoidAvatar(GameObject holder, GameObject avatarRoot, BasisGenericAvatarData avatarData)
    {
        HumanDescription description = avatarData.ToHumanDescription();
        Vector3 originalPosition = holder.transform.position;
        holder.transform.position = new Vector3(0f, -4096f, 0f);
        holder.SetActive(true);
        try
        {
            Avatar built = AvatarBuilder.BuildHumanAvatar(avatarRoot, description);
            if (built == null || !built.isValid || !built.isHuman)
            {
                BasisDebug.LogError("Generic (glTF) load: humanoid Avatar rebuild produced an invalid rig.");
                if (built != null)
                {
                    UnityEngine.Object.Destroy(built);
                }
                return null;
            }
            built.name = avatarRoot.name;
            return built;
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Generic (glTF) load: AvatarBuilder threw: {ex.Message}");
            return null;
        }
        finally
        {
            holder.SetActive(false);
            holder.transform.position = originalPosition;
        }
    }
}
