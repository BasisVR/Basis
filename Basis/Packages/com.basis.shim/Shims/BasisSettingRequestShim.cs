using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Shims
{
	public enum BasisSettingRequestResult
	{
		Shown = 0,
		Declined = 1,
		Blocked = 2,
		Busy = 3,
		Unsupported = 4,
		NotFound = 5,
		Throttled = 6,
	}

	public static class BasisSettingRequestShim
	{
		public static bool Request(object self, string settingKey) => CilboxSettingRequests.Request(self, settingKey, null, null);
		public static bool Request(object self, string settingKey, string message) => CilboxSettingRequests.Request(self, settingKey, message, null);
		public static bool Request(object self, string settingKey, string message, Action<BasisSettingRequestResult> onResult) => CilboxSettingRequests.Request(self, settingKey, message, onResult);
		public static bool IsAsking(object self) => CilboxSettingRequests.IsAsking(self);
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxSettingRequests
	{
		private const float RequestSpacing = 5f;
		private static readonly Dictionary<CilboxProxy, float> lastAsked = new Dictionary<CilboxProxy, float>();
		private static readonly List<CilboxProxy> stale = new List<CilboxProxy>();
		private static bool Running(object self, out CilboxProxy proxy, out CilboxBasisCommon box)
		{
			proxy = self as CilboxProxy;
			box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			return proxy != null && !proxy.disabled && box != null && !box.disabled && box.interpreterAccountingDepth != 0;
		}
		private static bool Alive(CilboxProxy proxy) => proxy != null && !proxy.disabled && proxy.isActiveAndEnabled && proxy.box is CilboxBasisCommon box && !box.disabled;
		internal static bool IsAsking(object self) => Running(self, out _, out _) && BasisSettingRequests.IsAsking;
		internal static bool Request(object self, string settingKey, string message, Action<Basis.Shims.BasisSettingRequestResult> onResult)
		{
			if (!Running(self, out CilboxProxy proxy, out CilboxBasisCommon box) || !BasisSettingRequests.ValidKey(settingKey)) return Reply(null, onResult, Basis.Shims.BasisSettingRequestResult.Blocked);
			if (!Describe(box, proxy.gameObject, out string askerId, out string contentName, out string kind, out string ownerName)) return Reply(null, onResult, Basis.Shims.BasisSettingRequestResult.Blocked);
			float now = Time.realtimeSinceStartup;
			Prune();
			if (lastAsked.TryGetValue(proxy, out float last) && now - last < RequestSpacing) return Reply(null, onResult, Basis.Shims.BasisSettingRequestResult.Throttled);
			lastAsked[proxy] = now;
			bool settled = false;
			BasisSettingRequests.Request(askerId, contentName, kind, ownerName, settingKey, message, result =>
			{
				settled = true;
				Reply(proxy, onResult, (Basis.Shims.BasisSettingRequestResult)(int)result);
			});
			return !settled;
		}
		private static bool Describe(CilboxBasisCommon box, GameObject caller, out string askerId, out string contentName, out string kind, out string ownerName)
		{
			askerId = null;
			contentName = null;
			kind = null;
			ownerName = null;
			CilboxBasisConsent.TryOwner(box, caller, out BasisContentBase content);
			if (box is CilboxSceneBasis || content is BasisScene)
			{
				string scene = caller.scene.IsValid() ? caller.scene.name : string.Empty;
				askerId = "world:" + (content != null ? content.GetEntityId().ToString() : scene);
				contentName = content != null ? ContentName(content) : scene;
				kind = BasisLocalization.Get("externalLinks.kind.world");
				return true;
			}
			if (content == null) return false;
			kind = BasisLocalization.Get(content is BasisAvatar ? "playerLocks.kind.avatar" : "externalLinks.kind.prop");
			contentName = ContentName(content);
			if (CilboxBasisConsent.ForLocalPlayer(box, caller))
			{
				askerId = "local:" + content.GetEntityId();
				return true;
			}
			BasisNetworkPlayer player = null;
			if (content is BasisAvatar avatar)
			{
				if (!(box is CilboxAvatarBasis) || !avatar.TryGetLinkedPlayer(out ushort id) || !BasisNetworkPlayers.GetPlayerById(id, out player)) return false;
			}
			else if (box is CilboxPropBasis && content is BasisProp)
			{
				string creator = content.CreatorUUID;
				if (string.IsNullOrEmpty(creator) || !BasisNetworkPlayers.TryGetPlayerByUUID(creator, out player)) return false;
			}
			else return false;
			if (player == null || player.IsLocal) return false;
			askerId = player.Player != null && !string.IsNullOrEmpty(player.Player.UUID) ? player.Player.UUID : "id:" + player.playerId;
			ownerName = !string.IsNullOrWhiteSpace(player.SafeDisplayName) ? player.SafeDisplayName : player.displayName;
			return true;
		}
		private static string ContentName(BasisContentBase content)
		{
			string name = content.BasisBundleDescription != null ? content.BasisBundleDescription.AssetBundleName : null;
			return string.IsNullOrWhiteSpace(name) ? content.gameObject.name.Replace("(Clone)", string.Empty).Trim() : name;
		}
		private static void Prune()
		{
			stale.Clear();
			float now = Time.realtimeSinceStartup;
			foreach (KeyValuePair<CilboxProxy, float> pair in lastAsked) if (pair.Key == null || now - pair.Value >= RequestSpacing) stale.Add(pair.Key);
			for (int i = 0; i < stale.Count; i++) lastAsked.Remove(stale[i]);
			stale.Clear();
		}
		private static bool Reply(CilboxProxy proxy, Action<Basis.Shims.BasisSettingRequestResult> onResult, Basis.Shims.BasisSettingRequestResult result)
		{
			if (onResult != null && (proxy == null || Alive(proxy)))
			{
				try
				{
					onResult(result);
				}
				catch (Exception e)
				{
					BasisDebug.LogErrorUnreported($"[SettingRequests] A setting request callback threw: {e}");
				}
			}
			return result == Basis.Shims.BasisSettingRequestResult.Shown;
		}
	}
}
