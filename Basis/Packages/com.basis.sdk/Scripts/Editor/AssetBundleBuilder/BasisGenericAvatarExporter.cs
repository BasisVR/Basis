using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk;
using GLTFast;
using GLTFast.Export;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Builds the platform-agnostic "Generic" bee section for avatars: the avatar exported as a
/// glTF 2.0 binary (.glb, an open standard glTFast can import at runtime on any platform),
/// encrypted with the same bundle password as the AssetBundle sections. Clients that have no
/// AssetBundle section for their platform fall back to this section instead of failing the
/// load. The humanoid rig and BasisAvatar wiring that glTF cannot express travel as
/// <see cref="BasisGenericAvatarData"/> JSON on the generated section entry.
/// </summary>
public static class BasisGenericAvatarExporter
{
    /// <summary>
    /// Returns the section entry plus the encrypted payload path (staged next to the
    /// per-platform bundles, consumed by the bee combine step). Returns (null, null) when the
    /// avatar cannot be represented (no humanoid animator) — the caller builds without the
    /// generic section, which is exactly the pre-feature behavior.
    /// </summary>
    public static async Task<(BasisBundleGenerated Generated, string EncryptedPath)> ExportEncryptedGlb(BasisAvatar sourceAvatar, BasisAssetBundleObject settings, string password, string stagingDirectory)
    {
        GameObject clone = Object.Instantiate(sourceAvatar.gameObject);
        List<Mesh> duplicatedMeshes = new List<Mesh>();
        List<Material> temporaryMaterials = new List<Material>();
        List<Texture2D> temporaryTextures = new List<Texture2D>();
        try
        {
            clone.name = sourceAvatar.gameObject.name;
            BasisAssetBundlePipeline.DestroyEditorOnlyInAvatar(clone);
            BasisAssetBundlePipeline.OnBeforeBuildPrefab?.Invoke(clone, settings);
            BasisAssetBundlePipeline.PostProcessAvatar(clone);
            // Skeleton rebuild and mesh lookup on the importing client are name-based, and
            // glTF nodes have no other stable identity — names must be unique before capture.
            EnsureUniqueTransformNames(clone.transform);
            clone.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            if (!clone.TryGetComponent(out BasisAvatar cloneAvatar))
            {
                BasisDebug.LogError("Generic avatar export skipped: clone lost its BasisAvatar component.");
                return (null, null);
            }

            // glTFast writes no morph targets, so blendshapes need two treatments: authored
            // nonzero weights get baked into the base vertices (the exported geometry then
            // LOOKS like the authored avatar), and driver-actuated shapes (visemes, blink,
            // laughter) ride a sparse sidecar appended after the GLB so the importing client
            // can rebuild them.
            BasisGenericBlendshapeSidecar sidecar = BakeAndCollectBlendshapes(cloneAvatar, duplicatedMeshes);

            // glTFast reads a material's alpha mode from its RenderType tag, which custom
            // avatar shaders rarely set — cutout fur/hair would export as glTF OPAQUE.
            // Detected modes are stamped onto temporary material copies; user assets are
            // never touched.
            NormalizeMaterialsForGltf(clone, temporaryMaterials, temporaryTextures);

            BasisGenericAvatarData avatarData = BasisGenericAvatarData.Capture(cloneAvatar);
            if (avatarData == null)
            {
                return (null, null);
            }
            try
            {
                avatarData.ComponentsJson = BasisGenericComponentReplicator.Capture(clone.transform);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Debug.LogWarning("Component replication capture failed — building the generic section without replicated components.");
            }

            byte[] glbBytes = await ExportGlb(clone);
            if (glbBytes == null || glbBytes.Length == 0)
            {
                BasisDebug.LogError("Generic avatar export produced no glb data.");
                return (null, null);
            }

            byte[] sectionPayload = glbBytes;
            if (sidecar != null && sidecar.HasContent)
            {
                byte[] sidecarBytes = sidecar.Serialize();
                sectionPayload = new byte[glbBytes.Length + sidecarBytes.Length];
                Buffer.BlockCopy(glbBytes, 0, sectionPayload, 0, glbBytes.Length);
                Buffer.BlockCopy(sidecarBytes, 0, sectionPayload, glbBytes.Length, sidecarBytes.Length);
                BasisDebug.Log($"Generic avatar blendshape sidecar: {sidecarBytes.LongLength} bytes.");
            }

            string uniqueId = BasisGenerateUniqueID.GenerateUniqueID();
            var basisPassword = new BasisEncryptionWrapper.BasisPassword { VP = password };
            BasisProgressReport report = new BasisProgressReport();
            byte[] encrypted = await BasisEncryptionWrapper.EncryptToBytesAsync(uniqueId, basisPassword, sectionPayload, report);
            if (encrypted == null || encrypted.Length == 0)
            {
                BasisDebug.LogError("Generic avatar export failed to encrypt the glb payload.");
                return (null, null);
            }

            Directory.CreateDirectory(stagingDirectory);
            string encryptedPath = Path.Combine(stagingDirectory, $"{uniqueId}{settings.BasisBundleEncryptedExtension}");
            await File.WriteAllBytesAsync(encryptedPath, encrypted);

            BasisBundleGenerated generated = new BasisBundleGenerated(
                Hash128.Compute(sectionPayload).ToString(),
                BasisBundleConnector.GltfAssetMode,
                clone.name + ".glb",
                0,
                true,
                password,
                BasisBundleConnector.GenericPlatform,
                encrypted.LongLength)
            {
                GenericAvatarDataJson = avatarData.ToJson(),
            };

            BasisDebug.Log($"Generic (glTF) avatar section: glb {glbBytes.LongLength} bytes, section {sectionPayload.LongLength} bytes, encrypted {encrypted.LongLength} bytes.");
            return (generated, encryptedPath);
        }
        finally
        {
            if (clone != null)
            {
                Object.DestroyImmediate(clone);
            }
            for (int Index = 0; Index < duplicatedMeshes.Count; Index++)
            {
                if (duplicatedMeshes[Index] != null)
                {
                    Object.DestroyImmediate(duplicatedMeshes[Index]);
                }
            }
            for (int Index = 0; Index < temporaryMaterials.Count; Index++)
            {
                if (temporaryMaterials[Index] != null)
                {
                    Object.DestroyImmediate(temporaryMaterials[Index]);
                }
            }
            for (int Index = 0; Index < temporaryTextures.Count; Index++)
            {
                if (temporaryTextures[Index] != null)
                {
                    Object.DestroyImmediate(temporaryTextures[Index]);
                }
            }
        }
    }

    /// <summary>
    /// Ensures every renderer material carries the RenderType override tag matching its real
    /// alpha behavior, so glTFast exports MASK/BLEND instead of OPAQUE. Materials whose shader
    /// already declares the tag (Unity's own shaders, cutout/transparent toon shader variants)
    /// are left untouched; the rest are detected from render queue, common keywords and URP
    /// surface properties, and swapped for tagged temporary copies.
    /// </summary>
    public static void NormalizeMaterialsForGltf(GameObject clone, List<Material> temporaryMaterials, List<Texture2D> temporaryTextures = null)
    {
        // Shared between materials: convert each distinct source texture once.
        Dictionary<Texture, Texture2D> pngReadyTextures = temporaryTextures != null ? new Dictionary<Texture, Texture2D>() : null;
        Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(true);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Material[] shared = renderers[rendererIndex].sharedMaterials;
            bool changed = false;
            for (int materialIndex = 0; materialIndex < shared.Length; materialIndex++)
            {
                Material source = shared[materialIndex];
                if (source == null || source.shader == null)
                {
                    continue;
                }
                string existingTag = source.GetTag("RenderType", false, string.Empty);
                if (existingTag == "TransparentCutout" || existingTag == "Transparent" || existingTag == "Fade")
                {
                    continue;
                }
                bool cutout = IsEffectivelyCutout(source);
                bool blend = !cutout && IsEffectivelyTransparent(source);
                if (!cutout && !blend)
                {
                    continue;
                }
                Material copy = new Material(source) { name = source.name };
                copy.SetOverrideTag("RenderType", cutout ? "TransparentCutout" : "Transparent");
                temporaryMaterials.Add(copy);
                shared[materialIndex] = copy;
                changed = true;
            }
            if (changed)
            {
                renderers[rendererIndex].sharedMaterials = shared;
            }

            // Second pass: surface emission through the standard convention glTFast reads
            // (_EMISSION keyword + _EmissionColor/_EmissionMap). Toon shaders drive emission
            // through their own layer properties, so emissive eyes/markings otherwise export
            // black — the visible base texture is often just a dark canvas under the glow.
            shared = renderers[rendererIndex].sharedMaterials;
            changed = false;
            for (int materialIndex = 0; materialIndex < shared.Length; materialIndex++)
            {
                Material material = shared[materialIndex];
                if (material == null || material.shader == null || material.IsKeywordEnabled("_EMISSION"))
                {
                    continue;
                }
                if (!TryDetectEmission(material, out Color emissionColor, out Texture emissionMap))
                {
                    continue;
                }
                Material copy;
                if (temporaryMaterials.Contains(material))
                {
                    copy = material;
                }
                else
                {
                    copy = new Material(material) { name = material.name };
                    temporaryMaterials.Add(copy);
                    shared[materialIndex] = copy;
                    changed = true;
                }
                copy.EnableKeyword("_EMISSION");
                if (copy.HasProperty("_EmissionColor"))
                {
                    copy.SetColor("_EmissionColor", emissionColor);
                }
                if (emissionMap != null && copy.HasProperty("_EmissionMap"))
                {
                    copy.SetTexture("_EmissionMap", emissionMap);
                }
            }
            if (changed)
            {
                renderers[rendererIndex].sharedMaterials = shared;
            }

            // Third pass: swap color textures for uncompressed RGBA32 copies. glTFast picks
            // JPEG for any texture whose format lacks an alpha channel (the typical BC1/BC7
            // avatar texture), and lossy JPEG shows as blocking on fur/flat-color regions.
            // A format WITH alpha makes it emit lossless PNG instead.
            if (pngReadyTextures == null)
            {
                continue;
            }
            shared = renderers[rendererIndex].sharedMaterials;
            changed = false;
            for (int materialIndex = 0; materialIndex < shared.Length; materialIndex++)
            {
                Material material = shared[materialIndex];
                if (material == null || material.shader == null)
                {
                    continue;
                }
                Texture mainTexture = material.mainTexture;
                Texture emissionTexture = material.HasProperty("_EmissionMap") ? material.GetTexture("_EmissionMap") : null;
                bool wantsMain = mainTexture is Texture2D && !TextureReportsAlpha((Texture2D)mainTexture);
                bool wantsEmission = emissionTexture is Texture2D && !TextureReportsAlpha((Texture2D)emissionTexture);
                if (!wantsMain && !wantsEmission)
                {
                    continue;
                }
                Material copy = material;
                if (!temporaryMaterials.Contains(material))
                {
                    copy = new Material(material) { name = material.name };
                    temporaryMaterials.Add(copy);
                    shared[materialIndex] = copy;
                    changed = true;
                }
                if (wantsMain)
                {
                    copy.mainTexture = GetPngReadyCopy((Texture2D)mainTexture, pngReadyTextures, temporaryTextures);
                }
                if (wantsEmission)
                {
                    copy.SetTexture("_EmissionMap", GetPngReadyCopy((Texture2D)emissionTexture, pngReadyTextures, temporaryTextures));
                }
            }
            if (changed)
            {
                renderers[rendererIndex].sharedMaterials = shared;
            }
        }
    }

    private static bool TextureReportsAlpha(Texture2D texture)
    {
        return UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(
            UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(texture.format, false));
    }

    /// <summary>
    /// Readable RGBA32 copy of a color texture, cached per source. Blitting through an sRGB
    /// render target keeps the stored color bytes identical to the source sample.
    /// </summary>
    private static Texture2D GetPngReadyCopy(Texture2D source, Dictionary<Texture, Texture2D> cache, List<Texture2D> temporaryTextures)
    {
        if (cache.TryGetValue(source, out Texture2D existing) && existing != null)
        {
            return existing;
        }
        RenderTexture target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, target);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;
        Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false) { name = source.name };
        copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);
        copy.Apply(false, false);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(target);
        cache[source] = copy;
        temporaryTextures.Add(copy);
        return copy;
    }

    /// <summary>
    /// Finds the strongest active emission layer on a material, checking the standard
    /// properties first and then Poiyomi-style numbered layers (gated on their per-layer
    /// strength so ubiquitous default-white colors don't light every material up).
    /// </summary>
    private static bool TryDetectEmission(Material material, out Color emissionColor, out Texture emissionMap)
    {
        emissionColor = Color.black;
        emissionMap = null;
        string[] suffixes = { "", "1", "2", "3" };
        bool found = false;
        float bestStrength = 0f;
        foreach (string suffix in suffixes)
        {
            string strengthProperty = "_EmissionStrength" + suffix;
            if (!material.HasProperty(strengthProperty))
            {
                continue;
            }
            float strength = material.GetFloat(strengthProperty);
            if (strength <= 0.001f || strength <= bestStrength)
            {
                continue;
            }
            string colorProperty = "_EmissionColor" + suffix;
            string mapProperty = "_EmissionMap" + suffix;
            Color color = material.HasProperty(colorProperty) ? material.GetColor(colorProperty) : Color.white;
            Texture map = material.HasProperty(mapProperty) ? material.GetTexture(mapProperty) : null;
            if (map == null && color.maxColorComponent <= 0.001f)
            {
                continue;
            }
            bestStrength = strength;
            emissionColor = color * strength;
            emissionMap = map;
            found = true;
        }
        return found;
    }

    private static bool IsEffectivelyCutout(Material material)
    {
        if (material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("ALPHATEST_ON"))
        {
            return true;
        }
        if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f)
        {
            return true;
        }
        // Standard shader convention: _Mode 1 = Cutout.
        if (material.HasProperty("_Mode") && Mathf.RoundToInt(material.GetFloat("_Mode")) == 1)
        {
            return true;
        }
        int queue = material.renderQueue;
        return queue >= (int)UnityEngine.Rendering.RenderQueue.AlphaTest && queue < (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    private static bool IsEffectivelyTransparent(Material material)
    {
        if (material.IsKeywordEnabled("_ALPHABLEND_ON") || material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") || material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"))
        {
            return true;
        }
        // URP convention: _Surface 1 = Transparent.
        if (material.HasProperty("_Surface") && Mathf.RoundToInt(material.GetFloat("_Surface")) == 1)
        {
            return true;
        }
        return material.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    /// <summary>
    /// For every skinned mesh on the clone: duplicates the mesh (the user's asset is never
    /// touched), bakes currently nonzero, non-driver blendshape weights into the base
    /// vertices/normals, and collects the driver shapes (visemes, blink, laughter) as sparse
    /// deltas pre-scaled to full application at weight 100. Shapes that are neither weighted
    /// nor driver-referenced are dropped — the fallback has nothing that would actuate them.
    /// </summary>
    private static BasisGenericBlendshapeSidecar BakeAndCollectBlendshapes(BasisAvatar cloneAvatar, List<Mesh> duplicatedMeshes)
    {
        Transform root = cloneAvatar.transform;
        BasisGenericBlendshapeSidecar sidecar = new BasisGenericBlendshapeSidecar();
        SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            SkinnedMeshRenderer renderer = renderers[rendererIndex];
            Mesh source = renderer.sharedMesh;
            if (source == null || source.blendShapeCount == 0)
            {
                continue;
            }

            HashSet<int> driverShapes = CollectDriverShapeIndices(cloneAvatar, renderer, source.blendShapeCount);

            Mesh working;
            try
            {
                working = Object.Instantiate(source);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Generic avatar export: could not duplicate mesh '{source.name}' for blendshape processing: {ex.Message}");
                continue;
            }
            working.name = source.name;
            duplicatedMeshes.Add(working);
            renderer.sharedMesh = working;

            try
            {
                BakeNonDriverWeights(renderer, working, driverShapes);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Generic avatar export: baking blendshape weights on '{working.name}' failed: {ex.Message}");
            }

            if (driverShapes.Count == 0)
            {
                continue;
            }

            BasisGenericBlendshapeSidecar.SidecarMesh entry = new BasisGenericBlendshapeSidecar.SidecarMesh
            {
                Path = BasisGenericAvatarData.GetPathRelativeTo(root, renderer.transform),
                VertexCount = working.vertexCount,
            };
            List<int> orderedShapes = new List<int>(driverShapes);
            orderedShapes.Sort();
            int vertexCount = working.vertexCount;
            Vector3[] deltaPositions = new Vector3[vertexCount];
            Vector3[] deltaNormals = new Vector3[vertexCount];
            Vector3[] deltaTangents = new Vector3[vertexCount];
            for (int orderIndex = 0; orderIndex < orderedShapes.Count; orderIndex++)
            {
                int shapeIndex = orderedShapes[orderIndex];
                int lastFrame = working.GetBlendShapeFrameCount(shapeIndex) - 1;
                float frameWeight = working.GetBlendShapeFrameWeight(shapeIndex, lastFrame);
                if (frameWeight <= 0f)
                {
                    continue;
                }
                working.GetBlendShapeFrameVertices(shapeIndex, lastFrame, deltaPositions, deltaNormals, deltaTangents);
                float scale = 100f / frameWeight;

                const float positionEpsilonSq = 1e-12f;
                const float normalEpsilonSq = 1e-10f;
                List<int> sparse = new List<int>(256);
                for (int i = 0; i < vertexCount; i++)
                {
                    if (deltaPositions[i].sqrMagnitude > positionEpsilonSq || deltaNormals[i].sqrMagnitude > normalEpsilonSq)
                    {
                        sparse.Add(i);
                    }
                }

                BasisGenericBlendshapeSidecar.SidecarShape shape = new BasisGenericBlendshapeSidecar.SidecarShape
                {
                    Name = working.GetBlendShapeName(shapeIndex),
                    SparseIndices = sparse.ToArray(),
                    DeltaPositions = new Vector3[sparse.Count],
                    DeltaNormals = new Vector3[sparse.Count],
                };
                for (int i = 0; i < sparse.Count; i++)
                {
                    shape.DeltaPositions[i] = deltaPositions[sparse[i]] * scale;
                    shape.DeltaNormals[i] = deltaNormals[sparse[i]] * scale;
                }
                entry.Shapes.Add(shape);
            }
            if (entry.Shapes.Count > 0)
            {
                sidecar.Meshes.Add(entry);
            }
        }
        return sidecar;
    }

    private static HashSet<int> CollectDriverShapeIndices(BasisAvatar avatar, SkinnedMeshRenderer renderer, int blendShapeCount)
    {
        HashSet<int> driverShapes = new HashSet<int>();
        void Add(int shapeIndex)
        {
            if (shapeIndex >= 0 && shapeIndex < blendShapeCount)
            {
                driverShapes.Add(shapeIndex);
            }
        }
        if (renderer == avatar.FaceVisemeMesh)
        {
            if (avatar.FaceVisemeMovement != null)
            {
                for (int i = 0; i < avatar.FaceVisemeMovement.Length; i++)
                {
                    Add(avatar.FaceVisemeMovement[i]);
                }
            }
            Add(avatar.laughterBlendTarget);
        }
        if (renderer == avatar.FaceBlinkMesh && avatar.BlinkViseme != null)
        {
            for (int i = 0; i < avatar.BlinkViseme.Length; i++)
            {
                Add(avatar.BlinkViseme[i]);
            }
        }
        return driverShapes;
    }

    /// <summary>
    /// Applies every nonzero, non-driver blendshape weight to the mesh's base vertices and
    /// normals (final frame, scaled by weight/frameWeight) and zeroes the weight — the shape
    /// state a creator authored becomes the exported geometry itself.
    /// </summary>
    private static void BakeNonDriverWeights(SkinnedMeshRenderer renderer, Mesh working, HashSet<int> driverShapes)
    {
        int shapeCount = working.blendShapeCount;
        int vertexCount = working.vertexCount;
        Vector3[] baseVertices = null;
        Vector3[] baseNormals = null;
        Vector4[] baseTangents = null;
        Vector3[] deltaPositions = null;
        Vector3[] deltaNormals = null;
        Vector3[] deltaTangents = null;
        bool anyBaked = false;

        for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
        {
            float weight = renderer.GetBlendShapeWeight(shapeIndex);
            if (weight <= 0f || driverShapes.Contains(shapeIndex))
            {
                continue;
            }
            int lastFrame = working.GetBlendShapeFrameCount(shapeIndex) - 1;
            float frameWeight = working.GetBlendShapeFrameWeight(shapeIndex, lastFrame);
            if (frameWeight <= 0f)
            {
                continue;
            }

            if (baseVertices == null)
            {
                baseVertices = working.vertices;
                baseNormals = working.normals;
                baseTangents = working.tangents;
                deltaPositions = new Vector3[vertexCount];
                deltaNormals = new Vector3[vertexCount];
                deltaTangents = new Vector3[vertexCount];
            }

            working.GetBlendShapeFrameVertices(shapeIndex, lastFrame, deltaPositions, deltaNormals, deltaTangents);
            float scale = weight / frameWeight;
            for (int i = 0; i < vertexCount; i++)
            {
                baseVertices[i] += deltaPositions[i] * scale;
            }
            if (baseNormals != null && baseNormals.Length == vertexCount)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    baseNormals[i] += deltaNormals[i] * scale;
                }
            }
            // Tangents must move with the surface or normal mapping shades the baked regions
            // wrong (w = handedness stays untouched).
            if (baseTangents != null && baseTangents.Length == vertexCount)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3 tangentDelta = deltaTangents[i] * scale;
                    baseTangents[i].x += tangentDelta.x;
                    baseTangents[i].y += tangentDelta.y;
                    baseTangents[i].z += tangentDelta.z;
                }
            }
            renderer.SetBlendShapeWeight(shapeIndex, 0f);
            anyBaked = true;
        }

        if (!anyBaked)
        {
            return;
        }
        working.vertices = baseVertices;
        // Deliberately NOT renormalized: Unity's GPU blendshape path feeds the raw
        // base+delta sums to the shader and normalizes per-PIXEL after interpolation.
        // Normalizing per-vertex here changes the interpolation weighting and shows up
        // as faceted shading wherever large deltas were baked (chest/face body shapes).
        if (baseNormals != null && baseNormals.Length == vertexCount)
        {
            working.normals = baseNormals;
        }
        if (baseTangents != null && baseTangents.Length == vertexCount)
        {
            working.tangents = baseTangents;
        }
        working.RecalculateBounds();
    }

    private static async Task<byte[]> ExportGlb(GameObject clone)
    {
        var exportSettings = new ExportSettings
        {
            Format = GltfFormat.Binary,
            ImageDestination = ImageDestination.MainBuffer,
            // Meshes/materials only — cameras, lights and animation have no place in an
            // avatar fallback and would only widen the payload.
            ComponentMask = ComponentType.Mesh,
        };
        // Inactive nodes and disabled renderers must still be exported: they carry bones and
        // hidden outfit meshes. Their authored off-state is restored after import from
        // BasisGenericAvatarData, since glTF has no active/enabled concept.
        var gameObjectSettings = new GameObjectExportSettings
        {
            OnlyActiveInHierarchy = false,
            DisabledComponents = true,
        };
        GameObjectExport export = new GameObjectExport(exportSettings, gameObjectSettings);
        // The scene name becomes a wrapper GameObject on import; it must not collide with the
        // avatar root node's name, which the loader locates by name.
        if (!export.AddScene(new[] { clone }, "BasisGenericScene"))
        {
            BasisDebug.LogError("Generic avatar export: AddScene failed.");
            return null;
        }
        using MemoryStream stream = new MemoryStream();
        bool success = await export.SaveToStreamAndDispose(stream, forceSync: true);
        if (!success)
        {
            BasisDebug.LogError("Generic avatar export: glb serialization failed.");
            return null;
        }
        return stream.ToArray();
    }

    public static void EnsureUniqueTransformNames(Transform root)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        Stack<Transform> pending = new Stack<Transform>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Transform current = pending.Pop();
            // Slashes would corrupt the Transform.Find paths recorded for viseme meshes and
            // node state.
            if (current.name.IndexOf('/') >= 0)
            {
                current.name = current.name.Replace('/', '_');
            }
            if (!seen.Add(current.name))
            {
                int suffix = 2;
                string candidate;
                do
                {
                    candidate = current.name + "_" + suffix;
                    suffix++;
                } while (!seen.Add(candidate));
                current.name = candidate;
            }
            int childCount = current.childCount;
            for (int Index = 0; Index < childCount; Index++)
            {
                pending.Push(current.GetChild(Index));
            }
        }
    }
}
