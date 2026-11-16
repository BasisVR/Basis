using System;
using System.Collections.Generic;
using Basis.EventDriver;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Cilbox;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Jobs;

namespace Basis.Shims
{
	public class BasisLocalPoseShim : CilboxShim
	{
		public const string CallbackName = "OnLocalPose";
		public const int HumanoidCount = (int)HumanBodyBones.LastBone, MaxTransforms = 8192;
		private readonly List<CilboxProxy> targets = new List<CilboxProxy>();
		private readonly List<CilboxMethod> callbacks = new List<CilboxMethod>();
		private CilboxBasisCommon box;
		private bool bound;
		private int allowedFrame = -1;
		private bool allowed;
		private void OnEnable()
		{
			Bind();
			CilboxLocalPosePass.Add(this);
		}
		private void OnDisable() => CilboxLocalPosePass.Remove(this);
		public void Rebind()
		{
			bound = false;
			Bind();
		}
		public bool IsReady => Own() && CilboxLocalPosePass.Count > 0;
		public int Count => Own() ? CilboxLocalPosePass.Count : 0;
		public int AdditionalCount => Own() ? Math.Max(0, CilboxLocalPosePass.Count - HumanoidCount) : 0;
		public int Version => Own() ? CilboxLocalPosePass.Version : 0;
		public int Frame => Own() ? CilboxLocalPosePass.Frame : -1;
		public bool TryGetBone(HumanBodyBones bone, out Vector3 localPosition, out Quaternion localRotation) => TryGet((int)bone, out localPosition, out localRotation);
		public bool TryGetAdditional(int index, out Vector3 localPosition, out Quaternion localRotation) => TryGet(index < 0 ? -1 : HumanoidCount + index, out localPosition, out localRotation);
		public bool TryGet(int index, out Vector3 localPosition, out Quaternion localRotation)
		{
			localPosition = Vector3.zero;
			localRotation = Quaternion.identity;
			return Own() && CilboxLocalPosePass.TryGet(index, out localPosition, out localRotation);
		}
		public bool TryGetRoot(out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			if (!Own() || CilboxLocalPosePass.Count == 0) return false;
			position = CilboxLocalPosePass.RootPosition;
			rotation = CilboxLocalPosePass.RootRotation;
			return true;
		}
		public bool Has(int index) => Own() && CilboxLocalPosePass.Has(index);
		public string GetName(int index) => Own() ? CilboxLocalPosePass.Name(index) : null;
		public int GetParent(int index) => Own() ? CilboxLocalPosePass.Parent(index) : -1;
		public int Find(string name) => Own() ? CilboxLocalPosePass.Find(name) : -1;
		public int GetLocalPositions(Vector3[] into) => Own() ? CilboxLocalPosePass.CopyPositions(into) : 0;
		public int GetLocalRotations(Quaternion[] into) => Own() ? CilboxLocalPosePass.CopyRotations(into) : 0;
		public int GetParents(int[] into) => Own() ? CilboxLocalPosePass.CopyParents(into) : 0;
		internal bool Wants => box != null && !box.disabled && isActiveAndEnabled && Allowed();
		internal void Dispatch()
		{
			for (int i = 0; i < callbacks.Count; i++)
			{
				CilboxProxy p = targets[i];
				if (p == null || p.disabled || !p.enabled) continue;
				try
				{
					callbacks[i].Interpret(p, null);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
		}
		private void Bind()
		{
			if (bound) return;
			bound = true;
			targets.Clear();
			callbacks.Clear();
			box = null;
			allowedFrame = -1;
			CilboxProxy[] proxies = GetComponents<CilboxProxy>();
			for (int i = 0; i < proxies.Length; i++)
			{
				CilboxProxy p = proxies[i];
				if (p != null && box == null) box = p.box as CilboxBasisCommon;
				CilboxClass cls = p != null ? p.cls : null;
				if (cls == null || cls.methodNameToIndex == null || !cls.methodNameToIndex.TryGetValue(CallbackName, out uint idx)) continue;
				CilboxMethod m = cls.methods[idx];
				if (m.isStatic || (m.signatureParameters != null && m.signatureParameters.Length > 0)) continue;
				targets.Add(p);
				callbacks.Add(m);
			}
		}
		private bool Allowed()
		{
			int frame = Time.frameCount;
			if (allowedFrame == frame) return allowed;
			allowedFrame = frame;
			return allowed = box != null && CilboxBasisConsent.ForLocalPlayer(box, gameObject);
		}
		private bool Own() => box != null && !box.disabled && box.interpreterAccountingDepth > 0 && Allowed();
	}
}

namespace Cilbox
{
	using Shim = Basis.Shims.BasisLocalPoseShim;
	[BurstCompile]
	internal struct CilboxLocalPoseReadJob : IJobParallelForTransform
	{
		[WriteOnly] public NativeArray<Vector3> positions;
		[WriteOnly] public NativeArray<Quaternion> rotations;
		[WriteOnly] public NativeArray<byte> valid;
		public void Execute(int index, TransformAccess transform)
		{
			if (!transform.isValid)
			{
				valid[index] = 0;
				return;
			}
			positions[index] = transform.localPosition;
			rotations[index] = transform.localRotation;
			valid[index] = 1;
		}
	}
	[AutoStaticsCleanup]
	internal static partial class CilboxLocalPosePass
	{
		private static readonly List<Shim> shims = new List<Shim>();
		private static readonly List<Transform> order = new List<Transform>(256);
		private static readonly Dictionary<Transform, int> lookup = new Dictionary<Transform, int>(256);
		private static readonly Stack<Transform> walk = new Stack<Transform>(64);
		private static int[] slots = Array.Empty<int>(), parents = Array.Empty<int>();
		private static string[] names = Array.Empty<string>();
		private static BasisAvatar avatar;
		private static Transform root;
		private static int hierarchyCount = -1, count, version, frame = -1;
		private static bool hooked;
		internal static Vector3 RootPosition;
		internal static Quaternion RootRotation = Quaternion.identity;
		[NoAutoStaticsCleanup] private static TransformAccessArray access;
		[NoAutoStaticsCleanup] private static NativeArray<Vector3> positions;
		[NoAutoStaticsCleanup] private static NativeArray<Quaternion> rotations;
		[NoAutoStaticsCleanup] private static NativeArray<byte> valid;
		[NoAutoStaticsCleanup] private static readonly UnityAction passHandler = Pass;
		[NoAutoStaticsCleanup] private static readonly Action quitHandler = Unhook;
		[NoAutoStaticsCleanup] private static readonly ProfilerMarker marker = new ProfilerMarker("Cilbox.LocalPosePass");
		internal static int Count => count;
		internal static int Version => version;
		internal static int Frame => frame;
		internal static int ShimCount => shims.Count;
		internal static void Add(Shim shim)
		{
			if (shim == null || shims.Contains(shim)) return;
			shims.Add(shim);
			Hook();
		}
		internal static void Remove(Shim shim) => shims.Remove(shim);
		internal static bool Has(int index) => (uint)index < (uint)count && slots[index] >= 0 && valid[slots[index]] != 0;
		internal static bool TryGet(int index, out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			if (!Has(index)) return false;
			int slot = slots[index];
			position = positions[slot];
			rotation = rotations[slot];
			return true;
		}
		internal static string Name(int index) => (uint)index < (uint)count ? names[index] : null;
		internal static int Parent(int index) => (uint)index < (uint)count ? parents[index] : -1;
		internal static int Find(string name)
		{
			if (string.IsNullOrEmpty(name)) return -1;
			for (int i = 0; i < count; i++) if (string.Equals(names[i], name, StringComparison.Ordinal)) return i;
			return -1;
		}
		internal static int CopyPositions(Vector3[] into)
		{
			if (into == null) return 0;
			int n = Math.Min(into.Length, count);
			for (int i = 0; i < n; i++) into[i] = Has(i) ? positions[slots[i]] : Vector3.zero;
			return n;
		}
		internal static int CopyRotations(Quaternion[] into)
		{
			if (into == null) return 0;
			int n = Math.Min(into.Length, count);
			for (int i = 0; i < n; i++) into[i] = Has(i) ? rotations[slots[i]] : Quaternion.identity;
			return n;
		}
		internal static int CopyParents(int[] into)
		{
			if (into == null) return 0;
			int n = Math.Min(into.Length, count);
			Array.Copy(parents, into, n);
			return n;
		}
		private static void Hook()
		{
			if (hooked) return;
			hooked = true;
			Application.onBeforeRender += passHandler;
			Application.quitting += quitHandler;
		}
		private static void Unhook()
		{
			if (!hooked) return;
			hooked = false;
			Application.onBeforeRender -= passHandler;
			Application.quitting -= quitHandler;
			Clear();
		}
		[BeforeRenderOrder(int.MaxValue)]
		private static void Pass()
		{
			bool wanted = false;
			for (int s = shims.Count - 1; s >= 0; s--)
			{
				Shim shim = shims[s];
				if (shim == null) shims.RemoveAt(s);
				else if (shim.Wants) wanted = true;
			}
			if (shims.Count == 0)
			{
				Unhook();
				return;
			}
			if (!wanted) return;
			using (marker.Auto())
			{
				if (!Snapshot()) return;
				for (int s = 0; s < shims.Count; s++)
				{
					Shim shim = shims[s];
					if (shim != null && shim.Wants) shim.Dispatch();
				}
			}
		}
		private static bool Snapshot()
		{
			BasisLateJiggleCompletion.CompleteIfPending();
			BasisLocalPlayer player = BasisLocalPlayer.PlayerReady ? BasisLocalPlayer.Instance : null;
			BasisAvatar current = player != null ? player.BasisAvatar : null;
			Animator animator = current != null && current.IsReady ? current.Animator : null;
			if (animator == null || !animator.isHuman)
			{
				if (count > 0 || avatar != null) Clear();
				return false;
			}
			Transform top = animator.transform;
			if (!ReferenceEquals(current, avatar) || top != root || top.hierarchyCount != hierarchyCount) Rebuild(current, animator);
			if (count == 0) return false;
			top.GetPositionAndRotation(out RootPosition, out RootRotation);
			new CilboxLocalPoseReadJob { positions = positions, rotations = rotations, valid = valid }.ScheduleReadOnly(access, 64).Complete();
			frame = Time.frameCount;
			return true;
		}
		private static void Rebuild(BasisAvatar current, Animator animator)
		{
			Release();
			avatar = current;
			root = animator.transform;
			hierarchyCount = root.hierarchyCount;
			order.Clear();
			lookup.Clear();
			BasisAvatarTransformStorage storage = current.TransformStorage != null && current.TransformStorage.HasData ? current.TransformStorage : BasisAvatarTransformStorage.CaptureFrom(animator);
			for (int i = 0; i < Shim.HumanoidCount; i++)
			{
				Transform t = storage != null ? storage.Get((HumanBodyBones)i) : null;
				if (t != null && (t == root || !t.IsChildOf(root) || lookup.ContainsKey(t))) t = null;
				order.Add(t);
				if (t != null) lookup[t] = i;
			}
			walk.Clear();
			for (int c = root.childCount - 1; c >= 0; c--) walk.Push(root.GetChild(c));
			while (walk.Count > 0 && order.Count < Shim.MaxTransforms)
			{
				Transform t = walk.Pop();
				if (t.TryGetComponent(out BasisContentBase content) && !ReferenceEquals(content, current)) continue;
				if (!lookup.ContainsKey(t))
				{
					lookup[t] = order.Count;
					order.Add(t);
				}
				for (int c = t.childCount - 1; c >= 0; c--) walk.Push(t.GetChild(c));
			}
			walk.Clear();
			count = order.Count;
			slots = new int[count];
			parents = new int[count];
			names = new string[count];
			List<Transform> live = new List<Transform>(count);
			for (int i = 0; i < count; i++)
			{
				Transform t = order[i];
				if (t == null)
				{
					slots[i] = parents[i] = -1;
					continue;
				}
				slots[i] = live.Count;
				live.Add(t);
				names[i] = t.name;
				Transform up = t.parent;
				parents[i] = up != null && lookup.TryGetValue(up, out int p) ? p : -1;
			}
			access = new TransformAccessArray(live.ToArray(), 64);
			positions = new NativeArray<Vector3>(live.Count, Allocator.Persistent);
			rotations = new NativeArray<Quaternion>(live.Count, Allocator.Persistent);
			valid = new NativeArray<byte>(live.Count, Allocator.Persistent);
			order.Clear();
			lookup.Clear();
			version++;
		}
		private static void Release()
		{
			if (access.isCreated) access.Dispose();
			if (positions.IsCreated) positions.Dispose();
			if (rotations.IsCreated) rotations.Dispose();
			if (valid.IsCreated) valid.Dispose();
			count = 0;
		}
		private static void Clear()
		{
			Release();
			avatar = null;
			root = null;
			hierarchyCount = -1;
			slots = parents = Array.Empty<int>();
			names = Array.Empty<string>();
			version++;
		}
	}
}
