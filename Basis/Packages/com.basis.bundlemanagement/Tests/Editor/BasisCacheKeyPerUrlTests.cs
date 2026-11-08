using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.TestTools;

public class BasisCacheKeyPerUrlTests
{
    private const string UrlA = "https://content.example.test/a/avatar.bee";
    private const string UrlB = "https://other.example.test/b/avatar.bee";
    private const string SharedVersion = "11111111-2222-3333-4444-555555555555";
    private string previousPath, tempRoot;
    private PropertyInfo persistentProperty;
    [SetUp]
    public void SetUp()
    {
        persistentProperty = typeof(BasisIOManagement).GetProperty(nameof(BasisIOManagement.PersistentDataPath), BindingFlags.Public | BindingFlags.Static);
        previousPath = BasisIOManagement.PersistentDataPath;
        tempRoot = Path.Combine(Path.GetTempPath(), "BasisCacheKeyPerUrlTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        persistentProperty.SetValue(null, tempRoot);
        BasisLoadHandler.OnDiscData.Clear();
    }
    [TearDown]
    public void TearDown()
    {
        BasisLoadHandler.OnDiscData.Clear();
        persistentProperty.SetValue(null, previousPath);
        try { Directory.Delete(tempRoot, true); } catch (Exception) { }
    }
    private static string Platform => BasisIOManagement.GetCurrentCachePlatform();
    [Test]
    public void DifferentUrlsSameVersion_GetDifferentBeePaths()
    {
        Assert.AreNotEqual(BasisIOManagement.GetBeeCacheFilePath(UrlA, SharedVersion, Platform), BasisIOManagement.GetBeeCacheFilePath(UrlB, SharedVersion, Platform));
    }
    [Test]
    public void DifferentUrlsSameVersion_GetDifferentConnectorPaths()
    {
        Assert.AreNotEqual(BasisIOManagement.GetConnectorCacheFilePath(UrlA, SharedVersion, Platform), BasisIOManagement.GetConnectorCacheFilePath(UrlB, SharedVersion, Platform));
    }
    [Test]
    public void DifferentUrlsSameVersion_GetDifferentMetaPaths()
    {
        Assert.AreNotEqual(BasisIOManagement.GetMetaCacheFilePath(UrlA, SharedVersion, Platform), BasisIOManagement.GetMetaCacheFilePath(UrlB, SharedVersion, Platform));
    }
    [Test]
    public void SameUrlDifferentSpelling_SharesPaths()
    {
        Assert.AreEqual(BasisIOManagement.GetBeeCacheFilePath("https://Content.Example.test/a/my avatar.bee", SharedVersion, Platform), BasisIOManagement.GetBeeCacheFilePath("https://content.example.test/a/my%20avatar.bee", SharedVersion, Platform));
    }
    [Test]
    public void EmptyUrl_Throws()
    {
        Assert.Throws<ArgumentException>(() => BasisIOManagement.GetBeeCacheFilePath(" ", SharedVersion, Platform));
    }
    private string WriteLegacyEntry(string url, string payload, out string legacyBeePath, out string legacyMetaPath)
    {
        string folder = BasisIOManagement.GenerateFolderPath(BasisBeeConstants.AssetBundlesFolder);
        legacyBeePath = Path.Combine(folder, $"{SharedVersion}.{Platform}{BasisBeeConstants.BasisEncryptedExtension}");
        legacyMetaPath = BasisIOManagement.GetUnscopedMetaCacheFilePath(SharedVersion, Platform);
        File.WriteAllText(legacyBeePath, payload);
        BasisBEEExtensionMeta meta = new BasisBEEExtensionMeta
        {
            StoredRemote = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = url },
            StoredLocal = new BasisStoredEncryptedBundle { DownloadedBeeFileLocation = legacyBeePath, DownloadedConnectorFileLocation = string.Empty },
            UniqueVersion = SharedVersion,
            DownloadedPlatform = Platform,
        };
        File.WriteAllBytes(legacyMetaPath, BasisSerialization.SerializeValue(meta));
        return legacyBeePath;
    }
    [Test]
    public void LegacyEntry_IsNotAdoptedByAnotherUrl()
    {
        WriteLegacyEntry(UrlA, "content of A", out string legacyBee, out string legacyMeta);
        Assert.IsFalse(BasisLoadHandler.IsMetaDataOnDisc(UrlB, out _));
        Assert.IsTrue(File.Exists(legacyBee));
        Assert.IsTrue(File.Exists(legacyMeta));
    }
    [Test]
    public void LegacyEntry_IsAdoptedAndMovedForItsOwnUrl()
    {
        WriteLegacyEntry(UrlA, "content of A", out string legacyBee, out string legacyMeta);
        Assert.IsTrue(BasisLoadHandler.IsMetaDataOnDisc(UrlA, out BasisBEEExtensionMeta info));
        string scopedBee = BasisIOManagement.GetBeeCacheFilePath(UrlA, SharedVersion, Platform);
        Assert.AreEqual(Path.GetFullPath(scopedBee), Path.GetFullPath(info.StoredLocal.DownloadedBeeFileLocation));
        Assert.AreEqual("content of A", File.ReadAllText(scopedBee));
        Assert.IsFalse(File.Exists(legacyBee));
        Assert.IsFalse(File.Exists(legacyMeta));
        Assert.IsTrue(File.Exists(BasisIOManagement.GetMetaCacheFilePath(UrlA, SharedVersion, Platform)));
        Assert.IsFalse(BasisLoadHandler.IsMetaDataOnDisc(UrlB, out _));
    }
    [UnityTest]
    public IEnumerator AddDiscInfo_KeepsAnotherUrlsLegacyMeta()
    {
        WriteLegacyEntry(UrlA, "content of A", out _, out string legacyMeta);
        string beeB = BasisIOManagement.GetBeeCacheFilePath(UrlB, SharedVersion, Platform);
        File.WriteAllText(beeB, "content of B");
        BasisBEEExtensionMeta metaB = new BasisBEEExtensionMeta
        {
            StoredRemote = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = UrlB },
            StoredLocal = new BasisStoredEncryptedBundle { DownloadedBeeFileLocation = beeB, DownloadedConnectorFileLocation = string.Empty },
            UniqueVersion = SharedVersion,
            DownloadedPlatform = Platform,
        };
        System.Threading.Tasks.Task save = BasisLoadHandler.AddDiscInfo(metaB);
        while (!save.IsCompleted) yield return null;
        save.GetAwaiter().GetResult();
        Assert.IsTrue(File.Exists(legacyMeta));
        Assert.AreEqual(UrlA, BasisSerialization.DeserializeValue<BasisBEEExtensionMeta>(File.ReadAllBytes(legacyMeta)).StoredRemote.RemoteBeeFileLocation);
        Assert.IsTrue(File.Exists(BasisIOManagement.GetMetaCacheFilePath(UrlB, SharedVersion, Platform)));
    }
    [Test]
    public void Sweep_RemovesOnlyOldUnreferencedFiles()
    {
        string folder = BasisIOManagement.GenerateFolderPath(BasisBeeConstants.AssetBundlesFolder);
        string referenced = BasisIOManagement.GetBeeCacheFilePath(UrlA, SharedVersion, Platform);
        string orphan = Path.Combine(folder, "orphan" + BasisBeeConstants.BasisEncryptedExtension);
        string fresh = Path.Combine(folder, "fresh" + BasisBeeConstants.BasisConnectorExtension);
        File.WriteAllText(referenced, "a");
        File.WriteAllText(orphan, "b");
        File.WriteAllText(fresh, "c");
        DateTime old = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(referenced, old);
        File.SetLastWriteTimeUtc(orphan, old);
        BasisLoadHandler.OnDiscData["k"] = new BasisBEEExtensionMeta
        {
            StoredRemote = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = UrlA },
            StoredLocal = new BasisStoredEncryptedBundle { DownloadedBeeFileLocation = referenced },
            UniqueVersion = SharedVersion,
            DownloadedPlatform = Platform,
        };
        Assert.AreEqual(1, BasisLoadHandler.SweepUnreferencedCacheFiles(DateTime.UtcNow.AddDays(-1)));
        Assert.IsTrue(File.Exists(referenced));
        Assert.IsFalse(File.Exists(orphan));
        Assert.IsTrue(File.Exists(fresh));
    }
}
