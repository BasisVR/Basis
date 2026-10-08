using System.Collections.Generic;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

[CreateAssetMenu(fileName = "NewBasisAssetBundleObject", menuName = "Basis/ScriptableObjects/BasisAssetBundleObject", order = 1)]
[System.Serializable]
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
    public bool RebakeOcclusionCulling = true;
    // Avatars only: also embed a platform-agnostic glTF (Generic) section so platforms
    // without a built AssetBundle can still load the avatar.
    public bool GenerateGenericGLTF = true;
    public bool StripLodCrossFadeVariants = true;
    public bool StripBakeOnlyShaderPasses = true;
    public bool StripSpaceWarpPassOffQuest = true;
    public bool StripDotsInstancingVariants = false;
    public bool WriteShaderVariantReport = true;
    public BuildTarget BuildTarget = BuildTarget.StandaloneWindows;
    public BuildAssetBundleOptions BuildAssetBundleOptions;
    public string AssetBundleDirectory = "./AssetBundles";
    public string AssetBundleUnCombined = "./AssetCache";
    [SerializeField]
    public List<BuildTarget> selectedTargets = new List<BuildTarget>();

    [SerializeField]
    public List<BuildTarget> RebakeOcclusionCullingInThese = new List<BuildTarget>();
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
        if (GUILayout.Button(BasisEditorLocalization.Get("sdk.assetBundleObject.restoreDefaults")))
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
        assetBundleObject.selectedTargets = new List<BuildTarget>(BasisSDKConstants.defaultTargets);
        assetBundleObject.RebakeOcclusionCullingInThese = new List<BuildTarget>(BasisSDKConstants.OcclusionCullingTargets);
        assetBundleObject.RebakeOcclusionCulling = true;
        assetBundleObject.GenerateGenericGLTF = true;
        assetBundleObject.StripLodCrossFadeVariants = true;
        assetBundleObject.StripBakeOnlyShaderPasses = true;
        assetBundleObject.StripSpaceWarpPassOffQuest = true;
        assetBundleObject.StripDotsInstancingVariants = false;
        assetBundleObject.WriteShaderVariantReport = true;
        // Mark the object as dirty to save changes
        EditorUtility.SetDirty(assetBundleObject);
        AssetDatabase.SaveAssets();
    }
}
