using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// Orchestrates object-sync networking for owned and remotely owned objects:
/// sends local updates on a cadence and smoothly lerps remote transforms via a Burst job.
/// </summary>
public static class BasisObjectSyncDriver
{
    public static readonly HashSet<BasisObjectSyncNetworking> OwnedObjectSyncs = new();
    public static readonly HashSet<BasisObjectSyncNetworking> RemoteOwnedObjectSyncs = new();

    public static float TargetMilliseconds = 0.25f;
    private static double _lastUpdateTime;

    // Consolidated per-object payload buffer. Capacity grows 2x, never shrinks.
    private static NativeArray<BasisTranslationUpdate> _updates;

    // Growable scratch filled single-pass each frame.
    private static Transform[] _scratchTransforms = Array.Empty<Transform>();

    // Exact-size array currently backing _remoteTransforms; doubles as the
    // dirty-check snapshot for the following frame.
    private static Transform[] _cachedTransforms = Array.Empty<Transform>();

    private static TransformAccessArray _remoteTransforms;
    private static JobHandle _remoteJobHandle;

    public static int TargetCount = -1;

    public static void Initalization()
    {
        _remoteTransforms = new TransformAccessArray(0);
        _updates = new NativeArray<BasisTranslationUpdate>(128, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        TargetCount = 0;
    }

    public static void OnDestroy()
    {
        _remoteJobHandle.Complete();

        if (_remoteTransforms.isCreated) _remoteTransforms.Dispose();
        if (_updates.IsCreated) _updates.Dispose();
    }

    public static void TransmitOwnedPickups(double currentTime)
    {
        if (!Basis.Scripts.Networking.BasisNetworkManagement.NetworkRunning
            || !Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerIsConnected
            || Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerPeer == null)
        {
            return;
        }

        if (currentTime - _lastUpdateTime < TargetMilliseconds) return;

        _lastUpdateTime = currentTime;

        foreach (BasisObjectSyncNetworking obj in OwnedObjectSyncs)
        {
            if (obj != null)
            {
                obj.SendNetworkSync();
            }
        }
    }

    public static void ScheduleRemoteLerp(float deltaTime)
    {
        _remoteJobHandle.Complete();

        int upperBound = RemoteOwnedObjectSyncs.Count;
        if (upperBound == 0) return;

        EnsureCapacity(upperBound);

        int count = 0;
        unsafe
        {
            BasisTranslationUpdate* updatesPtr = (BasisTranslationUpdate*)_updates.GetUnsafePtr();
            foreach (var obj in RemoteOwnedObjectSyncs)
            {
                if (obj == null || obj.IsOwnedLocallyOnClient) continue;

                _scratchTransforms[count] = obj.SelfTransform;
                updatesPtr[count] = obj.BTU;
                count++;
            }
        }

        if (count == 0) return;

        TargetCount = count;

        // Single native .length call per frame, reused below.
        int taaLength = _remoteTransforms.isCreated ? _remoteTransforms.length : -1;

        if (taaLength != count)
        {
            if (_cachedTransforms.Length != count) _cachedTransforms = new Transform[count];
            Array.Copy(_scratchTransforms, _cachedTransforms, count);

            if (_remoteTransforms.isCreated) _remoteTransforms.Dispose();
            _remoteTransforms = new TransformAccessArray(_cachedTransforms);
        }
        else
        {
            bool transformsChanged = false;
            for (int i = 0; i < count; i++)
            {
                if (_cachedTransforms[i] != _scratchTransforms[i])
                {
                    transformsChanged = true;
                    break;
                }
            }

            if (transformsChanged)
            {
                Array.Copy(_scratchTransforms, _cachedTransforms, count);
                _remoteTransforms.SetTransforms(_cachedTransforms);
            }
        }

        var job = new RemoteSyncJob
        {
            updates = _updates,
            deltaTime = deltaTime,
        };

        _remoteJobHandle = job.Schedule(_remoteTransforms);
    }

    private static void EnsureCapacity(int required)
    {
        if (_scratchTransforms.Length < required)
        {
            int newCap = _scratchTransforms.Length;
            if (newCap == 0) newCap = 16;
            while (newCap < required) newCap *= 2;
            Array.Resize(ref _scratchTransforms, newCap);
        }

        if (!_updates.IsCreated || _updates.Length < required)
        {
            int newCap = _updates.IsCreated ? _updates.Length : 0;
            if (newCap == 0) newCap = 128;
            while (newCap < required) newCap *= 2;

            if (_updates.IsCreated) _updates.Dispose();
            _updates = new NativeArray<BasisTranslationUpdate>(newCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }
    }

    public static void CompleteScheduledRemoteLerp()
    {
        _remoteJobHandle.Complete();
    }

    [BurstCompile]
    public struct RemoteSyncJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<BasisTranslationUpdate> updates;
        public float deltaTime;

        public void Execute(int index, TransformAccess transform)
        {
            BasisTranslationUpdate u = updates[index];
            float lerp = u.LerpMultipliers * deltaTime;
            if (lerp <= 0f) return;

            if (transform.isValid)
            {
                transform.GetLocalPositionAndRotation(out Vector3 currentPos, out Quaternion currentRot);

                float3 newPos = math.lerp(currentPos, u.TargetPosition, lerp);
                quaternion newRot = math.slerp(currentRot, u.TargetRotation, lerp);
                Vector3 currentScale = transform.localScale;
                float3 newScale = math.lerp(currentScale, u.TargetScales, lerp);

                transform.SetLocalPositionAndRotation(newPos, newRot);
                transform.localScale = new Vector3(newScale.x, newScale.y, newScale.z);
            }
        }
    }

    #region Static API
    public static void AddLocalOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null) OwnedObjectSyncs.Add(obj);
    }

    public static void RemoveLocalOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null) OwnedObjectSyncs.Remove(obj);
    }

    public static void AddRemoteOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null) RemoteOwnedObjectSyncs.Add(obj);
    }

    public static void RemoveRemoteOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null) RemoteOwnedObjectSyncs.Remove(obj);
    }

    public static void Reset()
    {
        _remoteJobHandle.Complete();
        OwnedObjectSyncs.Clear();
        RemoteOwnedObjectSyncs.Clear();
        _lastUpdateTime = 0;
    }
    #endregion
}

/// <summary>
/// Network translation update payload used to feed the interpolation system.
/// </summary>
[Serializable]
public struct BasisTranslationUpdate
{
    public float3 TargetPosition;
    public quaternion TargetRotation;
    public float3 TargetScales;
    public float LerpMultipliers;
}
