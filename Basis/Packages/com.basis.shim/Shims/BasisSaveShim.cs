using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cilbox;
using UnityEngine;

namespace Basis.Shims
{
	public enum BasisSaveScope
	{
		Protected = 0,
		Shared = 1,
		ReadOnly = 2,
	}

	public static class BasisSaveShim
	{
		public const int MaxSlotBytes = 256 * 1024, MaxSlots = 32, MaxStoreBytes = 1024 * 1024, MaxNameLength = 128;
		const int HeaderSize = 16, MaxStores = 128, MaxStoresPerBox = 4, MaxReadOnlyStoresPerBox = 16, MaxWritesPerSecond = 8, GeneratedIdLength = 40, GeneratedIdHexLength = 32, ScriptHashLength = 64;
		const uint Magic = (uint)('B' | 'S' << 8 | 'A' << 16 | 'V' << 24);
		const byte Version = 1;
		const string RootFolder = "CilboxSaves", Extension = ".bsave", SharedPrefix = "store:", ProtectedPrefix = "protected:", ReadOnlyPrefix = "readonly:";
		public static bool Save(object self, string slot, byte[] data) => Save(self, BasisSaveScope.Protected, null, slot, data);
		public static byte[] Load(object self, string slot) => Load(self, BasisSaveScope.Protected, null, slot);
		public static bool Delete(object self, string slot) => Delete(self, BasisSaveScope.Protected, null, slot);
		public static bool Save(object self, string store, string slot, byte[] data) => Save(self, store == null ? BasisSaveScope.Protected : BasisSaveScope.Shared, store, slot, data);
		public static byte[] Load(object self, string store, string slot) => Load(self, store == null ? BasisSaveScope.Protected : BasisSaveScope.Shared, store, slot);
		public static bool Delete(object self, string store, string slot) => Delete(self, store == null ? BasisSaveScope.Protected : BasisSaveScope.Shared, store, slot);
		public static bool Save(object self, BasisSaveScope scope, string store, string slot, byte[] data)
		{
			CilboxBasisCommon box = Caller(self, nameof(Save));
			string root = Root(), folder = box == null || data == null || data.Length > MaxSlotBytes ? null : Folder(box, root, scope, store), file = SlotFile(folder, slot);
			return file != null && Claim(box, folder) && Spend(box) && Write(root, folder, file, data);
		}
		public static byte[] Load(object self, BasisSaveScope scope, string store, string slot)
		{
			CilboxBasisCommon box = Caller(self, nameof(Load));
			string folder = box == null ? null : Folder(box, Root(), scope, store), file = SlotFile(folder, slot);
			return file != null && Claim(box, folder) ? Read(file) : null;
		}
		public static bool Delete(object self, BasisSaveScope scope, string store, string slot)
		{
			CilboxBasisCommon box = Caller(self, nameof(Delete));
			string folder = box == null ? null : Folder(box, Root(), scope, store), file = SlotFile(folder, slot);
			return file != null && Claim(box, folder) && Spend(box) && Remove(folder, file);
		}
		public static byte[] LoadReadOnly(object self, string ownerContentGroupId, string store, string slot)
		{
			CilboxBasisCommon box = Caller(self, nameof(LoadReadOnly));
			string folder = box == null || ownerContentGroupId == null ? null : Prefixed(Root(), ReadOnlyPrefix, ownerContentGroupId.Trim(), store), file = SlotFile(folder, slot);
			return file != null && ClaimRead(box, folder) ? Read(file) : null;
		}
		static bool Write(string root, string folder, string file, byte[] data)
		{
			string temp = Path.ChangeExtension(file, ".tmp");
			try
			{
				long total = HeaderSize + data.Length;
				int slots = 1;
				if (Directory.Exists(folder))
				{
					foreach (string other in Directory.GetFiles(folder, "*" + Extension))
					{
						if (string.Equals(Path.GetFileName(other), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) continue;
						total += new FileInfo(other).Length;
						slots++;
					}
				}
				else if (Directory.Exists(root) && Directory.GetDirectories(root).Length >= MaxStores) return false;
				if (slots > MaxSlots || total > MaxStoreBytes) return false;
				byte[] bytes = new byte[HeaderSize + data.Length];
				BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(bytes, 0, 4), Magic);
				bytes[4] = Version;
				BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(bytes, 8, 4), data.Length);
				BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(bytes, 12, 4), Checksum(data));
				Buffer.BlockCopy(data, 0, bytes, HeaderSize, data.Length);
				Directory.CreateDirectory(folder);
				File.WriteAllBytes(temp, bytes);
				if (File.Exists(file)) File.Replace(temp, file, null);
				else File.Move(temp, file);
				return true;
			}
			catch (Exception)
			{
				try { File.Delete(temp); } catch (Exception) { }
				return false;
			}
		}
		static byte[] Read(string file)
		{
			try
			{
				if (!File.Exists(file)) return null;
				using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					long length = stream.Length;
					if (length < HeaderSize || length > HeaderSize + MaxSlotBytes) return null;
					byte[] header = new byte[HeaderSize], data = new byte[length - HeaderSize];
					if (!Fill(stream, header) || !Fill(stream, data)) return null;
					ReadOnlySpan<byte> fields = header;
					if (BinaryPrimitives.ReadUInt32LittleEndian(fields) != Magic || header[4] != Version || header[5] != 0 || header[6] != 0 || header[7] != 0) return null;
					if (BinaryPrimitives.ReadInt32LittleEndian(fields.Slice(8)) != data.Length || BinaryPrimitives.ReadUInt32LittleEndian(fields.Slice(12)) != Checksum(data)) return null;
					return data;
				}
			}
			catch (Exception) { return null; }
		}
		static bool Remove(string folder, string file)
		{
			try
			{
				if (!File.Exists(file)) return false;
				File.Delete(file);
				if (Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder);
				return true;
			}
			catch (Exception) { return false; }
		}
		static CilboxBasisCommon Caller(object self, string method)
		{
			if (!(self is CilboxProxy proxy) || proxy == null || !(proxy.box is CilboxBasisCommon box) || box == null || box.interpreterAccountingDepth == 0 || !CilboxBasisConsent.ForLocalPlayer(box, proxy.gameObject) || !CilboxBasisConsent.TryRunning(out CilboxBasisCommon running) || !ReferenceEquals(running, box)) return null;
			return CilboxBasisConsent.Permits(box, CilboxBasisCommon.MethodItem(typeof(BasisSaveShim), method)) ? box : null;
		}
		static string Root() => Path.Combine(Application.persistentDataPath, RootFolder);
		static string Folder(CilboxBasisCommon box, string root, BasisSaveScope scope, string store)
		{
			string owner = ContentKey(box);
			if (scope == BasisSaveScope.Shared) return SharedFolder(root, owner, store);
			if (scope == BasisSaveScope.ReadOnly) return Prefixed(root, ReadOnlyPrefix, owner, store);
			if (scope != BasisSaveScope.Protected) return null;
			if (store != null) return Prefixed(root, ProtectedPrefix, owner, store);
			return string.IsNullOrEmpty(owner) || owner.Length > MaxNameLength ? null : Path.Combine(root, Hash(owner.ToLowerInvariant()));
		}
		static string Prefixed(string root, string prefix, string owner, string store)
		{
			string name = store == null ? string.Empty : store.Trim();
			if (string.IsNullOrEmpty(owner) || owner.Length > MaxNameLength || owner.IndexOf('\n') >= 0 || (store != null && name.Length == 0) || name.Length > MaxNameLength || name.IndexOf('\n') >= 0) return null;
			return Path.Combine(root, Hash(prefix + owner.ToLowerInvariant() + "\n" + name.ToLowerInvariant()));
		}
		static string SharedFolder(string root, string owner, string store)
		{
			string name = store == null ? string.Empty : store.Trim().ToLowerInvariant();
			if (name.Length == 0 || name.Length > MaxNameLength) return null;
			string folder = Path.Combine(root, Hash(SharedPrefix + name));
			if (!MaybeContentKey(name, owner)) Migrate(Path.Combine(root, Hash(name)), folder);
			return folder;
		}
		static bool MaybeContentKey(string name, string owner)
		{
			if (!string.IsNullOrEmpty(owner) && name == owner.ToLowerInvariant()) return true;
			if (name.Length != GeneratedIdLength && name.Length != ScriptHashLength) return false;
			for (int i = 0; i < name.Length; i++)
			{
				bool digit = name[i] >= '0' && name[i] <= '9', hex = digit || (name[i] >= 'a' && name[i] <= 'f');
				if (name.Length == GeneratedIdLength && i >= GeneratedIdHexLength ? !digit : !hex) return false;
			}
			return true;
		}
		static void Migrate(string legacy, string folder)
		{
			try
			{
				if (!Directory.Exists(folder) && Directory.Exists(legacy)) Directory.Move(legacy, folder);
			}
			catch (Exception) { }
		}
		static string SlotFile(string folder, string slot) => folder == null || string.IsNullOrEmpty(slot) || slot.Length > MaxNameLength ? null : Path.Combine(folder, Hash(slot) + Extension);
		static string ContentKey(CilboxBasisCommon box)
		{
			if (box.saveContentKey != null) return box.saveContentKey;
			string id = CilboxBasisConsent.TryOwner(box, box.gameObject, out BasisContentBase content) && content.BasisBundleDescription != null ? content.BasisBundleDescription.ContentGroupId : null;
			box.saveContentKey = string.IsNullOrWhiteSpace(id) ? box.consentHash ?? string.Empty : id.Trim();
			return box.saveContentKey;
		}
		static bool Claim(CilboxBasisCommon box, string folder)
		{
			box.saveStores ??= new HashSet<string>(StringComparer.Ordinal);
			return box.saveStores.Contains(folder) || (box.saveStores.Count < MaxStoresPerBox && box.saveStores.Add(folder));
		}
		static bool ClaimRead(CilboxBasisCommon box, string folder)
		{
			box.saveReadStores ??= new HashSet<string>(StringComparer.Ordinal);
			return box.saveReadStores.Contains(folder) || (box.saveReadStores.Count < MaxReadOnlyStoresPerBox && box.saveReadStores.Add(folder));
		}
		static bool Spend(CilboxBasisCommon box)
		{
			double now = Time.realtimeSinceStartupAsDouble;
			if (now - box.saveWindowStart >= 1d || now < box.saveWindowStart)
			{
				box.saveWindowStart = now;
				box.saveWindowWrites = 0;
			}
			return ++box.saveWindowWrites <= MaxWritesPerSecond;
		}
		static bool Fill(Stream stream, byte[] buffer)
		{
			for (int read = 0, got; read < buffer.Length; read += got)
			{
				if ((got = stream.Read(buffer, read, buffer.Length - read)) <= 0) return false;
			}
			return true;
		}
		static string Hash(string text)
		{
			using (SHA256 sha = SHA256.Create())
			{
				byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
				StringBuilder hex = new StringBuilder(32);
				for (int i = 0; i < 16; i++) hex.Append(digest[i].ToString("x2"));
				return hex.ToString();
			}
		}
		static uint Checksum(byte[] data)
		{
			uint hash = 2166136261;
			foreach (byte value in data) hash = (hash ^ value) * 16777619;
			return hash;
		}
	}
}
