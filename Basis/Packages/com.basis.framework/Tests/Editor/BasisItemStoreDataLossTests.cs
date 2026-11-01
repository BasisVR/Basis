using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Basis.Scripts.UI.UI_Panels;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests
{
    public class BasisItemStoreDataLossTests
    {
        const string Bad = "not json {";
        static readonly BindingFlags Statics = BindingFlags.NonPublic | BindingFlags.Static;
        string dir;
        string keyPath;
        string folderPath;
        string good;

        static void SetField(Type type, string name, object value) => type.GetField(name, Statics).SetValue(null, value);

        static void SetStore(Type type, string path, bool loaded)
        {
            SetField(type, "_filePath", path);
            SetField(type, "_loaded", loaded);
            SetField(type, "_readOnly", false);
        }

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "BasisItemStoreDataLoss_" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            keyPath = Path.Combine(dir, "ItemKeyStore.json");
            folderPath = Path.Combine(dir, "ItemFolderStore.json");
            SetStore(typeof(BasisDataStoreItemKeys), keyPath, false);
            SetStore(typeof(BasisDataStoreItemFolders), folderPath, false);
            SetField(typeof(BasisDataStoreItemKeys), "keys", new BasisDataStoreItemKeys.ItemKeys { Data = Array.Empty<BasisDataStoreItemKeys.ItemKey>() });
            SetField(typeof(BasisDataStoreItemFolders), "folders", new BasisDataStoreItemFolders.ItemFolders { Data = Array.Empty<BasisDataStoreItemFolders.ItemFolder>() });
            good = JsonUtility.ToJson(new Wrapper { Value = new BasisDataStoreItemKeys.ItemKeys { Data = new[] { Key("https://example.com/mine.BEE") } } });
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            SetStore(typeof(BasisDataStoreItemKeys), null, false);
            SetStore(typeof(BasisDataStoreItemFolders), null, false);
            SetField(typeof(BasisDataStoreItemKeys), "keys", new BasisDataStoreItemKeys.ItemKeys { Data = Array.Empty<BasisDataStoreItemKeys.ItemKey>() });
            SetField(typeof(BasisDataStoreItemFolders), "folders", new BasisDataStoreItemFolders.ItemFolders { Data = Array.Empty<BasisDataStoreItemFolders.ItemFolder>() });
            Directory.Delete(dir, true);
        }

        [Serializable]
        class Wrapper
        {
            public BasisDataStoreItemKeys.ItemKeys Value;
        }

        static BasisDataStoreItemKeys.ItemKey Key(string url) => new BasisDataStoreItemKeys.ItemKey { Mode = BundledContentHolder.Mode.Avatar, Url = url, Pass = "pass" };

        static bool Has(string url) => BasisDataStoreItemKeys.DisplayKeys().Any(k => k.Url == url);

        static IEnumerator Wait(Task task)
        {
            while (!task.IsCompleted) yield return null;
            task.GetAwaiter().GetResult();
        }

        [UnityTest]
        public IEnumerator FailedRead_NeverOverwritesTheLibrary()
        {
            File.WriteAllText(keyPath, good);
            using (new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                yield return Wait(BasisDataStoreItemKeys.LoadKeys());
            }
            yield return Wait(BasisDataStoreItemKeys.AddNewKey(Key("https://example.com/later.BEE")));
            Assert.AreEqual(good, File.ReadAllText(keyPath));
            Assert.IsFalse(File.Exists(keyPath + ".bak"));
            yield return Wait(BasisDataStoreItemKeys.LoadKeys());
            Assert.IsTrue(Has("https://example.com/mine.BEE"));
        }

        [UnityTest]
        public IEnumerator FailedRead_ShowsTheBackupWithoutSavingIt()
        {
            File.WriteAllText(keyPath, good);
            File.WriteAllText(keyPath + ".bak", good.Replace("mine", "older"));
            using (new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                yield return Wait(BasisDataStoreItemKeys.LoadKeys());
            }
            Assert.IsTrue(Has("https://example.com/older.BEE"));
            Assert.AreEqual(good, File.ReadAllText(keyPath));
        }

        [UnityTest]
        public IEnumerator CorruptFile_IsKeptBeforeAnythingReplacesIt()
        {
            File.WriteAllText(keyPath, Bad);
            yield return Wait(BasisDataStoreItemKeys.LoadKeys());
            string kept = Directory.GetFiles(dir, "ItemKeyStore.json.corrupt_*").Single();
            Assert.AreEqual(Bad, File.ReadAllText(kept));
            Assert.AreNotEqual(Bad, File.ReadAllText(keyPath));
        }

        [UnityTest]
        public IEnumerator CorruptFile_RestoresFromBackup()
        {
            File.WriteAllText(keyPath, Bad);
            File.WriteAllText(keyPath + ".bak", good);
            yield return Wait(BasisDataStoreItemKeys.LoadKeys());
            Assert.IsTrue(Has("https://example.com/mine.BEE"));
            Assert.AreEqual(1, Directory.GetFiles(dir, "ItemKeyStore.json.corrupt_*").Length);
        }

        [UnityTest]
        public IEnumerator HealthyFile_StillSavesChanges()
        {
            File.WriteAllText(keyPath, good);
            yield return Wait(BasisDataStoreItemKeys.LoadKeys());
            yield return Wait(BasisDataStoreItemKeys.AddNewKey(Key("https://example.com/later.BEE")));
            string saved = File.ReadAllText(keyPath);
            StringAssert.Contains("mine", saved);
            StringAssert.Contains("later", saved);
            Assert.AreEqual(good, File.ReadAllText(keyPath + ".bak"));
        }

        [UnityTest]
        public IEnumerator Folders_FailedReadNeverOverwrites()
        {
            const string folders = "{\"Value\":{\"Data\":[{\"Id\":\"0123456789abcdef0123456789abcdef\",\"Name\":\"Mine\",\"CreatedUtcTicks\":1,\"Items\":[]}]}}";
            File.WriteAllText(folderPath, folders);
            using (new FileStream(folderPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                yield return Wait(BasisDataStoreItemFolders.EnsureLoaded());
            }
            yield return Wait(BasisDataStoreItemFolders.CreateFolder("Later"));
            Assert.AreEqual(folders, File.ReadAllText(folderPath));
        }

        [UnityTest]
        public IEnumerator Folders_CorruptFileIsKept()
        {
            File.WriteAllText(folderPath, Bad);
            yield return Wait(BasisDataStoreItemFolders.EnsureLoaded());
            yield return Wait(BasisDataStoreItemFolders.CreateFolder("Later"));
            string kept = Directory.GetFiles(dir, "ItemFolderStore.json.corrupt_*").Single();
            Assert.AreEqual(Bad, File.ReadAllText(kept));
            StringAssert.Contains("Later", File.ReadAllText(folderPath));
        }
    }
}
