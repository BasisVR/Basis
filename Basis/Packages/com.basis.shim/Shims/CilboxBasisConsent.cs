using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Device_Management;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxBasisConsent
	{
		internal enum State : byte
		{
			Unchecked,
			NotNeeded,
			Pending,
			Allowed,
			Denied,
		}

		internal sealed class Scan
		{
			public readonly SortedSet<string> Items = new SortedSet<string>(StringComparer.Ordinal);
			public readonly SortedSet<string> Blocked = new SortedSet<string>(StringComparer.Ordinal);
		}

		private sealed class Result
		{
			public string[] Items;
			public string[] Blocked;
			public bool Failed;
		}

		private sealed class Request
		{
			public string Hash;
			public string Name;
			public string Kind;
			public string[] Items;
			public readonly List<CilboxBasisCommon> Boxes = new List<CilboxBasisCommon>();
			public readonly List<CilboxProxy> Held = new List<CilboxProxy>();
		}

		private static readonly Dictionary<string, Result> scans = new Dictionary<string, Result>(StringComparer.Ordinal);
		private static readonly Dictionary<string, Request> requests = new Dictionary<string, Request>(StringComparer.Ordinal);
		private static readonly Queue<string> queue = new Queue<string>();
		private const int HashChunk = 4096;
		private static string showing;

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Hook()
		{
			ContentPoliceControl.OnBeforeContentActivated -= GateContent;
			ContentPoliceControl.OnBeforeContentActivated += GateContent;
			SceneManager.sceneLoaded -= GateScene;
			SceneManager.sceneLoaded += GateScene;
			Application.quitting -= Unhook;
			Application.quitting += Unhook;
			BasisCilboxPermissions.OnRevoked -= Revoke;
			BasisCilboxPermissions.OnRevoked += Revoke;
		}

		private static void Unhook()
		{
			SceneManager.sceneLoaded -= GateScene;
			Application.quitting -= Unhook;
		}

		internal static void GateContent(GameObject root)
		{
			if (root == null) return;
			GateProxies(root.GetComponentsInChildren<CilboxProxy>(true));
		}

		private static void GateScene(Scene scene, LoadSceneMode mode)
		{
			if (!scene.IsValid() || !scene.isLoaded) return;
			List<CilboxProxy> proxies = new List<CilboxProxy>();
			List<CilboxProxy> found = new List<CilboxProxy>();
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				root.GetComponentsInChildren(true, found);
				proxies.AddRange(found);
			}
			GateProxies(proxies);
		}

		private static void GateProxies(IList<CilboxProxy> proxies)
		{
			if (proxies == null || proxies.Count == 0) return;
			Dictionary<CilboxBasisCommon, List<CilboxProxy>> byBox = null;
			foreach (CilboxProxy proxy in proxies)
			{
				if (proxy == null || !(proxy.box is CilboxBasisCommon box) || box == null) continue;
				byBox ??= new Dictionary<CilboxBasisCommon, List<CilboxProxy>>();
				if (!byBox.TryGetValue(box, out List<CilboxProxy> list))
				{
					list = new List<CilboxProxy>();
					byBox[box] = list;
				}
				list.Add(proxy);
			}
			if (byBox == null) return;
			foreach (KeyValuePair<CilboxBasisCommon, List<CilboxProxy>> pair in byBox)
			{
				Gate(pair.Key, pair.Value);
			}
		}

		private static void Gate(CilboxBasisCommon box, List<CilboxProxy> proxies)
		{
			if (box.consentState == State.Unchecked) Check(box);
			if (box.consentState != State.Pending || box.consentHash == null || !requests.TryGetValue(box.consentHash, out Request request)) return;
			foreach (CilboxProxy proxy in proxies)
			{
				if (proxy == null || !proxy.enabled || request.Held.Contains(proxy)) continue;
				proxy.enabled = false;
				request.Held.Add(proxy);
			}
		}

		private static void Check(CilboxBasisCommon box)
		{
			box.consentState = State.NotNeeded;
			if (!Application.isPlaying || string.IsNullOrEmpty(box.assemblyData) || box.metadatas != null) return;
			string hash = KeyOf(box);
			box.consentHash = hash;
			if (!scans.TryGetValue(hash, out Result result))
			{
				result = Run(box);
				scans[hash] = result;
				if (result.Blocked.Length > 0)
				{
					Debug.LogWarning($"[Cilbox] {NameOf(box)} uses things scripts can never be allowed: {string.Join(", ", result.Blocked)}");
				}
			}
			if (result.Failed || result.Items.Length == 0) return;
			if (BasisCilboxPermissions.Covers(hash, result.Items))
			{
				Allow(box, result.Items);
				return;
			}
			if (BasisCilboxPermissions.IsDeclined(hash))
			{
				box.consentState = State.Denied;
				return;
			}
			box.consentState = State.Pending;
			if (!requests.TryGetValue(hash, out Request request))
			{
				request = new Request { Hash = hash, Name = NameOf(box), Kind = KindOf(box), Items = result.Items };
				requests[hash] = request;
				queue.Enqueue(hash);
				BasisDeviceManagement.EnqueueOnMainThread(ShowNext);
			}
			request.Boxes.Add(box);
		}

		private static Result Run(CilboxBasisCommon box)
		{
			Scan scan = new Scan();
			Result result = new Result();
			box.consentScan = scan;
			bool logging = Debug.unityLogger.logEnabled;
			Debug.unityLogger.logEnabled = false;
			try
			{
				box.BoxInitialize(true);
			}
			catch (Exception)
			{
				result.Failed = true;
			}
			finally
			{
				Debug.unityLogger.logEnabled = logging;
				box.consentScan = null;
				box.ForceReinit();
			}
			result.Items = new string[scan.Items.Count];
			scan.Items.CopyTo(result.Items);
			result.Blocked = new string[scan.Blocked.Count];
			scan.Blocked.CopyTo(result.Blocked);
			return result;
		}

		internal static bool Permits(CilboxBasisCommon box, string item)
		{
			if (box.consentState == State.Unchecked) LateCheck(box);
			return box.consentState == State.Allowed && box.consentGranted != null && box.consentGranted.Contains(item);
		}

		private static void LateCheck(CilboxBasisCommon box)
		{
			box.consentState = State.Denied;
			if (!Application.isPlaying || string.IsNullOrEmpty(box.assemblyData)) return;
			box.consentHash = KeyOf(box);
			BasisCilboxPermissions.Approval approval = BasisCilboxPermissions.Find(box.consentHash);
			if (approval != null)
			{
				Allow(box, approval.Items);
				return;
			}
			Debug.LogWarning($"[Cilbox] {NameOf(box)} started its scripts before Basis could ask about the extra permissions they need, so those were refused.");
		}

		private static void Allow(CilboxBasisCommon box, IEnumerable<string> items)
		{
			box.consentGranted = new HashSet<string>(items, StringComparer.Ordinal);
			box.consentState = State.Allowed;
		}

		private static void ShowNext()
		{
			if (showing != null) return;
			while (queue.Count > 0)
			{
				string hash = queue.Dequeue();
				if (!requests.TryGetValue(hash, out Request request)) continue;
				request.Boxes.RemoveAll(box => box == null);
				if (request.Boxes.Count == 0)
				{
					requests.Remove(hash);
					continue;
				}
				showing = hash;
				BasisCilboxConsentPrompt.Show(request.Name, request.Kind, request.Items, answer => Answer(hash, answer), () => Deferred(hash));
				return;
			}
		}

		private static void Deferred(string hash)
		{
			if (showing == hash) showing = null;
			ShowNext();
		}

		private static void Answer(string hash, BasisCilboxConsentPrompt.Answer answer)
		{
			if (showing == hash) showing = null;
			if (requests.TryGetValue(hash, out Request request))
			{
				requests.Remove(hash);
				if (answer == BasisCilboxConsentPrompt.Answer.Deny)
				{
					BasisCilboxPermissions.Decline(hash);
					foreach (CilboxBasisCommon box in request.Boxes)
					{
						if (box != null) box.consentState = State.Denied;
					}
				}
				else
				{
					BasisCilboxPermissions.Allow(hash, request.Name, request.Kind, request.Items, answer == BasisCilboxConsentPrompt.Answer.AllowAndRemember);
					foreach (CilboxBasisCommon box in request.Boxes)
					{
						if (box != null) Allow(box, request.Items);
					}
				}
				foreach (CilboxProxy proxy in request.Held)
				{
					if (proxy != null) proxy.enabled = true;
				}
			}
			ShowNext();
		}

		private static void Revoke(string hash)
		{
			foreach (CilboxBasisCommon box in UnityEngine.Object.FindObjectsByType<CilboxBasisCommon>(FindObjectsInactive.Include))
			{
				if (box.consentState != State.Allowed || !string.Equals(box.consentHash, hash, StringComparison.Ordinal)) continue;
				box.consentState = State.Denied;
				box.consentGranted = null;
				box.disabledReason = "Its script permissions were removed in Settings.";
				box.disabled = true;
				Debug.LogWarning($"[Cilbox] Stopped the scripts in {NameOf(box)}: its script permissions were removed in Settings.");
			}
		}

		private static string KeyOf(CilboxBasisCommon box)
		{
			using SHA256 sha = SHA256.Create();
			byte[] buffer = new byte[Encoding.UTF8.GetMaxByteCount(HashChunk)];
			Feed(sha, box.GetType().FullName + "\n", buffer);
			Feed(sha, box.assemblyData, buffer);
			sha.TransformFinalBlock(buffer, 0, 0);
			StringBuilder hex = new StringBuilder(sha.Hash.Length * 2);
			foreach (byte b in sha.Hash) hex.Append(b.ToString("x2"));
			return hex.ToString();
		}

		private static void Feed(SHA256 sha, string text, byte[] buffer)
		{
			int index = 0;
			while (index < text.Length)
			{
				int count = Math.Min(HashChunk, text.Length - index);
				if (count > 1 && index + count < text.Length && char.IsHighSurrogate(text[index + count - 1])) count--;
				int bytes = Encoding.UTF8.GetBytes(text, index, count, buffer, 0);
				sha.TransformBlock(buffer, 0, bytes, null, 0);
				index += count;
			}
		}

		private static string NameOf(CilboxBasisCommon box)
		{
			if (box == null) return string.Empty;
			BasisContentBase content = box.GetComponentInParent<BasisContentBase>(true);
			string name = content != null && !(content is BasisScene) ? content.gameObject.name : box.gameObject.scene.name;
			return string.IsNullOrEmpty(name) ? box.gameObject.name : name.Replace("(Clone)", string.Empty).Trim();
		}

		private static string KindOf(CilboxBasisCommon box)
		{
			BasisContentBase content = box.GetComponentInParent<BasisContentBase>(true);
			if (content is BasisAvatar || (content == null && box is CilboxAvatarBasis)) return "Avatar";
			if (content != null && !(content is BasisScene)) return "Prop";
			return "World";
		}
	}
}
