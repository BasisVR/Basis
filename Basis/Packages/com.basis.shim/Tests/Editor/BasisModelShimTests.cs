using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Basis.Scripts.Device_Management;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Shims
{
    public sealed class BasisModelShimTests
    {
        const long Lots = 1L << 40;
        const BindingFlags Everything = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        readonly List<GameObject> hosts = new List<GameObject>();
        [TearDown]
        public void TearDown()
        {
            foreach (GameObject host in hosts) if (host != null) UnityEngine.Object.DestroyImmediate(host);
            hosts.Clear();
            Drain();
        }
        static void Drain()
        {
            while (BasisDeviceManagement.mainThreadActions.TryDequeue(out Action action)) action();
        }
        CilboxBasisCommon Box(Type boxType)
        {
            GameObject host = new GameObject(boxType.Name);
            hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }
        static bool Allowed(CilboxBasisCommon box, string method) => box.CheckTypeAllowed(typeof(BasisModelShim)) && box.CheckMethodAllowed(out MethodInfo _, typeof(BasisModelShim), method, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), method + "()");
        static readonly Assembly Shims = typeof(BasisModelShim).Assembly;
        static object Format(string name) => Enum.Parse(Shims.GetType("Cilbox.CilboxModelFormat"), name);
        static string Detect(byte[] d) => Shims.GetType("Cilbox.CilboxMeshParsers").GetMethod("Detect", Everything).Invoke(null, new object[] { d }).ToString();
        static BasisModelResult Parse(string text, string format, out long triangles, long maxTriangles = Lots) => Parse(Encoding.ASCII.GetBytes(text), format, out triangles, maxTriangles);
        static BasisModelResult Parse(byte[] d, string format, out long triangles, long maxTriangles = Lots)
        {
            object[] args = { d, Format(format), maxTriangles, Lots, null };
            BasisModelResult result = (BasisModelResult)Shims.GetType("Cilbox.CilboxMeshParsers").GetMethod("Parse", Everything).Invoke(null, args);
            triangles = args[4] == null ? 0 : (long)args[4].GetType().GetProperty("Triangles").GetValue(args[4]);
            return result;
        }
        static BasisModelResult Check(byte[] d, string format, out long triangles, long maxTriangles = Lots)
        {
            object[] args = { d, Format(format), maxTriangles, Lots, 0L, 0L };
            BasisModelResult result = (BasisModelResult)Shims.GetType("Cilbox.CilboxGltfCheck").GetMethods(Everything).First(m => m.Name == "Inspect" && m.GetParameters().Length == 6 && m.GetParameters()[0].ParameterType == typeof(byte[])).Invoke(null, args);
            triangles = (long)args[4];
            return result;
        }
        static byte[] Glb(string json, byte[] bin)
        {
            while (json.Length % 4 != 0) json += " ";
            byte[] j = Encoding.ASCII.GetBytes(json);
            List<byte> o = new List<byte>();
            void U(uint v) => o.AddRange(BitConverter.GetBytes(v));
            int binPart = bin != null ? 8 + bin.Length : 0;
            U(0x46546C67);
            U(2);
            U((uint)(12 + 8 + j.Length + binPart));
            U((uint)j.Length);
            U(0x4E4F534A);
            o.AddRange(j);
            if (bin != null)
            {
                U((uint)bin.Length);
                U(0x004E4942);
                o.AddRange(bin);
            }
            return o.ToArray();
        }
        static byte[] TriangleBin()
        {
            List<byte> b = new List<byte>();
            foreach (float f in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }) b.AddRange(BitConverter.GetBytes(f));
            foreach (ushort i in new ushort[] { 0, 1, 2, 0 }) b.AddRange(BitConverter.GetBytes(i));
            return b.ToArray();
        }
        const string Views = "\"buffers\":[{\"byteLength\":44}],\"bufferViews\":[{\"buffer\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":8}]";
        const string Accessors = "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]";
        const string Meshes = "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]";
        const string Scene = "\"scene\":0,\"scenes\":[{\"nodes\":[0]}]";
        static string Gltf(string views = Views, string accessors = Accessors, string meshes = Meshes, string nodes = "\"nodes\":[{\"mesh\":0}]", string scene = Scene) => "{\"asset\":{\"version\":\"2.0\"}," + views + "," + accessors + "," + meshes + "," + nodes + "," + scene + "}";
        static BasisModelResult CheckGlb(string json, out long triangles, long maxTriangles = Lots) => Check(Glb(json, TriangleBin()), "Glb", out triangles, maxTriangles);

        [Test]
        public void WorldsAndPropsReachTheEntryPoints_AvatarsNever()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis) })
            {
                CilboxBasisCommon box = Box(boxType);
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisModelResult)), boxType.Name);
                foreach (string m in new[] { "Load", "LoadMesh", "Unload", "UnloadMesh", "RemainingTriangles", "RemainingBytes" }) Assert.IsTrue(Allowed(box, m), boxType.Name + "." + m);
                Assert.IsFalse(Allowed(box, "Begin"), "helpers must never resolve");
            }
            Assert.IsFalse(Allowed(Box(typeof(CilboxAvatarBasis)), "Load"), "avatars travel with their wearer");
        }
        [Test]
        public void TheShimHasNothingButEntryPoints()
        {
            Assert.IsEmpty(typeof(BasisModelShim).GetNestedTypes(Everything));
            foreach (MethodInfo m in typeof(BasisModelShim).GetMethods(Everything)) Assert.IsTrue(m.IsPublic && m.IsStatic, m.Name);
            foreach (FieldInfo f in typeof(BasisModelShim).GetFields(Everything)) Assert.IsTrue(f.IsLiteral, f.Name);
        }
        [Test]
        public void CallersThatAreNotRunningScriptsGetNothing()
        {
            Assert.AreEqual(0L, BasisModelShim.RemainingTriangles(null));
            Assert.AreEqual(0L, BasisModelShim.RemainingBytes(new object()));
            Assert.IsFalse(BasisModelShim.Unload(null, null));
            Assert.IsFalse(BasisModelShim.UnloadMesh("not a script", null));
            BasisModelShim.Load(null, new byte[8], null, (r, o) => Assert.Fail("nobody to answer"));
            Drain();
        }
        [Test]
        public void FormatsAreDetectedFromContents()
        {
            Assert.AreEqual("Glb", Detect(Glb("{}", null)));
            Assert.AreEqual("Gltf", Detect(Encoding.ASCII.GetBytes("  {\"asset\":{}}")));
            Assert.AreEqual("Obj", Detect(Encoding.ASCII.GetBytes("# made by hand\nv 0 0 0\n")));
            Assert.AreEqual("Ply", Detect(Encoding.ASCII.GetBytes("ply\nformat ascii 1.0\n")));
            Assert.AreEqual("Stl", Detect(Encoding.ASCII.GetBytes("solid a\nfacet normal 0 0 1\n")));
            Assert.AreEqual("Stl", Detect(new byte[84]));
            Assert.AreEqual("Unknown", Detect(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0")));
            Assert.AreEqual("Unknown", Detect(new byte[2]));
        }
        [Test]
        public void ObjFansQuadsAndResolvesRelativeIndices()
        {
            Assert.AreEqual(BasisModelResult.Ok, Parse("v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n", "Obj", out long quad));
            Assert.AreEqual(2, quad);
            Assert.AreEqual(BasisModelResult.Ok, Parse("v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvn 0 0 1\nf -3/1/1 -2/1/1 -1/1/1\n", "Obj", out long relative));
            Assert.AreEqual(1, relative);
        }
        [TestCase("v 0 0 0\nf 1 1 9\n")]
        [TestCase("v 0 0 0\nf 0 0 0\n")]
        [TestCase("v NaN 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n")]
        [TestCase("v 0 0\n")]
        [TestCase("v 0 0 0\n")]
        public void MalformedObjIsRejected(string text) => Assert.AreEqual(BasisModelResult.Invalid, Parse(text, "Obj", out _));
        [Test]
        public void BudgetStopsAParse()
        {
            Assert.AreEqual(BasisModelResult.TooLarge, Parse("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\nf 1 2 3\n", "Obj", out _, 1));
        }
        [Test]
        public void StlBothEncodings()
        {
            Assert.AreEqual(BasisModelResult.Ok, Parse("solid a\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid a\n", "Stl", out long ascii));
            Assert.AreEqual(1, ascii);
            byte[] binary = new byte[84 + 50];
            BitConverter.GetBytes(1u).CopyTo(binary, 80);
            Assert.AreEqual(BasisModelResult.Ok, Parse(binary, "Stl", out long one));
            Assert.AreEqual(1, one);
        }
        [Test]
        public void PlyAsciiAndBinary()
        {
            const string header = "element vertex 3\nproperty float x\nproperty float y\nproperty float z\nelement face 1\nproperty list uchar int vertex_indices\nend_header\n";
            Assert.AreEqual(BasisModelResult.Ok, Parse("ply\nformat ascii 1.0\n" + header + "0 0 0\n1 0 0\n0 1 0\n3 0 1 2\n", "Ply", out long ascii));
            Assert.AreEqual(1, ascii);
            List<byte> b = new List<byte>(Encoding.ASCII.GetBytes("ply\nformat binary_little_endian 1.0\n" + header));
            foreach (float f in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }) b.AddRange(BitConverter.GetBytes(f));
            b.Add(3);
            foreach (int i in new[] { 0, 1, 2 }) b.AddRange(BitConverter.GetBytes(i));
            Assert.AreEqual(BasisModelResult.Ok, Parse(b.ToArray(), "Ply", out long binary));
            Assert.AreEqual(1, binary);
            Assert.AreEqual(BasisModelResult.Invalid, Parse("ply\nformat ascii 1.0\n" + header + "0 0 0\n1 0 0\n0 1 0\n3 0 1 9\n", "Ply", out _));
            Assert.AreEqual(BasisModelResult.Invalid, Parse("ply\nformat ascii 1.0\n" + header + "0 0 0\n", "Ply", out _));
        }
        [Test]
        public void AValidGlbPasses_AndCountsTriangles()
        {
            Assert.AreEqual(BasisModelResult.Ok, CheckGlb(Gltf(), out long triangles));
            Assert.AreEqual(1, triangles);
            Assert.AreEqual(BasisModelResult.TooLarge, CheckGlb(Gltf(nodes: "\"nodes\":[{\"mesh\":0},{\"mesh\":0}]"), out _, 1), "each node drawing a mesh costs its triangles");
        }
        [TestCase("https://example.com/a.bin")]
        [TestCase("file:///C:/Windows/win.ini")]
        [TestCase("a.bin")]
        public void BuffersOutsideTheFileAreRefused(string uri)
        {
            string views = Views.Replace("{\"byteLength\":44}", "{\"byteLength\":44,\"uri\":\"" + uri + "\"}");
            Assert.AreEqual(BasisModelResult.Unsupported, CheckGlb(Gltf(views), out _));
        }
        [Test]
        public void ImagesOutsideTheFileAreRefused()
        {
            string json = Gltf().TrimEnd('}') + ",\"images\":[{\"uri\":\"https://example.com/track.png\"}]}";
            Assert.AreEqual(BasisModelResult.Unsupported, CheckGlb(json, out _));
        }
        [TestCase("\"nodes\":[{\"children\":[0]}]", "\"scene\":0,\"scenes\":[{\"nodes\":[]}]", TestName = "A node that is its own child")]
        [TestCase("\"nodes\":[{\"children\":[1]},{\"children\":[0]}]", "\"scene\":0,\"scenes\":[{\"nodes\":[]}]", TestName = "A cycle out of the scene's reach")]
        [TestCase("\"nodes\":[{\"children\":[2]},{\"children\":[2]},{}]", Scene, TestName = "A node with two parents")]
        [TestCase("\"nodes\":[{\"children\":[9]}]", Scene, TestName = "A child out of range")]
        [TestCase("\"nodes\":[{\"mesh\":9}]", Scene, TestName = "A mesh out of range")]
        [TestCase("\"nodes\":[{}]", "\"scene\":0,\"scenes\":[{\"nodes\":[4]}]", TestName = "A scene root out of range")]
        [TestCase("\"nodes\":[{}]", "\"scene\":3,\"scenes\":[{\"nodes\":[0]}]", TestName = "A main scene out of range")]
        public void BadHierarchiesAreRefused(string nodes, string scene) => Assert.AreEqual(BasisModelResult.Invalid, CheckGlb(Gltf(nodes: nodes, scene: scene), out _));
        [Test]
        public void AccessorsMustStayInsideTheirBuffers()
        {
            Assert.AreEqual(BasisModelResult.Invalid, CheckGlb(Gltf(Views.Replace("\"byteLength\":44", "\"byteLength\":4000")), out _), "buffer claims more than the file has");
            Assert.AreEqual(BasisModelResult.Invalid, CheckGlb(Gltf(Views.Replace("\"byteLength\":36}", "\"byteLength\":3600}")), out _), "view runs off the buffer");
            Assert.AreEqual(BasisModelResult.Invalid, CheckGlb(Gltf(accessors: Accessors.Replace("\"count\":3,\"type\":\"VEC3\"", "\"count\":300,\"type\":\"VEC3\"")), out _), "accessor runs off the view");
            Assert.AreEqual(BasisModelResult.Invalid, CheckGlb(Gltf(accessors: Accessors.Replace("\"bufferView\":0,", "\"bufferView\":0,\"byteOffset\":-4,")), out _), "negative offset");
        }
        [Test]
        public void IndicesPastTheVerticesAreRefused()
        {
            string accessors = Accessors.Replace("\"count\":3,\"type\":\"VEC3\"", "\"count\":2,\"type\":\"VEC3\"");
            Assert.AreEqual(BasisModelResult.Invalid, CheckGlb(Gltf(accessors: accessors), out _), "index 2 with only two vertices");
        }
        [Test]
        public void AttributesMustMatchTheVertexCount()
        {
            string accessors = Accessors.Replace("]", ",{\"bufferView\":0,\"componentType\":5126,\"count\":1,\"type\":\"VEC3\"}]");
            string meshes = Meshes.Replace("\"POSITION\":0", "\"POSITION\":0,\"NORMAL\":2");
            Assert.AreEqual(BasisModelResult.Invalid, CheckGlb(Gltf(accessors: accessors, meshes: meshes), out _));
        }
        [Test]
        public void GpuInstancingAndSparseAreRefused()
        {
            Assert.AreEqual(BasisModelResult.Unsupported, CheckGlb(Gltf(nodes: "\"nodes\":[{\"mesh\":0,\"extensions\":{\"EXT_mesh_gpu_instancing\":{\"attributes\":{\"TRANSLATION\":0}}}}]"), out _));
            Assert.AreEqual(BasisModelResult.Unsupported, CheckGlb(Gltf(accessors: Accessors.Replace("\"bufferView\":0,", "\"bufferView\":0,\"sparse\":{\"count\":1},")), out _));
            Assert.AreEqual(BasisModelResult.Unsupported, CheckGlb(Gltf(accessors: Accessors.Replace("\"bufferView\":0,", "")), out _), "an accessor with no view is zeros of any size");
        }
        [Test]
        public void BrokenContainersAreRefused()
        {
            byte[] glb = Glb(Gltf(), TriangleBin());
            BitConverter.GetBytes(3u).CopyTo(glb, 4);
            Assert.AreEqual(BasisModelResult.Invalid, Check(glb, "Glb", out _), "version 3");
            glb = Glb(Gltf(), TriangleBin());
            BitConverter.GetBytes(int.MaxValue).CopyTo(glb, 12);
            Assert.AreEqual(BasisModelResult.Invalid, Check(glb, "Glb", out _), "chunk longer than the file");
            Assert.AreEqual(BasisModelResult.Invalid, Check(Encoding.ASCII.GetBytes("{ not json"), "Gltf", out _));
        }
    }
}
