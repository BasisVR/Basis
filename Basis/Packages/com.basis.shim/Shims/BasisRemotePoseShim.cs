using System;
using System.Collections.Generic;
using System.Diagnostics;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
using Unity.Collections;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Basis.Shims
{
	public class BasisRemotePoseShim : CilboxShim
	{
		public const string CallbackName = "OnRemotePose";
		public const int MaxPlayersPerFrame = 64, MinPlayersPerFrame = 4;
		public const float MaxPassMilliseconds = 1f;
		internal enum Scope : byte { Unknown, World, Owner, None }
		private readonly List<CilboxProxy> targets = new List<CilboxProxy>();
		private readonly List<CilboxMethod> callbacks = new List<CilboxMethod>();
		private readonly HashSet<int> tracked = new HashSet<int>();
		private readonly object[] arguments = new object[1];
		private CilboxBasisCommon box;
		private BasisAvatarTransformStorage current, read;
		private Scope scope;
		private bool bound, trackAll;
		private int dispatching = -1, readKey = -1, ownerKey = -1, nextResolve;
		private ulong written;
		private void OnEnable()
		{
			Bind();
			CilboxRemotePosePass.Add(this);
		}
		private void OnDisable()
		{
			CilboxRemotePosePass.Remove(this);
			dispatching = -1;
		}
		public void Rebind()
		{
			bound = false;
			Bind();
		}
		public bool Track(int playerId)
		{
			if (!Own() || (uint)playerId > ushort.MaxValue) return false;
			tracked.Add(playerId);
			return true;
		}
		public void Untrack(int playerId)
		{
			if (Own()) tracked.Remove(playerId);
		}
		public void TrackAll(bool all)
		{
			if (Own()) trackAll = all;
		}
		public void UntrackAll()
		{
			if (!Own()) return;
			trackAll = false;
			tracked.Clear();
		}
		public bool IsTracked(int playerId) => Own() && Wanted(playerId);
		public bool CanPose(int playerId) => Own() && Allowed(playerId);
		public bool TryGetBone(int playerId, HumanBodyBones bone, out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			Transform t = Readable(playerId, bone);
			if (t == null) return false;
			t.GetPositionAndRotation(out position, out rotation);
			return true;
		}
		public bool TryGetBoneLocalRotation(int playerId, HumanBodyBones bone, out Quaternion rotation)
		{
			rotation = Quaternion.identity;
			Transform t = Readable(playerId, bone);
			if (t == null) return false;
			rotation = t.localRotation;
			return true;
		}
		public bool SetBoneRotation(int playerId, HumanBodyBones bone, Quaternion rotation)
		{
			if (!RemoteBoneJobSystem.IsSyncedBone(bone) || !Unit(ref rotation)) return false;
			Transform t = Writable(playerId, bone);
			if (t == null) return false;
			t.rotation = rotation;
			return true;
		}
		public bool SetBoneLocalRotation(int playerId, HumanBodyBones bone, Quaternion rotation)
		{
			if (!RemoteBoneJobSystem.IsSyncedBone(bone) || !Unit(ref rotation)) return false;
			Transform t = Writable(playerId, bone);
			if (t == null) return false;
			t.localRotation = rotation;
			return true;
		}
		public bool SetHips(int playerId, Vector3 position, Quaternion rotation)
		{
			if (!Finite(position) || !Unit(ref rotation)) return false;
			Transform t = Writable(playerId, HumanBodyBones.Hips);
			if (t == null) return false;
			t.SetPositionAndRotation(position, rotation);
			return true;
		}
		public bool SolveLimb(int playerId, HumanBodyBones tip, Vector3 target, Quaternion targetRotation, Vector3 hint, float weight)
		{
			if (!Limb(tip, out HumanBodyBones upper, out HumanBodyBones lower) || !Finite(target) || !Finite(hint) || float.IsNaN(weight) || !Unit(ref targetRotation)) return false;
			weight = Mathf.Clamp01(weight);
			if (weight <= 0f) return true;
			Transform a = Writable(playerId, upper), b = Writable(playerId, lower), c = Writable(playerId, tip);
			if (a == null || b == null || c == null) return false;
			Quaternion upperLocal = a.localRotation, lowerLocal = b.localRotation, tipLocal = c.localRotation;
			if (!CilboxRemotePosePass.TwoBone(a, b, c, target, targetRotation, hint)) return false;
			if (weight >= 1f) return true;
			a.localRotation = Quaternion.Slerp(upperLocal, a.localRotation, weight);
			b.localRotation = Quaternion.Slerp(lowerLocal, b.localRotation, weight);
			c.localRotation = Quaternion.Slerp(tipLocal, c.localRotation, weight);
			return true;
		}
		internal bool HasCallbacks => callbacks.Count > 0 && box != null && !box.disabled && isActiveAndEnabled;
		internal bool OwnerBound => Resolve() == Scope.Owner;
		internal bool TracksAll => trackAll;
		internal HashSet<int> Tracked => tracked;
		internal bool Wanted(int playerId)
		{
			Scope s = Resolve();
			if (s == Scope.Owner) return playerId == OwnerKey();
			return s == Scope.World && (trackAll || tracked.Contains(playerId));
		}
		internal int OwnerKey()
		{
			if (ownerKey >= 0 && RemoteBoneJobSystem.IsPosed(ownerKey)) return ownerKey;
			ownerKey = -1;
			if (Resolve() != Scope.Owner || Time.frameCount < nextResolve) return -1;
			nextResolve = Time.frameCount + 30;
			if (!CilboxBasisConsent.TryOwner(box, gameObject, out BasisContentBase content) || content == null) return -1;
			if (content is BasisAvatar avatar)
			{
				if (avatar.TryGetLinkedPlayer(out ushort wearer) && RemoteBoneJobSystem.IsPosed(wearer) && Remote(wearer, out IBasisPlayer player) && ReferenceEquals(player.BasisAvatar, avatar)) ownerKey = wearer;
				return ownerKey;
			}
			string spawner = content.CreatorUUID;
			if (string.IsNullOrEmpty(spawner)) return -1;
			NativeArray<int> keys = RemoteBoneJobSystem.GetPlayerKeyArray();
			int count = keys.IsCreated ? Math.Min(RemoteBoneJobSystem.AuthoringLength, keys.Length) : 0;
			for (int i = 0; i < count; i++)
			{
				if (Remote(keys[i], out IBasisPlayer player) && string.Equals(player.UUID, spawner, StringComparison.Ordinal))
				{
					ownerKey = keys[i];
					break;
				}
			}
			return ownerKey;
		}
		internal ulong Dispatch(int playerId, BasisAvatarTransformStorage storage)
		{
			arguments[0] = CilboxRemotePosePass.Boxed(playerId);
			dispatching = playerId;
			current = storage;
			readKey = -1;
			read = null;
			written = 0ul;
			try
			{
				for (int i = 0; i < callbacks.Count; i++)
				{
					CilboxProxy p = targets[i];
					if (p == null || p.disabled || !p.enabled) continue;
					try
					{
						callbacks[i].Interpret(p, arguments);
					}
					catch (Exception e)
					{
						Debug.LogException(e);
					}
				}
			}
			finally
			{
				dispatching = -1;
				current = null;
				read = null;
			}
			return written;
		}
		internal static bool Remote(int key, out IBasisPlayer player)
		{
			player = null;
			return BasisNetworkPlayer.GetPlayerById(key, out BasisNetworkPlayer network) && network != null && !network.IsLocal && network.TryGetPlayer(out player) && player != null && !player.IsDestroyed;
		}
		internal static BasisAvatarTransformStorage Storage(int key)
		{
			if (!Remote(key, out IBasisPlayer player)) return null;
			BasisAvatar avatar = player.BasisAvatar;
			return avatar != null && avatar.IsReady ? avatar.TransformStorage : null;
		}
		private void Bind()
		{
			if (bound) return;
			bound = true;
			targets.Clear();
			callbacks.Clear();
			box = null;
			scope = Scope.Unknown;
			ownerKey = -1;
			nextResolve = 0;
			CilboxProxy[] proxies = GetComponents<CilboxProxy>();
			for (int i = 0; i < proxies.Length; i++)
			{
				CilboxProxy p = proxies[i];
				if (p != null && box == null) box = p.box as CilboxBasisCommon;
				CilboxClass cls = p != null ? p.cls : null;
				if (cls == null || cls.methodNameToIndex == null || !cls.methodNameToIndex.TryGetValue(CallbackName, out uint idx)) continue;
				CilboxMethod m = cls.methods[idx];
				if (m.isStatic || m.signatureParameters == null || m.signatureParameters.Length != 1) continue;
				targets.Add(p);
				callbacks.Add(m);
			}
		}
		private Scope Resolve()
		{
			if (scope != Scope.Unknown || box == null) return scope;
			if (box is CilboxSceneBasis) return scope = Scope.World;
			if (!CilboxBasisConsent.TryOwner(box, gameObject, out BasisContentBase content) || content == null) return Scope.Unknown;
			if (content is BasisAvatar) return scope = box is CilboxAvatarBasis ? Scope.Owner : Scope.None;
			if (box is CilboxAvatarBasis) return scope = Scope.None;
			return scope = content is BasisScene ? Scope.World : Scope.Owner;
		}
		private bool Own() => box != null && !box.disabled && box.interpreterAccountingDepth > 0;
		private bool Allowed(int playerId)
		{
			if ((uint)playerId > ushort.MaxValue || !RemoteBoneJobSystem.IsPosed(playerId)) return false;
			Scope s = Resolve();
			return s == Scope.World || (s == Scope.Owner && playerId == OwnerKey());
		}
		private Transform Readable(int playerId, HumanBodyBones bone)
		{
			if (dispatching < 0 || bone < HumanBodyBones.Hips || bone >= HumanBodyBones.LastBone || !Own()) return null;
			BasisAvatarTransformStorage storage;
			if (playerId == dispatching) storage = current;
			else
			{
				if (playerId != readKey)
				{
					readKey = playerId;
					read = (uint)playerId <= ushort.MaxValue && RemoteBoneJobSystem.IsPosed(playerId) ? Storage(playerId) : null;
				}
				storage = read;
			}
			if (storage == null) return null;
			Transform t = storage.Get(bone);
			return t != null ? t : null;
		}
		private Transform Writable(int playerId, HumanBodyBones bone)
		{
			if (dispatching < 0 || playerId != dispatching || current == null || !Own()) return null;
			int bit = RemoteBoneJobSystem.ScriptBit(bone);
			if (bit < 0) return null;
			Transform t = current.Get(bone);
			if (t == null) return null;
			written |= 1ul << bit;
			return t;
		}
		private static bool Limb(HumanBodyBones tip, out HumanBodyBones upper, out HumanBodyBones lower)
		{
			switch (tip)
			{
				case HumanBodyBones.LeftHand: upper = HumanBodyBones.LeftUpperArm; lower = HumanBodyBones.LeftLowerArm; return true;
				case HumanBodyBones.RightHand: upper = HumanBodyBones.RightUpperArm; lower = HumanBodyBones.RightLowerArm; return true;
				case HumanBodyBones.LeftFoot: upper = HumanBodyBones.LeftUpperLeg; lower = HumanBodyBones.LeftLowerLeg; return true;
				case HumanBodyBones.RightFoot: upper = HumanBodyBones.RightUpperLeg; lower = HumanBodyBones.RightLowerLeg; return true;
				default: upper = lower = HumanBodyBones.LastBone; return false;
			}
		}
		private static bool Finite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
		private static bool Unit(ref Quaternion q)
		{
			if (float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w) || float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w)) return false;
			float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
			if (length < 1e-4f) return false;
			q = new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
			return true;
		}
	}
}

namespace Cilbox
{
	using Shim = Basis.Shims.BasisRemotePoseShim;
	[AutoStaticsCleanup]
	internal static partial class CilboxRemotePosePass
	{
		internal sealed class Owner
		{
			internal Shim shim;
			internal ulong mask;
		}
		internal sealed class KeyState
		{
			internal int key, wantFrame = int.MinValue, dispatchedFrame = int.MinValue / 2;
			internal ulong combined;
			internal bool posed;
			internal readonly List<Shim> wanting = new List<Shim>(2);
			internal readonly List<Owner> owners = new List<Owner>(2);
		}
		internal struct Candidate
		{
			internal KeyState state;
			internal float score;
		}
		private sealed class ByScore : IComparer<Candidate>
		{
			public int Compare(Candidate a, Candidate b) => a.score.CompareTo(b.score);
		}
		private static readonly List<Shim> shims = new List<Shim>();
		private static readonly Dictionary<int, KeyState> states = new Dictionary<int, KeyState>();
		private static readonly List<KeyState> posed = new List<KeyState>();
		private static readonly List<KeyState> stale = new List<KeyState>();
		private static readonly Stack<KeyState> freeStates = new Stack<KeyState>();
		private static readonly Stack<Owner> freeOwners = new Stack<Owner>();
		private static readonly Dictionary<int, object> boxed = new Dictionary<int, object>();
		private static Candidate[] candidates = new Candidate[64];
		private static int candidateCount, frame;
		private static bool hooked;
		[NoAutoStaticsCleanup] private static readonly IComparer<Candidate> byScore = new ByScore();
		[NoAutoStaticsCleanup] private static readonly Action passHandler = Pass;
		[NoAutoStaticsCleanup] private static readonly ProfilerMarker marker = new ProfilerMarker("Cilbox.RemotePosePass");
		[NoAutoStaticsCleanup] private static readonly long passTicks = (long)(Stopwatch.Frequency * (Shim.MaxPassMilliseconds / 1000.0));
		internal static int ShimCount => shims.Count;
		internal static int PosedCount => posed.Count;
		internal static int LastCandidateCount { get; private set; }
		internal static int LastDispatchCount { get; private set; }
		internal static void Add(Shim shim)
		{
			if (shim == null || shims.Contains(shim)) return;
			shims.Add(shim);
			Hook();
		}
		internal static void Remove(Shim shim) => shims.Remove(shim);
		internal static object Boxed(int value)
		{
			if (!boxed.TryGetValue(value, out object o))
			{
				o = value;
				boxed[value] = o;
			}
			return o;
		}
		private static void Hook()
		{
			if (hooked) return;
			hooked = true;
			RemoteBoneJobSystem.PosePass += passHandler;
		}
		private static void Unhook()
		{
			if (!hooked) return;
			hooked = false;
			RemoteBoneJobSystem.PosePass -= passHandler;
		}
		private static void Pass()
		{
			using (marker.Auto())
			{
				frame = Time.frameCount;
				candidateCount = 0;
				Gather();
				LastCandidateCount = candidateCount;
				LastDispatchCount = Run();
				Sweep();
				if ((frame & 255) == 0) Trim();
			}
			if (shims.Count == 0 && posed.Count == 0) Unhook();
		}
		private static void Gather()
		{
			NativeArray<int> keys = RemoteBoneJobSystem.GetPlayerKeyArray();
			int count = keys.IsCreated ? Math.Min(RemoteBoneJobSystem.AuthoringLength, keys.Length) : 0;
			for (int s = shims.Count - 1; s >= 0; s--)
			{
				Shim shim = shims[s];
				if (shim == null)
				{
					shims.RemoveAt(s);
					continue;
				}
				if (!shim.HasCallbacks) continue;
				if (shim.OwnerBound)
				{
					int key = shim.OwnerKey();
					if (key >= 0) Want(key, shim);
				}
				else if (shim.TracksAll)
				{
					for (int i = 0; i < count; i++) Want(keys[i], shim);
				}
				else
				{
					foreach (int key in shim.Tracked) if (RemoteBoneJobSystem.IsPosed(key)) Want(key, shim);
				}
			}
		}
		private static void Want(int key, Shim shim)
		{
			if (!states.TryGetValue(key, out KeyState state))
			{
				state = freeStates.Count > 0 ? freeStates.Pop() : new KeyState();
				state.key = key;
				state.wantFrame = int.MinValue;
				state.dispatchedFrame = int.MinValue / 2;
				states[key] = state;
			}
			if (state.wantFrame != frame)
			{
				state.wantFrame = frame;
				state.wanting.Clear();
				if (candidateCount == candidates.Length) Array.Resize(ref candidates, candidates.Length * 2);
				candidates[candidateCount++] = new Candidate { state = state };
			}
			if (!state.wanting.Contains(shim)) state.wanting.Add(shim);
		}
		private static int Run()
		{
			if (candidateCount == 0) return 0;
			if (candidateCount > Shim.MinPlayersPerFrame)
			{
				Vector3 eye = BasisLocalCameraDriver.Position;
				for (int i = 0; i < candidateCount; i++)
				{
					KeyState state = candidates[i].state;
					float distance = RemoteBoneJobSystem.TryGetHipsPosition(state.key, out Vector3 hips) ? (hips - eye).sqrMagnitude + 1f : 1e6f;
					if (!RemoteBoneJobSystem.IsFaceVisible(state.key)) distance *= 16f;
					float age = Math.Max(1, Math.Min(frame - state.dispatchedFrame, 1 << 20));
					candidates[i].score = distance / (age * age);
				}
				Array.Sort(candidates, 0, candidateCount, byScore);
			}
			long start = Stopwatch.GetTimestamp();
			int dispatched = 0;
			for (int i = 0; i < candidateCount && dispatched < Shim.MaxPlayersPerFrame; i++)
			{
				if (dispatched >= Shim.MinPlayersPerFrame && Stopwatch.GetTimestamp() - start > passTicks) break;
				KeyState state = candidates[i].state;
				candidates[i].state = null;
				Basis.Scripts.BasisSdk.BasisAvatarTransformStorage storage = Shim.Storage(state.key);
				if (storage == null) continue;
				RemoteBoneJobSystem.RestoreNetworkPose(state.key);
				ulong combined = 0ul;
				for (int w = 0; w < state.wanting.Count; w++)
				{
					Shim shim = state.wanting[w];
					if (shim == null || !shim.HasCallbacks) continue;
					SetOwner(state, shim, shim.Dispatch(state.key, storage));
				}
				for (int o = 0; o < state.owners.Count; o++) combined |= state.owners[o].mask;
				state.combined = combined;
				state.dispatchedFrame = frame;
				RemoteBoneJobSystem.CommitScriptPose(state.key, combined);
				Posed(state);
				dispatched++;
			}
			for (int i = 0; i < candidateCount; i++) candidates[i].state = null;
			return dispatched;
		}
		private static void SetOwner(KeyState state, Shim shim, ulong mask)
		{
			for (int o = 0; o < state.owners.Count; o++)
			{
				Owner owner = state.owners[o];
				if (!ReferenceEquals(owner.shim, shim)) continue;
				if (mask != 0ul) owner.mask = mask;
				else
				{
					state.owners.RemoveAt(o);
					owner.shim = null;
					freeOwners.Push(owner);
				}
				return;
			}
			if (mask == 0ul) return;
			Owner added = freeOwners.Count > 0 ? freeOwners.Pop() : new Owner();
			added.shim = shim;
			added.mask = mask;
			state.owners.Add(added);
		}
		private static void Posed(KeyState state)
		{
			if (state.owners.Count == 0 || state.posed) return;
			state.posed = true;
			posed.Add(state);
		}
		private static void Sweep()
		{
			for (int i = posed.Count - 1; i >= 0; i--)
			{
				KeyState state = posed[i];
				bool live = RemoteBoneJobSystem.IsPosed(state.key);
				bool changed = false;
				for (int o = state.owners.Count - 1; o >= 0; o--)
				{
					Owner owner = state.owners[o];
					Shim shim = owner.shim;
					if (live && shim != null && shim.HasCallbacks && state.wantFrame == frame && state.wanting.Contains(shim)) continue;
					state.owners.RemoveAt(o);
					owner.shim = null;
					freeOwners.Push(owner);
					changed = true;
				}
				if (changed)
				{
					ulong combined = 0ul;
					for (int o = 0; o < state.owners.Count; o++) combined |= state.owners[o].mask;
					state.combined = combined;
					if (live) RemoteBoneJobSystem.NarrowScriptPose(state.key, combined);
				}
				if (state.owners.Count > 0) continue;
				state.posed = false;
				state.combined = 0ul;
				posed.RemoveAt(i);
			}
		}
		private static void Trim()
		{
			stale.Clear();
			foreach (KeyValuePair<int, KeyState> pair in states) if (!pair.Value.posed && frame - pair.Value.wantFrame > 600) stale.Add(pair.Value);
			for (int i = 0; i < stale.Count; i++)
			{
				KeyState state = stale[i];
				states.Remove(state.key);
				state.wanting.Clear();
				freeStates.Push(state);
			}
			stale.Clear();
		}
		internal static bool TwoBone(Transform upper, Transform lower, Transform tip, Vector3 target, Quaternion targetRotation, Vector3 hint)
		{
			Vector3 a = upper.position, b = lower.position, c = tip.position;
			float ab = (b - a).magnitude, bc = (c - b).magnitude;
			if (ab < 1e-5f || bc < 1e-5f) return false;
			Vector3 toTarget = target - a;
			float reach = toTarget.magnitude;
			if (reach < 1e-5f) return false;
			Vector3 dir = toTarget / reach;
			float d = Mathf.Clamp(reach, Mathf.Abs(ab - bc) + 1e-4f, ab + bc - 1e-4f);
			Vector3 bend = hint - a;
			bend -= Vector3.Dot(bend, dir) * dir;
			if (bend.sqrMagnitude < 1e-8f)
			{
				bend = b - a;
				bend -= Vector3.Dot(bend, dir) * dir;
			}
			if (bend.sqrMagnitude < 1e-8f) bend = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.forward);
			bend.Normalize();
			float cos = Mathf.Clamp((ab * ab + d * d - bc * bc) / (2f * ab * d), -1f, 1f);
			float sin = Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos));
			Vector3 joint = a + (dir * cos + bend * sin) * ab, end = a + dir * d;
			upper.rotation = Quaternion.FromToRotation(b - a, joint - a) * upper.rotation;
			Vector3 jointNow = lower.position;
			lower.rotation = Quaternion.FromToRotation(tip.position - jointNow, end - jointNow) * lower.rotation;
			tip.rotation = targetRotation;
			return true;
		}
	}
}
