using System;
using System.Collections.Generic;
using System.Text;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Shims;
using Cilbox;
using GLTFast;
using GLTFast.Materials;
using GLTFast.Schema;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using GltfMesh = GLTFast.Schema.MeshBase;
using Mesh = UnityEngine.Mesh;
using Material = UnityEngine.Material;
using Texture = UnityEngine.Texture;

namespace Basis.Shims
{
	/// <summary>
	/// Builds models from bytes for world and prop scripts: GLB, glTF, OBJ, STL and PLY, chosen from
	/// the file's contents. Pass <c>this</c> so Basis knows which content is asking. The callback runs
	/// on a later frame. A file never reaches the network: use BasisWebRequestShim to fetch bytes, and
	/// embed anything a glTF needs. Each world or prop shares the triangle and memory budget the player
	/// set in Script Permissions.
	/// </summary>
	public static class BasisModelShim
	{
		public const int MaxFileBytes = 64 * 1024 * 1024;
		public static void Load(object self, byte[] data, Transform parent, Action<BasisModelResult, GameObject> done) => CilboxModels.Load(self, data, parent, done);
		public static void LoadMesh(object self, byte[] data, Action<BasisModelResult, Mesh> done) => CilboxModels.LoadMesh(self, data, done);
		public static bool Unload(object self, GameObject model) => CilboxModels.Unload(self, model);
		public static bool UnloadMesh(object self, Mesh mesh) => CilboxModels.UnloadMesh(self, mesh);
		public static long RemainingTriangles(object self) => CilboxModels.Remaining(self, true);
		public static long RemainingBytes(object self) => CilboxModels.Remaining(self, false);
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxModels
	{
		private const int MaxLoadingPerBox = 2, MaxLoading = 4;
		private sealed class Loaded
		{
			public CilboxBasisCommon box;
			public GameObject root;
			public Mesh mesh;
			public Material material;
			public GltfImport gltf;
			public long triangles, bytes;
			public bool loading;
		}
		private static readonly List<Loaded> loaded = new List<Loaded>();
		internal static bool IsEntryPoint(string name)
		{
			switch (name)
			{
				case nameof(BasisModelShim.Load):
				case nameof(BasisModelShim.LoadMesh):
				case nameof(BasisModelShim.Unload):
				case nameof(BasisModelShim.UnloadMesh):
				case nameof(BasisModelShim.RemainingTriangles):
				case nameof(BasisModelShim.RemainingBytes):
					return true;
				default:
					return false;
			}
		}
		private static long TriangleLimit => (long)BasisSettingsDefaults.ScriptModelTriangles.RawValue * 1000L;
		private static long ByteLimit => (long)BasisSettingsDefaults.ScriptModelMemory.RawValue << 20;
		private static bool Caller(object self, out CilboxProxy proxy, out CilboxBasisCommon box) => BasisSteamAudioShim.TryCaller(self, out proxy, out box) && !(box is CilboxAvatarBasis);
		private static void Used(CilboxBasisCommon box, out long triangles, out long bytes, out int loading)
		{
			triangles = bytes = 0;
			loading = 0;
			foreach (Loaded l in loaded)
			{
				if (l.box != box) continue;
				triangles += l.triangles;
				bytes += l.bytes;
				if (l.loading) loading++;
			}
		}
		internal static long Remaining(object self, bool wantTriangles)
		{
			if (!Caller(self, out _, out CilboxBasisCommon box)) return 0;
			Used(box, out long triangles, out long bytes, out _);
			return Math.Max(0L, wantTriangles ? TriangleLimit - triangles : ByteLimit - bytes);
		}
		internal static void Load(object self, byte[] data, Transform parent, Action<BasisModelResult, GameObject> done)
		{
			if (!Caller(self, out CilboxProxy proxy, out CilboxBasisCommon box))
			{
				Later(self, done == null ? (Action)null : () => done(BasisModelResult.NotAllowed, null));
				return;
			}
			if (parent != null && !BasisSteamAudioShim.SameContent(box, proxy, parent))
			{
				Later(self, done == null ? (Action)null : () => done(BasisModelResult.NotAllowed, null));
				return;
			}
			Begin(proxy, box, data, parent != null ? parent : proxy.transform, true, done == null ? null : (Action<BasisModelResult, Loaded>)((r, l) => done(r, l != null ? l.root : null)));
		}
		internal static void LoadMesh(object self, byte[] data, Action<BasisModelResult, Mesh> done)
		{
			if (!Caller(self, out CilboxProxy proxy, out CilboxBasisCommon box))
			{
				Later(self, done == null ? (Action)null : () => done(BasisModelResult.NotAllowed, null));
				return;
			}
			Begin(proxy, box, data, null, false, done == null ? null : (Action<BasisModelResult, Loaded>)((r, l) => done(r, l != null ? l.mesh : null)));
		}
		internal static bool Unload(object self, GameObject model)
		{
			if (model == null || !Caller(self, out _, out CilboxBasisCommon box)) return false;
			return ReleaseWhere(l => l.box == box && !l.loading && l.root == model);
		}
		internal static bool UnloadMesh(object self, Mesh mesh)
		{
			if (mesh == null || !Caller(self, out _, out CilboxBasisCommon box)) return false;
			return ReleaseWhere(l => l.box == box && !l.loading && l.root == null && l.mesh == mesh);
		}
		private static bool ReleaseWhere(Func<Loaded, bool> match)
		{
			for (int i = loaded.Count - 1; i >= 0; i--)
			{
				if (!match(loaded[i])) continue;
				Loaded l = loaded[i];
				loaded.RemoveAt(i);
				Release(l);
				return true;
			}
			return false;
		}
		private static void Later(object self, Action answer)
		{
			if (answer == null || !(self is CilboxProxy proxy) || proxy == null) return;
			BasisDeviceManagement.EnqueueOnMainThread(() =>
			{
				if (proxy == null) return;
				try { answer(); }
				catch (Exception e) { BasisDebug.LogErrorUnreported($"[Models] A load callback threw: {e}", BasisDebug.LogTag.Shims); }
			});
		}
		private static void Begin(CilboxProxy proxy, CilboxBasisCommon box, byte[] data, Transform parent, bool wantObject, Action<BasisModelResult, Loaded> done)
		{
			Used(box, out long triangles, out long bytes, out int loadingHere);
			int loadingAll = 0;
			foreach (Loaded l in loaded) if (l.loading) loadingAll++;
			BasisModelResult refused = data == null || data.Length == 0 ? BasisModelResult.Invalid : data.Length > BasisModelShim.MaxFileBytes ? BasisModelResult.TooLarge : loadingHere >= MaxLoadingPerBox || loadingAll >= MaxLoading ? BasisModelResult.Busy : BasisModelResult.Ok;
			if (refused != BasisModelResult.Ok)
			{
				Later(proxy, done == null ? (Action)null : () => done(refused, null));
				return;
			}
			Loaded entry = new Loaded { box = box, loading = true };
			loaded.Add(entry);
			BasisFrameClock.OnTick -= Tick;
			BasisFrameClock.OnTick += Tick;
			Run(proxy, box, (byte[])data.Clone(), parent, wantObject, entry, done, TriangleLimit - triangles, ByteLimit - bytes);
		}
		private static async void Run(CilboxProxy proxy, CilboxBasisCommon box, byte[] data, Transform parent, bool wantObject, Loaded entry, Action<BasisModelResult, Loaded> done, long maxTriangles, long maxBytes)
		{
			BasisModelResult result = BasisModelResult.Failed;
			try
			{
				CilboxModelFormat format = CilboxMeshParsers.Detect(data);
				result = format == CilboxModelFormat.Unknown || (!wantObject && (format == CilboxModelFormat.Glb || format == CilboxModelFormat.Gltf)) ? BasisModelResult.Unsupported : await Import(data, format, parent, wantObject, entry, maxTriangles, maxBytes);
			}
			catch (Exception e)
			{
				BasisDebug.LogErrorUnreported($"[Models] A model failed to load: {e.Message}", BasisDebug.LogTag.Shims);
				result = BasisModelResult.Failed;
			}
			entry.loading = false;
			bool alive = proxy != null && box != null && !box.disabled;
			if (result != BasisModelResult.Ok || !alive)
			{
				loaded.Remove(entry);
				Release(entry);
				entry = null;
			}
			if (!alive || done == null) return;
			try { done(result, entry); }
			catch (Exception e) { BasisDebug.LogErrorUnreported($"[Models] A load callback threw: {e}", BasisDebug.LogTag.Shims); }
		}
		private static async System.Threading.Tasks.Task<BasisModelResult> Import(byte[] data, CilboxModelFormat format, Transform parent, bool wantObject, Loaded entry, long maxTriangles, long maxBytes)
		{
			if (format == CilboxModelFormat.Glb || format == CilboxModelFormat.Gltf)
			{
				(BasisModelResult checkedResult, long triangles, long bytes) = await BasisTasks.Run(() =>
				{
					BasisModelResult r = CilboxGltfCheck.Inspect(data, format, maxTriangles, maxBytes, out long t, out long b);
					return (r, t, b);
				});
				if (checkedResult != BasisModelResult.Ok) return checkedResult;
				if (!Claim(entry, triangles, bytes)) return BasisModelResult.TooLarge;
				return await ImportGltf(data, parent, entry);
			}
			(BasisModelResult parsed, MeshArrays arrays) = await BasisTasks.Run(() =>
			{
				BasisModelResult r = CilboxMeshParsers.Parse(data, format, maxTriangles, maxBytes, out CilboxMeshData mesh);
				return (r, r == BasisModelResult.Ok ? new MeshArrays(mesh) : null);
			});
			if (parsed != BasisModelResult.Ok) return parsed;
			if (!Claim(entry, arrays.triangles, arrays.bytes)) return BasisModelResult.TooLarge;
			entry.mesh = arrays.ToMesh();
			if (!wantObject) return BasisModelResult.Ok;
			if (parent == null) return BasisModelResult.Failed;
			entry.root = NewRoot(parent);
			entry.material = new Material(MaterialGenerator.GetDefaultMaterialGenerator().GetDefaultMaterial());
			entry.root.AddComponent<MeshFilter>().sharedMesh = entry.mesh;
			entry.root.AddComponent<MeshRenderer>().sharedMaterial = entry.material;
			entry.root.SetActive(true);
			return BasisModelResult.Ok;
		}
		private static bool Claim(Loaded entry, long triangles, long bytes)
		{
			Used(entry.box, out long usedTriangles, out long usedBytes, out _);
			if (usedTriangles + triangles > TriangleLimit || usedBytes + bytes > ByteLimit) return false;
			entry.triangles = triangles;
			entry.bytes = bytes;
			return true;
		}
		private static GameObject NewRoot(Transform parent)
		{
			GameObject root = new GameObject("Model");
			root.SetActive(false);
			root.layer = parent.gameObject.layer;
			root.transform.Reparent(parent, false);
			return root;
		}
		private static async System.Threading.Tasks.Task<BasisModelResult> ImportGltf(byte[] data, Transform parent, Loaded entry)
		{
			GltfImport gltf = new GltfImport();
			entry.gltf = gltf;
			ImportSettings settings = new ImportSettings { AnimationMethod = AnimationMethod.None, GenerateMipMaps = true, NodeNameMethod = NameImportMethod.Original };
			if (!await gltf.Load(data, null, settings)) return BasisModelResult.Invalid;
			long textureBytes = 0;
			for (int i = 0; i < gltf.TextureCount; i++)
			{
				Texture texture = gltf.GetTexture(i);
				if (texture != null) textureBytes += CilboxGltfCheck.TextureBytes(texture.width, texture.height);
			}
			if (textureBytes > entry.bytes) return BasisModelResult.TooLarge;
			if (parent == null) return BasisModelResult.Failed;
			entry.root = NewRoot(parent);
			InstantiationSettings instantiation = new InstantiationSettings { Mask = ComponentType.Mesh, Layer = parent.gameObject.layer, SceneObjectCreation = SceneObjectCreation.Never };
			GameObjectInstantiator instantiator = new GameObjectInstantiator(gltf, entry.root.transform, null, instantiation);
			if (!await gltf.InstantiateSceneAsync(instantiator, gltf.DefaultSceneIndex ?? 0)) return BasisModelResult.Invalid;
			if (entry.root == null) return BasisModelResult.Failed;
			entry.root.SetActive(true);
			return BasisModelResult.Ok;
		}
		private static void Release(Loaded l)
		{
			if (l.root != null) UnityEngine.Object.Destroy(l.root);
			if (l.mesh != null) UnityEngine.Object.Destroy(l.mesh);
			if (l.material != null) UnityEngine.Object.Destroy(l.material);
			l.gltf?.Dispose();
			l.root = null;
			l.mesh = null;
			l.material = null;
			l.gltf = null;
		}
		private static void Tick()
		{
			for (int i = loaded.Count - 1; i >= 0; i--)
			{
				Loaded l = loaded[i];
				if (l.loading) continue;
				bool ownerGone = l.box == null || l.box.disabled;
				bool wasObject = !ReferenceEquals(l.root, null);
				bool destroyed = wasObject ? l.root == null : ReferenceEquals(l.mesh, null) || l.mesh == null;
				if (!ownerGone && !destroyed) continue;
				loaded.RemoveAt(i);
				Release(l);
			}
			if (loaded.Count == 0) BasisFrameClock.OnTick -= Tick;
		}
		private sealed class MeshArrays
		{
			public readonly long triangles, bytes;
			private readonly Vector3[] positions, normals;
			private readonly Vector2[] uvs;
			private readonly Color[] colors;
			private readonly int[] indices;
			public MeshArrays(CilboxMeshData data)
			{
				triangles = data.Triangles;
				bytes = data.Bytes;
				indices = data.indices;
				positions = Vectors(data.positions);
				normals = data.normals != null ? Vectors(data.normals) : null;
				if (data.uvs != null)
				{
					uvs = new Vector2[data.uvs.Length / 2];
					for (int i = 0; i < uvs.Length; i++) uvs[i] = new Vector2(data.uvs[i * 2], data.uvs[i * 2 + 1]);
				}
				if (data.colors != null)
				{
					colors = new Color[data.colors.Length / 4];
					for (int i = 0; i < colors.Length; i++) colors[i] = new Color(data.colors[i * 4], data.colors[i * 4 + 1], data.colors[i * 4 + 2], data.colors[i * 4 + 3]);
				}
			}
			private static Vector3[] Vectors(float[] flat)
			{
				Vector3[] vectors = new Vector3[flat.Length / 3];
				for (int i = 0; i < vectors.Length; i++) vectors[i] = new Vector3(flat[i * 3], flat[i * 3 + 1], flat[i * 3 + 2]);
				return vectors;
			}
			public Mesh ToMesh()
			{
				Mesh mesh = new Mesh { name = "Model", indexFormat = positions.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
				mesh.SetVertices(positions);
				if (normals != null) mesh.SetNormals(normals);
				if (uvs != null) mesh.SetUVs(0, uvs);
				if (colors != null) mesh.SetColors(colors);
				mesh.SetTriangles(indices, 0);
				if (normals == null) mesh.RecalculateNormals();
				mesh.RecalculateBounds();
				return mesh;
			}
		}
	}

	// glTFast trusts the file: it follows absolute URIs in buffers and images, walks node children
	// recursively, reads accessors through unchecked pointers and uploads indices without
	// validation. Everything here runs before a byte reaches it, against its own schema classes so
	// both sides read the JSON the same way.
	internal static class CilboxGltfCheck
	{
		private const int MaxNodes = 65536, MaxDepth = 256, MaxPrimitives = 16384, MaxMaterials = 4096, MaxTextures = 1024, MaxJoints = 4096;
		private struct Blob { public byte[] data; public int start; public long length; }
		private sealed class Reject : Exception
		{
			public readonly BasisModelResult result;
			public Reject(BasisModelResult result) { this.result = result; }
		}
		private static Exception Bad => new Reject(BasisModelResult.Invalid);
		internal static long TextureBytes(int width, int height) => (long)width * height * 16 / 3;
		internal static BasisModelResult Inspect(byte[] d, CilboxModelFormat format, long maxTriangles, long maxBytes, out long triangles, out long bytes)
		{
			triangles = bytes = 0;
			try
			{
				string json;
				Blob bin = default;
				if (format == CilboxModelFormat.Glb) json = SplitGlb(d, out bin);
				else json = Encoding.UTF8.GetString(d);
				Root root = JsonUtility.FromJson<Root>(json);
				if (root == null) return BasisModelResult.Invalid;
				Inspect(root, bin, maxTriangles, maxBytes, out triangles, out bytes);
				return BasisModelResult.Ok;
			}
			catch (Reject reject) { return reject.result; }
			catch (OutOfMemoryException) { return BasisModelResult.TooLarge; }
			catch (Exception) { return BasisModelResult.Invalid; }
		}
		private static string SplitGlb(byte[] d, out Blob bin)
		{
			bin = default;
			if (d.Length < 20 || BitConverter.ToUInt32(d, 4) != 2 || BitConverter.ToUInt32(d, 8) > d.Length) throw Bad;
			long total = BitConverter.ToUInt32(d, 8), at = 12;
			string json = null;
			bool haveBin = false;
			while (at < total)
			{
				if (at + 8 > total) throw Bad;
				uint length = BitConverter.ToUInt32(d, (int)at), type = BitConverter.ToUInt32(d, (int)at + 4);
				at += 8;
				if (at + length > total) throw Bad;
				if (type == 0x4E4F534A && json == null && !haveBin) json = new System.IO.StreamReader(new System.IO.MemoryStream(d, (int)at, (int)length)).ReadToEnd();
				else if (type == 0x004E4942 && json != null && !haveBin)
				{
					haveBin = true;
					bin = new Blob { data = d, start = (int)at, length = length };
				}
				else throw Bad;
				at += length;
			}
			if (json == null) throw Bad;
			return json;
		}
		private static bool IsDataUri(string uri) => uri.StartsWith("data:", StringComparison.Ordinal);
		private static Blob Decode(string uri)
		{
			int mediaEnd = uri.IndexOf(';', 5, Math.Min(uri.Length - 5, 1000));
			if (mediaEnd < 0 || string.CompareOrdinal(uri, mediaEnd + 1, "base64,", 0, 7) != 0) throw Bad;
			byte[] decoded = Convert.FromBase64String(uri.Substring(mediaEnd + 8));
			return new Blob { data = decoded, length = decoded.Length };
		}
		private static void Inspect(Root root, Blob bin, long maxTriangles, long maxBytes, out long triangles, out long bytes)
		{
			int bufferCount = root.buffers?.Length ?? 0, viewCount = root.bufferViews?.Length ?? 0, accessorCount = root.accessors?.Length ?? 0;
			int nodeCount = root.nodes?.Length ?? 0, meshCount = root.meshes?.Length ?? 0, skinCount = root.skins?.Length ?? 0, imageCount = root.images?.Length ?? 0;
			if (nodeCount > MaxNodes || (root.materials?.Length ?? 0) > MaxMaterials || (root.textures?.Length ?? 0) > MaxTextures) throw new Reject(BasisModelResult.TooLarge);
			Blob[] buffers = new Blob[bufferCount];
			for (int i = 0; i < bufferCount; i++)
			{
				GLTFast.Schema.Buffer buffer = root.buffers[i];
				if (string.IsNullOrEmpty(buffer.uri))
				{
					if (i != 0 || bin.data == null) throw Bad;
					buffers[i] = bin;
				}
				else if (IsDataUri(buffer.uri)) buffers[i] = Decode(buffer.uri);
				else throw new Reject(BasisModelResult.Unsupported);
				if (buffer.byteLength > buffers[i].length) throw Bad;
				buffers[i].length = buffer.byteLength;
			}
			for (int i = 0; i < viewCount; i++)
			{
				BufferViewBase view = root.bufferViews[i];
				if (view.buffer < 0 || view.buffer >= bufferCount || view.byteOffset < 0 || view.byteLength < 0 || (long)view.byteOffset + view.byteLength > buffers[view.buffer].length) throw Bad;
			}
			for (int i = 0; i < accessorCount; i++)
			{
				AccessorBase a = root.accessors[i];
				if (a.Sparse != null && a.Sparse.count > 0) throw new Reject(BasisModelResult.Unsupported);
				if (a.bufferView < 0) throw new Reject(BasisModelResult.Unsupported);
				if (a.bufferView >= viewCount || a.count < 0 || a.byteOffset < 0 || a.GetAttributeType() == GltfAccessorAttributeType.Undefined) throw Bad;
				BufferViewBase view = root.bufferViews[a.bufferView];
				long element = a.ElementByteSize, stride = view.byteStride > 0 ? view.byteStride : element;
				if (stride < element) throw Bad;
				if (a.count > 0 && a.byteOffset + (a.count - 1L) * stride + element > view.byteLength) throw Bad;
			}
			triangles = bytes = 0;
			long[] maxIndex = new long[accessorCount], maxJoint = new long[accessorCount];
			for (int i = 0; i < accessorCount; i++) maxIndex[i] = maxJoint[i] = -2;
			long[] meshTriangles = new long[meshCount], meshJoint = new long[meshCount];
			int primitives = 0;
			for (int m = 0; m < meshCount; m++)
			{
				meshJoint[m] = -1;
				IReadOnlyList<MeshPrimitiveBase> list = ((GltfMesh)root.meshes[m]).Primitives;
				if (list == null) throw Bad;
				foreach (MeshPrimitiveBase p in list)
				{
					if (++primitives > MaxPrimitives) throw new Reject(BasisModelResult.TooLarge);
					Attributes at = p.attributes;
					if (at == null) throw Bad;
					long vertices = Attribute(root, at.POSITION, GltfAccessorAttributeType.VEC3, GltfAccessorAttributeType.VEC3, -1);
					if (vertices < 0) throw Bad;
					Attribute(root, at.NORMAL, GltfAccessorAttributeType.VEC3, GltfAccessorAttributeType.VEC3, vertices);
					Attribute(root, at.TANGENT, GltfAccessorAttributeType.VEC4, GltfAccessorAttributeType.VEC4, vertices);
					foreach (int uv in new[] { at.TEXCOORD_0, at.TEXCOORD_1, at.TEXCOORD_2, at.TEXCOORD_3, at.TEXCOORD_4, at.TEXCOORD_5, at.TEXCOORD_6, at.TEXCOORD_7, at.TEXCOORD_8 }) Attribute(root, uv, GltfAccessorAttributeType.VEC2, GltfAccessorAttributeType.VEC2, vertices);
					Attribute(root, at.COLOR_0, GltfAccessorAttributeType.VEC3, GltfAccessorAttributeType.VEC4, vertices);
					Attribute(root, at.WEIGHTS_0, GltfAccessorAttributeType.VEC4, GltfAccessorAttributeType.VEC4, vertices);
					if (Attribute(root, at.JOINTS_0, GltfAccessorAttributeType.VEC4, GltfAccessorAttributeType.VEC4, vertices) >= 0)
					{
						if (maxJoint[at.JOINTS_0] == -2) maxJoint[at.JOINTS_0] = Largest(root, buffers, at.JOINTS_0, false);
						meshJoint[m] = Math.Max(meshJoint[m], maxJoint[at.JOINTS_0]);
					}
					int targets = p.targets?.Length ?? 0;
					for (int t = 0; t < targets; t++)
					{
						Attribute(root, p.targets[t].POSITION, GltfAccessorAttributeType.VEC3, GltfAccessorAttributeType.VEC3, vertices);
						Attribute(root, p.targets[t].NORMAL, GltfAccessorAttributeType.VEC3, GltfAccessorAttributeType.VEC3, vertices);
						Attribute(root, p.targets[t].TANGENT, GltfAccessorAttributeType.VEC3, GltfAccessorAttributeType.VEC3, vertices);
					}
					long elements = vertices;
					if (p.indices >= 0)
					{
						if (p.indices >= accessorCount) throw Bad;
						AccessorBase ia = root.accessors[p.indices];
						if (ia.GetAttributeType() != GltfAccessorAttributeType.SCALAR) throw Bad;
						if (maxIndex[p.indices] == -2) maxIndex[p.indices] = Largest(root, buffers, p.indices, true);
						if (maxIndex[p.indices] >= vertices) throw Bad;
						elements = ia.count;
						bytes += elements * 4;
					}
					bytes += vertices * (CilboxMeshParsers.BytesPerVertex + targets * 36L);
					long count = p.mode == DrawMode.Triangles ? elements / 3 : p.mode == DrawMode.TriangleStrip || p.mode == DrawMode.TriangleFan ? Math.Max(0L, elements - 2) : elements;
					meshTriangles[m] += count;
					if (bytes > maxBytes) throw new Reject(BasisModelResult.TooLarge);
				}
			}
			int[] parents = new int[nodeCount];
			for (int i = 0; i < nodeCount; i++) parents[i] = -1;
			for (int i = 0; i < nodeCount; i++)
			{
				NodeBase node = root.nodes[i];
				MeshGpuInstancing.Attributes gpu = node.Extensions?.EXT_mesh_gpu_instancing?.attributes;
				if (gpu != null && (gpu.TRANSLATION >= 0 || gpu.ROTATION >= 0 || gpu.SCALE >= 0)) throw new Reject(BasisModelResult.Unsupported);
				if (node.mesh >= meshCount || node.skin >= skinCount) throw Bad;
				if (node.mesh >= 0) triangles += meshTriangles[node.mesh];
				if (triangles > maxTriangles) throw new Reject(BasisModelResult.TooLarge);
				if (node.skin >= 0)
				{
					if (node.mesh < 0) throw Bad;
					if (meshJoint[node.mesh] >= (root.skins[node.skin].joints?.Length ?? 0)) throw Bad;
				}
				if (node.children == null) continue;
				foreach (uint child in node.children)
				{
					if (child >= nodeCount || child == i || parents[child] != -1) throw Bad;
					parents[child] = i;
				}
			}
			for (int i = 0; i < nodeCount; i++)
			{
				int depth = 0;
				for (int at = parents[i]; at != -1; at = parents[at]) if (++depth > MaxDepth) throw Bad;
			}
			for (int i = 0; i < skinCount; i++)
			{
				Skin skin = root.skins[i];
				int joints = skin.joints?.Length ?? 0;
				if (joints > MaxJoints) throw new Reject(BasisModelResult.TooLarge);
				for (int j = 0; j < joints; j++) if (skin.joints[j] >= nodeCount) throw Bad;
				if (skin.skeleton >= nodeCount) throw Bad;
				if (skin.inverseBindMatrices >= 0)
				{
					if (skin.inverseBindMatrices >= accessorCount) throw Bad;
					AccessorBase matrices = root.accessors[skin.inverseBindMatrices];
					if (matrices.GetAttributeType() != GltfAccessorAttributeType.MAT4 || matrices.count < joints) throw Bad;
				}
			}
			int sceneCount = root.scenes?.Length ?? 0;
			if (sceneCount == 0 || root.scene >= sceneCount) throw Bad;
			bool[] seen = new bool[nodeCount];
			for (int s = 0; s < sceneCount; s++)
			{
				Array.Clear(seen, 0, nodeCount);
				uint[] roots = root.scenes[s].nodes;
				if (roots == null) continue;
				foreach (uint n in roots)
				{
					if (n >= nodeCount || parents[n] != -1 || seen[n]) throw Bad;
					seen[n] = true;
				}
			}
			for (int i = 0; i < imageCount; i++)
			{
				string uri = root.images[i].uri;
				if (!string.IsNullOrEmpty(uri) && !IsDataUri(uri)) throw new Reject(BasisModelResult.Unsupported);
			}
			int textureCount = root.textures?.Length ?? 0;
			for (int i = 0; i < textureCount; i++)
			{
				TextureBase texture = root.textures[i];
				if (texture.Extensions?.KHR_texture_basisu != null && texture.Extensions.KHR_texture_basisu.source >= 0) throw new Reject(BasisModelResult.Unsupported);
				if (texture.source < 0) continue;
				if (texture.source >= imageCount) throw Bad;
				ImageSize(root, buffers, texture.source, out int width, out int height);
				bytes += TextureBytes(width, height);
				if (bytes > maxBytes) throw new Reject(BasisModelResult.TooLarge);
			}
		}
		private static long Attribute(Root root, int accessor, GltfAccessorAttributeType a, GltfAccessorAttributeType b, long expected)
		{
			if (accessor < 0) return -1;
			if (root.accessors == null || accessor >= root.accessors.Length) throw Bad;
			AccessorBase found = root.accessors[accessor];
			GltfAccessorAttributeType type = found.GetAttributeType();
			if ((type != a && type != b) || (expected >= 0 && found.count != expected)) throw Bad;
			return found.count;
		}
		private static long Largest(Root root, Blob[] buffers, int accessor, bool tight)
		{
			AccessorBase a = root.accessors[accessor];
			BufferViewBase view = root.bufferViews[a.bufferView];
			int size;
			switch (a.componentType)
			{
				case GltfComponentType.UnsignedByte: size = 1; break;
				case GltfComponentType.UnsignedShort: size = 2; break;
				case GltfComponentType.UnsignedInt when tight: size = 4; break;
				default: throw Bad;
			}
			long element = a.ElementByteSize, stride = view.byteStride > 0 ? view.byteStride : element;
			if (tight && stride != element) throw Bad;
			Blob blob = buffers[view.buffer];
			long at = blob.start + (long)view.byteOffset + a.byteOffset, largest = -1;
			int perElement = (int)(element / size);
			for (long i = 0; i < a.count; i++, at += stride)
			{
				for (int c = 0; c < perElement; c++)
				{
					int o = (int)at + c * size;
					long value = size == 1 ? blob.data[o] : size == 2 ? BitConverter.ToUInt16(blob.data, o) : BitConverter.ToUInt32(blob.data, o);
					if (value > largest) largest = value;
				}
			}
			return largest;
		}
		private static void ImageSize(Root root, Blob[] buffers, int index, out int width, out int height)
		{
			Image image = root.images[index];
			Blob source;
			if (image.bufferView >= 0)
			{
				if (root.bufferViews == null || image.bufferView >= root.bufferViews.Length) throw Bad;
				BufferViewBase view = root.bufferViews[image.bufferView];
				Blob buffer = buffers[view.buffer];
				source = new Blob { data = buffer.data, start = buffer.start + view.byteOffset, length = view.byteLength };
			}
			else if (!string.IsNullOrEmpty(image.uri)) source = Decode(image.uri);
			else throw Bad;
			if (!Png(source, out width, out height) && !Jpeg(source, out width, out height)) throw new Reject(BasisModelResult.Unsupported);
			if (width <= 0 || height <= 0) throw Bad;
		}
		private static uint Big(byte[] d, long at, int size)
		{
			uint value = 0;
			for (int i = 0; i < size; i++) value = value << 8 | d[at + i];
			return value;
		}
		private static bool Png(Blob b, out int width, out int height)
		{
			width = height = 0;
			if (b.length < 24 || Big(b.data, b.start, 4) != 0x89504E47 || Big(b.data, b.start + 12, 4) != 0x49484452) return false;
			uint w = Big(b.data, b.start + 16, 4), h = Big(b.data, b.start + 20, 4);
			if (w > int.MaxValue || h > int.MaxValue) return false;
			width = (int)w;
			height = (int)h;
			return true;
		}
		private static bool Jpeg(Blob b, out int width, out int height)
		{
			width = height = 0;
			if (b.length < 4 || b.data[b.start] != 0xFF || b.data[b.start + 1] != 0xD8) return false;
			long at = b.start + 2, end = b.start + b.length;
			while (at + 4 <= end)
			{
				if (b.data[at] != 0xFF) return false;
				byte marker = b.data[at + 1];
				if (marker == 0xFF)
				{
					at++;
					continue;
				}
				long length = Big(b.data, at + 2, 2);
				bool frame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
				if (frame)
				{
					if (at + 9 > end) return false;
					height = (int)Big(b.data, at + 5, 2);
					width = (int)Big(b.data, at + 7, 2);
					return true;
				}
				if (length < 2) return false;
				at += 2 + length;
			}
			return false;
		}
	}
}
