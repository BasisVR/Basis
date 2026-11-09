using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

public class BasisContentKeyUnityTests
{
    private const string Password = "unity side creator password";
    private sealed class Piece { public byte[] Blob, Plain; }
    private sealed class Bee { public byte[] File, Connector; public List<Piece> Pieces = new List<Piece>(); public List<byte[]> Salts = new List<byte[]>(); }

    private static byte[] Bytes(int count)
    {
        byte[] bytes = new byte[count];
        using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        return bytes;
    }
    private static BasisEncryptionWrapper.BasisPassword Secret(string value) => new BasisEncryptionWrapper.BasisPassword { VP = value };

    private static async Task<Bee> MakeBee(params (string Platform, byte[] Bundle, int[] Sidecars, string AssetName)[] sections)
    {
        Bee bee = new Bee();
        List<BasisBundleGenerated> generated = new List<BasisBundleGenerated>();
        List<byte[]> body = new List<byte[]>();
        foreach ((string platform, byte[] bundle, int[] sidecars, string assetName) in sections)
        {
            byte[] blob = await BasisEncryptionWrapper.EncryptToBytesAsync("t", Secret(Password), bundle, new BasisProgressReport());
            bee.Pieces.Add(new Piece { Blob = blob, Plain = bundle });
            List<byte[]> parts = new List<byte[]> { blob };
            List<BasisGraphicsStatePayload> payloads = new List<BasisGraphicsStatePayload>();
            long offset = blob.Length;
            foreach (int size in sidecars)
            {
                byte[] plain = Bytes(size);
                byte[] sidecar = await BasisEncryptionWrapper.EncryptToBytesAsync("t", Secret(Password), plain, new BasisProgressReport());
                bee.Pieces.Add(new Piece { Blob = sidecar, Plain = plain });
                payloads.Add(new BasisGraphicsStatePayload { GraphicsAPI = "Direct3D11", RuntimePlatform = platform, Offset = offset, Length = sidecar.Length, VariantCount = 1 });
                parts.Add(sidecar);
                offset += sidecar.Length;
            }
            generated.Add(new BasisBundleGenerated("hash", "GameObject", assetName ?? "unused.bundle", 0, true, string.Empty, platform, offset) { AssetBundleEndByte = blob.Length, GraphicsStatePayloads = payloads.Count == 0 ? null : payloads.ToArray() });
            body.AddRange(parts);
        }
        BasisBundleConnector connector = new BasisBundleConnector("unity-key-test", new BasisBundleDescription("key test", "key test"), generated.ToArray(), string.Empty, new BasisBounds(Vector3.zero, Vector3.one), new BasisBundleConnector.BasisMetaData());
        byte[] json = BasisSerialization.SerializeValue(connector);
        bee.Connector = await BasisEncryptionWrapper.EncryptToBytesAsync("t", Secret(Password), json, new BasisProgressReport());
        bee.Pieces.Insert(0, new Piece { Blob = bee.Connector, Plain = json });
        foreach (Piece piece in bee.Pieces) bee.Salts.Add(piece.Blob.Take(16).ToArray());
        List<byte[]> file = new List<byte[]> { BitConverter.GetBytes((long)bee.Connector.Length), bee.Connector };
        file.AddRange(body);
        bee.File = file.SelectMany(part => part).ToArray();
        return bee;
    }

    private static string TempBee(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "basis-key-test-" + Guid.NewGuid().ToString("N") + ".bee");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static IEnumerator Await(Task task, float seconds = 60f)
    {
        float deadline = (float)EditorApplication.timeSinceStartup + seconds;
        while (!task.IsCompleted)
        {
            while (BasisDeviceManagement.mainThreadActions.TryDequeue(out Action action)) action();
            if (EditorApplication.timeSinceStartup > deadline) Assert.Fail("timed out");
            yield return null;
        }
        if (task.IsFaulted) throw task.Exception.GetBaseException();
    }

    private static HttpListener Serve(byte[] file, out string url, out Func<int> rangedCount, out Func<int> fullCount)
    {
        TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        int[] counts = new int[2];
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { return; }
                string range = context.Request.Headers["Range"];
                long start = 0, end = file.Length - 1;
                if (range != null && range.StartsWith("bytes="))
                {
                    string[] parts = range.Substring(6).Split('-');
                    start = long.Parse(parts[0]);
                    if (parts[1].Length > 0) end = Math.Min(long.Parse(parts[1]), file.Length - 1);
                    context.Response.StatusCode = 206;
                    context.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{file.Length}");
                    Interlocked.Increment(ref counts[0]);
                }
                else Interlocked.Increment(ref counts[1]);
                context.Response.AddHeader("Accept-Ranges", "bytes");
                context.Response.ContentLength64 = end - start + 1;
                try { await context.Response.OutputStream.WriteAsync(file, (int)start, (int)(end - start + 1)); } catch { }
                context.Response.Close();
            }
        });
        url = $"http://127.0.0.1:{port}/item.bee";
        rangedCount = () => Volatile.Read(ref counts[0]);
        fullCount = () => Volatile.Read(ref counts[1]);
        return listener;
    }

    private static async Task AssertTokenOpensEveryPiece(Bee bee, string token)
    {
        foreach (Piece piece in bee.Pieces)
        {
            BasisEncryptionWrapper.BasisDecryptResult result = await BasisEncryptionWrapper.DecryptFromBytesAsync("t", Secret(token), piece.Blob, new BasisProgressReport());
            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(piece.Plain, result.Data);
        }
    }

    [UnityTest]
    public IEnumerator UnityBuilderReadsALocalBeeAndMatchesTheServerBuilder()
    {
        Task<Bee> made = MakeBee(("StandaloneWindows64", Bytes(40000), new[] { 3000, 1200 }, null), ("Android", Bytes(30000), new[] { 900 }, null), ("Generic", Bytes(20000), new int[0], null));
        yield return Await(made);
        Bee bee = made.Result;
        string path = TempBee(bee.File);
        try
        {
            BasisBeeKeyGrant.Clear();
            Task<(byte[] Grant, string Error)> built = BasisBeeKeyGrant.BuildAsync(path, Password, string.Empty);
            yield return Await(built);
            Assert.IsNull(built.Result.Error);
            CollectionAssert.AreEqual(BasisServerBeeKeys.Derive(Password, bee.Salts), built.Result.Grant, "Unity and the server must build byte-identical keys, or holders would disagree on the commit");
            Assert.AreEqual(bee.Pieces.Count, built.Result.Grant[0]);
            string token = BasisContentKeyProtocol.Token(built.Result.Grant);
            Assert.AreEqual(token, BasisEncryptionWrapper.GrantToken(built.Result.Grant));
            Task check = AssertTokenOpensEveryPiece(bee, token);
            yield return Await(check);
            Task<BasisBundleConnector> connector = BasisEncryptionToData.GenerateMetaFromBytes(token, bee.Connector, new BasisProgressReport());
            yield return Await(connector);
            Assert.AreEqual(3, connector.Result.BasisBundleGenerated.Length);
            Assert.AreEqual(2, connector.Result.BasisBundleGenerated[0].GraphicsStatePayloads.Length);
            Assert.IsTrue(connector.Result.BasisBundleGenerated.All(section => string.IsNullOrEmpty(section.Password)));
            LogAssert.ignoreFailingMessages = true;
            Task<(byte[] Grant, string Error)> wrong = BasisBeeKeyGrant.BuildAsync(path, "not the password", string.Empty);
            yield return Await(wrong);
            LogAssert.ignoreFailingMessages = false;
            Assert.IsNull(wrong.Result.Grant);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [UnityTest]
    public IEnumerator UnityBuilderReadsAnHttpBeeWithRangeRequestsAndMatchesTheServer()
    {
        Task<Bee> made = MakeBee(("StandaloneWindows64", Bytes(50000), new[] { 2000 }, null), ("Android", Bytes(25000), new int[0], null));
        yield return Await(made);
        Bee bee = made.Result;
        HttpListener listener = Serve(bee.File, out string url, out Func<int> ranged, out Func<int> full);
        try
        {
            BasisBeeKeyGrant.Clear();
            Task<(byte[] Grant, string Error)> built = BasisBeeKeyGrant.BuildAsync(url, Password, string.Empty);
            yield return Await(built);
            Assert.IsNull(built.Result.Error);
            Task<(byte[] Grant, string Error)> server = BasisServerBeeKeys.DeriveAsync(url, Password);
            yield return Await(server);
            CollectionAssert.AreEqual(server.Result.Grant, built.Result.Grant);
            Assert.AreEqual(0, full(), "the key builder must not download the whole file");
            Assert.GreaterOrEqual(ranged(), 2 * 5);
            Task check = AssertTokenOpensEveryPiece(bee, BasisContentKeyProtocol.Token(built.Result.Grant));
            yield return Await(check);
        }
        finally
        {
            listener.Stop();
        }
    }

    [UnityTest]
    public IEnumerator ClientShareBuildsOnTheMainThreadAndResolvesItsOwnMarker()
    {
        Task<Bee> made = MakeBee(("StandaloneWindows64", Bytes(30000), new[] { 800 }, null));
        yield return Await(made);
        Bee bee = made.Result;
        string path = TempBee(bee.File);
        try
        {
            BasisContentKeyClient.Reset();
            BasisContentKeyClient.Initialize();
            string marker = BasisContentKeyClient.Share(path, "v1", path, Password);
            Assert.IsTrue(BasisContentKeyProtocol.IsMarker(marker));
            Assert.IsFalse(marker.Contains(Password));
            string tagged = BasisContentKeyClient.Tagged(marker, 12);
            Assert.IsTrue(BasisContentKeyProtocol.TryParseMarker(tagged, out ushort origin, out byte[] contentId));
            Assert.AreEqual(12, origin);
            CollectionAssert.AreEqual(BasisContentKeyProtocol.ContentId(path, "v1"), contentId);
            Assert.AreEqual(tagged, BasisContentKeyClient.Tagged(tagged, 99), "an origin already stamped must not be replaced");
            BasisLoadableBundle bundle = new BasisLoadableBundle { BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = "https://elsewhere.invalid/renamed.bee", RemoteVersionTag = "other" }, UnlockPassword = tagged };
            Task resolved = BasisBeeKeyGrant.EnsureResolvedAsync(bundle, CancellationToken.None);
            yield return Await(resolved);
            Assert.IsTrue(BasisEncryptionWrapper.IsKeyGrant(bundle.UnlockPassword), "the marker carries its own content id, so a receiver whose url or version differs still resolves it");
            CollectionAssert.AreEqual(BasisServerBeeKeys.Derive(Password, bee.Salts), BasisEncryptionWrapper.GrantBytes(bundle.UnlockPassword));
            Task check = AssertTokenOpensEveryPiece(bee, bundle.UnlockPassword);
            yield return Await(check);
            Task<string> plain = BasisContentKeyClient.ResolvePasswordAsync(path, string.Empty, "an ordinary password", CancellationToken.None);
            yield return Await(plain);
            Assert.AreEqual("an ordinary password", plain.Result);
            Task<string> bare = BasisContentKeyClient.ResolvePasswordAsync(path, string.Empty, BasisContentKeyProtocol.Marker(contentId), CancellationToken.None);
            yield return Await(bare);
            Assert.IsNull(bare.Result, "a marker with no origin cannot be asked for");
            string again = BasisContentKeyClient.Share(path, "v1", path, bundle.UnlockPassword);
            Assert.AreEqual(marker, again, "content held only as keys is shared the same way");
        }
        finally
        {
            BasisContentKeyClient.Reset();
            File.Delete(path);
        }
    }

    [Test]
    public void OnAVersion57ServerPasswordsAreSentTheOldWay()
    {
        bool legacy = BasisNetworkManagement.LegacyLogin;
        ushort protocol = BasisNetworkManagement.ProtocolVersion;
        try
        {
            BasisContentKeyClient.Reset();
            BasisNetworkManagement.LegacyLogin = false;
            BasisNetworkManagement.ProtocolVersion = BasisNetworkVersion.PasswordRecordsVersion;
            Assert.IsTrue(BasisNetworkManagement.SendsUnlockPasswords);
            Assert.AreEqual(Password, BasisContentKeyClient.Share("https://example.invalid/a.bee", string.Empty, "https://example.invalid/a.bee", Password));
            BasisNetworkManagement.ProtocolVersion = 0;
            BasisNetworkManagement.LegacyLogin = true;
            Assert.AreEqual(Password, BasisContentKeyClient.Share("https://example.invalid/a.bee", string.Empty, "https://example.invalid/a.bee", Password));
            BasisNetworkManagement.LegacyLogin = false;
            Assert.IsFalse(BasisNetworkManagement.SendsUnlockPasswords);
            Assert.IsTrue(BasisContentKeyProtocol.IsMarker(BasisContentKeyClient.Share("https://example.invalid/a.bee", string.Empty, "https://example.invalid/a.bee", Password)));
        }
        finally
        {
            BasisNetworkManagement.LegacyLogin = legacy;
            BasisNetworkManagement.ProtocolVersion = protocol;
            BasisContentKeyClient.Reset();
        }
    }

    [Test]
    public void EveryLanguageExplainsTheVersion57Server()
    {
        string folder = Path.GetFullPath("Packages/com.basis.framework/BasisUI/Localization/Languages");
        string[] files = Directory.GetFiles(folder, "*.json");
        Assert.GreaterOrEqual(files.Length, 16);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            StringAssert.Contains("\"network.passwordServer.title\"", text, file);
            string body = JsonValueAfter(text, "\"network.passwordServer.body\"");
            Assert.IsNotNull(body, file);
            StringAssert.Contains("{0}", body, file);
            StringAssert.Contains("{1}", body, file);
            if (Path.GetFileNameWithoutExtension(file) == "en")
            {
                string english = string.Format(body, BasisNetworkVersion.PasswordRecordsVersion, BasisNetworkVersion.ServerVersion);
                StringAssert.Contains("v57", english);
                StringAssert.Contains("v58", english);
            }
        }
    }

    private static string JsonValueAfter(string text, string key)
    {
        int at = text.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        at = text.IndexOf("\"value\"", at + key.Length, StringComparison.Ordinal);
        if (at < 0) return null;
        at = text.IndexOf('"', text.IndexOf(':', at) + 1) + 1;
        System.Text.StringBuilder value = new System.Text.StringBuilder();
        for (int i = at; i < text.Length; i++)
        {
            if (text[i] == '\\') { value.Append(text[++i]); continue; }
            if (text[i] == '"') return value.ToString();
            value.Append(text[i]);
        }
        return null;
    }

    [UnityTest]
    public IEnumerator MarkerWithoutKeysFailsToDecryptCleanly()
    {
        Task<Bee> made = MakeBee(("StandaloneWindows64", Bytes(4000), new int[0], null));
        yield return Await(made);
        LogAssert.ignoreFailingMessages = true;
        Task<BasisEncryptionWrapper.BasisDecryptResult> result = BasisEncryptionWrapper.DecryptFromBytesAsync("t", Secret(BasisContentKeyProtocol.Marker(3, BasisContentKeyProtocol.ContentId("x", ""))), made.Result.Connector, new BasisProgressReport());
        yield return Await(result);
        LogAssert.ignoreFailingMessages = false;
        Assert.IsFalse(result.Result.Success);
        StringAssert.Contains("have not been received", result.Result.Message + result.Result.Exception);
    }

    [UnityTest]
    public IEnumerator RealAssetBundleLoadsThroughTheLoaderWithAMarkerForAPassword()
    {
        string folder = "Assets/ZZContentKeyTest";
        string prefabPath = folder + "/zzcontentkeyprop.prefab";
        string output = Path.Combine(Path.GetTempPath(), "basis-key-bundle-" + Guid.NewGuid().ToString("N"));
        HttpListener http = null;
        GameObject instance = null;
        GameObject holder = new GameObject("ZZContentKeyHolder");
        holder.SetActive(false);
        Directory.CreateDirectory(output);
        string cache = Path.Combine(Path.GetTempPath(), "basis-key-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        System.Reflection.PropertyInfo dataPath = typeof(BasisIOManagement).GetProperty(nameof(BasisIOManagement.PersistentDataPath));
        typeof(BasisIOManagement).GetMethod("CacheUnityMainThreadValues", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
        string realPath = BasisIOManagement.PersistentDataPath;
        dataPath.SetValue(null, cache);
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "ZZContentKeyTest");
        try
        {
            GameObject source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.name = "ZZContentKeyCube";
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            UnityEngine.Object.DestroyImmediate(source);
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild { assetBundleName = "zzcontentkeyprop.bundle", assetNames = new[] { prefabPath } } }, BuildAssetBundleOptions.None, target);
            Assert.IsNotNull(manifest, "AssetBundle build failed");
            byte[] bundleBytes = File.ReadAllBytes(Path.Combine(output, "zzcontentkeyprop.bundle"));
            Task<Bee> made = MakeBee((target.ToString(), bundleBytes, new int[0], "zzcontentkeyprop.bundle"));
            yield return Await(made);
            http = Serve(made.Result.File, out string url, out _, out _);
            BasisContentKeyClient.Reset();
            BasisContentKeyClient.Initialize();
            string marker = BasisContentKeyClient.Tagged(BasisContentKeyClient.Share(url, string.Empty, url, Password), 4);
            BasisLoadableBundle bundle = new BasisLoadableBundle
            {
                BasisRemoteBundleEncrypted = new BasisRemoteEncyptedBundle { RemoteBeeFileLocation = url, IsNetworkSourced = true },
                UnlockPassword = marker,
                BasisBundleConnector = new BasisBundleConnector(),
                BasisLocalEncryptedBundle = new BasisStoredEncryptedBundle(),
            };
            Task<GameObject> load = BasisLoadHandler.LoadGameObjectBundle(holder, bundle, false, new BasisProgressReport(), CancellationToken.None, Vector3.zero, Quaternion.identity, Vector3.one, false, BundledContentHolder.Selector.Prop, holder.transform);
            yield return Await(load, 120f);
            instance = load.Result;
            Assert.IsNotNull(instance, "the loader produced nothing");
            Assert.IsNotNull(instance.GetComponent<MeshFilter>(), "the instance is not the bundled cube");
            Assert.IsTrue(BasisEncryptionWrapper.IsKeyGrant(bundle.UnlockPassword), "the loader decrypted with received keys, not a password");
            Task release = BasisLoadHandler.RequestDeIncrementOfBundle(bundle);
            yield return Await(release);
        }
        finally
        {
            BasisContentKeyClient.Reset();
            dataPath.SetValue(null, realPath);
            try { Directory.Delete(cache, true); } catch { }
            if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            UnityEngine.Object.DestroyImmediate(holder);
            AssetDatabase.DeleteAsset(folder);
            try { Directory.Delete(output, true); } catch { }
            http?.Stop();
        }
    }
}
