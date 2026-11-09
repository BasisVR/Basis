using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.TransformBinders.BoneControl;
using Cilbox;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Shims
{
	public static class BasisGrabShim
	{
		public const int Left = 0, Right = 1;
		public static bool IsHoldingJiggle(object self, int hand) => CilboxGrabs.Jiggle(self, hand, out _, out _, out _);
		public static int GetHeldJigglePlayerId(object self, int hand) => CilboxGrabs.Jiggle(self, hand, out ushort target, out _, out _) ? target : -1;
		public static bool TryGetHeldJiggleBone(object self, int hand, out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			if (!CilboxGrabs.Jiggle(self, hand, out _, out _, out Transform bone) || bone == null) return false;
			bone.GetPositionAndRotation(out position, out rotation);
			return true;
		}
		public static string GetHeldJiggleBoneName(object self, int hand) => CilboxGrabs.Jiggle(self, hand, out _, out _, out Transform bone) && bone != null ? bone.name : string.Empty;
		public static string GetHeldJiggleRigName(object self, int hand) => CilboxGrabs.Jiggle(self, hand, out _, out Transform rig, out _) && rig != null ? rig.name : string.Empty;
		public static HumanBodyBones GetHeldJiggleAttachedBone(object self, int hand) => CilboxGrabs.Jiggle(self, hand, out ushort target, out _, out Transform bone) ? CilboxGrabs.AttachedBone(target, bone) : HumanBodyBones.LastBone;
		public static bool ReleaseJiggle(object self, int hand) => CilboxGrabs.Caller(self, out _, out _) && CilboxGrabs.ValidHand(hand) && BasisJiggleGrabDriver.ReleaseLocalGrab((byte)hand);
		public static bool BlockJiggle(object self, int hand, bool blocked) => CilboxGrabs.Block(self, hand, blocked);
		public static bool IsJiggleBlocked(object self, int hand) => CilboxGrabs.IsBlocked(self, hand);
		public static bool IsHoldingPickup(object self, int hand) => CilboxGrabs.Pickup(self, hand) != null;
		public static bool IsHolding(object self, int hand, GameObject target)
		{
			BasisPickupInteractable held = CilboxGrabs.Pickup(self, hand);
			return held != null && target != null && (held.gameObject == target || held.transform.IsChildOf(target.transform));
		}
		public static GameObject GetHeldPickup(object self, int hand) => CilboxGrabs.OwnPickup(self, hand);
		public static string GetHeldPickupName(object self, int hand)
		{
			BasisPickupInteractable held = CilboxGrabs.Pickup(self, hand);
			return held != null ? held.name : string.Empty;
		}
		public static bool TryGetHeldPickupPose(object self, int hand, out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			BasisPickupInteractable held = CilboxGrabs.Pickup(self, hand);
			if (held == null) return false;
			held.transform.GetPositionAndRotation(out position, out rotation);
			return true;
		}
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxGrabs
	{
		private struct BlockEntry
		{
			internal CilboxProxy proxy;
			internal CilboxBasisCommon box;
			internal byte mask;
		}
		private static readonly List<BlockEntry> blocks = new List<BlockEntry>();
		[NoAutoStaticsCleanup] private static readonly Transform[] humanBones = new Transform[(int)HumanBodyBones.LastBone];
		[NoAutoStaticsCleanup] private static readonly System.Func<byte, bool> blockedCheck = Blocked;
		internal static bool ValidHand(int hand) => hand == Basis.Shims.BasisGrabShim.Left || hand == Basis.Shims.BasisGrabShim.Right;
		internal static bool Caller(object self, out CilboxProxy proxy, out CilboxBasisCommon box)
		{
			proxy = self as CilboxProxy;
			box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			if (proxy == null || proxy.disabled || box == null || box.disabled || box.interpreterAccountingDepth == 0) return false;
			return CilboxBasisConsent.ForLocalPlayer(box, proxy.gameObject);
		}
		internal static bool Jiggle(object self, int hand, out ushort target, out Transform rig, out Transform bone)
		{
			target = 0;
			rig = null;
			bone = null;
			return ValidHand(hand) && Caller(self, out _, out _) && BasisJiggleGrabDriver.TryGetLocalGrab((byte)hand, out target, out rig, out bone);
		}
		internal static HumanBodyBones AttachedBone(ushort target, Transform bone)
		{
			if (bone == null) return HumanBodyBones.LastBone;
			Animator animator = null;
			BasisJiggleGrabDriver.TryGetLocalPlayerId(out ushort localId);
			if (target == localId) animator = BasisLocalPlayer.Instance != null && BasisLocalPlayer.Instance.BasisAvatar != null ? BasisLocalPlayer.Instance.BasisAvatar.Animator : null;
			else if (BasisNetworkPlayers.RemotePlayers.TryGetValue(target, out BasisRemotePlayer remote) && remote != null && remote.BasisAvatar != null) animator = remote.BasisAvatar.Animator;
			if (animator == null || !animator.isHuman) return HumanBodyBones.LastBone;
			for (int i = 0; i < humanBones.Length; i++) humanBones[i] = animator.GetBoneTransform((HumanBodyBones)i);
			HumanBodyBones found = HumanBodyBones.LastBone;
			for (Transform walk = bone; walk != null && found == HumanBodyBones.LastBone; walk = walk.parent)
			{
				for (int i = 0; i < humanBones.Length; i++)
				{
					if (humanBones[i] == walk)
					{
						found = (HumanBodyBones)i;
						break;
					}
				}
				if (walk == animator.transform) break;
			}
			System.Array.Clear(humanBones, 0, humanBones.Length);
			return found;
		}
		internal static BasisPickupInteractable Pickup(object self, int hand)
		{
			if (!ValidHand(hand) || !Caller(self, out _, out _)) return null;
			BasisPlayerInteract interact = BasisPlayerInteract.Instance;
			if (interact == null || interact.InteractInputs == null) return null;
			BasisInteractInput[] inputs = interact.InteractInputs;
			for (int i = 0; i < inputs.Length; i++)
			{
				Basis.Scripts.Device_Management.Devices.BasisInput input = inputs[i].input;
				if (input == null || !(inputs[i].lastTarget is BasisPickupInteractable pickup) || pickup == null || HandOf(input) != hand || !pickup.IsInteractingWith(input)) continue;
				return pickup;
			}
			return null;
		}
		internal static GameObject OwnPickup(object self, int hand)
		{
			BasisPickupInteractable held = Pickup(self, hand);
			if (held == null || !Caller(self, out CilboxProxy proxy, out CilboxBasisCommon box)) return null;
			bool resolved = BasisContent.TryResolve(held.gameObject, out BasisContentBase content);
			if (box is CilboxSceneBasis) return (!resolved && held.gameObject.scene == proxy.gameObject.scene) || content is BasisScene ? held.gameObject : null;
			return resolved && CilboxBasisConsent.TryOwner(box, proxy.gameObject, out BasisContentBase owner) && ReferenceEquals(owner, content) ? held.gameObject : null;
		}
		private static int HandOf(Basis.Scripts.Device_Management.Devices.BasisInput input)
		{
			if (!input.TryGetRole(out BasisBoneTrackedRole role)) return -1;
			if (role == BasisBoneTrackedRole.LeftHand) return Basis.Shims.BasisGrabShim.Left;
			if (role == BasisBoneTrackedRole.RightHand) return Basis.Shims.BasisGrabShim.Right;
			if (BasisPlayerInteract.IsDesktopCenterEye(input)) return BasisDominantHand.IsLeftHanded ? Basis.Shims.BasisGrabShim.Left : Basis.Shims.BasisGrabShim.Right;
			return -1;
		}
		internal static bool Block(object self, int hand, bool blocked)
		{
			if (!ValidHand(hand) || !Caller(self, out CilboxProxy proxy, out CilboxBasisCommon box)) return false;
			byte bit = (byte)(1 << hand);
			int index = Find(proxy);
			BlockEntry entry = index >= 0 ? blocks[index] : new BlockEntry { proxy = proxy, box = box };
			entry.mask = blocked ? (byte)(entry.mask | bit) : (byte)(entry.mask & ~bit);
			if (index >= 0 && entry.mask == 0) blocks.RemoveAt(index);
			else if (index >= 0) blocks[index] = entry;
			else if (entry.mask != 0) blocks.Add(entry);
			BasisJiggleGrabDriver.LocalGrabBlocked = blocks.Count > 0 ? blockedCheck : null;
			return true;
		}
		internal static bool IsBlocked(object self, int hand)
		{
			if (!ValidHand(hand) || !Caller(self, out CilboxProxy proxy, out _)) return false;
			int index = Find(proxy);
			return index >= 0 && (blocks[index].mask & (1 << hand)) != 0;
		}
		private static int Find(CilboxProxy proxy)
		{
			for (int i = 0; i < blocks.Count; i++) if (ReferenceEquals(blocks[i].proxy, proxy)) return i;
			return -1;
		}
		private static bool Blocked(byte hand)
		{
			bool result = false;
			for (int i = blocks.Count - 1; i >= 0; i--)
			{
				BlockEntry entry = blocks[i];
				if (entry.proxy == null || entry.proxy.disabled || !entry.proxy.isActiveAndEnabled || entry.box == null || entry.box.disabled)
				{
					blocks.RemoveAt(i);
					continue;
				}
				if ((entry.mask & (1 << hand)) != 0) result = true;
			}
			if (blocks.Count == 0) BasisJiggleGrabDriver.LocalGrabBlocked = null;
			return result;
		}
	}
}
