using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisTemporaryStorageMaterialTests
    {
        [Test]
        public void SavePrefabToTemporaryStorage_PreservesTransientRendererMaterial()
        {
            string folderName = $"BasisTempMaterialTest_{Guid.NewGuid():N}";
            string temporaryStorage = $"Assets/{folderName}";
            string sourceMaterialPath = $"{temporaryStorage}/Source.mat";
            BasisAssetBundleObject settings = null;
            GameObject root = null;
            Material transientMaterial = null;

            try
            {
                AssetDatabase.CreateFolder("Assets", folderName);

                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                Assert.IsNotNull(shader, "URP Lit is required to construct the material test fixture.");

                Texture2D authoringTexture = new Texture2D(1, 1) { name = "AuthoringReference" };
                string authoringTexturePath = $"{temporaryStorage}/AuthoringReference.asset";
                AssetDatabase.CreateAsset(authoringTexture, authoringTexturePath);
                AssetDatabase.ImportAsset(authoringTexturePath, ImportAssetOptions.ForceSynchronousImport);
                authoringTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(authoringTexturePath);

                Material sourceMaterial = new Material(shader) { name = "Source" };
                sourceMaterial.SetTexture("_BaseMap", authoringTexture);
                AssetDatabase.CreateAsset(sourceMaterial, sourceMaterialPath);
                AssetDatabase.ImportAsset(sourceMaterialPath, ImportAssetOptions.ForceSynchronousImport);
                sourceMaterial = AssetDatabase.LoadAssetAtPath<Material>(sourceMaterialPath);
                Assert.IsNotNull(sourceMaterial);
                Assert.IsTrue(EditorUtility.IsPersistent(sourceMaterial));
                Assert.AreSame(authoringTexture, sourceMaterial.GetTexture("_BaseMap"));

                transientMaterial = UnityEngine.Object.Instantiate(sourceMaterial);
                transientMaterial.name = "Source_Stripped";
                transientMaterial.SetTexture("_BaseMap", null);
                Assert.IsFalse(EditorUtility.IsPersistent(transientMaterial),
                    "The fixture must exercise a transient material reference.");
                Assert.IsNull(transientMaterial.GetTexture("_BaseMap"));

                root = new GameObject("TransientMaterialPrefabRoot");
                MeshRenderer renderer = root.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = transientMaterial;
                GameObject child = new GameObject("SharedMaterialChild");
                child.transform.SetParent(root.transform, false);
                MeshRenderer childRenderer = child.AddComponent<MeshRenderer>();
                childRenderer.sharedMaterial = transientMaterial;

                settings = ScriptableObject.CreateInstance<BasisAssetBundleObject>();
                settings.TemporaryStorage = temporaryStorage;

                bool wasModified = false;
                string prefabPath = TemporaryStorageHandler.SavePrefabToTemporaryStorage(
                    root, settings, ref wasModified, out _);

                Assert.IsTrue(wasModified);
                UnityEngine.Object.DestroyImmediate(root);
                root = null;

                AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
                GameObject savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.IsNotNull(savedPrefab, "The staged prefab could not be reloaded from the AssetDatabase.");

                MeshRenderer savedRenderer = savedPrefab.GetComponent<MeshRenderer>();
                Assert.IsNotNull(savedRenderer);
                Assert.IsNotNull(savedRenderer.sharedMaterial,
                    "A transient material assigned before prefab staging was serialized as a missing material reference.");
                Assert.IsTrue(EditorUtility.IsPersistent(savedRenderer.sharedMaterial),
                    "The staged prefab still references a non-persistent material after serialization.");
                string persistedMaterialPath = AssetDatabase.GetAssetPath(savedRenderer.sharedMaterial);
                Assert.IsFalse(string.IsNullOrEmpty(persistedMaterialPath),
                    "The staged material does not have a durable AssetDatabase path.");
                Assert.IsNull(savedRenderer.sharedMaterial.GetTexture("_BaseMap"),
                    "The persisted build material regained the authoring-only texture that the transient clone removed.");

                string[] stagedDependencies = AssetDatabase.GetDependencies(prefabPath, true);
                CollectionAssert.Contains(stagedDependencies, persistedMaterialPath,
                    "The staged prefab dependency graph does not include its generated persistent material.");
                CollectionAssert.DoesNotContain(stagedDependencies, authoringTexturePath,
                    "The authoring-only texture cleared from the transient material leaked back into staged dependencies.");

                MeshRenderer savedChildRenderer = savedPrefab.transform.Find("SharedMaterialChild")
                    .GetComponent<MeshRenderer>();
                Assert.AreSame(savedRenderer.sharedMaterial, savedChildRenderer.sharedMaterial,
                    "One transient material shared by multiple renderers should map to one persistent temporary material asset.");

                Material originalMaterial = AssetDatabase.LoadAssetAtPath<Material>(sourceMaterialPath);
                Assert.AreSame(authoringTexture, originalMaterial.GetTexture("_BaseMap"),
                    "Persisting the build clone must not mutate the authored source material asset.");
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }

                if (transientMaterial != null && !EditorUtility.IsPersistent(transientMaterial))
                {
                    UnityEngine.Object.DestroyImmediate(transientMaterial);
                }

                if (settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(settings);
                }

                AssetDatabase.DeleteAsset(temporaryStorage);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }
    }
}
