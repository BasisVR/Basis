using System;
using System.Collections.Generic;
using Basis.EventDriver;
using Basis.IK;
using Basis.Scripts.BasisSdk;
using Cilbox;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Jobs;

namespace Basis.Shims
{
	public static class BasisIKShim
	{
		public const int MaxBatchesPerScript = 8, MaxSolversPerBatch = 256, MaxBonesPerBatch = 1024, MinChainBones = 4, MaxChainBones = 31;
		public static BasisIKBatch CreateBatch(object self) => CilboxIKBatches.Create(self);
		public static float TwistAngle(Quaternion rotation, Vector3 axis) => axis.sqrMagnitude > 1e-12f ? BasisTwistSolveCore.SignedTwistAngleDeg(rotation, axis.normalized) : 0f;
		public static Quaternion ExtractTwist(Quaternion rotation, Vector3 axis) => axis.sqrMagnitude > 1e-12f ? BasisTwistSolveCore.ExtractTwist(rotation, axis.normalized) : Quaternion.identity;
		public static Quaternion ExtractSwing(Quaternion rotation, Vector3 axis) => rotation * Quaternion.Inverse(ExtractTwist(rotation, axis));
		public static bool TwistRotation(Quaternion parentRotation, Quaternion childRotation, Vector3 parentToChild, float fraction, Quaternion childBindLocal, Quaternion twistBindLocal, out Quaternion twistRotation) => BasisTwistSolveCore.Solve(parentRotation, childRotation, parentToChild, fraction, childBindLocal, twistBindLocal, out twistRotation, out _, out _);
	}
	public sealed class BasisIKBatch
	{
		internal readonly CilboxProxy owner;
		internal readonly CilboxBasisCommon box;
		internal readonly BasisContentBase content;
		internal readonly bool host;
		internal readonly List<CilboxIKSolver> solvers = new List<CilboxIKSolver>();
		readonly HashSet<Transform> written = new HashSet<Transform>();
		readonly List<Transform> boneList = new List<Transform>();
		internal int scheduledFrame, boneCount;
		bool dirty, scheduled, disposed;
		JobHandle handle;
		TransformAccessArray access;
		NativeArray<CilboxIKRead> reads;
		NativeArray<CilboxIKWrite> writes;
		NativeArray<CilboxIKRest> rests;
		NativeArray<CilboxIKLimbData> limbs;
		NativeArray<CilboxIKArmData> arms;
		NativeArray<CilboxIKChainData> chains;
		NativeArray<RalivIKSpine.SpineData> spines;
		internal BasisIKBatch(CilboxProxy owner, CilboxBasisCommon box, BasisContentBase content, bool host)
		{
			this.owner = owner;
			this.box = box;
			this.content = content;
			this.host = host;
		}
		public bool IsValid => !disposed && (host || (owner != null && !owner.disabled && box != null && !box.disabled));
		public bool IsScheduled => scheduled;
		public int SolverCount => solvers.Count;
		public int BoneCount => boneCount;
		public BasisIKLimb AddLimb(Transform root, Transform mid, Transform tip)
		{
			if (!CanAdd(3) || !Child(mid, root) || !Child(tip, mid)) return null;
			Transform[] bones = { root, mid, tip };
			if (!Writable(bones)) return null;
			CilboxIKSolver s = Add(new CilboxIKSolver(this, CilboxIKSolver.Limb, bones, null, false));
			return new BasisIKLimb(s);
		}
		public BasisIKArm AddArm(Transform body, Transform upper, Transform lower, Transform hand, bool isLeft)
		{
			if (!CanAdd(4) || body == null || !Allowed(body) || !Child(lower, upper) || !Child(hand, lower)) return null;
			Transform[] bones = { upper, lower, hand };
			if (!Writable(bones)) return null;
			CilboxIKSolver s = Add(new CilboxIKSolver(this, CilboxIKSolver.Arm, bones, body, isLeft));
			return new BasisIKArm(s);
		}
		public BasisIKChain AddChain(Transform[] bones)
		{
			if (bones == null || bones.Length < BasisIKShim.MinChainBones || bones.Length > BasisIKShim.MaxChainBones || !CanAdd(bones.Length)) return null;
			Transform[] copy = new Transform[bones.Length];
			for (int i = 0; i < bones.Length; i++)
			{
				if (bones[i] == null || (i > 0 && !Child(bones[i], copy[i - 1]))) return null;
				copy[i] = bones[i];
			}
			if (!Writable(copy)) return null;
			CilboxIKSolver s = Add(new CilboxIKSolver(this, CilboxIKSolver.Chain, copy, null, false));
			return new BasisIKChain(s);
		}
		public bool Schedule()
		{
			if (!IsValid) return false;
			Complete();
			if (dirty) Rebuild();
			if (boneCount == 0) return false;
			Push(Time.deltaTime);
			JobHandle read = new CilboxIKReadJob { reads = reads, rests = rests }.ScheduleReadOnly(access, 32);
			JobHandle solve = JobHandle.CombineDependencies(new CilboxIKLimbJob { reads = reads, rests = rests, writes = writes, limbs = limbs }.Schedule(limbs.Length, 4, read), new CilboxIKArmJob { reads = reads, rests = rests, writes = writes, arms = arms }.Schedule(arms.Length, 4, read), new CilboxIKChainJob { reads = reads, rests = rests, writes = writes, chains = chains, spines = spines }.Schedule(chains.Length, 1, read));
			handle = new CilboxIKWriteJob { writes = writes }.Schedule(access, solve);
			scheduled = true;
			scheduledFrame = Time.frameCount;
			CilboxIKBatches.Watch(this);
			JobHandle.ScheduleBatchedJobs();
			return true;
		}
		public void Complete()
		{
			if (!scheduled) return;
			handle.Complete();
			handle = default;
			scheduled = false;
			Pull();
		}
		public bool Run()
		{
			if (!Schedule()) return false;
			Complete();
			return true;
		}
		public void Dispose()
		{
			if (disposed) return;
			Complete();
			disposed = true;
			for (int i = 0; i < solvers.Count; i++) solvers[i].removed = true;
			solvers.Clear();
			written.Clear();
			Free();
			CilboxIKBatches.Forget(this);
		}
		internal bool Remove(CilboxIKSolver s)
		{
			if (s == null || s.removed || !solvers.Contains(s)) return false;
			Complete();
			solvers.Remove(s);
			for (int i = 0; i < s.bones.Length; i++) written.Remove(s.bones[i]);
			s.removed = true;
			dirty = true;
			boneCount -= s.BoneSpan;
			return true;
		}
		internal void MarkDirty()
		{
			Complete();
			dirty = true;
		}
		bool CanAdd(int bones)
		{
			if (!IsValid || solvers.Count >= BasisIKShim.MaxSolversPerBatch || boneCount + bones > BasisIKShim.MaxBonesPerBatch) return false;
			Complete();
			return true;
		}
		CilboxIKSolver Add(CilboxIKSolver s)
		{
			for (int i = 0; i < s.bones.Length; i++) written.Add(s.bones[i]);
			s.CaptureRest();
			solvers.Add(s);
			boneCount += s.BoneSpan;
			dirty = true;
			return s;
		}
		static bool Child(Transform child, Transform parent) => child != null && parent != null && child != parent && child.IsChildOf(parent);
		bool Writable(Transform[] bones)
		{
			for (int i = 0; i < bones.Length; i++)
			{
				if (bones[i] == null || written.Contains(bones[i]) || !Allowed(bones[i])) return false;
				for (int j = 0; j < i; j++) if (bones[j] == bones[i]) return false;
			}
			return true;
		}
		bool Allowed(Transform t)
		{
			if (host) return true;
			if (CilboxProtectedObjects.Protected(t) || !BasisContent.TryResolve(t.gameObject, out BasisContentBase c) || !ReferenceEquals(c, content)) return false;
			if (c is BasisAvatar avatar && avatar.Animator != null && avatar.Animator.isHuman)
			{
				for (HumanBodyBones b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++) if (avatar.Animator.GetBoneTransform(b) == t) return false;
			}
			return true;
		}
		void Rebuild()
		{
			Free();
			dirty = false;
			boneList.Clear();
			int limbCount = 0, armCount = 0, chainCount = 0;
			for (int i = 0; i < solvers.Count; i++)
			{
				CilboxIKSolver s = solvers[i];
				if (s.kind == CilboxIKSolver.Limb) s.slot = limbCount++;
				else if (s.kind == CilboxIKSolver.Arm) s.slot = armCount++;
				else s.slot = chainCount++;
			}
			boneCount = 0;
			for (int i = 0; i < solvers.Count; i++) boneCount += solvers[i].BoneSpan;
			if (boneCount == 0) return;
			reads = new NativeArray<CilboxIKRead>(boneCount, Allocator.Persistent);
			writes = new NativeArray<CilboxIKWrite>(boneCount, Allocator.Persistent);
			rests = new NativeArray<CilboxIKRest>(boneCount, Allocator.Persistent);
			limbs = new NativeArray<CilboxIKLimbData>(limbCount, Allocator.Persistent);
			arms = new NativeArray<CilboxIKArmData>(armCount, Allocator.Persistent);
			chains = new NativeArray<CilboxIKChainData>(chainCount, Allocator.Persistent);
			spines = new NativeArray<RalivIKSpine.SpineData>(chainCount, Allocator.Persistent);
			for (int i = 0; i < solvers.Count; i++)
			{
				CilboxIKSolver s = solvers[i];
				s.start = boneList.Count;
				for (int b = 0; b < s.bones.Length; b++)
				{
					rests[boneList.Count] = s.rest[b];
					boneList.Add(s.bones[b]);
				}
				if (s.kind == CilboxIKSolver.Arm)
				{
					rests[boneList.Count] = default;
					boneList.Add(s.body);
					arms[s.slot] = new CilboxIKArmData { start = s.start, state = s.armState };
				}
				else if (s.kind == CilboxIKSolver.Limb) limbs[s.slot] = new CilboxIKLimbData { start = s.start };
				else
				{
					chains[s.slot] = new CilboxIKChainData { start = s.start, count = s.bones.Length, seeded = s.seeded ? 1 : 0 };
					spines[s.slot] = s.spine;
				}
			}
			access = new TransformAccessArray(boneList.ToArray());
		}
		void Push(float dt)
		{
			for (int i = 0; i < solvers.Count; i++)
			{
				CilboxIKSolver s = solvers[i];
				bool active = s.enabled && s.hasTarget && s.weight > 0f;
				if (s.kind == CilboxIKSolver.Limb)
				{
					CilboxIKLimbData d = limbs[s.slot];
					d.active = active ? 1 : 0;
					s.Fill(ref d.target, ref d.targetRotation, ref d.hasRotation, ref d.weight, ref d.rotationWeight);
					d.hint = s.hint;
					d.hintWeight = s.hintWeight;
					d.reachSoftness = s.reachSoftness;
					d.bendAxis = s.bendAxis;
					d.hasBendAxis = s.hasBendAxis ? 1 : 0;
					limbs[s.slot] = d;
				}
				else if (s.kind == CilboxIKSolver.Arm)
				{
					CilboxIKArmData d = arms[s.slot];
					d.active = active ? 1 : 0;
					s.Fill(ref d.target, ref d.targetRotation, ref d.hasRotation, ref d.weight, ref d.rotationWeight);
					d.hint = s.hint;
					d.hasHint = s.hintWeight > 0f ? 1 : 0;
					d.reachSoftness = s.reachSoftness;
					d.smoothTime = s.smoothTime;
					d.isLeft = s.isLeft ? 1 : 0;
					d.dt = dt > 0f ? dt : 1f / 90f;
					arms[s.slot] = d;
				}
				else
				{
					CilboxIKChainData d = chains[s.slot];
					d.active = active ? 1 : 0;
					s.Fill(ref d.target, ref d.targetRotation, ref d.hasRotation, ref d.weight, ref d.rotationWeight);
					d.hasRoot = s.hasRoot ? 1 : 0;
					d.rootPosition = s.rootPosition;
					d.rootRotation = s.rootRotation;
					d.rootFollow = s.rootFollow;
					d.rootRotationFollow = s.rootRotationFollow;
					d.shapeFollow = s.shapeFollow;
					chains[s.slot] = d;
				}
			}
		}
		void Pull()
		{
			for (int i = 0; i < solvers.Count; i++)
			{
				CilboxIKSolver s = solvers[i];
				if (s.kind == CilboxIKSolver.Limb)
				{
					CilboxIKLimbData d = limbs[s.slot];
					if (d.solved == 0) continue;
					s.midPosition = d.midPosition;
					s.tipPosition = d.tipPosition;
					s.reach = d.reach;
				}
				else if (s.kind == CilboxIKSolver.Arm)
				{
					CilboxIKArmData d = arms[s.slot];
					s.armState = d.state;
					if (d.solved == 0) continue;
					s.midPosition = d.midPosition;
					s.tipPosition = d.tipPosition;
					s.reach = d.reach;
				}
				else
				{
					CilboxIKChainData d = chains[s.slot];
					if (d.solved == 0) continue;
					s.spine = spines[s.slot];
					s.seeded = true;
					s.tipPosition = d.tipPosition;
				}
			}
		}
		void Free()
		{
			if (access.isCreated) access.Dispose();
			if (reads.IsCreated) reads.Dispose();
			if (writes.IsCreated) writes.Dispose();
			if (rests.IsCreated) rests.Dispose();
			if (limbs.IsCreated) limbs.Dispose();
			if (arms.IsCreated) arms.Dispose();
			if (chains.IsCreated) chains.Dispose();
			if (spines.IsCreated) spines.Dispose();
		}
	}
	public sealed class BasisIKLimb
	{
		internal readonly CilboxIKSolver s;
		internal BasisIKLimb(CilboxIKSolver s) => this.s = s;
		public bool IsValid => s.Valid;
		public bool Enabled { get => s.enabled; set => s.enabled = value; }
		public float Weight { get => s.weight; set => s.weight = CilboxIKSolver.Unit(value, s.weight); }
		public float RotationWeight { get => s.rotationWeight; set => s.rotationWeight = CilboxIKSolver.Unit(value, s.rotationWeight); }
		public float ReachSoftness { get => s.reachSoftness; set => s.reachSoftness = CilboxIKSolver.Unit(value, s.reachSoftness); }
		public Vector3 MidPosition => s.midPosition;
		public Vector3 TipPosition => s.tipPosition;
		public float Reach => s.reach;
		public bool SetTarget(Vector3 position) => s.SetTarget(position, Quaternion.identity, false);
		public bool SetTarget(Vector3 position, Quaternion rotation) => s.SetTarget(position, rotation, true);
		public void ClearTarget() => s.hasTarget = false;
		public bool SetHint(Vector3 position, float weight) => s.SetHint(position, weight);
		public void ClearHint() => s.hintWeight = 0f;
		public bool SetBendAxis(Vector3 rootLocalAxis) => s.SetBendAxis(rootLocalAxis);
		public void ClearBendAxis() => s.hasBendAxis = false;
		public void CaptureRest() => s.Recapture();
		public bool Remove() => s.batch.Remove(s);
	}
	public sealed class BasisIKArm
	{
		internal readonly CilboxIKSolver s;
		internal BasisIKArm(CilboxIKSolver s) => this.s = s;
		public bool IsValid => s.Valid;
		public bool Enabled { get => s.enabled; set => s.enabled = value; }
		public float Weight { get => s.weight; set => s.weight = CilboxIKSolver.Unit(value, s.weight); }
		public float RotationWeight { get => s.rotationWeight; set => s.rotationWeight = CilboxIKSolver.Unit(value, s.rotationWeight); }
		public float ReachSoftness { get => s.reachSoftness; set => s.reachSoftness = CilboxIKSolver.Unit(value, s.reachSoftness); }
		public float Smoothing { get => s.smoothTime; set => s.smoothTime = float.IsNaN(value) ? s.smoothTime : Mathf.Clamp(value, 0f, 1f); }
		public Vector3 ElbowPosition => s.midPosition;
		public Vector3 HandPosition => s.tipPosition;
		public float Reach => s.reach;
		public bool SetTarget(Vector3 position) => s.SetTarget(position, Quaternion.identity, false);
		public bool SetTarget(Vector3 position, Quaternion rotation) => s.SetTarget(position, rotation, true);
		public void ClearTarget() => s.hasTarget = false;
		public bool SetHint(Vector3 position) => s.SetHint(position, 1f);
		public void ClearHint() => s.hintWeight = 0f;
		public void CaptureRest() => s.Recapture();
		public bool Remove() => s.batch.Remove(s);
	}
	public sealed class BasisIKChain
	{
		internal readonly CilboxIKSolver s;
		internal BasisIKChain(CilboxIKSolver s) => this.s = s;
		public bool IsValid => s.Valid;
		public int BoneCount => s.bones.Length;
		public bool Enabled { get => s.enabled; set => s.enabled = value; }
		public float Weight { get => s.weight; set => s.weight = CilboxIKSolver.Unit(value, s.weight); }
		public float RotationWeight { get => s.rotationWeight; set => s.rotationWeight = CilboxIKSolver.Unit(value, s.rotationWeight); }
		public float RootFollow { get => s.rootFollow; set => s.rootFollow = CilboxIKSolver.Unit(value, s.rootFollow); }
		public float RootRotationFollow { get => s.rootRotationFollow; set => s.rootRotationFollow = CilboxIKSolver.Unit(value, s.rootRotationFollow); }
		public float ShapeFollow { get => s.shapeFollow; set => s.shapeFollow = CilboxIKSolver.Unit(value, s.shapeFollow); }
		public Vector3 TipPosition => s.tipPosition;
		public bool SetTarget(Vector3 position) => s.SetTarget(position, Quaternion.identity, false);
		public bool SetTarget(Vector3 position, Quaternion rotation) => s.SetTarget(position, rotation, true);
		public void ClearTarget() => s.hasTarget = false;
		public bool SetRoot(Vector3 position, Quaternion rotation) => s.SetRoot(position, rotation);
		public void ClearRoot() => s.hasRoot = false;
		public void CaptureRest() => s.Recapture();
		public bool Remove() => s.batch.Remove(s);
	}
}

namespace Cilbox
{
	internal sealed class CilboxIKSolver
	{
		internal const int Limb = 0, Arm = 1, Chain = 2;
		internal readonly Basis.Shims.BasisIKBatch batch;
		internal readonly int kind;
		internal readonly Transform[] bones;
		internal readonly Transform body;
		internal readonly bool isLeft;
		internal readonly CilboxIKRest[] rest;
		internal int slot, start;
		internal bool enabled = true, removed, hasTarget, hasRotation, hasRoot, hasBendAxis, seeded;
		internal float weight = 1f, rotationWeight = 1f, reachSoftness, smoothTime = 0.08f, rootFollow, rootRotationFollow = 1f, shapeFollow = 1f, hintWeight, reach;
		internal Vector3 target, hint, bendAxis, rootPosition, midPosition, tipPosition;
		internal Quaternion targetRotation = Quaternion.identity, rootRotation = Quaternion.identity;
		internal BasisArmState armState;
		internal RalivIKSpine.SpineData spine;
		internal CilboxIKSolver(Basis.Shims.BasisIKBatch batch, int kind, Transform[] bones, Transform body, bool isLeft)
		{
			this.batch = batch;
			this.kind = kind;
			this.bones = bones;
			this.body = body;
			this.isLeft = isLeft;
			rest = new CilboxIKRest[bones.Length];
		}
		internal int BoneSpan => bones.Length + (kind == Arm ? 1 : 0);
		internal bool Valid => !removed && batch.IsValid;
		internal static float Unit(float value, float fallback) => float.IsNaN(value) ? fallback : Mathf.Clamp01(value);
		static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
		static bool Normalize(ref Quaternion q)
		{
			float m = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
			if (!(m > 1e-8f) || float.IsInfinity(m)) return false;
			float inv = 1f / Mathf.Sqrt(m);
			q = new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
			return true;
		}
		internal bool SetTarget(Vector3 position, Quaternion rotation, bool withRotation)
		{
			if (removed || !Finite(position) || (withRotation && !Normalize(ref rotation))) return false;
			target = position;
			targetRotation = withRotation ? rotation : Quaternion.identity;
			hasRotation = withRotation;
			hasTarget = true;
			return true;
		}
		internal bool SetHint(Vector3 position, float w)
		{
			if (removed || !Finite(position) || float.IsNaN(w)) return false;
			hint = position;
			hintWeight = Mathf.Clamp01(w);
			return true;
		}
		internal bool SetBendAxis(Vector3 axis)
		{
			if (removed || !Finite(axis) || axis.sqrMagnitude < 1e-10f) return false;
			bendAxis = axis.normalized;
			hasBendAxis = true;
			return true;
		}
		internal bool SetRoot(Vector3 position, Quaternion rotation)
		{
			if (removed || !Finite(position) || !Normalize(ref rotation)) return false;
			rootPosition = position;
			rootRotation = rotation;
			hasRoot = true;
			return true;
		}
		internal void Fill(ref Vector3 t, ref Quaternion r, ref int withRotation, ref float w, ref float rw)
		{
			t = target;
			r = targetRotation;
			withRotation = hasRotation ? 1 : 0;
			w = weight;
			rw = rotationWeight;
		}
		internal void Recapture()
		{
			if (removed) return;
			batch.MarkDirty();
			CaptureRest();
		}
		internal void CaptureRest()
		{
			for (int i = 0; i < bones.Length; i++)
			{
				Transform b = bones[i];
				if (b == null) continue;
				CilboxIKRest r = default;
				r.localPosition = b.localPosition;
				r.localRotation = b.localRotation;
				r.relativeRotation = Quaternion.identity;
				r.relative = float4x4.identity;
				r.first = i == 0 ? 1 : 0;
				if (i > 0 && bones[i - 1] != null)
				{
					r.relative = bones[i - 1].worldToLocalMatrix * b.localToWorldMatrix;
					r.relativeRotation = Quaternion.Inverse(bones[i - 1].rotation) * b.rotation;
				}
				rest[i] = r;
			}
			if (kind != Chain) return;
			spine = default;
			seeded = false;
			int n = bones.Length;
			spine.positions.Length = n;
			spine.rotations.Length = n;
			spine.restPositions.Length = n;
			spine.restRotations.Length = n;
			spine.t.Length = n;
			spine.targetSpinePositions.Length = n;
			spine.hipTargetAlignedSpinePositions.Length = n;
			spine.headTargetAlignedSpinePositions.Length = n;
			float total = 0f;
			for (int i = 1; i < n; i++)
			{
				total += (bones[i].position - bones[i - 1].position).magnitude;
				spine.t[i] = total;
			}
			for (int i = 0; i < n; i++) spine.t[i] = total > 1e-6f ? spine.t[i] / total : (float)i / (n - 1);
			spine.chestForward = Vector3.forward;
		}
	}
	internal struct CilboxIKRest
	{
		public Vector3 localPosition;
		public Quaternion localRotation, relativeRotation;
		public float4x4 relative;
		public int first;
	}
	internal struct CilboxIKRead
	{
		public Vector3 position, localPosition, localScale;
		public Quaternion rotation, localRotation;
		public float4x4 parent;
		public int valid;
	}
	internal struct CilboxIKWrite
	{
		public Quaternion localRotation;
		public Vector3 localPosition;
		public int flags;
	}
	internal struct CilboxIKLimbData
	{
		public int start, active, hasRotation, hasBendAxis, solved;
		public Vector3 target, hint, bendAxis, midPosition, tipPosition;
		public Quaternion targetRotation;
		public float weight, rotationWeight, hintWeight, reachSoftness, reach;
	}
	internal struct CilboxIKArmData
	{
		public int start, active, hasRotation, hasHint, isLeft, solved;
		public Vector3 target, hint, midPosition, tipPosition;
		public Quaternion targetRotation;
		public float weight, rotationWeight, reachSoftness, smoothTime, dt, reach;
		public BasisArmState state;
	}
	internal struct CilboxIKChainData
	{
		public int start, count, active, hasRotation, hasRoot, seeded, solved;
		public Vector3 target, rootPosition, tipPosition;
		public Quaternion targetRotation, rootRotation;
		public float weight, rotationWeight, rootFollow, rootRotationFollow, shapeFollow;
	}
	internal static class CilboxIKPose
	{
		internal static bool Finite(float4x4 m) => math.all(math.isfinite(m.c0)) && math.all(math.isfinite(m.c1)) && math.all(math.isfinite(m.c2)) && math.all(math.isfinite(m.c3));
		internal static Quaternion ParentRotation(in CilboxIKRead r) => r.rotation * Quaternion.Inverse(r.localRotation);
		internal static bool Rest(in NativeArray<CilboxIKRead> reads, in NativeArray<CilboxIKRest> rests, int start, int count, ref FixedList512Bytes<Vector3> p, ref FixedList512Bytes<Quaternion> q)
		{
			p.Clear();
			q.Clear();
			for (int i = 0; i < count; i++) if (reads[start + i].valid == 0) return false;
			CilboxIKRead r0 = reads[start];
			CilboxIKRest s0 = rests[start];
			if (!Finite(r0.parent)) return false;
			float4x4 m = math.mul(r0.parent, float4x4.TRS(s0.localPosition, s0.localRotation, r0.localScale));
			Quaternion rot = (ParentRotation(r0) * s0.localRotation).normalized;
			p.Add(m.c3.xyz);
			q.Add(rot);
			for (int i = 1; i < count; i++)
			{
				CilboxIKRest s = rests[start + i];
				m = math.mul(m, s.relative);
				rot = (rot * s.relativeRotation).normalized;
				p.Add(m.c3.xyz);
				q.Add(rot);
			}
			return Finite(m);
		}
		internal static void Write(in NativeArray<CilboxIKRead> reads, ref NativeArray<CilboxIKWrite> writes, int start, int count, ref FixedList512Bytes<Quaternion> solved, float weight)
		{
			for (int i = 0; i < count; i++)
			{
				CilboxIKRead r = reads[start + i];
				Quaternion parent = i == 0 ? ParentRotation(r) : solved[i - 1] * (Quaternion.Inverse(reads[start + i - 1].rotation) * ParentRotation(r));
				Quaternion local = Quaternion.Inverse(parent) * solved[i];
				if (weight < 1f) local = Quaternion.Slerp(r.localRotation, local, weight);
				writes[start + i] = new CilboxIKWrite { localRotation = local.normalized, localPosition = r.localPosition, flags = 1 };
			}
		}
		internal static void Clear(ref NativeArray<CilboxIKWrite> writes, int start, int count)
		{
			for (int i = 0; i < count; i++) writes[start + i] = default;
		}
		internal static Quaternion Tip(Quaternion follow, Quaternion target, int hasRotation, float rotationWeight) => hasRotation == 0 || rotationWeight <= 0f ? follow : rotationWeight >= 1f ? target : Quaternion.Slerp(follow, target, rotationWeight);
	}
	[BurstCompile]
	internal struct CilboxIKReadJob : IJobParallelForTransform
	{
		[WriteOnly] public NativeArray<CilboxIKRead> reads;
		[ReadOnly] public NativeArray<CilboxIKRest> rests;
		public void Execute(int index, TransformAccess t)
		{
			if (!t.isValid)
			{
				reads[index] = default;
				return;
			}
			CilboxIKRead r = default;
			t.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
			t.GetLocalPositionAndRotation(out Vector3 localPosition, out Quaternion localRotation);
			r.position = position;
			r.rotation = rotation;
			r.localPosition = localPosition;
			r.localRotation = localRotation;
			r.localScale = t.localScale;
			r.parent = rests[index].first != 0 ? math.mul((float4x4)t.localToWorldMatrix, math.inverse(float4x4.TRS(localPosition, localRotation, r.localScale))) : float4x4.identity;
			r.valid = 1;
			reads[index] = r;
		}
	}
	[BurstCompile]
	internal struct CilboxIKWriteJob : IJobParallelForTransform
	{
		[ReadOnly] public NativeArray<CilboxIKWrite> writes;
		public void Execute(int index, TransformAccess t)
		{
			CilboxIKWrite w = writes[index];
			if (w.flags == 0 || !t.isValid) return;
			if ((w.flags & 2) != 0) t.SetLocalPositionAndRotation(w.localPosition, w.localRotation);
			else t.localRotation = w.localRotation;
		}
	}
	[BurstCompile]
	internal struct CilboxIKLimbJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<CilboxIKRead> reads;
		[ReadOnly] public NativeArray<CilboxIKRest> rests;
		[NativeDisableParallelForRestriction] public NativeArray<CilboxIKWrite> writes;
		public NativeArray<CilboxIKLimbData> limbs;
		public void Execute(int index)
		{
			CilboxIKLimbData d = limbs[index];
			d.solved = 0;
			FixedList512Bytes<Vector3> p = default;
			FixedList512Bytes<Quaternion> q = default;
			if (d.active == 0 || !CilboxIKPose.Rest(reads, rests, d.start, 3, ref p, ref q))
			{
				CilboxIKPose.Clear(ref writes, d.start, 3);
				limbs[index] = d;
				return;
			}
			Vector3 ab = p[1] - p[0], bc = p[2] - p[1], bend = d.hasBendAxis != 0 ? q[0] * d.bendAxis : Vector3.Cross(ab, bc);
			if (bend.sqrMagnitude < 1e-10f) bend = Vector3.Cross(ab, Mathf.Abs(ab.normalized.y) < 0.99f ? Vector3.up : Vector3.forward);
			BasisLegSolveInput i = default;
			i.Root = p[0];
			i.Mid = p[1];
			i.Tip = p[2];
			i.RootRotation = q[0];
			i.MidRotation = q[1];
			i.TargetPosition = d.target;
			i.TargetRotation = d.hasRotation != 0 ? d.targetRotation : Quaternion.identity;
			i.TargetOffset = Quaternion.identity;
			i.HintPosition = d.hint;
			i.HintWeight = d.hintWeight;
			i.ReachSoftness = d.reachSoftness;
			i.BendNormal = bend.normalized;
			i.Up = Vector3.up;
			BasisLegSolveCore.Solve(i, out BasisLegSolveResult r);
			FixedList512Bytes<Quaternion> solved = default;
			solved.Add(r.RootRotationSolved);
			solved.Add(r.MidRotationSolved);
			solved.Add(CilboxIKPose.Tip(r.MidRotationSolved * (Quaternion.Inverse(q[1]) * q[2]), d.targetRotation, d.hasRotation, d.rotationWeight));
			CilboxIKPose.Write(reads, ref writes, d.start, 3, ref solved, d.weight);
			d.midPosition = r.KneeSolved;
			d.tipPosition = r.FootSolved;
			d.reach = r.ReachRatio;
			d.solved = 1;
			limbs[index] = d;
		}
	}
	[BurstCompile]
	internal struct CilboxIKArmJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<CilboxIKRead> reads;
		[ReadOnly] public NativeArray<CilboxIKRest> rests;
		[NativeDisableParallelForRestriction] public NativeArray<CilboxIKWrite> writes;
		public NativeArray<CilboxIKArmData> arms;
		public void Execute(int index)
		{
			CilboxIKArmData d = arms[index];
			d.solved = 0;
			FixedList512Bytes<Vector3> p = default;
			FixedList512Bytes<Quaternion> q = default;
			CilboxIKRead body = reads[d.start + 3];
			if (d.active == 0 || body.valid == 0 || !CilboxIKPose.Rest(reads, rests, d.start, 3, ref p, ref q))
			{
				CilboxIKPose.Clear(ref writes, d.start, 4);
				arms[index] = d;
				return;
			}
			bool left = d.isLeft != 0;
			Vector3 right = body.rotation * Vector3.right;
			BasisArmSolveInput i = BasisArmSolveInput.Defaults(left);
			i.Chest = body.position;
			i.Shoulder = p[0];
			i.RestElbow = p[1];
			i.RestHand = p[2];
			i.RestMidRotation = q[1];
			i.RestHandRotation = q[2];
			i.TargetPosition = d.target;
			i.TargetRotation = d.hasRotation != 0 ? d.targetRotation : q[2];
			i.TorsoUp = body.rotation * Vector3.up;
			i.TorsoForward = body.rotation * Vector3.forward;
			i.TorsoOut = left ? -right : right;
			i.HasTrackerHint = d.hasHint != 0;
			i.HintPosition = d.hint;
			i.Dt = d.dt;
			i.SmoothTime = d.smoothTime;
			i.ReachSoftness = d.reachSoftness;
			BasisArmSolveCore.Solve(i, ref d.state, out BasisArmSolveResult r);
			if (!r.Valid)
			{
				CilboxIKPose.Clear(ref writes, d.start, 4);
				arms[index] = d;
				return;
			}
			BasisArmSolveCore.Pose(i, r, q[0], q[1], out Quaternion upper, out Quaternion lower, out float roll);
			Vector3 forearm = r.Hand - r.Elbow;
			if ((roll > 0.001f || roll < -0.001f) && forearm.sqrMagnitude > 1e-10f) lower = Quaternion.AngleAxis(roll, forearm.normalized) * lower;
			FixedList512Bytes<Quaternion> solved = default;
			solved.Add(upper);
			solved.Add(lower);
			solved.Add(CilboxIKPose.Tip(lower * (Quaternion.Inverse(q[1]) * q[2]), d.targetRotation, d.hasRotation, d.rotationWeight));
			CilboxIKPose.Write(reads, ref writes, d.start, 3, ref solved, d.weight);
			writes[d.start + 3] = default;
			d.midPosition = r.Elbow;
			d.tipPosition = r.Hand;
			d.reach = r.ReachRatio;
			d.solved = 1;
			arms[index] = d;
		}
	}
	[BurstCompile]
	internal struct CilboxIKChainJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<CilboxIKRead> reads;
		[ReadOnly] public NativeArray<CilboxIKRest> rests;
		[NativeDisableParallelForRestriction] public NativeArray<CilboxIKWrite> writes;
		public NativeArray<CilboxIKChainData> chains;
		public NativeArray<RalivIKSpine.SpineData> spines;
		public void Execute(int index)
		{
			CilboxIKChainData d = chains[index];
			d.solved = 0;
			int n = d.count;
			FixedList512Bytes<Vector3> p = default;
			FixedList512Bytes<Quaternion> q = default;
			if (d.active == 0 || !CilboxIKPose.Rest(reads, rests, d.start, n, ref p, ref q))
			{
				CilboxIKPose.Clear(ref writes, d.start, n);
				chains[index] = d;
				return;
			}
			RalivIKSpine.SpineData s = spines[index];
			float length = 0f;
			for (int i = 0; i < n; i++)
			{
				s.restPositions[i] = p[i];
				s.restRotations[i] = q[i];
				if (i > 0) length += (p[i] - p[i - 1]).magnitude;
				if (d.seeded == 0)
				{
					s.positions[i] = p[i];
					s.rotations[i] = q[i];
				}
			}
			s.length = length;
			s.hipTargetPosition = d.hasRoot != 0 ? d.rootPosition : p[0];
			s.hipTargetRotation = d.hasRoot != 0 ? d.rootRotation : q[0];
			s.headTargetPosition = d.target;
			s.headTargetRotation = d.hasRotation != 0 ? d.targetRotation : q[n - 1];
			s.chestTargetPosition = p[2];
			s.chestTargetRotation = q[2];
			s.chestForward = Vector3.forward;
			s.chestHintWeight = 0f;
			s.chestIndex = 2;
			s.clampHipDistance = false;
			s.hipPositionFollow = d.rootFollow;
			s.hipRotationFollow = d.rootRotationFollow;
			s.headFollow = d.shapeFollow;
			RalivIKSpine.SolveSpine(ref s);
			FixedList512Bytes<Quaternion> solved = default;
			for (int i = 0; i < n - 1; i++) solved.Add(s.rotations[i]);
			solved.Add(CilboxIKPose.Tip(s.rotations[n - 2] * (Quaternion.Inverse(q[n - 2]) * q[n - 1]), d.targetRotation, d.hasRotation, d.rotationWeight));
			CilboxIKPose.Write(reads, ref writes, d.start, n, ref solved, d.weight);
			if (d.hasRoot != 0 || d.rootFollow > 0f)
			{
				CilboxIKRead r0 = reads[d.start];
				Vector3 local = math.mul(math.inverse(r0.parent), new float4(s.positions[0], 1f)).xyz;
				CilboxIKWrite w = writes[d.start];
				w.localPosition = d.weight < 1f ? Vector3.Lerp(r0.localPosition, local, d.weight) : local;
				w.flags |= 2;
				writes[d.start] = w;
			}
			d.tipPosition = s.positions[n - 1];
			d.seeded = 1;
			d.solved = 1;
			spines[index] = s;
			chains[index] = d;
		}
	}
	[AutoStaticsCleanup]
	internal static partial class CilboxIKBatches
	{
		const int StaleFrames = 3;
		static readonly List<Basis.Shims.BasisIKBatch> live = new List<Basis.Shims.BasisIKBatch>();
		static bool hooked;
		[NoAutoStaticsCleanup] static readonly Action tickHandler = Tick;
		internal static int LiveCount => live.Count;
		internal static Basis.Shims.BasisIKBatch Create(object self)
		{
			CilboxProxy proxy = self as CilboxProxy;
			CilboxBasisCommon box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			if (proxy == null || proxy.disabled || box == null || box.disabled || box.interpreterAccountingDepth == 0) return null;
			int mine = 0;
			for (int i = 0; i < live.Count; i++) if (ReferenceEquals(live[i].owner, proxy)) mine++;
			if (mine >= Basis.Shims.BasisIKShim.MaxBatchesPerScript || !CilboxBasisConsent.TryOwner(box, proxy.gameObject, out BasisContentBase content) || content == null) return null;
			return Track(new Basis.Shims.BasisIKBatch(proxy, box, content, false));
		}
		internal static Basis.Shims.BasisIKBatch CreateHost() => Track(new Basis.Shims.BasisIKBatch(null, null, null, true));
		static Basis.Shims.BasisIKBatch Track(Basis.Shims.BasisIKBatch batch)
		{
			live.Add(batch);
			Hook();
			return batch;
		}
		internal static void Watch(Basis.Shims.BasisIKBatch batch)
		{
			if (!live.Contains(batch)) live.Add(batch);
			Hook();
		}
		internal static void Forget(Basis.Shims.BasisIKBatch batch)
		{
			live.Remove(batch);
			if (live.Count == 0) Unhook();
		}
		internal static void DisposeAll()
		{
			for (int i = live.Count - 1; i >= 0; i--) if (i < live.Count) live[i].Dispose();
			live.Clear();
			Unhook();
		}
		static void Tick()
		{
			int frame = Time.frameCount;
			for (int i = live.Count - 1; i >= 0; i--)
			{
				if (i >= live.Count) continue;
				Basis.Shims.BasisIKBatch b = live[i];
				if (!b.IsValid) b.Dispose();
				else if (b.IsScheduled && frame - b.scheduledFrame >= StaleFrames) b.Complete();
			}
		}
		static void Hook()
		{
			if (hooked) return;
			hooked = true;
			BasisEventDriver.OnUpdate += tickHandler;
			Application.quitting -= DisposeAll;
			Application.quitting += DisposeAll;
		}
		static void Unhook()
		{
			if (!hooked) return;
			hooked = false;
			BasisEventDriver.OnUpdate -= tickHandler;
		}
	}
}
