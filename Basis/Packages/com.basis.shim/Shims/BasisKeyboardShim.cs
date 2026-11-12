using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
using TMPro;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Shims
{
	public enum BasisKeyboardContent
	{
		Standard = 0,
		Integer = 1,
		Decimal = 2,
		Alphanumeric = 3,
	}

	public static class BasisKeyboardShim
	{
		public static bool Open(object self, Action<string> onSubmit) => CilboxKeyboard.Open(self, null, null, 0, BasisKeyboardContent.Standard, onSubmit, null);
		public static bool Open(object self, string text, Action<string> onSubmit) => CilboxKeyboard.Open(self, text, null, 0, BasisKeyboardContent.Standard, onSubmit, null);
		public static bool Open(object self, string text, string prompt, Action<string> onSubmit, Action onCancel) => CilboxKeyboard.Open(self, text, prompt, 0, BasisKeyboardContent.Standard, onSubmit, onCancel);
		public static bool Open(object self, string text, string prompt, int characterLimit, BasisKeyboardContent content, Action<string> onSubmit, Action onCancel) => CilboxKeyboard.Open(self, text, prompt, characterLimit, content, onSubmit, onCancel);
		public static bool IsOpen(object self) => CilboxKeyboard.IsOpen(self);
		public static string GetText(object self) => CilboxKeyboard.GetText(self);
		public static void Close(object self) => CilboxKeyboard.Close(self);
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxKeyboard
	{
		private const float OpenSpacing = 2f;
		private const int MaxPromptLength = 128;
		private static readonly Dictionary<CilboxProxy, float> lastOpened = new Dictionary<CilboxProxy, float>();
		private static readonly List<CilboxProxy> stale = new List<CilboxProxy>();
		private static bool Running(object self, out CilboxProxy proxy, out CilboxBasisCommon box)
		{
			proxy = self as CilboxProxy;
			box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			return proxy != null && !proxy.disabled && box != null && !box.disabled && box.interpreterAccountingDepth != 0;
		}
		private static bool Alive(CilboxProxy proxy) => proxy != null && !proxy.disabled && proxy.isActiveAndEnabled && proxy.box is CilboxBasisCommon box && !box.disabled;
		internal static bool IsOpen(object self) => Running(self, out CilboxProxy proxy, out _) && BasisMenuVirtualKeyboardPanel.IsTextRequestOpen(proxy);
		internal static string GetText(object self) => Running(self, out CilboxProxy proxy, out _) ? BasisMenuVirtualKeyboardPanel.TextRequestText(proxy) : null;
		internal static void Close(object self)
		{
			if (Running(self, out CilboxProxy proxy, out _)) BasisMenuVirtualKeyboardPanel.CloseTextRequest(proxy);
		}
		internal static bool Open(object self, string text, string prompt, int characterLimit, Basis.Shims.BasisKeyboardContent content, Action<string> onSubmit, Action onCancel)
		{
			if (!Running(self, out CilboxProxy proxy, out CilboxBasisCommon box) || !Describe(box, proxy.gameObject, out string asker)) return false;
			float now = Time.realtimeSinceStartup;
			Prune(now);
			if (lastOpened.TryGetValue(proxy, out float last) && now - last < OpenSpacing && !BasisMenuVirtualKeyboardPanel.IsTextRequestOpen(proxy)) return false;
			lastOpened[proxy] = now;
			if (!string.IsNullOrEmpty(prompt) && prompt.Length > MaxPromptLength) prompt = prompt.Substring(0, MaxPromptLength);
			Action<string> submitted = onSubmit == null ? null : result =>
			{
				if (!Alive(proxy)) return;
				onSubmit(result);
			};
			Action cancelled = onCancel == null ? null : () =>
			{
				if (!Alive(proxy)) return;
				onCancel();
			};
			return BasisMenuVirtualKeyboardPanel.OpenTextRequest(proxy, asker, prompt, text, characterLimit, ContentType(content), submitted, cancelled);
		}
		private static TMP_InputField.ContentType ContentType(Basis.Shims.BasisKeyboardContent content)
		{
			switch (content)
			{
				case Basis.Shims.BasisKeyboardContent.Integer: return TMP_InputField.ContentType.IntegerNumber;
				case Basis.Shims.BasisKeyboardContent.Decimal: return TMP_InputField.ContentType.DecimalNumber;
				case Basis.Shims.BasisKeyboardContent.Alphanumeric: return TMP_InputField.ContentType.Alphanumeric;
				default: return TMP_InputField.ContentType.Standard;
			}
		}
		private static bool Describe(CilboxBasisCommon box, GameObject caller, out string asker)
		{
			asker = null;
			CilboxBasisConsent.TryOwner(box, caller, out BasisContentBase content);
			if (box is CilboxSceneBasis || content is BasisScene)
			{
				asker = content != null ? ContentName(content) : caller.scene.IsValid() ? caller.scene.name : string.Empty;
				return true;
			}
			if (content == null) return false;
			if (CilboxBasisConsent.ForLocalPlayer(box, caller))
			{
				asker = ContentName(content);
				return true;
			}
			if (!CilboxBasisConsent.ForRemoteProp(box, caller)) return false;
			string creator = content.CreatorUUID;
			if (string.IsNullOrEmpty(creator) || !BasisNetworkPlayers.TryGetPlayerByUUID(creator, out BasisNetworkPlayer player) || player == null || player.IsLocal) return false;
			string owner = !string.IsNullOrWhiteSpace(player.SafeDisplayName) ? player.SafeDisplayName : player.displayName;
			asker = string.IsNullOrWhiteSpace(owner) ? ContentName(content) : ContentName(content) + " (" + owner + ")";
			return true;
		}
		private static string ContentName(BasisContentBase content)
		{
			string name = content.BasisBundleDescription != null ? content.BasisBundleDescription.AssetBundleName : null;
			return string.IsNullOrWhiteSpace(name) ? content.gameObject.name.Replace("(Clone)", string.Empty).Trim() : name;
		}
		private static void Prune(float now)
		{
			stale.Clear();
			foreach (KeyValuePair<CilboxProxy, float> pair in lastOpened) if (pair.Key == null || now - pair.Value >= OpenSpacing) stale.Add(pair.Key);
			for (int i = 0; i < stale.Count; i++) lastOpened.Remove(stale[i]);
			stale.Clear();
		}
	}
}
