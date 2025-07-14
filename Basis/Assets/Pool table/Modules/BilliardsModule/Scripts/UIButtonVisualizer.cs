using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class UIButtonVisualizer : MonoBehaviour
{
}

#if UNITY_EDITOR
[CustomEditor(typeof(UIButtonVisualizer))]
public class TriggerVisualizerEditor : Editor
{
    private bool isButtonOn;

    public override void OnInspectorGUI()
    {
        UIButtonVisualizer visualizer = (UIButtonVisualizer)target;
        UIButton module = visualizer.GetComponent<UIButton>();
        var buttonOff = module.buttonOff;
        var buttonOn = module.buttonOn;
        var outlineColor = module.outlineColor;

        {
            MeshRenderer renderer = visualizer.transform.Find("Visual/DesktopOutline").GetComponent<MeshRenderer>();
            Material tempMaterial = new Material(renderer.sharedMaterial);
            tempMaterial.SetColor("_Color", outlineColor);
            renderer.sharedMaterial = tempMaterial;
        }

        {
            MeshRenderer renderer = visualizer.transform.Find("Visual/Button").GetComponent<MeshRenderer>();
            Material tempMaterial = new Material(renderer.sharedMaterial);
            if (GUILayout.Toggle(isButtonOn, "Turn Button On"))
            {
                tempMaterial.mainTexture = buttonOn;
                isButtonOn = true;
            }
            else
            {
                tempMaterial.mainTexture = buttonOff;
                isButtonOn = false;
            }
            renderer.sharedMaterial = tempMaterial;
        }
    }
}
#endif
