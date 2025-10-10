using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
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
    /// <summary>
    /// Set of objects owned locally by this client; these will be transmitted to remote peers.
    /// </summary>
    public static readonly HashSet<BasisObjectSyncNetworking> OwnedObjectSyncs = new();

    /// <summary>
    /// Set of objects owned by remote peers; these will be interpolated locally.
    /// </summary>
    public static readonly HashSet<BasisObjectSyncNetworking> RemoteOwnedObjectSyncs = new();

    /// <summary>
    /// Minimum interval between local transmit bursts. Units are the same as <see cref="_lastUpdateTime"/>/<c>currentTime</c>.
    /// </summary>
    public static float TargetMilliseconds = 0.25f;

    /// <summary>
    /// Timestamp of the last local transmit burst.
    /// </summary>
    private static double _lastUpdateTime;

    // Remote interpolation buffers
    private static TransformAccessArray _remoteTransforms;
    private static NativeList<float3> _targetPositions;
    private static NativeList<quaternion> _targetRotations;
    private static NativeList<float> _lerpMultipliers;

    /// <summary>
    /// Scratch array for building a contiguous set of transforms for the job.
    /// </summary>
    private static Transform[] _cachedTransforms = Array.Empty<Transform>();

    /// <summary>
    /// Handle for the scheduled remote-lerp job chain.
    /// </summary>
    private static JobHandle _remoteJobHandle;

    /// <summary>
    /// Initializes persistent containers used by the remote interpolation system.
    /// Must be called once before scheduling.
    /// </summary>
    public static void Initalization()
    {
        _remoteTransforms = new TransformAccessArray(0);
        _targetPositions = new NativeList<float3>(128, Allocator.Persistent);
        _targetRotations = new NativeList<quaternion>(128, Allocator.Persistent);
        _lerpMultipliers = new NativeList<float>(128, Allocator.Persistent);
    }

    /// <summary>
    /// Disposes persistent containers and completes any pending remote job.
    /// </summary>
    public static void OnDestroy()
    {
        _remoteJobHandle.Complete();

        if (_remoteTransforms.isCreated) _remoteTransforms.Dispose();
        if (_targetPositions.IsCreated) _targetPositions.Dispose();
        if (_targetRotations.IsCreated) _targetRotations.Dispose();
        if (_lerpMultipliers.IsCreated) _lerpMultipliers.Dispose();
    }

    /// <summary>
    /// Sends state for all locally owned objects at the configured cadence.
    /// </summary>
    /// <param name="currentTime">Current time source used to throttle transmission.</param>
    public static void TransmitOwnedPickups(double currentTime)
    {
        if (currentTime - _lastUpdateTime < TargetMilliseconds)
        {
            return;
        }

        _lastUpdateTime = currentTime;

        foreach (BasisObjectSyncNetworking obj in OwnedObjectSyncs)
        {
            if (obj != null)
            {
                obj.SendNetworkSync();
            }
        }
    }

    /// <summary>
    /// Builds job data and schedules the Burst job that interpolates remote-owned objects.
    /// </summary>
    /// <param name="deltaTime">Frame delta time used to scale per-object lerp multipliers.</param>
    public static void ScheduleRemoteLerp(float deltaTime)
    {
        _remoteJobHandle.Complete();

        int count = 0;

        foreach (var obj in RemoteOwnedObjectSyncs)
        {
            if (obj == null || obj.IsOwnedLocallyOnClient) continue;
            count++;
        }

        if (count == 0) return;

        if (_cachedTransforms.Length != count)
        {
            _cachedTransforms = new Transform[count];
        }

        int index = 0;
        bool State = _targetPositions.Length <= count;

        if (State)
        {
            _targetPositions.ResizeUninitialized(count);
            _targetRotations.ResizeUninitialized(count);
            _lerpMultipliers.ResizeUninitialized(count);
        }

        foreach (BasisObjectSyncNetworking obj in RemoteOwnedObjectSyncs)
        {
            if (obj == null || obj.IsOwnedLocallyOnClient) continue;

            _cachedTransforms[index] = obj.SelfTransform;

            _targetPositions[index] = obj.BTU.TargetPosition;
            _targetRotations[index] = obj.BTU.TargetRotation;
            _lerpMultipliers[index] = obj.BTU.LerpMultipliers * deltaTime;

            index++;
        }

        if (_remoteTransforms.isCreated)
        {
            if (_remoteTransforms.length != _cachedTransforms.Length)
            {
                _remoteTransforms.Dispose();
                _remoteTransforms = new TransformAccessArray(_cachedTransforms);
            }
            else
            {
                _remoteTransforms.SetTransforms(_cachedTransforms);
            }
        }
        else
        {
            _remoteTransforms = new TransformAccessArray(_cachedTransforms);
        }

        var job = new RemoteSyncJob
        {
            targetPositions = _targetPositions,
            targetRotations = _targetRotations,
            lerpMultipliers = _lerpMultipliers
        };

        _remoteJobHandle = job.Schedule(_remoteTransforms);
    }

    /// <summary>
    /// Completes the previously scheduled remote interpolation job.
    /// </summary>
    public static void CompleteScheduledRemoteLerp()
    {
        _remoteJobHandle.Complete();
    }

    /// <summary>
    /// Burst job that lerps transforms toward target position/rotation with per-instance multipliers.
    /// </summary>
    [BurstCompile]
    public struct RemoteSyncJob : IJobParallelForTransform
    {
        /// <summary>Target local positions for each transform.</summary>
        [ReadOnly] public NativeList<float3> targetPositions;
        /// <summary>Target local rotations for each transform.</summary>
        [ReadOnly] public NativeList<quaternion> targetRotations;
        /// <summary>Lerp factors per instance (already scaled by deltaTime).</summary>
        [ReadOnly] public NativeList<float> lerpMultipliers;

        /// <summary>
        /// Applies interpolation to a single transform instance.
        /// </summary>
        /// <param name="index">Transform index.</param>
        /// <param name="transform">Transform access wrapper.</param>
        public void Execute(int index, TransformAccess transform)
        {
            float lerp = lerpMultipliers[index];
            if (lerp <= 0f) return;

            if (transform.isValid)
            {
                transform.GetLocalPositionAndRotation(out Vector3 currentPos, out Quaternion currentRot);
                float3 newPos = math.lerp(currentPos, targetPositions[index], lerp);
                quaternion newRot = math.slerp(currentRot, targetRotations[index], lerp);

                transform.SetLocalPositionAndRotation(newPos, newRot);
            }
        }
    }

    #region Static API

    /// <summary>
    /// Registers an object as locally owned so it will be transmitted.
    /// </summary>
    public static void AddLocalOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null)
            OwnedObjectSyncs.Add(obj);
    }

    /// <summary>
    /// Unregisters an object from the locally owned set.
    /// </summary>
    public static void RemoveLocalOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null)
            OwnedObjectSyncs.Remove(obj);
    }

    /// <summary>
    /// Registers a remotely owned object so it will be interpolated locally.
    /// </summary>
    public static void AddRemoteOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null)
            RemoteOwnedObjectSyncs.Add(obj);
    }

    /// <summary>
    /// Unregisters a remotely owned object from local interpolation.
    /// </summary>
    public static void RemoveRemoteOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null)
            RemoteOwnedObjectSyncs.Remove(obj);
    }

    #endregion
}

/// <summary>
/// Network translation update payload used to feed the interpolation system.
/// </summary>
[Serializable]
public struct BasisTranslationUpdate
{
    /// <summary>Desired target local position.</summary>
    public float3 TargetPosition;
    /// <summary>Desired target local rotation.</summary>
    public quaternion TargetRotation;
    /// <summary>Desired target local scale (not currently applied by the job above).</summary>
    public float3 TargetScales;
    /// <summary>Lerp factor to apply (usually scaled by delta time each frame).</summary>
    public float LerpMultipliers;
}
