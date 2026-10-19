using System;
using System.Text;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Shims;
using UnityEngine;

namespace Cilbox
{
	internal static class CilboxServerStorage
	{
		private const string StorePrefix = "store:", CodePrefix = "code:";
		private const int CodeHashLength = 48;
		internal static bool IsEntryPoint(string name) => name == nameof(BasisServerStorageShim.IsAvailable) || NeedsConsent(name);
		internal static bool NeedsConsent(string name)
		{
			switch (name)
			{
				case nameof(BasisServerStorageShim.Save):
				case nameof(BasisServerStorageShim.SaveString):
				case nameof(BasisServerStorageShim.Load):
				case nameof(BasisServerStorageShim.LoadString):
				case nameof(BasisServerStorageShim.Delete):
				case nameof(BasisServerStorageShim.Keys):
				case nameof(BasisServerStorageShim.Clear):
				case nameof(BasisServerStorageShim.Usage):
					return true;
				default:
					return false;
			}
		}
		internal static bool IsAvailable(object self) => self is CilboxProxy && BasisServerStorageClient.IsSupported;
		internal static void Save(object self, string store, string key, byte[] data, Action<BasisServerStorageResult> done)
		{
			Action<BasisStorageReply> answer = done == null ? null : reply => done(Result(reply));
			if (Begin(self, nameof(BasisServerStorageShim.Save), store, BasisStorageOp.Set, answer, out CilboxProxy proxy, out string scope)) BasisServerStorageClient.Set(scope, key, data, answer, proxy);
		}
		internal static void SaveString(object self, string store, string key, string text, Action<BasisServerStorageResult> done)
		{
			Action<BasisStorageReply> answer = done == null ? null : reply => done(Result(reply));
			if (Begin(self, nameof(BasisServerStorageShim.SaveString), store, BasisStorageOp.Set, answer, out CilboxProxy proxy, out string scope)) BasisServerStorageClient.Set(scope, key, text == null ? null : Encoding.UTF8.GetBytes(text), answer, proxy);
		}
		internal static void Load(object self, string store, string key, Action<BasisServerStorageResult, byte[]> done)
		{
			Action<BasisStorageReply> answer = done == null ? null : reply => done(Result(reply), reply.Status == BasisStorageStatus.Ok ? reply.Value : null);
			if (Begin(self, nameof(BasisServerStorageShim.Load), store, BasisStorageOp.Get, answer, out CilboxProxy proxy, out string scope)) BasisServerStorageClient.Get(scope, key, answer, proxy);
		}
		internal static void LoadString(object self, string store, string key, Action<BasisServerStorageResult, string> done)
		{
			Action<BasisStorageReply> answer = done == null ? null : reply => done(Result(reply), reply.Status == BasisStorageStatus.Ok && reply.Value != null ? Encoding.UTF8.GetString(reply.Value) : null);
			if (Begin(self, nameof(BasisServerStorageShim.LoadString), store, BasisStorageOp.Get, answer, out CilboxProxy proxy, out string scope)) BasisServerStorageClient.Get(scope, key, answer, proxy);
		}
		internal static void Delete(object self, string store, string key, Action<BasisServerStorageResult> done)
		{
			Action<BasisStorageReply> answer = done == null ? null : reply => done(Result(reply));
			if (Begin(self, nameof(BasisServerStorageShim.Delete), store, BasisStorageOp.Delete, answer, out CilboxProxy proxy, out string scope)) BasisServerStorageClient.Delete(scope, key, answer, proxy);
		}
		internal static void Keys(object self, string store, Action<BasisServerStorageResult, string[]> done)
		{
			Action<BasisStorageReply> answer = done == null ? null : reply => done(Result(reply), reply.Status == BasisStorageStatus.Ok && reply.Keys != null ? reply.Keys : Array.Empty<string>());
			if (Begin(self, nameof(BasisServerStorageShim.Keys), store, BasisStorageOp.Keys, answer, out CilboxProxy proxy, out string scope)) BasisServerStorageClient.Keys(scope, answer, proxy);
		}
		internal static void Clear(object self, string store, Action<BasisServerStorageResult> done)
		{
			Action<BasisStorageReply> answer = done == null ? null : reply => done(Result(reply));
			if (Begin(self, nameof(BasisServerStorageShim.Clear), store, BasisStorageOp.Clear, answer, out CilboxProxy proxy, out string scope)) BasisServerStorageClient.Clear(scope, answer, proxy);
		}
		internal static void Usage(object self, Action<BasisServerStorageResult, int, int> done)
		{
			Action<BasisStorageReply> answer = done == null ? null : reply => done(Result(reply), reply.Used, reply.Limit);
			if (Begin(self, nameof(BasisServerStorageShim.Usage), null, BasisStorageOp.Usage, answer, out CilboxProxy proxy, out string scope)) BasisServerStorageClient.Usage(scope, answer, proxy);
		}
		private static BasisServerStorageResult Result(BasisStorageReply reply) => (BasisServerStorageResult)(int)reply.Status;
		private static bool Begin(object self, string method, string store, BasisStorageOp op, Action<BasisStorageReply> answer, out CilboxProxy proxy, out string scope)
		{
			scope = null;
			proxy = self as CilboxProxy;
			CilboxBasisCommon box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			if (proxy == null || box == null || box.disabled || box.interpreterAccountingDepth == 0 || !OnlyRunning(box) || !CilboxBasisConsent.ForLocalPlayer(box, proxy.gameObject) || !CilboxBasisConsent.Permits(box, CilboxBasisCommon.MethodItem(typeof(BasisServerStorageShim), method)))
			{
				BasisServerStorageClient.Refuse(op, BasisStorageStatus.NotAllowed, answer, proxy);
				return false;
			}
			scope = ScopeOf(box, store);
			if (scope != null) return true;
			BasisServerStorageClient.Refuse(op, BasisStorageStatus.Invalid, answer, proxy);
			return false;
		}
		private static bool OnlyRunning(CilboxBasisCommon caller)
		{
			foreach (CilboxBasisCommon other in UnityEngine.Object.FindObjectsByType<CilboxBasisCommon>(FindObjectsInactive.Include))
			{
				if (other != null && !ReferenceEquals(other, caller) && other.interpreterAccountingDepth > 0) return false;
			}
			return true;
		}
		private static string ScopeOf(CilboxBasisCommon box, string store)
		{
			if (store != null)
			{
				string named = StorePrefix + store.Trim().ToLowerInvariant();
				return store.Trim().Length > 0 && BasisStorageProtocol.IsValidName(named) ? named : null;
			}
			string id = CilboxBasisConsent.TryOwner(box, box.gameObject, out BasisContentBase content) && content.BasisBundleDescription != null ? content.BasisBundleDescription.ContentGroupId : null;
			if (!string.IsNullOrWhiteSpace(id)) return BasisStorageProtocol.IsValidName(id.Trim()) ? id.Trim() : null;
			string hash = box.consentHash;
			return string.IsNullOrEmpty(hash) ? null : CodePrefix + hash.Substring(0, Math.Min(CodeHashLength, hash.Length));
		}
	}
}
