using System;
using System.Collections.Generic;
using Basis.EventDriver;
using Basis.Scripts.BasisSdk.Interactions;
using Cilbox;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
namespace Basis.Shims
{
	public static class BasisRendererRayShim
	{
		public const int MaxBatchesPerContent = 4, MaxRenderersPerBatch = 63, MaxTrianglesPerBatch = 2000000, MaxRaysPerCall = 65536;
		public const float MaxDistance = 10000f;
		public static int Create(object self) => CilboxRendererRays.Create(self, false);
		public static void Release(object self, int batch) => CilboxRendererRays.Release(self, batch);
		public static bool Add(object self, int batch, Renderer renderer) => CilboxRendererRays.Add(self, batch, renderer);
		public static void Clear(object self, int batch) => CilboxRendererRays.Clear(self, batch);
		public static bool Rebuild(object self, int batch) => CilboxRendererRays.Rebuild(self, batch);
		public static int RendererCount(object self, int batch) => CilboxRendererRays.RendererCount(self, batch);
		public static int TriangleCount(object self, int batch) => CilboxRendererRays.TriangleCount(self, batch);
		public static Renderer GetRenderer(object self, int batch, int index) => CilboxRendererRays.GetRenderer(self, batch, index);
		public static int Raycast(object self, int batch, Vector3[] origins, Vector3[] directions, int count, float maxDistance, float[] distances, Vector3[] points, Vector3[] normals, int[] renderers) => CilboxRendererRays.Raycast(self, batch, origins, directions, count, maxDistance, distances, points, normals, renderers);
	}
	public static class BasisRendererRayGpuShim
	{
		public const int RayStride = BasisRendererRayBatchGpu.RayStride, HitStride = BasisRendererRayBatchGpu.HitStride, MaxRaysPerCall = 1 << 22;
		public static bool IsSupported => BasisRendererRayBatchGpu.Supported;
		public static int Create(object self) => CilboxRendererRays.Create(self, true);
		public static void Release(object self, int batch) => CilboxRendererRays.Release(self, batch);
		public static bool Add(object self, int batch, Renderer renderer) => CilboxRendererRays.Add(self, batch, renderer);
		public static void Clear(object self, int batch) => CilboxRendererRays.Clear(self, batch);
		public static bool Update(object self, int batch) => CilboxRendererRays.UpdateGpu(self, batch);
		public static bool Raycast(object self, int batch, GraphicsBuffer rays, GraphicsBuffer hits, int count) => CilboxRendererRays.RaycastGpu(self, batch, rays, hits, count);
		public static bool Bind(object self, int batch, ComputeShader shader, int kernel) => CilboxRendererRays.BindGpu(self, batch, shader, kernel);
		public static int RendererCount(object self, int batch) => CilboxRendererRays.RendererCount(self, batch);
		public static int TriangleCount(object self, int batch) => CilboxRendererRays.TriangleCount(self, batch);
		public static Renderer GetRenderer(object self, int batch, int index) => CilboxRendererRays.GetRenderer(self, batch, index);
	}
}
namespace Cilbox
{
	internal static partial class CilboxCompute
	{
		internal static GraphicsBuffer NewGraphicsBufferFor(CilboxBasisCommon box, GraphicsBuffer.Target target, int count, int stride)
		{
			if (box == null)
			{
				throw new InvalidOperationException("Basis could not tell which content owns this compute buffer.");
			}
			long bytes = (long)count * stride;
			if (bytes > Basis.Shims.BasisComputeShim.MaxBufferBytes)
			{
				throw new InvalidOperationException("A compute buffer can hold at most " + Basis.Shims.BasisComputeShim.MaxBufferBytes + " bytes; this one asks for " + bytes + ".");
			}
			if (!boxes.Contains(box))
			{
				boxes.Add(box);
			}
			long total = Prune();
			if (box.computeBytes + bytes > Bytes(box))
			{
				throw new InvalidOperationException(CilboxBasisConsent.NameOf(box) + " already holds " + box.computeBytes + " bytes of compute buffers and can hold " + Bytes(box) + ".");
			}
			if (total + bytes > Basis.Shims.BasisComputeShim.TotalBytes)
			{
				throw new InvalidOperationException("All content together already holds " + total + " bytes of compute buffers and can hold " + Basis.Shims.BasisComputeShim.TotalBytes + ".");
			}
			GraphicsBuffer buffer = new GraphicsBuffer(target, count, stride);
			Track(box, buffer, bytes);
			return buffer;
		}
		internal static bool SpendFor(CilboxBasisCommon box, long threads)
		{
			if (Spend(box, threads))
			{
				return true;
			}
			Warn(box);
			return false;
		}
	}
	[NoAutoStaticsCleanup]
	internal static class CilboxRendererRays
	{
		private sealed class Entry
		{
			public CilboxProxy owner;
			public CilboxBasisCommon box;
			public readonly List<Renderer> renderers = new List<Renderer>();
			public NativeArray<ulong> mask;
			public BasisRendererRayBatchGpu gpu;
			public NativeArray<float3> origins, directions, points, normals;
			public NativeArray<float> distances;
			public NativeArray<int> hitRenderers;
			public int triangles;
			public bool stale = true;
			public int RendererCount => gpu != null ? gpu.RendererCount : Live();
			private int Live()
			{
				int live = 0;
				for (int i = 0; i < renderers.Count; i++)
				{
					if (renderers[i] != null)
					{
						live++;
					}
				}
				return live;
			}
			public void Ensure(int count)
			{
				if (origins.IsCreated && origins.Length >= count)
				{
					return;
				}
				Free();
				int size = math.max(256, math.ceilpow2(count));
				origins = new NativeArray<float3>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
				directions = new NativeArray<float3>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
				points = new NativeArray<float3>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
				normals = new NativeArray<float3>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
				distances = new NativeArray<float>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
				hitRenderers = new NativeArray<int>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			}
			private void Free()
			{
				if (origins.IsCreated)
				{
					origins.Dispose();
				}
				if (directions.IsCreated)
				{
					directions.Dispose();
				}
				if (points.IsCreated)
				{
					points.Dispose();
				}
				if (normals.IsCreated)
				{
					normals.Dispose();
				}
				if (distances.IsCreated)
				{
					distances.Dispose();
				}
				if (hitRenderers.IsCreated)
				{
					hitRenderers.Dispose();
				}
			}
			public void Dispose()
			{
				for (int i = 0; i < renderers.Count; i++)
				{
					BasisRendererRayWorld.Release(renderers[i]);
				}
				renderers.Clear();
				gpu?.Dispose();
				if (mask.IsCreated)
				{
					mask.Dispose();
				}
				Free();
			}
		}
		private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
		private static readonly List<int> dead = new List<int>();
		private static readonly Action tickHandler = Tick;
		private static int next = 1;
		private static bool hooked;
		internal static int Count => entries.Count;
		private static bool Caller(object self, out CilboxProxy proxy, out CilboxBasisCommon box)
		{
			proxy = self as CilboxProxy;
			box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			return proxy != null && !proxy.disabled && box != null && !box.disabled && box.interpreterAccountingDepth != 0;
		}
		private static Entry Owned(object self, int handle)
		{
			if (!Caller(self, out _, out CilboxBasisCommon box) || !entries.TryGetValue(handle, out Entry e))
			{
				return null;
			}
			return ReferenceEquals(e.box, box) ? e : null;
		}
		private static Entry OwnedGpu(object self, int handle)
		{
			Entry e = Owned(self, handle);
			return e != null && e.gpu != null ? e : null;
		}
		internal static int Create(object self, bool gpu)
		{
			if (!Caller(self, out CilboxProxy proxy, out CilboxBasisCommon box))
			{
				return 0;
			}
			if (gpu && !BasisRendererRayBatchGpu.Supported)
			{
				return 0;
			}
			int owned = 0;
			foreach (Entry e in entries.Values)
			{
				if (ReferenceEquals(e.box, box))
				{
					owned++;
				}
			}
			if (owned >= Basis.Shims.BasisRendererRayShim.MaxBatchesPerContent)
			{
				return 0;
			}
			Entry entry = new Entry { owner = proxy, box = box };
			if (gpu)
			{
				CilboxBasisCommon charged = box;
				try
				{
					entry.gpu = new BasisRendererRayBatchGpu(null, (target, count, stride) => CilboxCompute.NewGraphicsBufferFor(charged, target, count, stride));
				}
				catch (InvalidOperationException)
				{
					return 0;
				}
			}
			int handle = next++;
			entries.Add(handle, entry);
			if (!hooked)
			{
				hooked = true;
				BasisEventDriver.OnUpdate += tickHandler;
			}
			return handle;
		}
		internal static void Release(object self, int handle)
		{
			if (Owned(self, handle) != null)
			{
				Drop(handle);
			}
		}
		private static int Triangles(Renderer renderer)
		{
			Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
			if (mesh == null)
			{
				return 0;
			}
			long indices = 0;
			for (int s = 0; s < mesh.subMeshCount; s++)
			{
				if (mesh.GetTopology(s) == MeshTopology.Triangles)
				{
					indices += mesh.GetIndexCount(s);
				}
			}
			return (int)math.min(indices / 3, int.MaxValue);
		}
		internal static bool Add(object self, int handle, Renderer renderer)
		{
			Entry e = Owned(self, handle);
			if (e == null || renderer == null || e.RendererCount >= Basis.Shims.BasisRendererRayShim.MaxRenderersPerBatch)
			{
				return false;
			}
			int triangles = Triangles(renderer);
			if (triangles == 0 || e.triangles + triangles > Basis.Shims.BasisRendererRayShim.MaxTrianglesPerBatch)
			{
				return false;
			}
			if (e.gpu != null ? !e.gpu.Add(renderer) : e.renderers.Contains(renderer) || !BasisRendererRayWorld.Acquire(renderer))
			{
				return false;
			}
			if (e.gpu == null)
			{
				e.renderers.Add(renderer);
			}
			e.triangles += triangles;
			e.stale = true;
			return true;
		}
		internal static void Clear(object self, int handle)
		{
			Entry e = Owned(self, handle);
			if (e == null)
			{
				return;
			}
			if (e.gpu != null)
			{
				e.gpu.Clear();
			}
			for (int i = 0; i < e.renderers.Count; i++)
			{
				BasisRendererRayWorld.Release(e.renderers[i]);
			}
			e.renderers.Clear();
			e.triangles = 0;
			e.stale = true;
		}
		internal static bool Rebuild(object self, int handle)
		{
			Entry e = Owned(self, handle);
			if (e == null || e.gpu != null)
			{
				return false;
			}
			BasisRendererRayWorld.Build().Complete();
			e.stale = false;
			return true;
		}
		internal static int RendererCount(object self, int handle) => Owned(self, handle)?.RendererCount ?? 0;
		internal static int TriangleCount(object self, int handle) => Owned(self, handle)?.triangles ?? 0;
		internal static Renderer GetRenderer(object self, int handle, int index)
		{
			Entry e = Owned(self, handle);
			if (e == null)
			{
				return null;
			}
			if (e.gpu != null)
			{
				return e.gpu.GetRenderer(index);
			}
			Renderer r = BasisRendererRayWorld.Batch?.GetRenderer(index);
			return r != null && e.renderers.Contains(r) ? r : null;
		}
		internal static int Raycast(object self, int handle, Vector3[] origins, Vector3[] directions, int count, float maxDistance, float[] distances, Vector3[] points, Vector3[] normals, int[] renderers)
		{
			Entry e = Owned(self, handle);
			if (e == null || e.gpu != null || e.renderers.Count == 0 || BasisRendererRayWorld.Batch == null || origins == null || directions == null || distances == null || count <= 0 || float.IsNaN(maxDistance) || maxDistance <= 0f)
			{
				return 0;
			}
			count = math.min(count, math.min(Basis.Shims.BasisRendererRayShim.MaxRaysPerCall, math.min(origins.Length, math.min(directions.Length, distances.Length))));
			if (points != null)
			{
				count = math.min(count, points.Length);
			}
			if (normals != null)
			{
				count = math.min(count, normals.Length);
			}
			if (renderers != null)
			{
				count = math.min(count, renderers.Length);
			}
			e.Ensure(count);
			NativeArray<Vector3>.Copy(origins, 0, e.origins.Reinterpret<Vector3>(), 0, count);
			NativeArray<Vector3>.Copy(directions, 0, e.directions.Reinterpret<Vector3>(), 0, count);
			float reach = math.min(maxDistance, Basis.Shims.BasisRendererRayShim.MaxDistance);
			BasisRendererRayBatch world = BasisRendererRayWorld.Batch;
			JobHandle built = BasisRendererRayWorld.BuildFor(e.origins, e.directions, count, reach);
			if (!e.mask.IsCreated || e.mask.Length < world.MaskLength)
			{
				if (e.mask.IsCreated)
				{
					e.mask.Dispose();
				}
				e.mask = new NativeArray<ulong>(math.max(1, world.MaskLength + 4), Allocator.Persistent);
			}
			world.FillMask(e.mask, e.renderers);
			world.Raycast(e.origins, e.directions, count, reach, e.distances, e.points, e.normals, e.hitRenderers, e.mask, built).Complete();
			NativeArray<float>.Copy(e.distances, 0, distances, 0, count);
			if (points != null)
			{
				NativeArray<Vector3>.Copy(e.points.Reinterpret<Vector3>(), 0, points, 0, count);
			}
			if (normals != null)
			{
				NativeArray<Vector3>.Copy(e.normals.Reinterpret<Vector3>(), 0, normals, 0, count);
			}
			if (renderers != null)
			{
				NativeArray<int>.Copy(e.hitRenderers, 0, renderers, 0, count);
			}
			int hits = 0;
			for (int i = 0; i < count; i++)
			{
				if (distances[i] >= 0f)
				{
					hits++;
				}
			}
			return hits;
		}
		internal static bool UpdateGpu(object self, int handle)
		{
			Entry e = OwnedGpu(self, handle);
			if (e == null)
			{
				return false;
			}
			long threads = (long)e.gpu.VertexCount + 2L * e.triangles + 1;
			if (!CilboxCompute.SpendFor(e.box, threads))
			{
				return false;
			}
			try
			{
				e.gpu.Update();
			}
			catch (InvalidOperationException)
			{
				return false;
			}
			e.stale = false;
			return e.gpu.Ready;
		}
		private static bool Structured(GraphicsBuffer buffer, int stride) => buffer != null && buffer.IsValid() && buffer.stride == stride && (buffer.target & GraphicsBuffer.Target.Structured) != 0;
		internal static bool RaycastGpu(object self, int handle, GraphicsBuffer rays, GraphicsBuffer hits, int count)
		{
			Entry e = OwnedGpu(self, handle);
			if (e == null || count <= 0 || !Structured(rays, BasisRendererRayBatchGpu.RayStride) || !Structured(hits, BasisRendererRayBatchGpu.HitStride))
			{
				return false;
			}
			if (e.stale && !UpdateGpu(self, handle))
			{
				return false;
			}
			if (!e.gpu.Ready)
			{
				return false;
			}
			count = math.min(count, math.min(Basis.Shims.BasisRendererRayGpuShim.MaxRaysPerCall, math.min(rays.count, hits.count)));
			if (!CilboxCompute.SpendFor(e.box, count))
			{
				return false;
			}
			using UnityEngine.Rendering.CommandBuffer cmd = new UnityEngine.Rendering.CommandBuffer { name = "Cilbox renderer rays" };
			e.gpu.Raycast(cmd, rays, hits, count);
			Graphics.ExecuteCommandBuffer(cmd);
			return true;
		}
		internal static bool BindGpu(object self, int handle, ComputeShader shader, int kernel)
		{
			Entry e = OwnedGpu(self, handle);
			if (e == null || shader == null || kernel < 0)
			{
				return false;
			}
			if (e.stale && !UpdateGpu(self, handle))
			{
				return false;
			}
			if (!e.gpu.Ready)
			{
				return false;
			}
			try
			{
				e.gpu.Bind(shader, kernel);
			}
			catch (ArgumentException)
			{
				return false;
			}
			return true;
		}
		private static void Drop(int handle)
		{
			if (!entries.TryGetValue(handle, out Entry e))
			{
				return;
			}
			entries.Remove(handle);
			e.Dispose();
			if (entries.Count == 0 && hooked)
			{
				hooked = false;
				BasisEventDriver.OnUpdate -= tickHandler;
			}
		}
		private static void Tick()
		{
			foreach (KeyValuePair<int, Entry> pair in entries)
			{
				Entry e = pair.Value;
				if (e.owner == null || e.owner.disabled || !e.owner.isActiveAndEnabled || e.box == null || e.box.disabled)
				{
					dead.Add(pair.Key);
				}
			}
			for (int i = 0; i < dead.Count; i++)
			{
				Drop(dead[i]);
			}
			dead.Clear();
		}
	}
}
