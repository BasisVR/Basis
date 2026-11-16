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

	internal static class CilboxGltfCheck
	{
		internal static long TextureBytes(int width, int height) => BasisGltfCheck.TextureBytes(width, height);
		internal static BasisModelResult Inspect(byte[] d, CilboxModelFormat format, long maxTriangles, long maxBytes, out long triangles, out long bytes)
		{
			switch (BasisGltfCheck.Inspect(d, format == CilboxModelFormat.Glb, maxTriangles, maxBytes, false, out triangles, out bytes))
			{
				case BasisGltfCheckResult.Ok: return BasisModelResult.Ok;
				case BasisGltfCheckResult.Unsupported: return BasisModelResult.Unsupported;
				case BasisGltfCheckResult.TooLarge: return BasisModelResult.TooLarge;
				default: return BasisModelResult.Invalid;
			}
		}
	}
}
