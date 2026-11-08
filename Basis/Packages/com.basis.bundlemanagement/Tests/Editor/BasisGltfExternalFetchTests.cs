using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using GLTFast;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class BasisGltfExternalFetchTests
{
    private TcpListener listener;
    private int port, connections;
    [SetUp]
    public void SetUp()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        connections = 0;
    }
    [TearDown]
    public void TearDown()
    {
        listener.Stop();
    }
    private void DrainConnections()
    {
        while (listener.Pending())
        {
            using TcpClient client = listener.AcceptTcpClient();
            connections++;
        }
    }
    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);
    private string BufferUriGltf => "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":12,\"uri\":\"http://127.0.0.1:" + port + "/probe.bin\"}],\"bufferViews\":[{\"buffer\":0,\"byteLength\":12}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":1,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[0,0,0]}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],\"nodes\":[{\"mesh\":0}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
    private string ImageUriGltf => "{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"http://127.0.0.1:" + port + "/probe.png\"}],\"textures\":[{\"source\":0}],\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}],\"nodes\":[{\"name\":\"n\"}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
    private const string DataUriGltf = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":12,\"uri\":\"data:application/octet-stream;base64,AAAAAAAAAAAAAAAA\"}],\"bufferViews\":[{\"buffer\":0,\"byteLength\":12}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":1,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[0,0,0]}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],\"nodes\":[{\"mesh\":0}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
    private IEnumerator Import(string json, Action<bool> done)
    {
        GltfImport gltf = new GltfImport(BasisGltfNoDownloadProvider.Instance, new UninterruptedDeferAgent());
        Task<bool> task = gltf.Load(Json(json), null, new ImportSettings { AnimationMethod = AnimationMethod.None });
        float deadline = Time.realtimeSinceStartup + 10f;
        while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
        {
            DrainConnections();
            yield return null;
        }
        for (int frame = 0; frame < 10; frame++)
        {
            DrainConnections();
            yield return null;
        }
        Assert.IsTrue(task.IsCompleted, "import did not finish");
        done(task.Result);
        gltf.Dispose();
    }
    [UnityTest]
    public IEnumerator ExternalBufferUri_MakesNoConnection()
    {
        LogAssert.ignoreFailingMessages = true;
        bool loaded = true;
        yield return Import(BufferUriGltf, result => loaded = result);
        Assert.AreEqual(0, connections);
        Assert.IsFalse(loaded);
    }
    [UnityTest]
    public IEnumerator ExternalImageUri_MakesNoConnection()
    {
        LogAssert.ignoreFailingMessages = true;
        yield return Import(ImageUriGltf, _ => { });
        Assert.AreEqual(0, connections);
    }
    [UnityTest]
    public IEnumerator DataUriBuffer_StillLoads()
    {
        bool loaded = false;
        yield return Import(DataUriGltf, result => loaded = result);
        Assert.IsTrue(loaded);
        Assert.AreEqual(0, connections);
    }
}
