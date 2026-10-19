using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking.NetworkedAvatar;
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
			public List<BasisCilboxPermissionText.Use> Uses;
			public bool Failed;
		}

		private sealed class Request
		{
			public string Hash;
			public string Name;
			public string Kind;
			public string[] Items;
			public string[] Blocked;
			public List<BasisCilboxPermissionText.Use> Uses;
			public BasisNotification Notice;
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
			BasisCilboxPermissions.OnUntrustedDisabled -= Disallow;
			BasisCilboxPermissions.OnUntrustedDisabled += Disallow;
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

		internal static bool TryOwner(CilboxBasisCommon box, GameObject caller, out BasisContentBase content)
		{
			if (box != null && box.ownerRecorded)
			{
				content = box.ownerContent;
				return content != null;
			}
			return BasisContent.TryResolve(caller, out content);
		}

		internal static bool ForLocalPlayer(CilboxBasisCommon box, GameObject caller)
		{
			if (box is CilboxSceneBasis) return true;
			if (!TryOwner(box, caller, out BasisContentBase content)) return false;
			if (content is BasisAvatar avatar) return WornLocally(avatar);
			return !(box is CilboxAvatarBasis) && (content is BasisScene || content.SpawnedByLocalPlayer);
		}

		private static bool WornLocally(BasisAvatar avatar)
		{
			BasisNetworkPlayer local = BasisNetworkPlayer.LocalPlayer;
			return (local != null && avatar.TryGetLinkedPlayer(out ushort id) && id == local.playerId) || (BasisLocalPlayer.Instance is BasisLocalPlayer player && ReferenceEquals(player.BasisAvatar, avatar));
		}

		internal static bool TryRunning(out CilboxBasisCommon box)
		{
			box = null;
			foreach (CilboxBasisCommon other in UnityEngine.Object.FindObjectsByType<CilboxBasisCommon>(FindObjectsInactive.Include))
			{
				if (other == null || other.interpreterAccountingDepth == 0) continue;
				if (box != null)
				{
					box = null;
					return false;
				}
				box = other;
			}
			return box != null;
		}

		private static void Record(CilboxBasisCommon box, List<CilboxProxy> proxies)
		{
			foreach (CilboxProxy proxy in proxies)
			{
				if (proxy == null || !BasisContent.TryResolve(proxy.gameObject, out BasisContentBase content)) continue;
				box.ownerContent = content;
				box.ownerRecorded = true;
				return;
			}
		}

		private static void Gate(CilboxBasisCommon box, List<CilboxProxy> proxies)
		{
			if (!box.ownerRecorded) Record(box, proxies);
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
			if (!BasisCilboxPermissions.UntrustedAllowed)
			{
				box.consentState = State.Denied;
				return;
			}
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
				request = new Request { Hash = hash, Name = NameOf(box), Kind = KindOf(box), Items = result.Items, Blocked = result.Blocked, Uses = result.Uses };
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
			if (!result.Failed && result.Items.Length > 0) result.Uses = Uses(box, scan);
			return result;
		}

		private static List<BasisCilboxPermissionText.Use> Uses(CilboxBasisCommon box, Scan scan)
		{
			List<BasisCilboxPermissionText.Use> uses = new List<BasisCilboxPermissionText.Use>();
			if (box.classesList == null) return uses;
			HashSet<string> wanted = new HashSet<string>(scan.Items, StringComparer.Ordinal);
			wanted.UnionWith(scan.Blocked);
			HashSet<(string, string, string)> seen = new HashSet<(string, string, string)>();
			Dictionary<uint, List<(string Item, string Code)>> tokens = new Dictionary<uint, List<(string Item, string Code)>>();
			List<(string Item, string Code)> found = new List<(string Item, string Code)>();
			try
			{
				foreach (CilboxClass cls in box.classesList)
				{
					if (cls == null) continue;
					found.Clear();
					AddTypes(found, wanted, cls.staticFieldTypes);
					AddTypes(found, wanted, cls.instanceFieldTypes);
					Record(uses, seen, found, Where(cls.className, null));
					if (cls.methods == null) continue;
					foreach (CilboxMethod method in cls.methods)
					{
						if (method == null) continue;
						found.Clear();
						AddTypes(found, wanted, method.typeParameters);
						AddTypes(found, wanted, method.typeLocals);
						if (method.exceptionClauses != null)
						{
							foreach (CilboxExceptionHandlingClause clause in method.exceptionClauses)
							{
								if (clause != null) AddType(found, wanted, clause.CatchType, null, 0);
							}
						}
						Walk(box, method.byteCode, wanted, tokens, found);
						Record(uses, seen, found, Where(cls.className, method.methodName));
					}
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Cilbox] Could not work out where the scripts in {NameOf(box)} use the permissions they ask for: {e.Message}");
			}
			return uses;
		}

		private static void Walk(CilboxBasisCommon box, byte[] code, HashSet<string> wanted, Dictionary<uint, List<(string Item, string Code)>> tokens, List<(string Item, string Code)> found)
		{
			if (code == null || box.metadatas == null) return;
			int at = 0;
			while (at < code.Length)
			{
				CilboxUtil.OpCodes.OperandType operand = CilboxUtil.OpCodes.ReadOpCode(code, ref at).OperandType;
				if (operand == CilboxUtil.OpCodes.OperandType.InlineSwitch)
				{
					if (at + 4 > code.Length) return;
					uint cases = (uint)CilboxUtil.BytecodePullLiteral(code, ref at, 4);
					if (cases > (uint)(code.Length - at) / 4) return;
					at += (int)cases * 4;
					continue;
				}
				int length = CilboxUtil.OpCodes.OperandLength[(int)operand];
				if (operand != CilboxUtil.OpCodes.OperandType.InlineMethod && operand != CilboxUtil.OpCodes.OperandType.InlineField && operand != CilboxUtil.OpCodes.OperandType.InlineType)
				{
					at += length;
					continue;
				}
				if (at + 4 > code.Length) return;
				uint token = (uint)CilboxUtil.BytecodePullLiteral(code, ref at, 4);
				if (!tokens.TryGetValue(token, out List<(string Item, string Code)> pairs))
				{
					pairs = TokenUses(box, token, wanted);
					tokens[token] = pairs;
				}
				found.AddRange(pairs);
			}
		}

		private static List<(string Item, string Code)> TokenUses(CilboxBasisCommon box, uint token, HashSet<string> wanted)
		{
			List<(string Item, string Code)> pairs = new List<(string Item, string Code)>();
			CilMetadataTokenInfo info = token < box.metadatas.Length ? box.metadatas[token] : null;
			if (info == null) return pairs;
			if (info.nativeMethod != null && info.nativeMethod.DeclaringType != null)
			{
				MethodBase method = info.nativeMethod;
				string call = CallOf(method);
				Add(pairs, wanted, BasisCilboxPermissionText.MethodPrefix + Cilbox.GetSanitizedTypeName(method.DeclaringType) + "." + method.Name, call);
				AddType(pairs, wanted, method.DeclaringType, call, 0);
				foreach (ParameterInfo parameter in method.GetParameters()) AddType(pairs, wanted, parameter.ParameterType, null, 0);
				if (method is MethodInfo function) AddType(pairs, wanted, function.ReturnType, null, 0);
				if (method.IsGenericMethod)
				{
					foreach (Type argument in method.GetGenericArguments()) AddType(pairs, wanted, argument, null, 0);
				}
			}
			else if (info.nativeField != null && info.nativeField.DeclaringType != null)
			{
				FieldInfo field = info.nativeField;
				string access = ShortType(field.DeclaringType) + "." + field.Name;
				Add(pairs, wanted, BasisCilboxPermissionText.FieldPrefix + Cilbox.GetSanitizedTypeName(field.DeclaringType) + "." + field.Name, access);
				AddType(pairs, wanted, field.DeclaringType, access, 0);
				AddType(pairs, wanted, field.FieldType, null, 0);
			}
			else
			{
				AddType(pairs, wanted, info.nativeType, null, 0);
				AddType(pairs, wanted, info.opaque as Type, null, 0);
			}
			return pairs;
		}

		private static void AddTypes(List<(string Item, string Code)> pairs, HashSet<string> wanted, Type[] types)
		{
			if (types == null) return;
			foreach (Type type in types) AddType(pairs, wanted, type, null, 0);
		}

		private static void AddType(List<(string Item, string Code)> pairs, HashSet<string> wanted, Type type, string code, int depth)
		{
			if (type == null || type.IsGenericParameter || depth > 8) return;
			if (type.HasElementType)
			{
				if (type.IsPointer && type.FullName != null) Add(pairs, wanted, BasisCilboxPermissionText.TypePrefix + type.FullName, code ?? ShortType(type));
				AddType(pairs, wanted, type.GetElementType(), code, depth + 1);
				return;
			}
			if (type.FullName != null) Add(pairs, wanted, BasisCilboxPermissionText.TypePrefix + Cilbox.GetSanitizedTypeName(type), code ?? ShortType(type));
			if (!type.IsGenericType) return;
			foreach (Type argument in type.GetGenericArguments()) AddType(pairs, wanted, argument, null, depth + 1);
		}

		private static void Add(List<(string Item, string Code)> pairs, HashSet<string> wanted, string item, string code)
		{
			if (wanted.Contains(item)) pairs.Add((item, code));
		}

		private static void Record(List<BasisCilboxPermissionText.Use> uses, HashSet<(string, string, string)> seen, List<(string Item, string Code)> found, string where)
		{
			foreach ((string item, string code) in found)
			{
				if (seen.Add((item, where, code))) uses.Add(new BasisCilboxPermissionText.Use(item, where, code));
			}
		}

		private static string CallOf(MethodBase method)
		{
			string owner = ShortType(method.DeclaringType);
			if (method.IsConstructor) return "new " + owner + "()";
			if (method.IsSpecialName && method.Name.StartsWith("get_", StringComparison.Ordinal)) return owner + "." + method.Name.Substring(4);
			if (method.IsSpecialName && method.Name.StartsWith("set_", StringComparison.Ordinal)) return owner + "." + method.Name.Substring(4) + " =";
			return owner + "." + method.Name + "()";
		}

		private static string ShortType(Type type)
		{
			string name = type.FullName != null ? Cilbox.GetSanitizedTypeName(type) : type.Name;
			return name.Substring(name.LastIndexOf('.') + 1).Replace('+', '.');
		}

		private static string Where(string className, string methodName)
		{
			string type = className ?? string.Empty, member = methodName;
			int generated = type.IndexOf("+<", StringComparison.Ordinal);
			if (generated >= 0)
			{
				string inner = Angled(type, generated + 1);
				type = type.Substring(0, generated);
				if (!string.IsNullOrEmpty(inner)) member = inner;
			}
			if (member != null && member.StartsWith("<", StringComparison.Ordinal)) member = Angled(member, 0);
			type = type.Substring(type.LastIndexOf('.') + 1).Replace('+', '.');
			return string.IsNullOrEmpty(member) || member == ".ctor" || member == ".cctor" ? type : type + "." + member;
		}

		private static string Angled(string text, int open)
		{
			int close = text.IndexOf('>', open + 1);
			return close > open + 1 ? text.Substring(open + 1, close - open - 1) : null;
		}

		internal static bool Permits(CilboxBasisCommon box, string item)
		{
			if (box.consentState == State.Unchecked) LateCheck(box);
			return box.consentState == State.Allowed && box.consentGranted != null && box.consentGranted.Contains(item) && BasisCilboxPermissions.UntrustedAllowed;
		}

		private static void LateCheck(CilboxBasisCommon box)
		{
			box.consentState = State.Denied;
			if (!Application.isPlaying || string.IsNullOrEmpty(box.assemblyData) || !BasisCilboxPermissions.UntrustedAllowed) return;
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
				BasisCilboxConsentPrompt.Show(request.Name, request.Kind, request.Items, request.Blocked, request.Uses, answer => Answer(hash, answer), notice => Deferred(hash, notice));
				return;
			}
		}

		private static void Deferred(string hash, BasisNotification notice)
		{
			if (requests.TryGetValue(hash, out Request request)) request.Notice = notice;
			if (showing == hash) showing = null;
			ShowNext();
		}

		private static void Answer(string hash, BasisCilboxConsentPrompt.Answer answer)
		{
			if (showing == hash) showing = null;
			if (requests.TryGetValue(hash, out Request request))
			{
				requests.Remove(hash);
				bool allowed = answer != BasisCilboxConsentPrompt.Answer.Deny && BasisCilboxPermissions.UntrustedAllowed;
				if (answer == BasisCilboxConsentPrompt.Answer.Deny) BasisCilboxPermissions.Decline(hash);
				else if (allowed) BasisCilboxPermissions.Allow(hash, request.Name, request.Kind, request.Items, answer == BasisCilboxConsentPrompt.Answer.AllowAndRemember);
				Release(request, allowed);
			}
			ShowNext();
		}

		private static void Release(Request request, bool allowed)
		{
			foreach (CilboxBasisCommon box in request.Boxes)
			{
				if (box == null) continue;
				if (allowed) Allow(box, request.Items);
				else box.consentState = State.Denied;
			}
			foreach (CilboxProxy proxy in request.Held)
			{
				if (proxy != null) proxy.enabled = true;
			}
		}

		private static void Disallow()
		{
			List<Request> pending = new List<Request>(requests.Values);
			requests.Clear();
			queue.Clear();
			showing = null;
			foreach (Request request in pending)
			{
				Release(request, false);
				BasisNotificationCenter.Dismiss(request.Notice);
			}
			Stop(null, "Untrusted script permissions were turned off in Settings.");
		}

		private static void Revoke(string hash) => Stop(hash, "Its script permissions were removed in Settings.");

		private static void Stop(string hash, string reason)
		{
			foreach (CilboxBasisCommon box in UnityEngine.Object.FindObjectsByType<CilboxBasisCommon>(FindObjectsInactive.Include))
			{
				if (box.consentState != State.Allowed || (hash != null && !string.Equals(box.consentHash, hash, StringComparison.Ordinal))) continue;
				box.consentState = State.Denied;
				box.consentGranted = null;
				box.disabledReason = reason;
				box.disabled = true;
				Debug.LogWarning($"[Cilbox] Stopped the scripts in {NameOf(box)}. {reason}");
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

		internal static string NameOf(CilboxBasisCommon box)
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
