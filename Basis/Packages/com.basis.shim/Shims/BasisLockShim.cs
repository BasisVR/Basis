using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.EventDriver;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Shims
{
	[Flags]
	public enum BasisPlayerLock
	{
		None = 0,
		Movement = 1,
		LookRotation = 2,
		Crouching = 4,
		All = Movement | LookRotation | Crouching,
	}

	public enum BasisLockResult
	{
		Locked = 0,
		Declined = 1,
		Blocked = 2,
		Busy = 3,
		Unsupported = 4,
		Throttled = 5,
	}

	/// <summary>
	/// Lets a script hold the local player's BasisLocks (movement, look rotation, crouching). Each script owns
	/// its own lock key, so it can only release what it took, and every lock it holds is released as soon as
	/// the script is disabled or destroyed.
	///
	/// Worlds, props the local player spawned, and the avatar the local player is wearing lock straight away.
	/// Content someone else owns (their avatar, or a prop they spawned) has to ask first: the local player gets
	/// a prompt naming the player and the content, and the answer covers only the kinds that were shown. Declines
	/// are counted against that player, not the content, with a cooldown and then a mute for the session.
	///
	/// Respawning from the menu releases every script lock, revokes every grant, cancels an open prompt and
	/// refuses new locks for a few seconds. A script that keeps re-taking locks is throttled.
	/// </summary>
	public static class BasisLockShim
	{
		public static bool Lock(object self, BasisPlayerLock kinds) => CilboxPlayerLocks.Lock(self, kinds, null);
		public static bool Lock(object self, BasisPlayerLock kinds, Action<BasisLockResult> onResult) => CilboxPlayerLocks.Lock(self, kinds, onResult);
		public static bool Unlock(object self, BasisPlayerLock kinds) => CilboxPlayerLocks.Unlock(self, kinds);
		public static void UnlockAll(object self) => CilboxPlayerLocks.Unlock(self, BasisPlayerLock.All);
		public static bool IsLocking(object self, BasisPlayerLock kinds) => CilboxPlayerLocks.Holds(self, kinds);
		public static bool IsLocked(object self, BasisPlayerLock kinds) => CilboxPlayerLocks.Any(self, kinds);
		public static bool IsGranted(object self, BasisPlayerLock kinds) => CilboxPlayerLocks.Granted(self, kinds);
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxPlayerLocks
	{
		private sealed class Holder
		{
			internal CilboxProxy proxy;
			internal CilboxBasisCommon box;
			internal string key;
			internal byte mask;
			internal float windowStart;
			internal int takes;
		}
		private sealed class Waiter
		{
			internal CilboxProxy proxy;
			internal CilboxBasisCommon box;
			internal byte kinds;
			internal Action<Basis.Shims.BasisLockResult> onResult;
		}
		private sealed class Pending
		{
			internal BasisContentBase content;
			internal byte kinds;
			internal readonly List<Waiter> waiters = new List<Waiter>();
		}
		private const int KindCount = 3;
		private const byte AllKinds = (byte)Basis.Shims.BasisPlayerLock.All;
		private const float RespawnCooldown = 10f;
		private const float TakeWindow = 2f;
		private const int MaxTakesPerWindow = 6;
		private static float refuseUntil = -1f;
		private static readonly List<Holder> holders = new List<Holder>();
		private static readonly Dictionary<BasisContentBase, byte> grants = new Dictionary<BasisContentBase, byte>();
		private static readonly List<BasisContentBase> stale = new List<BasisContentBase>();
		private static Pending pending;
		private static bool hooked;
		[NoAutoStaticsCleanup] private static readonly Action tickHandler = Tick;
		[NoAutoStaticsCleanup] private static readonly BasisLocks.LockContext[] contexts = { BasisLocks.GetContext(BasisLocks.Movement), BasisLocks.GetContext(BasisLocks.LookRotation), BasisLocks.GetContext(BasisLocks.Crouching) };
		private static bool Valid(Basis.Shims.BasisPlayerLock kinds) => kinds != 0 && ((int)kinds & ~AllKinds) == 0;
		private static bool Running(object self, out CilboxProxy proxy, out CilboxBasisCommon box)
		{
			proxy = self as CilboxProxy;
			box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			return proxy != null && !proxy.disabled && box != null && !box.disabled && box.interpreterAccountingDepth != 0;
		}
		private static bool Alive(CilboxProxy proxy, CilboxBasisCommon box) => proxy != null && !proxy.disabled && proxy.isActiveAndEnabled && box != null && !box.disabled;
		private static bool Caller(object self, out CilboxProxy proxy, out CilboxBasisCommon box, out bool local, out BasisContentBase content)
		{
			local = false;
			content = null;
			if (!Running(self, out proxy, out box)) return false;
			if (CilboxBasisConsent.ForLocalPlayer(box, proxy.gameObject))
			{
				local = true;
				return true;
			}
			return Remote(box, proxy.gameObject, out content, out _, out _);
		}
		private static bool Remote(CilboxBasisCommon box, GameObject caller, out BasisContentBase content, out string ownerId, out string ownerName)
		{
			ownerId = null;
			ownerName = null;
			content = null;
			if (box is CilboxSceneBasis || !CilboxBasisConsent.TryOwner(box, caller, out content) || content == null || content is BasisScene) return false;
			BasisNetworkPlayer player = null;
			if (content is BasisAvatar avatar)
			{
				if (!(box is CilboxAvatarBasis)) return false;
				if (!avatar.TryGetLinkedPlayer(out ushort id) || !BasisNetworkPlayers.GetPlayerById(id, out player)) return false;
			}
			else if (box is CilboxPropBasis && content is BasisProp && !content.SpawnedByLocalPlayer)
			{
				string creator = content.CreatorUUID;
				if (string.IsNullOrEmpty(creator) || !BasisNetworkPlayers.TryGetPlayerByUUID(creator, out player)) return false;
			}
			else return false;
			if (player == null || player.IsLocal) return false;
			ownerId = player.Player != null && !string.IsNullOrEmpty(player.Player.UUID) ? player.Player.UUID : "id:" + player.playerId;
			ownerName = !string.IsNullOrWhiteSpace(player.SafeDisplayName) ? player.SafeDisplayName : player.displayName;
			return true;
		}
		private static string ContentName(BasisContentBase content)
		{
			string name = content.BasisBundleDescription != null ? content.BasisBundleDescription.AssetBundleName : null;
			return string.IsNullOrWhiteSpace(name) ? content.gameObject.name.Replace("(Clone)", string.Empty).Trim() : name;
		}
		private static int Find(CilboxProxy proxy)
		{
			for (int i = 0; i < holders.Count; i++) if (ReferenceEquals(holders[i].proxy, proxy)) return i;
			return -1;
		}
		private static byte GrantOf(BasisContentBase content) => content != null && grants.TryGetValue(content, out byte mask) ? mask : (byte)0;
		internal static bool Lock(object self, Basis.Shims.BasisPlayerLock kinds, Action<Basis.Shims.BasisLockResult> onResult)
		{
			if (!Valid(kinds) || !Caller(self, out CilboxProxy proxy, out CilboxBasisCommon box, out bool local, out BasisContentBase content)) return Reply(onResult, Basis.Shims.BasisLockResult.Blocked);
			byte want = (byte)kinds;
			if (Time.realtimeSinceStartup < refuseUntil) return Reply(onResult, Basis.Shims.BasisLockResult.Blocked);
			if (!local && (GrantOf(content) & want) != want) return Ask(proxy, box, content, want, onResult);
			return Reply(onResult, Take(proxy, box, want));
		}
		private static Basis.Shims.BasisLockResult Take(CilboxProxy proxy, CilboxBasisCommon box, byte want)
		{
			int index = Find(proxy);
			Holder holder = index >= 0 ? holders[index] : new Holder { proxy = proxy, box = box, key = "Cilbox:" + proxy.GetEntityId() };
			byte missing = (byte)(want & ~holder.mask);
			if (missing == 0) return Basis.Shims.BasisLockResult.Locked;
			float now = Time.realtimeSinceStartup;
			if (now - holder.windowStart > TakeWindow)
			{
				holder.windowStart = now;
				holder.takes = 0;
			}
			if (holder.takes >= MaxTakesPerWindow) return Basis.Shims.BasisLockResult.Throttled;
			holder.takes++;
			for (int k = 0; k < KindCount; k++) if ((missing & (1 << k)) != 0) contexts[k].Add(holder.key);
			holder.mask |= missing;
			if (index < 0) holders.Add(holder);
			Hook();
			return Basis.Shims.BasisLockResult.Locked;
		}
		private static bool Ask(CilboxProxy proxy, CilboxBasisCommon box, BasisContentBase content, byte want, Action<Basis.Shims.BasisLockResult> onResult)
		{
			if (pending != null)
			{
				if (!ReferenceEquals(pending.content, content) || (pending.kinds & want) != want) return Reply(onResult, Basis.Shims.BasisLockResult.Busy);
				pending.waiters.Add(new Waiter { proxy = proxy, box = box, kinds = want, onResult = onResult });
				return false;
			}
			if (!Remote(box, proxy.gameObject, out _, out string ownerId, out string ownerName)) return Reply(onResult, Basis.Shims.BasisLockResult.Blocked);
			if (BasisPlayerLockRequests.IsMuted(ownerId)) return Reply(onResult, Basis.Shims.BasisLockResult.Blocked);
			if (BasisPlayerLockRequests.IsAsking) return Reply(onResult, Basis.Shims.BasisLockResult.Busy);
			Pending ask = new Pending { content = content, kinds = want };
			ask.waiters.Add(new Waiter { proxy = proxy, box = box, kinds = want, onResult = onResult });
			pending = ask;
			BasisPlayerLockRequests.Request(ownerId, ownerName, ContentName(content), content is BasisAvatar, (BasisPlayerLockRequests.Kinds)want, result => Answered(ask, result));
			return false;
		}
		private static void Answered(Pending ask, BasisPlayerLockRequests.Result result)
		{
			if (ReferenceEquals(pending, ask)) pending = null;
			bool accepted = result == BasisPlayerLockRequests.Result.Accepted && ask.content != null && Time.realtimeSinceStartup >= refuseUntil;
			if (accepted)
			{
				PruneGrants();
				grants[ask.content] = (byte)(GrantOf(ask.content) | ask.kinds);
			}
			Basis.Shims.BasisLockResult refused = result == BasisPlayerLockRequests.Result.Busy ? Basis.Shims.BasisLockResult.Busy
				: result == BasisPlayerLockRequests.Result.Blocked ? Basis.Shims.BasisLockResult.Blocked
				: result == BasisPlayerLockRequests.Result.Unsupported ? Basis.Shims.BasisLockResult.Unsupported
				: Basis.Shims.BasisLockResult.Declined;
			for (int i = 0; i < ask.waiters.Count; i++)
			{
				Waiter waiter = ask.waiters[i];
				if (!Alive(waiter.proxy, waiter.box)) continue;
				Reply(waiter.onResult, accepted ? Take(waiter.proxy, waiter.box, waiter.kinds) : refused);
			}
		}
		private static bool Reply(Action<Basis.Shims.BasisLockResult> onResult, Basis.Shims.BasisLockResult result)
		{
			if (onResult != null)
			{
				try
				{
					onResult(result);
				}
				catch (Exception e)
				{
					BasisDebug.LogErrorUnreported($"[PlayerLocks] A lock callback threw: {e}");
				}
			}
			return result == Basis.Shims.BasisLockResult.Locked;
		}
		internal static bool Unlock(object self, Basis.Shims.BasisPlayerLock kinds)
		{
			if (!Valid(kinds) || !Running(self, out CilboxProxy proxy, out _)) return false;
			int index = Find(proxy);
			if (index < 0) return true;
			Holder holder = holders[index];
			byte drop = (byte)(holder.mask & (byte)kinds);
			for (int k = 0; k < KindCount; k++) if ((drop & (1 << k)) != 0) contexts[k].Remove(holder.key);
			holder.mask = (byte)(holder.mask & ~drop);
			return true;
		}
		internal static bool Holds(object self, Basis.Shims.BasisPlayerLock kinds)
		{
			if (!Valid(kinds) || !Running(self, out CilboxProxy proxy, out _)) return false;
			int index = Find(proxy);
			return index >= 0 && (holders[index].mask & (byte)kinds) == (byte)kinds;
		}
		internal static bool Any(object self, Basis.Shims.BasisPlayerLock kinds)
		{
			if (!Valid(kinds) || !Caller(self, out _, out _, out _, out _)) return false;
			for (int k = 0; k < KindCount; k++) if (((int)kinds & (1 << k)) != 0 && contexts[k].Count > 0) return true;
			return false;
		}
		internal static bool Granted(object self, Basis.Shims.BasisPlayerLock kinds)
		{
			if (!Valid(kinds) || !Caller(self, out _, out _, out bool local, out BasisContentBase content)) return false;
			return local || (GrantOf(content) & (byte)kinds) == (byte)kinds;
		}
		private static void ReleaseEveryone()
		{
			refuseUntil = Time.realtimeSinceStartup + RespawnCooldown;
			grants.Clear();
			BasisPlayerLockRequests.Cancel();
			pending = null;
			for (int i = holders.Count - 1; i >= 0; i--) Release(i);
		}
		private static void Release(int index)
		{
			Holder holder = holders[index];
			for (int k = 0; k < KindCount; k++) if ((holder.mask & (1 << k)) != 0) contexts[k].Remove(holder.key);
			holder.mask = 0;
			holders.RemoveAt(index);
			if (holders.Count == 0) Unhook();
		}
		private static void PruneGrants()
		{
			stale.Clear();
			foreach (BasisContentBase key in grants.Keys) if (key == null) stale.Add(key);
			for (int i = 0; i < stale.Count; i++) grants.Remove(stale[i]);
			stale.Clear();
		}
		private static void Tick()
		{
			float now = Time.realtimeSinceStartup;
			for (int i = holders.Count - 1; i >= 0; i--)
			{
				Holder holder = holders[i];
				if (!Alive(holder.proxy, holder.box) || (holder.mask == 0 && now - holder.windowStart > TakeWindow)) Release(i);
			}
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
