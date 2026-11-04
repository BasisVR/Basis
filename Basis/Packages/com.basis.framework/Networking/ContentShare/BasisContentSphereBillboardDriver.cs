using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.Drivers;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Yaw-only billboards every content-share orb title toward the local camera in a
/// single Burst job. Orbs keep spinning; only the title label is re-faced each frame.
/// Ticked from BasisEventDriver.LateUpdateBody alongside the remote nameplate jobs.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisContentSphereBillboardDriver
{
    private static readonly List<Transform> _labels = new List<Transform>(16);
    [NoAutoStaticsCleanup] private static TransformAccessArray _transforms;
    private static JobHandle _handle;
    private static bool _scheduled;
    private static bool _dirty;
    private static readonly List<BasisContentSphere> _spheres = new List<BasisContentSphere>(16);
    private static readonly List<BasisContentSphere> _bobbing = new List<BasisContentSphere>(16);
    [NoAutoStaticsCleanup] private static TransformAccessArray _bobTransforms;
    [NoAutoStaticsCleanup] private static NativeArray<float4> _bob;
    private static JobHandle _bobHandle;
    private static bool _bobScheduled, _spheresDirty;

    public static void RegisterSphere(BasisContentSphere sphere)
    {
        if (sphere == null) return;
        _spheres.Add(sphere);
        _spheresDirty = true;
    }

    public static void UnregisterSphere(BasisContentSphere sphere)
    {
        if (_spheres.Remove(sphere)) _spheresDirty = true;
    }

    private static void RebuildSpheres()
    {
        _spheresDirty = false;
        for (int i = 0; i < _bobbing.Count; i++) if (_bobbing[i] != null) _bobbing[i].BobPhase = _bob[i].w;
        if (_bobTransforms.isCreated) _bobTransforms.Dispose();
        if (_bob.IsCreated) _bob.Dispose();
        _bobbing.Clear();
        for (int i = _spheres.Count - 1; i >= 0; i--) if (_spheres[i] == null) _spheres.RemoveAt(i);
        for (int i = 0; i < _spheres.Count; i++) if (_spheres[i].Bobs) _bobbing.Add(_spheres[i]);
        if (_bobbing.Count == 0) return;
        _bobTransforms = new TransformAccessArray(_bobbing.Count);
        _bob = new NativeArray<float4>(_bobbing.Count, Allocator.Persistent);
        for (int i = 0; i < _bobbing.Count; i++)
        {
            _bobTransforms.Add(_bobbing[i].transform);
            _bob[i] = new float4(_bobbing[i].RestPosition, _bobbing[i].BobPhase);
        }
    }

    public static void ScheduleBob(float deltaTime)
    {
        CompleteBob();
        if (_spheresDirty) RebuildSpheres();
        for (int i = 0; i < _spheres.Count; i++) if (_spheres[i] != null && _spheres[i].WatchesPortalEntry) _spheres[i].UpdatePortalEntry();
        if (!_bobTransforms.isCreated || _bobTransforms.length == 0) return;
        _bobHandle = new BobJob
        {
            Bob = _bob,
            Step = deltaTime * BasisContentSphere.BobPhaseClock,
            Offset = BasisContentSphere.BobPhaseOffset,
            Spin = quaternion.AxisAngle(new float3(0f, 1f, 0f), math.radians(BasisContentSphere.RotationSpeed * deltaTime)),
        }.Schedule(_bobTransforms);
        _bobScheduled = true;
        JobHandle.ScheduleBatchedJobs();
    }

    private static void CompleteBob()
    {
        if (!_bobScheduled) return;
        _bobHandle.Complete();
        _bobScheduled = false;
    }

    public static void Register(Transform label)
    {
        if (label == null) return;
        _labels.Add(label);
        _dirty = true;
    }

    public static void Unregister(Transform label)
    {
        if (label == null) return;
        if (_labels.Remove(label)) _dirty = true;
    }

    private static void RebuildIfDirty()
    {
        if (!_dirty) return;
        _dirty = false;

        for (int i = _labels.Count - 1; i >= 0; i--)
        {
            if (_labels[i] == null) _labels.RemoveAt(i);
        }

        if (_transforms.isCreated) _transforms.Dispose();

        int count = _labels.Count;
        if (count == 0) return;

        _transforms = new TransformAccessArray(count);
        for (int i = 0; i < count; i++)
        {
            _transforms.Add(_labels[i]);
        }
    }

    public static void ScheduleSimulate()
    {
        if (_scheduled) Complete();
        if (!BasisLocalCameraDriver.HasInstance) return;

        RebuildIfDirty();
        if (!_transforms.isCreated || _transforms.length == 0) return;

        BillboardJob job = new BillboardJob { CameraPosition = BasisLocalCameraDriver.Position };
        _handle = _bobScheduled ? job.Schedule(_transforms, _bobHandle) : job.Schedule(_transforms);
        _scheduled = true;
        // Self-sufficient kick: BasisEventDriver currently calls this right before
        // BasisRemoteFaceManagement.Simulate, and flushes the whole batch right after that with its
        // own JobHandle.ScheduleBatchedJobs() — so this job has always actually started by the time
        // Complete() joins it later in LateUpdate. That overlap is incidental to caller ordering, not
        // guaranteed by this function on its own; kicking here makes it self-sufficient instead.
        JobHandle.ScheduleBatchedJobs();
    }

    public static void Complete()
    {
        CompleteBob();
        if (!_scheduled) return;
        _handle.Complete();
        _scheduled = false;
    }

    public static void Dispose()
    {
        Complete();
        if (_transforms.isCreated) _transforms.Dispose();
        if (_bobTransforms.isCreated) _bobTransforms.Dispose();
        if (_bob.IsCreated) _bob.Dispose();
        _labels.Clear();
        _spheres.Clear();
        _bobbing.Clear();
        _dirty = false;
        _spheresDirty = false;
    }

    [BurstCompile]
    private struct BobJob : IJobParallelForTransform
    {
        public NativeArray<float4> Bob;
        public float Step, Offset;
        public quaternion Spin;

        public void Execute(int index, TransformAccess tx)
        {
            if (!tx.isValid) return;
            float4 b = Bob[index];
            b.w += Step;
            Bob[index] = b;
            tx.position = new float3(b.x, b.y + math.sin(b.w) * Offset, b.z);
            tx.rotation = math.mul(Spin, (quaternion)tx.rotation);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    private struct BillboardJob : IJobParallelForTransform
    {
        public float3 CameraPosition;

        public void Execute(int index, TransformAccess tx)
        {
            float3 fromCamera = (float3)tx.position - CameraPosition;
            float2 xz = new float2(fromCamera.x, fromCamera.z);
            float yaw = math.lengthsq(xz) > 1e-12f ? math.atan2(xz.x, xz.y) : 0f;
            tx.rotation = quaternion.RotateY(yaw);
        }
    }
}
