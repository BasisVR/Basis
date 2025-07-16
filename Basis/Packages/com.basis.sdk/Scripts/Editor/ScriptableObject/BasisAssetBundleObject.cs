using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CreateAssetMenu(fileName = "NewBasisAssetBundleObject", menuName = "Basis/ScriptableObjects/BasisAssetBundleObject", order = 1)]
public class BasisAssetBundleObject : ScriptableObject
{
    public static string AssetBundleObject = "Packages/com.basis.sdk/Settings/AssetBundleBuildSettings.asset";
    public string TemporaryStorage = "Packages/com.basis.basisdk/TemporaryStorage";
    public string BundleExtension = ".bundle";
    public string hashExtension = ".hash";
    public string BasisMetaExtension = ".BME";
    public string BasisBundleEncryptedExtension = ".BEB";
    public string BasisBundleDecryptedExtension = ".BDB";
    public string BasisMetaEncryptedExtension = ".BEM";
    public string BasisEncryptedExtension = ".BEE";
    public string ProtectedPasswordFileName = "dontuploadmepassword";
    public bool useCompression = true;
    public bool GenerateImage = true;
    public bool OpenFolderOnDisc = true;
    public BuildTarget BuildTarget = BuildTarget.StandaloneWindows;
    public BuildAssetBundleOptions BuildAssetBundleOptions;
    public string AssetBundleDirectory = "./AssetBundles";
    public List<BuildTarget> selectedTargets = new List<BuildTarget>(BasisSDKConstants.allowedTargets);
}
[CustomEditor(typeof(BasisAssetBundleObject))]
public class BasisAssetBundleObjectEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // Draw the default inspector UI
        DrawDefaultInspector();

        // Reference to the target scriptable object
        BasisAssetBundleObject assetBundleObject = (BasisAssetBundleObject)target;

        // Add a button to restore default values
        if (GUILayout.Button("Restore Defaults"))
        {
            RestoreDefaults(assetBundleObject);
        }
    }

    private void RestoreDefaults(BasisAssetBundleObject assetBundleObject)
    {
        // Reset properties to their default values
        assetBundleObject.TemporaryStorage = "Packages/com.basis.basisdk/TemporaryStorage";
        assetBundleObject.BundleExtension = ".bundle";
        assetBundleObject.hashExtension = ".hash";
        assetBundleObject.BasisMetaExtension = ".BME";
        assetBundleObject.BasisBundleEncryptedExtension = ".BEB";
        assetBundleObject.BasisBundleDecryptedExtension = ".BDB";
        assetBundleObject.BasisMetaEncryptedExtension = ".BEM";
        assetBundleObject.useCompression = true;
        assetBundleObject.GenerateImage = true;
        assetBundleObject.BuildTarget = BuildTarget.StandaloneWindows;
        assetBundleObject.BuildAssetBundleOptions = BuildAssetBundleOptions.None;
        assetBundleObject.AssetBundleDirectory = "./AssetBundles";
        assetBundleObject.ProtectedPasswordFileName = "dontuploadmepassword";
        assetBundleObject.BasisEncryptedExtension = ".BEE";
        assetBundleObject.selectedTargets = new List<BuildTarget>(BasisSDKConstants.allowedTargets);

        // Mark the object as dirty to save changes
        EditorUtility.SetDirty(assetBundleObject);
        AssetDatabase.SaveAssets();
    }
}
