using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text;

namespace Basis.Shims
{
	public enum BasisModelResult
	{
		Ok = 0,
		Invalid = 1,
		Unsupported = 2,
		TooLarge = 3,
		Busy = 4,
		NotAllowed = 5,
		Failed = 6,
	}
}

namespace Cilbox
{
	using Basis.Shims;

	internal enum CilboxModelFormat { Unknown, Glb, Gltf, Obj, Stl, Ply }

	internal sealed class CilboxMeshData
	{
		public float[] positions, normals, uvs, colors;
		public int[] indices;
		public long Triangles => indices.Length / 3;
		public long Bytes => (long)positions.Length / 3 * CilboxMeshParsers.BytesPerVertex + (long)indices.Length * 4;
	}

	internal static class CilboxMeshParsers
	{
		internal const long BytesPerVertex = 48;
		private sealed class OverBudget : Exception { }
		private sealed class Malformed : Exception { }
		private sealed class Builder
		{
			public readonly List<float> positions = new List<float>(), normals = new List<float>(), uvs = new List<float>(), colors = new List<float>();
			public readonly List<int> indices = new List<int>();
			private readonly long maxTriangles, maxBytes;
			public Builder(long maxTriangles, long maxBytes) { this.maxTriangles = maxTriangles; this.maxBytes = maxBytes; }
			public void Check()
			{
				if (indices.Count / 3 > maxTriangles || (long)positions.Count / 3 * BytesPerVertex + (long)indices.Count * 4 > maxBytes) throw new OverBudget();
			}
			public void Triangle(int a, int b, int c)
			{
				indices.Add(a);
				indices.Add(c);
				indices.Add(b);
				Check();
			}
			public void Position(float x, float y, float z)
			{
				positions.Add(Finite(-x));
				positions.Add(Finite(y));
				positions.Add(Finite(z));
				Check();
			}
			public CilboxMeshData Build()
			{
				int vertices = positions.Count / 3;
				if (indices.Count == 0) throw new Malformed();
				foreach (int i in indices) if (i < 0 || i >= vertices) throw new Malformed();
				return new CilboxMeshData
				{
					positions = positions.ToArray(),
					indices = indices.ToArray(),
					normals = normals.Count == positions.Count ? normals.ToArray() : null,
					uvs = uvs.Count == vertices * 2 ? uvs.ToArray() : null,
					colors = colors.Count == vertices * 4 ? colors.ToArray() : null,
				};
			}
		}
		private static float Finite(float v)
		{
			if (float.IsNaN(v) || float.IsInfinity(v)) throw new Malformed();
			return v;
		}
		internal static CilboxModelFormat Detect(byte[] d)
		{
			if (d == null || d.Length < 4) return CilboxModelFormat.Unknown;
			if (d[0] == (byte)'g' && d[1] == (byte)'l' && d[2] == (byte)'T' && d[3] == (byte)'F') return CilboxModelFormat.Glb;
			if (IsBinaryStl(d)) return CilboxModelFormat.Stl;
			int start = d.Length > 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF ? 3 : 0;
			while (start < d.Length && IsSpace(d[start])) start++;
			if (start < d.Length && d[start] == (byte)'{') return CilboxModelFormat.Gltf;
			if (Starts(d, start, "ply") && start + 3 < d.Length && IsSpace(d[start + 3])) return CilboxModelFormat.Ply;
			if (Starts(d, start, "solid") && new ReadOnlySpan<byte>(d, start, Math.Min(d.Length - start, 4096)).IndexOf(Encoding.ASCII.GetBytes("facet")) >= 0) return CilboxModelFormat.Stl;
			int end = Math.Min(d.Length, 1 << 20);
			for (int i = start; i < end - 1;)
			{
				if (d[i] == (byte)'v' && (d[i + 1] == (byte)' ' || d[i + 1] == (byte)'\t')) return CilboxModelFormat.Obj;
				while (i < end && d[i] != (byte)'\n') i++;
				i++;
			}
			return CilboxModelFormat.Unknown;
		}
		internal static BasisModelResult Parse(byte[] d, CilboxModelFormat format, long maxTriangles, long maxBytes, out CilboxMeshData mesh)
		{
			mesh = null;
			try
			{
				Builder b = new Builder(maxTriangles, maxBytes);
				switch (format)
				{
					case CilboxModelFormat.Obj: Obj(d, b); break;
					case CilboxModelFormat.Stl: if (IsBinaryStl(d)) BinaryStl(d, b); else AsciiStl(d, b); break;
					case CilboxModelFormat.Ply: Ply(d, b); break;
					default: return BasisModelResult.Unsupported;
				}
				mesh = b.Build();
				return BasisModelResult.Ok;
			}
			catch (OverBudget) { return BasisModelResult.TooLarge; }
			catch (Malformed) { return BasisModelResult.Invalid; }
			catch (IndexOutOfRangeException) { return BasisModelResult.Invalid; }
			catch (ArgumentException) { return BasisModelResult.Invalid; }
			catch (OutOfMemoryException) { return BasisModelResult.TooLarge; }
		}
		private static bool IsSpace(byte c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';
		private static bool Starts(byte[] d, int at, string text)
		{
			if (at + text.Length > d.Length) return false;
			for (int i = 0; i < text.Length; i++) if (d[at + i] != (byte)text[i]) return false;
			return true;
		}
		private static bool IsBinaryStl(byte[] d) => d.Length >= 84 && 84L + 50L * BitConverter.ToUInt32(d, 80) == d.Length;
		private static ReadOnlySpan<byte> Token(byte[] d, ref int at, int end)
		{
			while (at < end && IsSpace(d[at])) at++;
			int start = at;
			while (at < end && !IsSpace(d[at])) at++;
			return new ReadOnlySpan<byte>(d, start, at - start);
		}
		private static float Float(ReadOnlySpan<byte> t)
		{
			if (!Utf8Parser.TryParse(t, out float v, out int used) || used != t.Length) throw new Malformed();
			return Finite(v);
		}
		private static float Float(byte[] d, ref int at, int end) => Float(Token(d, ref at, end));
		private static bool Is(ReadOnlySpan<byte> t, string s)
		{
			if (t.Length != s.Length) return false;
			for (int i = 0; i < s.Length; i++) if (t[i] != (byte)s[i]) return false;
			return true;
		}
		private static void Obj(byte[] d, Builder b)
		{
			List<float> positions = new List<float>(), colors = new List<float>(), uvs = new List<float>(), normals = new List<float>();
			Dictionary<(int, int, int), int> welded = new Dictionary<(int, int, int), int>();
			List<int> face = new List<int>();
			bool allNormals = true, allUvs = true;
			for (int at = 0, next; at < d.Length; at = next)
			{
				int end = Array.IndexOf(d, (byte)'\n', at);
				if (end < 0) end = d.Length;
				next = end + 1;
				int cursor = at;
				ReadOnlySpan<byte> key = Token(d, ref cursor, end);
				if (Is(key, "v"))
				{
					positions.Add(Float(d, ref cursor, end));
					positions.Add(Float(d, ref cursor, end));
					positions.Add(Float(d, ref cursor, end));
					ReadOnlySpan<byte> red = Token(d, ref cursor, end);
					if (red.Length == 0) colors.AddRange(new[] { 1f, 1f, 1f });
					else
					{
						colors.Add(Float(red));
						colors.Add(Float(d, ref cursor, end));
						colors.Add(Float(d, ref cursor, end));
					}
				}
				else if (Is(key, "vt"))
				{
					uvs.Add(Float(d, ref cursor, end));
					uvs.Add(Float(d, ref cursor, end));
				}
				else if (Is(key, "vn"))
				{
					normals.Add(Float(d, ref cursor, end));
					normals.Add(Float(d, ref cursor, end));
					normals.Add(Float(d, ref cursor, end));
				}
				else if (Is(key, "f"))
				{
					face.Clear();
					for (ReadOnlySpan<byte> t = Token(d, ref cursor, end); t.Length > 0; t = Token(d, ref cursor, end))
					{
						int p = Reference(ref t, positions.Count / 3), u = -1, n = -1;
						if (t.Length > 0 && t[0] == (byte)'/')
						{
							t = t.Slice(1);
							if (t.Length > 0 && t[0] != (byte)'/') u = Reference(ref t, uvs.Count / 2);
							if (t.Length > 0 && t[0] == (byte)'/')
							{
								t = t.Slice(1);
								n = Reference(ref t, normals.Count / 3);
							}
						}
						if (t.Length != 0) throw new Malformed();
						allUvs &= u >= 0;
						allNormals &= n >= 0;
						if (!welded.TryGetValue((p, u, n), out int vertex))
						{
							vertex = welded.Count;
							welded.Add((p, u, n), vertex);
							b.Position(positions[p * 3], positions[p * 3 + 1], positions[p * 3 + 2]);
							b.colors.Add(colors[p * 3]);
							b.colors.Add(colors[p * 3 + 1]);
							b.colors.Add(colors[p * 3 + 2]);
							b.colors.Add(1f);
							b.uvs.Add(u >= 0 ? uvs[u * 2] : 0f);
							b.uvs.Add(u >= 0 ? uvs[u * 2 + 1] : 0f);
							b.normals.Add(n >= 0 ? -normals[n * 3] : 0f);
							b.normals.Add(n >= 0 ? normals[n * 3 + 1] : 0f);
							b.normals.Add(n >= 0 ? normals[n * 3 + 2] : 0f);
						}
						face.Add(vertex);
					}
					if (face.Count < 3) throw new Malformed();
					for (int i = 2; i < face.Count; i++) b.Triangle(face[0], face[i - 1], face[i]);
				}
			}
			if (!allNormals) b.normals.Clear();
			if (!allUvs) b.uvs.Clear();
			bool coloured = false;
			foreach (float c in colors) if (c != 1f) { coloured = true; break; }
			if (!coloured) b.colors.Clear();
		}
		private static int Reference(ref ReadOnlySpan<byte> t, int count)
		{
			if (!Utf8Parser.TryParse(t, out int v, out int used) || used == 0) throw new Malformed();
			t = t.Slice(used);
			int resolved = v > 0 ? v - 1 : count + v;
			if (v == 0 || resolved < 0 || resolved >= count) throw new Malformed();
			return resolved;
		}
		private static float LittleFloat(byte[] d, int at) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(d, at, 4)));
		private static void BinaryStl(byte[] d, Builder b)
		{
			for (int at = 84; at + 50 <= d.Length; at += 50)
			{
				int first = b.positions.Count / 3;
				for (int v = 0; v < 3; v++)
				{
					int p = at + 12 + v * 12;
					b.Position(LittleFloat(d, p), LittleFloat(d, p + 4), LittleFloat(d, p + 8));
				}
				b.Triangle(first, first + 1, first + 2);
			}
		}
		private static void AsciiStl(byte[] d, Builder b)
		{
			int at = 0, inFacet = 0;
			for (ReadOnlySpan<byte> t = Token(d, ref at, d.Length); t.Length > 0; t = Token(d, ref at, d.Length))
			{
				if (!Is(t, "vertex")) continue;
				float x = Float(d, ref at, d.Length), y = Float(d, ref at, d.Length), z = Float(d, ref at, d.Length);
				b.Position(x, y, z);
				if (++inFacet < 3) continue;
				int last = b.positions.Count / 3 - 1;
				b.Triangle(last - 2, last - 1, last);
				inFacet = 0;
			}
			if (inFacet != 0) throw new Malformed();
		}
		private enum PlyEncoding { Ascii, Little, Big }
		private sealed class PlyProperty { public string name, type, countType; public bool IsList => countType != null; }
		private sealed class PlyElement { public string name; public long count; public readonly List<PlyProperty> properties = new List<PlyProperty>(); }
		private static int PlySize(string type)
		{
			switch (type)
			{
				case "char": case "int8": case "uchar": case "uint8": return 1;
				case "short": case "int16": case "ushort": case "uint16": return 2;
				case "int": case "int32": case "uint": case "uint32": case "float": case "float32": return 4;
				case "double": case "float64": return 8;
				default: throw new Malformed();
			}
		}
		private static double PlyValue(byte[] d, ref int at, string type, PlyEncoding encoding)
		{
			if (encoding == PlyEncoding.Ascii)
			{
				ReadOnlySpan<byte> t = Token(d, ref at, d.Length);
				if (!Utf8Parser.TryParse(t, out double parsed, out int used) || used != t.Length) throw new Malformed();
				return parsed;
			}
			int size = PlySize(type);
			if (at > d.Length - size) throw new Malformed();
			ReadOnlySpan<byte> s = new ReadOnlySpan<byte>(d, at, size);
			at += size;
			bool big = encoding == PlyEncoding.Big;
			switch (type)
			{
				case "char": case "int8": return (sbyte)s[0];
				case "uchar": case "uint8": return s[0];
				case "short": case "int16": return big ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s);
				case "ushort": case "uint16": return big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
				case "int": case "int32": return big ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s);
				case "uint": case "uint32": return big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
				case "float": case "float32": return BitConverter.Int32BitsToSingle(big ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s));
				default: return BitConverter.Int64BitsToDouble(big ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s));
			}
		}
		private static void Ply(byte[] d, Builder b)
		{
			int at = 0;
			PlyEncoding encoding = PlyEncoding.Ascii;
			bool formatSeen = false, headerDone = false;
			List<PlyElement> elements = new List<PlyElement>();
			while (at < d.Length && !headerDone)
			{
				int end = Array.IndexOf(d, (byte)'\n', at);
				if (end < 0) throw new Malformed();
				string[] line = Encoding.ASCII.GetString(d, at, end - at).Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
				at = end + 1;
				if (line.Length == 0) continue;
				switch (line[0])
				{
					case "format":
						if (line.Length < 2) throw new Malformed();
						formatSeen = true;
						encoding = line[1] == "ascii" ? PlyEncoding.Ascii : line[1] == "binary_little_endian" ? PlyEncoding.Little : line[1] == "binary_big_endian" ? PlyEncoding.Big : throw new Malformed();
						break;
					case "element":
						if (line.Length < 3 || !long.TryParse(line[2], out long count) || count < 0) throw new Malformed();
						elements.Add(new PlyElement { name = line[1], count = count });
						break;
					case "property":
						if (elements.Count == 0 || line.Length < 3) throw new Malformed();
						PlyProperty property = line[1] == "list" ? (line.Length < 5 ? throw new Malformed() : new PlyProperty { countType = line[2], type = line[3], name = line[4] }) : new PlyProperty { type = line[1], name = line[2] };
						PlySize(property.type);
						if (property.IsList) PlySize(property.countType);
						elements[elements.Count - 1].properties.Add(property);
						break;
					case "end_header":
						headerDone = true;
						break;
				}
			}
			if (!formatSeen || !headerDone) throw new Malformed();
			List<float> normals = new List<float>(), uvs = new List<float>(), colors = new List<float>();
			List<int> polygon = new List<int>();
			foreach (PlyElement element in elements)
			{
				if (element.count > 0 && element.properties.Count == 0) throw new Malformed();
				bool isVertex = element.name == "vertex", isFace = element.name == "face";
				for (long row = 0; row < element.count; row++)
				{
					float x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0, u = 0, v = 0, r = 1, g = 1, bl = 1, a = 1;
					bool hasNormal = false, hasUv = false, hasColor = false;
					foreach (PlyProperty p in element.properties)
					{
						if (p.IsList)
						{
							double length = PlyValue(d, ref at, p.countType, encoding);
							if (length < 0 || length > d.Length) throw new Malformed();
							bool isIndices = isFace && (p.name == "vertex_indices" || p.name == "vertex_index");
							polygon.Clear();
							for (int i = 0; i < (int)length; i++)
							{
								double index = PlyValue(d, ref at, p.type, encoding);
								if (isIndices) polygon.Add(index < 0 || index > int.MaxValue ? throw new Malformed() : (int)index);
							}
							if (isIndices)
							{
								if (polygon.Count < 3) throw new Malformed();
								for (int i = 2; i < polygon.Count; i++) b.Triangle(polygon[0], polygon[i - 1], polygon[i]);
							}
							continue;
						}
						double value = PlyValue(d, ref at, p.type, encoding);
						if (!isVertex) continue;
						float scale = p.type == "uchar" || p.type == "uint8" ? 1f / 255f : 1f;
						switch (p.name)
						{
							case "x": x = (float)value; break;
							case "y": y = (float)value; break;
							case "z": z = (float)value; break;
							case "nx": nx = (float)value; hasNormal = true; break;
							case "ny": ny = (float)value; break;
							case "nz": nz = (float)value; break;
							case "s": case "u": case "texture_u": u = (float)value; hasUv = true; break;
							case "t": case "v": case "texture_v": v = (float)value; break;
							case "red": r = (float)value * scale; hasColor = true; break;
							case "green": g = (float)value * scale; break;
							case "blue": bl = (float)value * scale; break;
							case "alpha": a = (float)value * scale; break;
						}
					}
					if (!isVertex) continue;
					b.Position(x, y, z);
					if (hasNormal)
					{
						normals.Add(-Finite(nx));
						normals.Add(Finite(ny));
						normals.Add(Finite(nz));
					}
					if (hasUv)
					{
						uvs.Add(Finite(u));
						uvs.Add(Finite(v));
					}
					if (hasColor)
					{
						colors.Add(Finite(r));
						colors.Add(Finite(g));
						colors.Add(Finite(bl));
						colors.Add(Finite(a));
					}
				}
			}
			b.normals.AddRange(normals);
			b.uvs.AddRange(uvs);
			b.colors.AddRange(colors);
		}
	}
}
