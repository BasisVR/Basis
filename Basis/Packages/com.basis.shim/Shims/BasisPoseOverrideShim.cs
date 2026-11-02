using System;
using System.Collections.Generic;
using Basis.EventDriver;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Cilbox;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Shims
{
	public static class BasisPoseOverrideShim
	{
		public static bool Claim(object self, HumanBodyBones bone) => CilboxPoseOverrides.Claim(self, bone, false);
		public static void Release(object self, HumanBodyBones bone) => CilboxPoseOverrides.Release(self, bone, false);
		public static void ReleaseAll(object self) => CilboxPoseOverrides.ReleaseAll(self);
		public static bool IsClaimed(object self, HumanBodyBones bone) => CilboxPoseOverrides.IsClaimed(self, bone, false);
		public static bool Set(object self, HumanBodyBones bone, Vector3 position, Quaternion rotation) => CilboxPoseOverrides.Set(self, bone, position, rotation, true, false);
		public static bool SetRotation(object self, HumanBodyBones bone, Quaternion rotation) => CilboxPoseOverrides.Set(self, bone, Vector3.zero, rotation, false, false);
		public static bool SetBlend(object self, HumanBodyBones bone, float blend) => CilboxPoseOverrides.SetBlend(self, bone, blend, false);
		public static bool TryGetUnoverridden(object self, HumanBodyBones bone, out Vector3 position, out Quaternion rotation) => CilboxPoseOverrides.TryGetUnoverridden(self, bone, out position, out rotation);
		public static bool ClaimEffector(object self, HumanBodyBones bone) => CilboxPoseOverrides.Claim(self, bone, true);
		public static void ReleaseEffector(object self, HumanBodyBones bone) => CilboxPoseOverrides.Release(self, bone, true);
		public static bool IsEffectorClaimed(object self, HumanBodyBones bone) => CilboxPoseOverrides.IsClaimed(self, bone, true);
		public static bool SetEffector(object self, HumanBodyBones bone, Vector3 position, Quaternion rotation) => CilboxPoseOverrides.Set(self, bone, position, rotation, true, true);
		public static bool SetEffectorBlend(object self, HumanBodyBones bone, float blend) => CilboxPoseOverrides.SetBlend(self, bone, blend, true);
		public static bool TryGetTracked(object self, HumanBodyBones bone, out Vector3 position, out Quaternion rotation) => CilboxPoseOverrides.TryGetTracked(self, bone, out position, out rotation);
	}
}

namespace Cilbox
{
	internal sealed class CilboxPoseClaim
	{
		internal HumanBodyBones bone;
		internal CilboxProxy owner;
		internal CilboxBasisCommon box;
		internal Vector3 position;
		internal Quaternion rotation = Quaternion.identity;
		internal float blend = 1f;
		internal bool effector, hasData, hasPosition;
	}

	[AutoStaticsCleanup]
	internal static partial class CilboxPoseOverrides
	{
		internal const int SlotCount = 22;
		private static readonly List<CilboxPoseClaim> claims = new List<CilboxPoseClaim>(SlotCount);
		private static bool hooked;
		[NoAutoStaticsCleanup] private static readonly Action tickHandler = Tick;
		internal static int ClaimCount => claims.Count;
		internal static int Slot(HumanBodyBones bone)
		{
			if (bone >= HumanBodyBones.Hips && bone <= HumanBodyBones.RightToes) return (int)bone;
			return bone == HumanBodyBones.UpperChest ? SlotCount - 1 : -1;
		}
		internal static bool SeatBone(HumanBodyBones bone)
		{
			switch (bone)
			{
				case HumanBodyBones.Hips:
				case HumanBodyBones.LeftUpperLeg:
				case HumanBodyBones.LeftLowerLeg:
				case HumanBodyBones.LeftFoot:
				case HumanBodyBones.LeftToes:
				case HumanBodyBones.RightUpperLeg:
				case HumanBodyBones.RightLowerLeg:
				case HumanBodyBones.RightFoot:
				case HumanBodyBones.RightToes:
					return true;
				default:
					return false;
			}
		}
		private static bool Caller(object self, out CilboxProxy proxy, out CilboxBasisCommon box)
		{
			proxy = self as CilboxProxy;
			box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			if (proxy == null || proxy.disabled || box == null || box.disabled || box.interpreterAccountingDepth == 0) return false;
			return CilboxBasisConsent.ForLocalPlayer(box, proxy.gameObject);
		}
		private static BasisLocalRigDriver Rig()
		{
			BasisLocalPlayer player = BasisLocalPlayer.Instance;
			return player != null ? player.LocalRigDriver : null;
		}
		private static bool Seated() => BasisLocalSeatDriver.ActiveSeat != null;
		private static bool Valid(HumanBodyBones bone, bool effector) => effector ? BasisLocalRigDriver.IsEffector(bone) : Slot(bone) >= 0;
		private static int Find(HumanBodyBones bone, bool effector)
		{
			for (int i = 0; i < claims.Count; i++) if (claims[i].bone == bone && claims[i].effector == effector) return i;
			return -1;
		}
		private static CilboxPoseClaim Owned(object self, HumanBodyBones bone, bool effector)
		{
			if (!Valid(bone, effector) || !Caller(self, out _, out CilboxBasisCommon box)) return null;
			int index = Find(bone, effector);
			return index >= 0 && ReferenceEquals(claims[index].box, box) ? claims[index] : null;
		}
		internal static bool Claim(object self, HumanBodyBones bone, bool effector)
		{
			if (!Valid(bone, effector) || !Caller(self, out CilboxProxy proxy, out CilboxBasisCommon box)) return false;
			int index = Find(bone, effector);
			if (index >= 0) return ReferenceEquals(claims[index].box, box);
			if ((SeatBone(bone) && Seated()) || Rig() == null) return false;
			CilboxPoseClaim claim = new CilboxPoseClaim { bone = bone, effector = effector, owner = proxy, box = box };
			claims.Add(claim);
			Hook();
			Push(claim);
			return true;
		}
		internal static void Release(object self, HumanBodyBones bone, bool effector)
		{
			if (Owned(self, bone, effector) != null) Drop(Find(bone, effector), true);
		}
		internal static void ReleaseAll(object self)
		{
			if (!Caller(self, out _, out CilboxBasisCommon box)) return;
			for (int i = claims.Count - 1; i >= 0; i--) if (ReferenceEquals(claims[i].box, box)) Drop(i, true);
		}
		internal static bool IsClaimed(object self, HumanBodyBones bone, bool effector) => Owned(self, bone, effector) != null;
		internal static bool Set(object self, HumanBodyBones bone, Vector3 position, Quaternion rotation, bool withPosition, bool effector)
		{
			CilboxPoseClaim claim = Owned(self, bone, effector);
			if (claim == null || !Finite(rotation) || (withPosition && !Finite(position))) return false;
			float length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
			if (length < 1e-4f) return false;
			claim.rotation = new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length);
			if (withPosition)
			{
				claim.position = position;
				claim.hasPosition = true;
			}
			claim.hasData = true;
			Push(claim);
			return true;
		}
		internal static bool SetBlend(object self, HumanBodyBones bone, float blend, bool effector)
		{
			CilboxPoseClaim claim = Owned(self, bone, effector);
			if (claim == null || float.IsNaN(blend)) return false;
			claim.blend = Mathf.Clamp01(blend);
			Push(claim);
			return true;
		}
		internal static bool TryGetUnoverridden(object self, HumanBodyBones bone, out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			if (Slot(bone) < 0 || !Caller(self, out _, out _)) return false;
			BasisLocalRigDriver rig = Rig();
			return rig != null && rig.TryGetUnoverriddenPose(bone, out position, out rotation);
		}
		internal static bool TryGetTracked(object self, HumanBodyBones bone, out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			if (!BasisLocalRigDriver.IsEffector(bone) || !Caller(self, out _, out _)) return false;
			BasisLocalRigDriver rig = Rig();
			return rig != null && rig.TryGetTrackedEffector(bone, out position, out rotation);
		}
		private static bool Finite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
		private static bool Finite(Quaternion q) => !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w) || float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w));
		private static void Push(CilboxPoseClaim claim)
		{
			BasisLocalRigDriver rig = Rig();
			if (rig == null) return;
			if (claim.effector)
			{
				rig.SetEffectorBlend(claim.bone, claim.blend);
				if (claim.hasData) rig.SetEffectorOverride(claim.bone, claim.position, claim.rotation);
				rig.SetEffectorUsage(claim.bone, claim.hasData);
				return;
			}
			rig.SetOverrideBlend(claim.bone, claim.blend);
			if (claim.hasData)
			{
				if (claim.hasPosition) rig.SetOverrideData(claim.bone, claim.position, claim.rotation);
				else rig.SetOverrideRotation(claim.bone, claim.rotation);
			}
			rig.SetOverrideUsage(claim.bone, claim.hasData);
		}
		private static void Drop(int index, bool clearUsage)
		{
			if (index < 0 || index >= claims.Count) return;
			CilboxPoseClaim claim = claims[index];
			claims.RemoveAt(index);
			BasisLocalRigDriver rig = Rig();
			if (rig != null && claim.effector)
			{
				rig.SetEffectorBlend(claim.bone, 1f);
				rig.SetEffectorUsage(claim.bone, false);
			}
			else if (rig != null)
			{
				rig.SetOverrideBlend(claim.bone, 1f);
				if (clearUsage) rig.SetOverrideUsage(claim.bone, false);
			}
			if (claims.Count == 0) Unhook();
		}
		private static void Tick()
		{
			bool seated = Seated();
			for (int i = claims.Count - 1; i >= 0; i--)
			{
				CilboxPoseClaim claim = claims[i];
				if (claim.owner == null || claim.owner.disabled || !claim.owner.isActiveAndEnabled || claim.box == null || claim.box.disabled) Drop(i, true);
				else if (seated && SeatBone(claim.bone)) Drop(i, false);
				else Push(claim);
			}
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
