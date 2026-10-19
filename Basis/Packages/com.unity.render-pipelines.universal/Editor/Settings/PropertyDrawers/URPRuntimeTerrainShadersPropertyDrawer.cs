using UnityEditor.UIElements;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace UnityEditor.Rendering.Universal
{
    [CustomPropertyDrawer(typeof(URPTerrainShaderSetting))]
    class URPRuntimeTerrainShadersPropertyDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var container = new VisualElement();

            var relativeProperty = property.FindPropertyRelative("m_IncludeTerrainShaders");
            var field = new PropertyField(relativeProperty);

#if !ENABLE_TERRAIN_MODULE
            field.SetEnabled(false);
            field.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var toggle = field.Q<Toggle>();
                if (toggle != null)
                {
                    toggle.SetValueWithoutNotify(false);
                    toggle.tooltip = "The Terrain module is disabled in Package Manager. Terrain shaders will be automatically stripped from the build.";
                }
            });
#endif

            container.Add(field);
            return container;
        }
    }
}
