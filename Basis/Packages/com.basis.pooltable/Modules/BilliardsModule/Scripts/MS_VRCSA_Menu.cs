#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public class MS_VRCSA_Menu : MonoBehaviour
{
    [MenuItem("MS-VRCSA/Set Up Pool Table Layers\\Collision", false, 0)]
    private static void setPoolTableCollisionLayers()
    {
        for (int i = 0; i < 32; i++)
        {
            Physics.IgnoreLayerCollision(22, i, true);
        }
        Physics.IgnoreLayerCollision(22, 22, false);
        msvrca_SetLayerName(22, "BilliardsModule");
    }
    private static void msvrca_SetLayerName(int layer, string name)
    {
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset"));
        tagManager.Update();

        var layersProperty = tagManager.FindProperty("layers");
        layersProperty.arraySize = Mathf.Max(layersProperty.arraySize, layer);
        layersProperty.GetArrayElementAtIndex(layer).stringValue = name;

        tagManager.ApplyModifiedProperties();
    }
}
#endif