using System;
using System.Collections.Generic;
using Basis.EventDriver;
using Basis.Scripts.BasisSdk.Interactions;
using Cilbox;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Shims
{
	[Flags]
	public enum BasisHandBlock
	{
		None = 0,
		Raycast = 1,
		Pickup = 2,
		Touch = 4,
		All = Raycast | Pickup | Touch,
	}

	public static class BasisHandBlockShim
	{
		public const int Left = 0, Right = 1, Both = 2;
		public static bool Block(object self, int hand, BasisHandBlock kinds) => CilboxHandBlocks.Change(self, hand, kinds, true);
		public static bool Unblock(object self, int hand, BasisHandBlock kinds) => CilboxHandBlocks.Change(self, hand, kinds, false);
		public static bool UnblockAll(object self) => CilboxHandBlocks.Change(self, Both, BasisHandBlock.All, false);
		public static bool IsBlocking(object self, int hand, BasisHandBlock kinds) => CilboxHandBlocks.Holds(self, hand, kinds);
		public static bool IsBlocked(object self, int hand, BasisHandBlock kinds) => CilboxHandBlocks.Any(self, hand, kinds);
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxHandBlocks
	{
		private sealed class Holder
		{
			internal CilboxProxy proxy;
			internal CilboxBasisCommon box;
			internal string key;
		}
		private static readonly List<Holder> holders = new List<Holder>();
		private static bool hooked;
		[NoAutoStaticsCleanup] private static readonly Action tickHandler = Tick;
		private static bool Valid(int hand, Basis.Shims.BasisHandBlock kinds) => hand >= Basis.Shims.BasisHandBlockShim.Left && hand <= Basis.Shims.BasisHandBlockShim.Both && BasisHandBlocks.ValidKinds((BasisHandBlockKind)(int)kinds);
		private static bool Covers(int hand, int side) => hand == Basis.Shims.BasisHandBlockShim.Both || hand == side;
		private static bool Running(object self, out CilboxProxy proxy, out CilboxBasisCommon box)
		{
			proxy = self as CilboxProxy;
			box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			return proxy != null && !proxy.disabled && box != null && !box.disabled && box.interpreterAccountingDepth != 0;
		}
		private static bool Alive(Holder holder) => holder.proxy != null && !holder.proxy.disabled && holder.proxy.isActiveAndEnabled && holder.box != null && !holder.box.disabled;
		private static int Find(CilboxProxy proxy)
		{
			for (int i = 0; i < holders.Count; i++) if (ReferenceEquals(holders[i].proxy, proxy)) return i;
			return -1;
		}
		internal static bool Change(object self, int hand, Basis.Shims.BasisHandBlock kinds, bool add)
		{
			if (!Valid(hand, kinds) || !Running(self, out CilboxProxy proxy, out CilboxBasisCommon box)) return false;
			if (add && !CilboxBasisConsent.ForLocalPlayer(box, proxy.gameObject)) return false;
			int index = Find(proxy);
			if (!add && index < 0) return true;
			Holder holder = index >= 0 ? holders[index] : new Holder { proxy = proxy, box = box, key = "Cilbox:" + proxy.GetEntityId() };
			BasisHandBlockKind kind = (BasisHandBlockKind)(int)kinds;
			for (int side = BasisHandBlocks.Left; side <= BasisHandBlocks.Right; side++)
			{
				if (!Covers(hand, side)) continue;
				if (add) BasisHandBlocks.Add(holder.key, side, kind);
				else BasisHandBlocks.Remove(holder.key, side, kind);
			}
			bool holds = BasisHandBlocks.Holds(holder.key);
			if (holds && index < 0)
			{
				holders.Add(holder);
				Hook();
			}
			else if (!holds && index >= 0) Release(index);
			return true;
		}
		internal static bool Holds(object self, int hand, Basis.Shims.BasisHandBlock kinds)
		{
			if (!Valid(hand, kinds) || !Running(self, out CilboxProxy proxy, out _)) return false;
			int index = Find(proxy);
			if (index < 0) return false;
			BasisHandBlockKind kind = (BasisHandBlockKind)(int)kinds;
			for (int side = BasisHandBlocks.Left; side <= BasisHandBlocks.Right; side++) if (Covers(hand, side) && (BasisHandBlocks.HeldBy(holders[index].key, side) & kind) != kind) return false;
			return true;
		}
		internal static bool Any(object self, int hand, Basis.Shims.BasisHandBlock kinds)
		{
			if (!Valid(hand, kinds) || !Running(self, out _, out _)) return false;
			BasisHandBlockKind kind = (BasisHandBlockKind)(int)kinds;
			for (int side = BasisHandBlocks.Left; side <= BasisHandBlocks.Right; side++) if (Covers(hand, side) && BasisHandBlocks.IsBlocked(side, kind)) return true;
			return false;
		}
		private static void Release(int index)
		{
			BasisHandBlocks.RemoveOwner(holders[index].key);
			holders.RemoveAt(index);
			if (holders.Count == 0) Unhook();
		}
		private static void ReleaseEveryone()
		{
			for (int i = holders.Count - 1; i >= 0; i--) Release(i);
		}
		private static void Tick()
		{
			for (int i = holders.Count - 1; i >= 0; i--) if (!Alive(holders[i])) Release(i);
		}
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void HookRespawn()
		{
			BasisSceneFactory.OnUserRespawn -= ReleaseEveryone;
			BasisSceneFactory.OnUserRespawn += ReleaseEveryone;
		}
		private static void Hook()
		{
			if (hooked) return;
			hooked = true;
			BasisEventDriver.OnUpdate += tickHandler;
		}
		private static void Unhook()
		{
			if (!hooked) return;
			hooked = false;
			BasisEventDriver.OnUpdate -= tickHandler;
		}
	}
}
