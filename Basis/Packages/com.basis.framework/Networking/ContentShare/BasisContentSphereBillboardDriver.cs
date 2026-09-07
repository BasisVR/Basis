using System.Collections.Generic;
using Basis.Scripts.Drivers;
using Unity.Burst;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Yaw-only billboards every content-share orb title toward the local camera in a
/// single Burst job. Orbs keep spinning; only the title label is re-faced each frame.
/// Ticked from BasisEventDriver.LateUpdateBody alongside the remote nameplate jobs.
/// </summary>
public static class BasisContentSphereBillboardDriver
{
    private static readonly List<Transform> _labels = new List<Transform>(16);
    private static TransformAccessArray _transforms;
    private static JobHandle _handle;
    private static bool _scheduled;
    private static bool _dirty;

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
        _handle = job.Schedule(_transforms);
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
        if (!_scheduled) return;
        _handle.Complete();
        _scheduled = false;
    }

    public static void Dispose()
    {
        Complete();
        if (_transforms.isCreated) _transforms.Dispose();
        _labels.Clear();
        _dirty = false;
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
