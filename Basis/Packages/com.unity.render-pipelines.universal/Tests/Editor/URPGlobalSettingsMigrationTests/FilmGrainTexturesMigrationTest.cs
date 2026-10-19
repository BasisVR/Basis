using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace UnityEditor.Rendering.Universal.Test.GlobalSettingsMigration
{
#pragma warning disable 618
    class FilmGrainTexturesMigrationUnitTests
    {
        [Test]
        public void MigrateFilmGrainTextures_ClearsDeprecatedField()
        {
            var postProcessData = ScriptableObject.CreateInstance<PostProcessData>();
            postProcessData.textures = new PostProcessData.TextureResources { filmGrainTex = new Texture2D[]
                {
                    new Texture2D(1, 1),
                    new Texture2D(1, 1),
                }
            };

            UniversalRenderPipelineGlobalSettings.ClearObsoleteFilmGrainTexturesAndLogWarnings(postProcessData, filmGrainResources: null);
            Assert.IsNull(postProcessData.textures.filmGrainTex, "filmGrainTex should be null after migration");
            Object.DestroyImmediate(postProcessData);
        }

        [Test]
        public void MigrateFilmGrainTextures_WithNullFilmGrainTex_DoesNotThrow()
        {
            var postProcessData = ScriptableObject.CreateInstance<PostProcessData>();
            postProcessData.textures = new PostProcessData.TextureResources { filmGrainTex = null };

            Assert.DoesNotThrow(() => UniversalRenderPipelineGlobalSettings.ClearObsoleteFilmGrainTexturesAndLogWarnings(postProcessData, filmGrainResources: null));

            Assert.IsNull(postProcessData.textures.filmGrainTex);
            Object.DestroyImmediate(postProcessData);
        }

        [Test]
        public void MigrateFilmGrainTextures_WithNullTextures_DoesNotThrow()
        {
            var postProcessData = ScriptableObject.CreateInstance<PostProcessData>();
            postProcessData.textures = null;
            Assert.DoesNotThrow(() => UniversalRenderPipelineGlobalSettings.ClearObsoleteFilmGrainTexturesAndLogWarnings(postProcessData, filmGrainResources: null));
            Object.DestroyImmediate(postProcessData);
        }

        [Test]
        public void MigrateFilmGrainTextures_WithMatchingTextures_ClearsWithoutWarning()
        {
            var postProcessData = ScriptableObject.CreateInstance<PostProcessData>();
            postProcessData.textures = new PostProcessData.TextureResources();
            var sharedTex = new Texture2D(1, 1) { name = "SharedGrain" };
            var filmGrainResources = new UniversalRenderPipelineFilmGrainResources { textures = new Texture2D[] { sharedTex } };
            postProcessData.textures.filmGrainTex = new Texture2D[] { sharedTex };

            // Shared texture exists in both old and new -- no warning expected.
            UniversalRenderPipelineGlobalSettings.ClearObsoleteFilmGrainTexturesAndLogWarnings(postProcessData, filmGrainResources);
            Assert.IsNull(postProcessData.textures.filmGrainTex, "filmGrainTex should be null after migration");
            Object.DestroyImmediate(postProcessData);
        }

        [Test]
        public void MigrateFilmGrainTextures_WithCustomTextures_LogsWarningAndClears()
        {
            var postProcessData = ScriptableObject.CreateInstance<PostProcessData>();
            postProcessData.textures = new PostProcessData.TextureResources();

            string customTexPath = "Assets/URP/MigrationTests/CustomGrain.asset";
            CoreUtils.EnsureFolderTreeInAssetFilePath(customTexPath);
            var customTex = new Texture2D(1, 1) { name = "CustomGrain" };
            AssetDatabase.CreateAsset(customTex, customTexPath);
            AssetDatabase.SaveAssets();

            var defaultTex = new Texture2D(1, 1) { name = "DefaultGrain" };
            var filmGrainResources = new UniversalRenderPipelineFilmGrainResources { textures = new Texture2D[] { defaultTex } };
            postProcessData.textures.filmGrainTex = new Texture2D[] { customTex };

            LogAssert.Expect(LogType.Warning,
                "Film Grain texture list has been moved from PostProcessData to URP " +
                "Graphics Settings, and it can no longer be edited. Use " +
                "`FilmGrain.type = FilmGrainLookup.Custom` instead. As a consequence, " +
                "following custom film grain textures are no longer referenced:\n" +
                $"{customTexPath}.");

            UniversalRenderPipelineGlobalSettings.ClearObsoleteFilmGrainTexturesAndLogWarnings(postProcessData, filmGrainResources);
            Assert.IsNull(postProcessData.textures.filmGrainTex, "filmGrainTex should be null after migration even when custom textures were present");
            AssetDatabase.DeleteAsset(customTexPath);
            Object.DestroyImmediate(postProcessData);
        }
    }
#pragma warning restore 618
}
