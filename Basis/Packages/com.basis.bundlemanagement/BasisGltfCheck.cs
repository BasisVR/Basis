using System;
using System.Collections.Generic;
using System.Text;
using GLTFast;
using GLTFast.Schema;
using UnityEngine;
using GltfMesh = GLTFast.Schema.MeshBase;

public enum BasisGltfCheckResult
{
	Ok = 0,
	Invalid = 1,
	Unsupported = 2,
	TooLarge = 3,
}
// glTFast trusts the file: it follows absolute URIs in buffers and images, walks node children
// recursively, reads accessors through unchecked pointers and uploads indices without
// validation. Everything here runs before a byte reaches it, against its own schema classes so
// both sides read the JSON the same way.
public static class BasisGltfCheck
{
	public const long BytesPerVertex = 48;
	private const int MaxNodes = 65536, MaxDepth = 256, MaxPrimitives = 16384, MaxMaterials = 4096, MaxTextures = 1024, MaxJoints = 4096;
	private struct Blob { public byte[] data; public int start; public long length; }
	private sealed class Reject : Exception
	{
		public readonly BasisGltfCheckResult result;
		public Reject(BasisGltfCheckResult result) { this.result = result; }
	}
	private static Exception Bad => new Reject(BasisGltfCheckResult.Invalid);
	public static long TextureBytes(int width, int height) => (long)width * height * 16 / 3;
	public static BasisGltfCheckResult Inspect(byte[] d, bool glb, long maxTriangles, long maxBytes, bool allowSparse, out long triangles, out long bytes)
	{
		triangles = bytes = 0;
		try
		{
			string json;
			Blob bin = default;
			if (glb) json = SplitGlb(d, out bin);
			else json = Encoding.UTF8.GetString(d);
			Root root = JsonUtility.FromJson<Root>(json);
			if (root == null) return BasisGltfCheckResult.Invalid;
			Inspect(root, bin, maxTriangles, maxBytes, allowSparse, out triangles, out bytes);
			return BasisGltfCheckResult.Ok;
		}
		catch (Reject reject) { return reject.result; }
		catch (OutOfMemoryException) { return BasisGltfCheckResult.TooLarge; }
		catch (Exception) { return BasisGltfCheckResult.Invalid; }
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
	private static void Inspect(Root root, Blob bin, long maxTriangles, long maxBytes, bool allowSparse, out long triangles, out long bytes)
	{
		int bufferCount = root.buffers?.Length ?? 0, viewCount = root.bufferViews?.Length ?? 0, accessorCount = root.accessors?.Length ?? 0;
		int nodeCount = root.nodes?.Length ?? 0, meshCount = root.meshes?.Length ?? 0, skinCount = root.skins?.Length ?? 0, imageCount = root.images?.Length ?? 0;
		if (nodeCount > MaxNodes || (root.materials?.Length ?? 0) > MaxMaterials || (root.textures?.Length ?? 0) > MaxTextures) throw new Reject(BasisGltfCheckResult.TooLarge);
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
			else throw new Reject(BasisGltfCheckResult.Unsupported);
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
			bool sparse = IsSparse(a);
			if (sparse && !allowSparse) throw new Reject(BasisGltfCheckResult.Unsupported);
			if (a.bufferView < 0 && !sparse) throw new Reject(BasisGltfCheckResult.Unsupported);
			if (a.bufferView >= viewCount || a.count < 0 || a.byteOffset < 0 || a.GetAttributeType() == GltfAccessorAttributeType.Undefined) throw Bad;
			if (sparse) Sparse(root, buffers, a);
			if (a.bufferView < 0) continue;
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
				if (++primitives > MaxPrimitives) throw new Reject(BasisGltfCheckResult.TooLarge);
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
				bytes += vertices * (BytesPerVertex + targets * 36L);
				long count = p.mode == DrawMode.Triangles ? elements / 3 : p.mode == DrawMode.TriangleStrip || p.mode == DrawMode.TriangleFan ? Math.Max(0L, elements - 2) : elements;
				meshTriangles[m] += count;
				if (bytes > maxBytes) throw new Reject(BasisGltfCheckResult.TooLarge);
			}
		}
		int[] parents = new int[nodeCount];
		for (int i = 0; i < nodeCount; i++) parents[i] = -1;
		for (int i = 0; i < nodeCount; i++)
		{
			NodeBase node = root.nodes[i];
			MeshGpuInstancing.Attributes gpu = node.Extensions?.EXT_mesh_gpu_instancing?.attributes;
			if (gpu != null && (gpu.TRANSLATION >= 0 || gpu.ROTATION >= 0 || gpu.SCALE >= 0)) throw new Reject(BasisGltfCheckResult.Unsupported);
			if (node.mesh >= meshCount || node.skin >= skinCount) throw Bad;
			if (node.mesh >= 0) triangles += meshTriangles[node.mesh];
			if (triangles > maxTriangles) throw new Reject(BasisGltfCheckResult.TooLarge);
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
			if (joints > MaxJoints) throw new Reject(BasisGltfCheckResult.TooLarge);
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
			if (!string.IsNullOrEmpty(uri) && !IsDataUri(uri)) throw new Reject(BasisGltfCheckResult.Unsupported);
		}
		int textureCount = root.textures?.Length ?? 0;
		for (int i = 0; i < textureCount; i++)
		{
			TextureBase texture = root.textures[i];
			if (texture.Extensions?.KHR_texture_basisu != null && texture.Extensions.KHR_texture_basisu.source >= 0) throw new Reject(BasisGltfCheckResult.Unsupported);
			if (texture.source < 0) continue;
			if (texture.source >= imageCount) throw Bad;
			ImageSize(root, buffers, texture.source, out int width, out int height);
			bytes += TextureBytes(width, height);
			if (bytes > maxBytes) throw new Reject(BasisGltfCheckResult.TooLarge);
		}
	}
	private static bool IsSparse(AccessorBase a) => a.Sparse != null && a.Sparse.count > 0;
	private static void Sparse(Root root, Blob[] buffers, AccessorBase a)
	{
		AccessorSparseBase s = a.Sparse;
		AccessorSparseIndices indices = s.Indices;
		AccessorSparseValues values = s.Values;
		int viewCount = root.bufferViews?.Length ?? 0;
		if (indices == null || values == null || s.count > a.count || indices.bufferView >= viewCount || values.bufferView >= viewCount || indices.byteOffset < 0 || values.byteOffset < 0) throw Bad;
		int size;
		switch (indices.componentType)
		{
			case GltfComponentType.UnsignedByte: size = 1; break;
			case GltfComponentType.UnsignedShort: size = 2; break;
			case GltfComponentType.UnsignedInt: size = 4; break;
			default: throw Bad;
		}
		BufferViewBase indexView = root.bufferViews[indices.bufferView], valueView = root.bufferViews[values.bufferView];
		if ((long)indices.byteOffset + (long)s.count * size > indexView.byteLength || (long)values.byteOffset + s.count * a.ElementByteSize > valueView.byteLength) throw Bad;
		Blob blob = buffers[indexView.buffer];
		long at = blob.start + (long)indexView.byteOffset + indices.byteOffset;
		for (long i = 0; i < s.count; i++, at += size)
		{
			long value = size == 1 ? blob.data[at] : size == 2 ? BitConverter.ToUInt16(blob.data, (int)at) : BitConverter.ToUInt32(blob.data, (int)at);
			if (value >= a.count) throw Bad;
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
		if (IsSparse(a) || a.bufferView < 0) throw new Reject(BasisGltfCheckResult.Unsupported);
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
		if (!Png(source, out width, out height) && !Jpeg(source, out width, out height)) throw new Reject(BasisGltfCheckResult.Unsupported);
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
