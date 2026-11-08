using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class BasisBundleReuseOwnerTests
{
    private const string AssetFolder = "Assets/BasisBundleReuseOwnerTestsTemp";
    private const string PrefabPath = AssetFolder + "/ReuseOwnerProbe.prefab";
    private const string SourceA = "https://content.example.test/a/probe.bee";
    private const string SourceB = "https://other.example.test/b/probe.bee";
    private string outputFolder;
    private AssetBundle bundle;
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!AssetDatabase.IsValidFolder(AssetFolder)) AssetDatabase.CreateFolder("Assets", "BasisBundleReuseOwnerTestsTemp");
        GameObject probe = new GameObject("ReuseOwnerProbe");
        PrefabUtility.SaveAsPrefabAsset(probe, PrefabPath);
        UnityEngine.Object.DestroyImmediate(probe);
        outputFolder = Path.Combine(Path.GetTempPath(), "BasisBundleReuseOwnerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputFolder);
        AssetBundleBuild build = new AssetBundleBuild { assetBundleName = "reuseownerprobe", assetNames = new[] { PrefabPath } };
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(outputFolder, new[] { build }, BuildAssetBundleOptions.None, EditorUserBuildSettings.activeBuildTarget);
        Assert.IsNotNull(manifest, "test bundle build failed");
        bundle = AssetBundle.LoadFromFile(Path.Combine(outputFolder, "reuseownerprobe"));
        Assert.IsNotNull(bundle, "test bundle load failed");
        Assert.IsTrue(bundle.Contains(PrefabPath.ToLowerInvariant()));
    }
    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (bundle != null) bundle.Unload(true);
        AssetDatabase.DeleteAsset(AssetFolder);
        try { Directory.Delete(outputFolder, true); } catch (Exception) { }
    }
    [Test]
    public void UnregisteredBundle_IsNotHandedOut()
    {
        Assert.IsNull(BasisBeeManagement.FindLoadedBundleForSource(SourceB + "?never", PrefabPath.ToLowerInvariant()));
    }
    [Test]
    public void BundleRegisteredForA_IsFoundForA()
    {
        BasisBeeManagement.RegisterBundleSource(bundle, BasisIOManagement.CanonicalizeRemoteUrl(SourceA));
        Assert.AreSame(bundle, BasisBeeManagement.FindLoadedBundleForSource(BasisIOManagement.CanonicalizeRemoteUrl(SourceA), PrefabPath.ToLowerInvariant()));
    }
    [Test]
    public void BundleRegisteredForA_IsNotHandedToB()
    {
        BasisBeeManagement.RegisterBundleSource(bundle, BasisIOManagement.CanonicalizeRemoteUrl(SourceA));
        Assert.IsNull(BasisBeeManagement.FindLoadedBundleForSource(BasisIOManagement.CanonicalizeRemoteUrl(SourceB), PrefabPath.ToLowerInvariant()));
    }
    [Test]
    public void WrapperForB_KeysToItsOwnSource()
    {
        BasisTrackedBundleWrapper wrapper = new BasisTrackedBundleWrapper();
        wrapper.LoadableBundle = new BasisLoadableBundle { BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = SourceB } };
        BasisBeeManagement.RegisterBundleSource(bundle, BasisIOManagement.CanonicalizeRemoteUrl(SourceA));
        Assert.IsNull(BasisBeeManagement.FindLoadedBundleForSource(BasisBeeManagement.SourceKeyOf(wrapper), PrefabPath.ToLowerInvariant()));
    }
}
