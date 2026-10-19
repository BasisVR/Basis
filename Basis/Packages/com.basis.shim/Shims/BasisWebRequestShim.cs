using System;
using System.Text;
using Basis.BasisUI;
using UnityEngine;
using Cilbox;
using Basis.Shims;

namespace Basis.Shims
{
	public enum BasisWebResult
	{
		Ok = 0,
		HttpError = 1,
		InvalidUrl = 2,
		Blocked = 3,
		Declined = 4,
		Busy = 5,
		TooLarge = 6,
		TimedOut = 7,
		NetworkError = 8,
		NotAllowed = 9,
		Unsupported = 10,
	}

	/// <summary>
	/// Web requests for world and prop scripts. Pass <c>this</c> so Basis knows which content is
	/// asking. The player approves the script once (Script Permissions) and then each site it
	/// contacts; until then the callback gets NotAllowed or Declined and the rest of the script
	/// runs. https only, default port, host names only, nothing on the local network. Uploads up
	/// to 256 KB, downloads up to 4 MB, 15 s timeout, 4 at a time and 30 a minute per content.
	/// The callback gets the result, the HTTP status code (0 when none) and the response body.
	/// </summary>
	public static class BasisWebRequestShim
	{
		public const int MaxUploadBytes = 256 * 1024, MaxDownloadBytes = 4 * 1024 * 1024;
		public const string Json = "application/json", Text = "text/plain", Binary = "application/octet-stream", Form = "application/x-www-form-urlencoded";
		public static bool IsValidUrl(string url) => CilboxWebRequests.IsValidUrl(url);
		public static void Get(object self, string url, Action<BasisWebResult, int, byte[]> done) => CilboxWebRequests.Get(self, url, done);
		public static void GetText(object self, string url, Action<BasisWebResult, int, string> done) => CilboxWebRequests.GetText(self, url, done);
		public static void Post(object self, string url, byte[] body, string contentType, Action<BasisWebResult, int, byte[]> done) => CilboxWebRequests.Post(self, url, body, contentType, done);
		public static void PostText(object self, string url, string text, string contentType, Action<BasisWebResult, int, string> done) => CilboxWebRequests.PostText(self, url, text, contentType, done);
	}
}

namespace Cilbox
{
	internal static class CilboxWebRequests
	{
		internal static bool IsEntryPoint(string name) => name == nameof(BasisWebRequestShim.IsValidUrl) || NeedsConsent(name);
		internal static bool NeedsConsent(string name)
		{
			switch (name)
			{
				case nameof(BasisWebRequestShim.Get):
				case nameof(BasisWebRequestShim.GetText):
				case nameof(BasisWebRequestShim.Post):
				case nameof(BasisWebRequestShim.PostText):
					return true;
				default:
					return false;
			}
		}
		internal static bool IsValidUrl(string url) => BasisWebRequests.TryParse(url, out _);
		internal static void Get(object self, string url, Action<BasisWebResult, int, byte[]> done)
		{
			Begin(self, nameof(BasisWebRequestShim.Get), url, null, null, done == null ? null : reply => done(Result(reply), reply.Code, reply.Status == BasisWebRequests.Status.Ok || reply.Status == BasisWebRequests.Status.HttpError ? reply.Body : null));
		}
		internal static void GetText(object self, string url, Action<BasisWebResult, int, string> done)
		{
			Begin(self, nameof(BasisWebRequestShim.GetText), url, null, null, done == null ? null : reply => done(Result(reply), reply.Code, Text(reply)));
		}
		internal static void Post(object self, string url, byte[] body, string contentType, Action<BasisWebResult, int, byte[]> done)
		{
			Begin(self, nameof(BasisWebRequestShim.Post), url, body ?? Array.Empty<byte>(), contentType ?? BasisWebRequestShim.Binary, done == null ? null : reply => done(Result(reply), reply.Code, reply.Status == BasisWebRequests.Status.Ok || reply.Status == BasisWebRequests.Status.HttpError ? reply.Body : null));
		}
		internal static void PostText(object self, string url, string text, string contentType, Action<BasisWebResult, int, string> done)
		{
			byte[] body = text == null ? Array.Empty<byte>() : (text.Length > BasisWebRequests.MaxUploadBytes ? null : Encoding.UTF8.GetBytes(text));
			Action<BasisWebRequests.Reply> answer = done == null ? null : reply => done(Result(reply), reply.Code, Text(reply));
			if (body == null)
			{
				Refuse(self, BasisWebRequests.Status.TooLarge, answer);
				return;
			}
			Begin(self, nameof(BasisWebRequestShim.PostText), url, body, contentType ?? BasisWebRequestShim.Text, answer);
		}
		private static BasisWebResult Result(BasisWebRequests.Reply reply) => (BasisWebResult)(int)reply.Status;
		private static string Text(BasisWebRequests.Reply reply)
		{
			if ((reply.Status != BasisWebRequests.Status.Ok && reply.Status != BasisWebRequests.Status.HttpError) || reply.Body == null) return null;
			try { return new UTF8Encoding(false, true).GetString(reply.Body); }
			catch (ArgumentException) { return null; }
		}
		private static void Begin(object self, string method, string url, byte[] body, string contentType, Action<BasisWebRequests.Reply> answer)
		{
			CilboxProxy proxy = self as CilboxProxy;
			CilboxBasisCommon box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			if (proxy == null || box == null || box is CilboxAvatarBasis || box.disabled || box.interpreterAccountingDepth == 0 || !OnlyRunning(box) || string.IsNullOrEmpty(box.consentHash) || !CilboxBasisConsent.Permits(box, CilboxBasisCommon.MethodItem(typeof(BasisWebRequestShim), method)))
			{
				Refuse(proxy, BasisWebRequests.Status.NotAllowed, answer);
				return;
			}
			Action<BasisWebRequests.Reply> guarded = answer == null ? null : reply =>
			{
				if (box != null && !box.disabled) answer(reply);
			};
			BasisWebRequests.Send(proxy, box.consentHash, proxy.gameObject, url, body, contentType, guarded);
		}
		private static void Refuse(object self, BasisWebRequests.Status status, Action<BasisWebRequests.Reply> answer)
		{
			if (answer == null || !(self is CilboxProxy proxy) || proxy == null) return;
			Basis.Scripts.Device_Management.BasisDeviceManagement.EnqueueOnMainThread(() =>
			{
				if (proxy == null) return;
				try { answer(new BasisWebRequests.Reply { Status = status }); }
				catch (Exception e) { BasisDebug.LogErrorUnreported($"[WebRequests] A request callback threw: {e}", BasisDebug.LogTag.Shims); }
			});
		}
		private static bool OnlyRunning(CilboxBasisCommon caller)
		{
			foreach (CilboxBasisCommon other in UnityEngine.Object.FindObjectsByType<CilboxBasisCommon>(FindObjectsInactive.Include))
			{
				if (other != null && !ReferenceEquals(other, caller) && other.interpreterAccountingDepth > 0) return false;
			}
			return true;
		}
	}
}
