using System;
using Cilbox;

namespace Basis.Shims
{
	public enum BasisServerStorageResult
	{
		Ok = 0,
		NotFound = 1,
		Denied = 2,
		Full = 3,
		RateLimited = 4,
		Invalid = 5,
		Unavailable = 6,
		Busy = 7,
		TimedOut = 8,
		NotConnected = 9,
		NotAllowed = 10,
	}

	public static class BasisServerStorageShim
	{
		public const int MaxKeyBytes = 64, MaxValueBytes = 65535;
		public static bool IsAvailable(object self) => CilboxServerStorage.IsAvailable(self);
		public static void Save(object self, string key, byte[] data, Action<BasisServerStorageResult> done) => CilboxServerStorage.Save(self, null, key, data, done);
		public static void Save(object self, string store, string key, byte[] data, Action<BasisServerStorageResult> done) => CilboxServerStorage.Save(self, store, key, data, done);
		public static void SaveString(object self, string key, string text, Action<BasisServerStorageResult> done) => CilboxServerStorage.SaveString(self, null, key, text, done);
		public static void SaveString(object self, string store, string key, string text, Action<BasisServerStorageResult> done) => CilboxServerStorage.SaveString(self, store, key, text, done);
		public static void Load(object self, string key, Action<BasisServerStorageResult, byte[]> done) => CilboxServerStorage.Load(self, null, key, done);
		public static void Load(object self, string store, string key, Action<BasisServerStorageResult, byte[]> done) => CilboxServerStorage.Load(self, store, key, done);
		public static void LoadString(object self, string key, Action<BasisServerStorageResult, string> done) => CilboxServerStorage.LoadString(self, null, key, done);
		public static void LoadString(object self, string store, string key, Action<BasisServerStorageResult, string> done) => CilboxServerStorage.LoadString(self, store, key, done);
		public static void Delete(object self, string key, Action<BasisServerStorageResult> done) => CilboxServerStorage.Delete(self, null, key, done);
		public static void Delete(object self, string store, string key, Action<BasisServerStorageResult> done) => CilboxServerStorage.Delete(self, store, key, done);
		public static void Keys(object self, Action<BasisServerStorageResult, string[]> done) => CilboxServerStorage.Keys(self, null, done);
		public static void Keys(object self, string store, Action<BasisServerStorageResult, string[]> done) => CilboxServerStorage.Keys(self, store, done);
		public static void Clear(object self, Action<BasisServerStorageResult> done) => CilboxServerStorage.Clear(self, null, done);
		public static void Clear(object self, string store, Action<BasisServerStorageResult> done) => CilboxServerStorage.Clear(self, store, done);
		public static void Usage(object self, Action<BasisServerStorageResult, int, int> done) => CilboxServerStorage.Usage(self, done);
	}
}
