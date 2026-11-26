using System;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// The distance attenuation formula used by URP lights.
    /// </summary>
    public enum LightFalloffMode
    {
        /// <summary>
        /// Physically-based inverse squared falloff. URP default.
        /// </summary>
        [InspectorName("Inverse Squared")]
        InverseSquared = 0,

        /// <summary>
        /// Falloff matching the Built-in Render Pipeline. Use for Built-in to URP migration.
        /// </summary>
        [InspectorName("Quadratic")]
        Quadratic = 1,
    }

    /// <summary>
    /// Project-wide light falloff settings for URP.
    /// </summary>
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    [Categorization.CategoryInfo(Name = "Lighting", Order = 21)]
    public class URPLightFalloffSettings : IRenderPipelineGraphicsSettings
    {
        [SerializeField, HideInInspector] private int m_Version = 1;

        int IRenderPipelineGraphicsSettings.version => m_Version;
        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;

        [SerializeField, Tooltip("How light intensity falls off with distance. Use Quadratic to match the Built-in Render Pipeline.")]
        private LightFalloffMode m_LightFalloffMode = LightFalloffMode.InverseSquared;

        /// <summary>
        /// Defines the light attenuation formula used by URP lights.
        /// </summary>
        public LightFalloffMode lightFalloffMode
        {
            get => m_LightFalloffMode;
#if UNITY_EDITOR
            set => this.SetValueAndNotify(ref m_LightFalloffMode, value, nameof(m_LightFalloffMode));
#endif
        }

#if UNITY_EDITOR
        // Migrate on settings creation instead of in the Global Settings version chain, which stays at 14
        // so the version 15 step added in 7000.0 still runs on upgrade. Clear the old value on the URP Assets
        // so the version 16 step, which sets Quadratic from them, keeps the value chosen here.
        void IRenderPipelineGraphicsSettings.Reset()
        {
            MigrateLightFalloffMode();
        }

        void MigrateLightFalloffMode()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets"))
                    continue;

                var urpAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
#pragma warning disable 618 // Type or member is obsolete
                if (urpAsset == null || urpAsset.m_ObsoleteLightFalloffMode != LightFalloffMode.Quadratic)
                    continue;

                // Quadratic is the value a user had to opt into, so it wins when assets disagree
                lightFalloffMode = LightFalloffMode.Quadratic;

                if (!AssetDatabase.MakeEditable(path))
                {
                    Debug.LogWarning($"Unable to make '{urpAsset.name}' editable, so its Light Falloff Mode can revert to Quadratic when the project is upgraded to a newer version of Unity.", urpAsset);
                    continue;
                }

                urpAsset.m_ObsoleteLightFalloffMode = LightFalloffMode.InverseSquared;
#pragma warning restore 618
                EditorUtility.SetDirty(urpAsset);
                EditorApplication.delayCall += () => AssetDatabase.SaveAssetIfDirty(urpAsset);
            }
        }
#endif
    }
}
