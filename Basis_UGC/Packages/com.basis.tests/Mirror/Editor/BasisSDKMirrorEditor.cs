using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

[CustomEditor(typeof(BasisSDKMirror))]
public class BasisSDKMirrorEditor : Editor
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();

        var serializedObj = serializedObject;

        // MAIN SETTINGS GROUP
        var mainSettings = new Foldout
        {
            text = "Main Settings",
            value = true
        };
        mainSettings.Add(CreateProperty(serializedObj, "Renderer", "Renderer"));
        mainSettings.Add(CreateProperty(serializedObj, "MirrorsMaterial", "Mirror Material"));
        mainSettings.Add(CreateProperty(serializedObj, "ReflectingLayers", "Reflecting Layers"));

        var clipSettings = new VisualElement();
        clipSettings.style.marginTop = 6;
        clipSettings.Add(new Label("Clipping Settings") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
        clipSettings.Add(CreateProperty(serializedObj, "ClipPlaneOffset", "Clip Plane Offset"));
        clipSettings.Add(CreateProperty(serializedObj, "nearClipLimit", "Near Clip Limit"));
        clipSettings.Add(CreateProperty(serializedObj, "FarClipPlane", "Far Clip Plane"));
        mainSettings.Add(clipSettings);

        var textureSettings = new VisualElement();
        textureSettings.style.marginTop = 6;
        textureSettings.Add(new Label("Texture Settings") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
        textureSettings.Add(CreateProperty(serializedObj, "XSize", "Texture Width"));
        textureSettings.Add(CreateProperty(serializedObj, "YSize", "Texture Height"));
        textureSettings.Add(CreateProperty(serializedObj, "depth", "Depth Buffer"));
        textureSettings.Add(CreateProperty(serializedObj, "Antialiasing", "Anti-Aliasing"));
        mainSettings.Add(textureSettings);

        root.Add(mainSettings);

        // OPTIONS GROUP
        var optionsSettings = new Foldout
        {
            text = "Rendering Options",
            value = true
        };
        optionsSettings.Add(CreateProperty(serializedObj, "allowXRRendering", "Allow XR Rendering"));
        optionsSettings.Add(CreateProperty(serializedObj, "RenderPostProcessing", "Render Post-Processing"));
        optionsSettings.Add(CreateProperty(serializedObj, "OcclusionCulling", "Occlusion Culling"));
        optionsSettings.Add(CreateProperty(serializedObj, "renderShadows", "Render Shadows"));

        root.Add(optionsSettings);
        /*
        // CAMERAS GROUP
        var cameraSettings = new Foldout
        {
            text = "Cameras",
            value = true
        };
        cameraSettings.Add(CreateProperty(serializedObj, "LeftCamera", "Left Eye Camera"));
        cameraSettings.Add(CreateProperty(serializedObj, "RightCamera", "Right Eye Camera"));
        cameraSettings.Add(CreateProperty(serializedObj, "PortalTextureLeft", "Left Eye Texture"));
        cameraSettings.Add(CreateProperty(serializedObj, "PortalTextureRight", "Right Eye Texture"));

        root.Add(cameraSettings);
        */
        return root;
    }

    private PropertyField CreateProperty(SerializedObject serializedObject, string propertyName, string label)
    {
        var property = serializedObject.FindProperty(propertyName);
        var field = new PropertyField(property, label);
        field.Bind(serializedObject);
        return field;
    }
}
