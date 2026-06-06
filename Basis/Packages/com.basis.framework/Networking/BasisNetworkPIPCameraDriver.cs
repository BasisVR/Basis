using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using static SerializableBasis;

/// <summary>
/// Client-side manager for networked PIP cameras.
/// Fully static - driven by BasisLocalPlayer.AfterSimulateOnLate via Simulate().
/// Uses Unity Jobs (Burst-compiled IJobParallelFor) for smoothing remote camera transforms.
/// Spawns/destroys the remote PIP prefab via Addressables.
/// </summary>
public static class BasisNetworkPIPCameraDriver
{
    /// <summary>
    /// Fired when a remote player's PIP camera is created.
    /// </summary>
    public static event Action<ushort, float3, quaternion> OnRemotePIPCreated;

    /// <summary>
    /// Fired when a remote player's PIP camera is destroyed.
    /// </summary>
    public static event Action<ushort> OnRemotePIPDestroyed;

    /// <summary>
    /// Fired when a remote player takes a photo. Passes (playerID, worldPosition).
    /// </summary>
    public static event Action<ushort, Vector3> OnRemoteShutterSoundReceived;


    // Addressable path for the remote PIP prefab
    private const string RemotePIPPrefabPath = "Packages/com.basis.sdk/Prefabs/UI/Camera Prefab/BasisCameraRemotePip.prefab";

    // Cached prefab loaded via Addressables
    private static AsyncOperationHandle<GameObject> prefabHandle;
    private static GameObject loadedPrefab;

    // Mapping from playerID -> index in NativeArrays
    private static readonly Dictionary<ushort, int> playerIdToIndex = new();
    private static readonly Dictionary<int, ushort> indexToPlayerId = new();
    private static readonly HashSet<ushort> activeRemotePIPs = new();

    // Job data
    private static NativeArray<float3> currentPositions;
    private static NativeArray<float3> targetPositions;
    private static NativeArray<quaternion> currentRotations;
    private static NativeArray<quaternion> targetRotations;
    private static NativeArray<byte> activeFlags;
    private static JobHandle smoothHandle;

    // Transform references and spawned instances for active PIP models
    private static readonly Dictionary<ushort, Transform> pipTransforms = new();
    private static readonly Dictionary<ushort, GameObject> pipInstances = new();

    private const int MaxPIPCameras = 256;
    private const float LerpSpeed = 12f;

    /// <summary>
    /// Priority for AfterSimulateOnLate. Runs after the hand-held camera interactable (202).
    /// </summary>
    private const int SimulatePriority = 203;

    private static bool initialized;
    private static int nextFreeIndex;
    private static readonly Queue<int> recycledIndices = new();

    /// <summary>
    /// Initialize native arrays and subscribe to the simulation loop.
    /// Called from BasisNetworkLifeCycle.Initialize().
    /// </summary>
    public static void Create()
    {
        if (initialized) return;

        currentPositions = new NativeArray<float3>(MaxPIPCameras, Allocator.Persistent);
        targetPositions = new NativeArray<float3>(MaxPIPCameras, Allocator.Persistent);
        currentRotations = new NativeArray<quaternion>(MaxPIPCameras, Allocator.Persistent);
        targetRotations = new NativeArray<quaternion>(MaxPIPCameras, Allocator.Persistent);
        activeFlags = new NativeArray<byte>(MaxPIPCameras, Allocator.Persistent);

        // Initialize rotations to identity
        for (int i = 0; i < MaxPIPCameras; i++)
        {
            currentRotations[i] = quaternion.identity;
            targetRotations[i] = quaternion.identity;
        }

        nextFreeIndex = 0;

        // Preload the remote PIP prefab
        prefabHandle = Addressables.LoadAssetAsync<GameObject>(RemotePIPPrefabPath);
        loadedPrefab = prefabHandle.WaitForCompletion();

        BasisLocalPlayer.AfterSimulateOnLate.AddAction(SimulatePriority, Simulate);
        initialized = true;
    }

    /// <summary>
    /// Dispose native arrays and unsubscribe from the simulation loop.
    /// Called from BasisNetworkLifeCycle shutdown path.
    /// </summary>
    public static void Shutdown()
    {
        if (!initialized) return;

        BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(SimulatePriority, Simulate);

        ClearRemotePIPs();

        // Release the loaded prefab
        if (prefabHandle.IsValid())
        {
            Addressables.Release(prefabHandle);
        }
        loadedPrefab = null;

        if (currentPositions.IsCreated) currentPositions.Dispose();
        if (targetPositions.IsCreated) targetPositions.Dispose();
        if (currentRotations.IsCreated) currentRotations.Dispose();
        if (targetRotations.IsCreated) targetRotations.Dispose();
        if (activeFlags.IsCreated) activeFlags.Dispose();

        initialized = false;
    }

    public static void ClearRemotePIPs()
    {
        if (!initialized) return;

        smoothHandle.Complete();

        foreach (var kvp in pipInstances)
        {
            if (kvp.Value != null)
            {
                UnityEngine.Object.Destroy(kvp.Value);
            }
        }
        pipInstances.Clear();
        pipTransforms.Clear();
        playerIdToIndex.Clear();
        indexToPlayerId.Clear();
        activeRemotePIPs.Clear();
        recycledIndices.Clear();
        nextFreeIndex = 0;

        if (activeFlags.IsCreated)
        {
            for (int i = 0; i < activeFlags.Length; i++)
            {
                activeFlags[i] = 0;
            }
        }
    }

    /// <summary>
    /// Per-frame simulation driven by BasisLocalPlayer.AfterSimulateOnLate.
    /// Completes the previous frame's job, applies transforms, schedules next job.
    /// </summary>
    private static void Simulate()
    {
        if (!initialized || activeRemotePIPs.Count == 0) return;

        smoothHandle.Complete();

        // Apply smoothed positions and rotations to transforms
        foreach (var kvp in pipTransforms)
        {
            if (playerIdToIndex.TryGetValue(kvp.Key, out int index))
            {
                float3 pos = currentPositions[index];
                quaternion rot = currentRotations[index];
                kvp.Value.SetPositionAndRotation(
                    new Vector3(pos.x, pos.y, pos.z),
                    new Quaternion(rot.value.x, rot.value.y, rot.value.z, rot.value.w)
                );
            }
        }

        // Schedule next frame's smoothing job
        var job = new PIPSmoothJob
        {
            DeltaTime = Time.deltaTime,
            LerpSpeed = LerpSpeed,
            CurrentPositions = currentPositions,
            TargetPositions = targetPositions,
            CurrentRotations = currentRotations,
            TargetRotations = targetRotations,
            ActiveFlags = activeFlags,
        };
        smoothHandle = job.Schedule(MaxPIPCameras, 32);
    }

    /// <summary>
    /// Called from network receive when a remote PIP state message arrives.
    /// </summary>
    public static void OnRemotePIPState(CameraPIPStateMessage msg)
    {
        if (!initialized) return;

        smoothHandle.Complete();

        if (msg.IsActive)
        {
            int index = GetOrAllocateIndex(msg.PlayerID);
            float3 pos = new float3(msg.PositionX, msg.PositionY, msg.PositionZ);
            quaternion rot = new quaternion(msg.RotationX, msg.RotationY, msg.RotationZ, msg.RotationW);
            currentPositions[index] = pos;
            targetPositions[index] = pos;
            currentRotations[index] = rot;
            targetRotations[index] = rot;
            activeFlags[index] = 1;
            activeRemotePIPs.Add(msg.PlayerID);

            SpawnRemotePIP(msg.PlayerID, pos, rot);

            OnRemotePIPCreated?.Invoke(msg.PlayerID, pos, rot);
        }
        else
        {
            if (playerIdToIndex.TryGetValue(msg.PlayerID, out int index))
            {
                activeFlags[index] = 0;
                FreeIndex(msg.PlayerID, index);
            }
            activeRemotePIPs.Remove(msg.PlayerID);

            DestroyRemotePIP(msg.PlayerID);

            OnRemotePIPDestroyed?.Invoke(msg.PlayerID);
        }
    }

    /// <summary>
    /// Called from network receive when a remote PIP position update arrives.
    /// </summary>
    public static void OnRemotePIPPosition(CameraPIPPositionMessage msg)
    {
        if (!initialized) return;

        if (playerIdToIndex.TryGetValue(msg.PlayerID, out int index))
        {
            smoothHandle.Complete();
            targetPositions[index] = new float3(msg.PositionX, msg.PositionY, msg.PositionZ);
            targetRotations[index] = new quaternion(msg.RotationX, msg.RotationY, msg.RotationZ, msg.RotationW);
        }
    }

    /// <summary>
    /// Send local PIP camera state to server.
    /// </summary>
    public static void SendPIPState(bool isActive, Vector3 position, Quaternion rotation)
    {
        ClientCameraPIPStateMessage msg = new ClientCameraPIPStateMessage
        {
            IsActive = isActive,
            PositionX = position.x,
            PositionY = position.y,
            PositionZ = position.z,
            RotationX = rotation.x,
            RotationY = rotation.y,
            RotationZ = rotation.z,
            RotationW = rotation.w,
        };

        NetDataWriter writer = new NetDataWriter();
        msg.Serialize(writer);
        BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.CameraPIPStateChannel, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Send local PIP camera position and rotation to server.
    /// </summary>
    public static void SendPIPPosition(Vector3 position, Quaternion rotation)
    {
        ClientCameraPIPPositionMessage msg = new ClientCameraPIPPositionMessage
        {
            PositionX = position.x,
            PositionY = position.y,
            PositionZ = position.z,
            RotationX = rotation.x,
            RotationY = rotation.y,
            RotationZ = rotation.z,
            RotationW = rotation.w,
        };

        NetDataWriter writer = new NetDataWriter();
        msg.Serialize(writer);
        BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.CameraPIPPositionChannel, DeliveryMethod.Sequenced);
    }

    /// <summary>
    /// Try to get the world position of a remote PIP camera by player ID.
    /// </summary>
    public static bool TryGetPIPPosition(ushort playerId, out Vector3 position)
    {
        if (pipTransforms.TryGetValue(playerId, out Transform t) && t != null)
        {
            position = t.position;
            return true;
        }
        position = Vector3.zero;
        return false;
    }

    /// <summary>
    /// Try to get the world pose of a remote PIP camera by player ID.
    /// </summary>
    public static bool TryGetPIPPose(ushort playerId, out Vector3 position, out Quaternion rotation)
    {
        if (pipTransforms.TryGetValue(playerId, out Transform t) && t != null)
        {
            position = t.position;
            rotation = t.rotation;
            return true;
        }

        position = Vector3.zero;
        rotation = Quaternion.identity;
        return false;
    }

    /// <summary>
    /// Send a camera shutter sound event to the server for broadcast to other clients.
    /// </summary>
    public static void SendShutterSound()
    {
        NetDataWriter writer = new NetDataWriter();
        writer.Put(BasisNetworkCommons.EventType_CameraShutterSound);
        BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.EventsChannel, DeliveryMethod.Sequenced);
    }

    /// <summary>
    /// Send a camera countdown event to the server for broadcast to other clients.
    /// </summary>
    public static void SendCountdown(byte seconds)
    {
        ClientCameraCountdownMessage msg = new ClientCameraCountdownMessage
        {
            Seconds = seconds,
        };

        NetDataWriter writer = new NetDataWriter();
        writer.Put(BasisNetworkCommons.EventType_CameraCountdown);
        msg.Serialize(writer);
        BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.EventsChannel, DeliveryMethod.Sequenced);
    }

    /// <summary>
    /// Called from BasisNetworkEvents when a remote player's shutter sound message arrives.
    /// Plays the shutter sound at the remote PIP camera position.
    /// </summary>
    public static void OnRemoteShutterSound(CameraShutterSoundMessage msg)
    {
        if (BasisDeviceManagement.Instance.CameraShutterSound != null && TryGetPIPPosition(msg.PlayerID, out Vector3 position))
        {
            AudioSource.PlayClipAtPoint(BasisDeviceManagement.Instance.CameraShutterSound, position, SMModuleAudio.ActivePropVolume);
        }

        OnRemoteShutterSoundReceived?.Invoke(msg.PlayerID, TryGetPIPPosition(msg.PlayerID, out Vector3 pos) ? pos : Vector3.zero);
    }

    /// <summary>
    /// Called from BasisNetworkEvents when a remote player starts a countdown.
    /// Replays the same tick/shutter timing at the remote PIP camera position.
    /// </summary>
    public static void OnRemoteCountdown(CameraCountdownMessage msg)
    {
        if (!initialized) return;
        BasisDeviceManagement.Instance.StartCoroutine(RemoteCountdownCoroutine(msg.PlayerID, msg.Seconds));
    }

    private static IEnumerator RemoteCountdownCoroutine(ushort playerId, int seconds)
    {
        for (int i = seconds; i > 0; i--)
        {
            if (BasisDeviceManagement.Instance.CameraCountdownTickSound != null && TryGetPIPPosition(playerId, out Vector3 tickPos))
            {
                AudioSource.PlayClipAtPoint(BasisDeviceManagement.Instance.CameraCountdownTickSound, tickPos, SMModuleAudio.ActivePropVolume);
            }
            yield return new WaitForSeconds(1f);
        }

        // Match the 0.5s pause before capture
        yield return new WaitForSeconds(0.5f);

        if (BasisDeviceManagement.Instance.CameraShutterSound != null && TryGetPIPPosition(playerId, out Vector3 shutterPos))
        {
            AudioSource.PlayClipAtPoint(BasisDeviceManagement.Instance.CameraShutterSound, shutterPos, SMModuleAudio.ActivePropVolume);
        }
    }

    /// <summary>
    /// Clean up when a remote player disconnects.
    /// </summary>
    public static void HandlePlayerDisconnect(ushort playerId)
    {
        if (!initialized) return;

        if (activeRemotePIPs.Contains(playerId))
        {
            smoothHandle.Complete();
            if (playerIdToIndex.TryGetValue(playerId, out int index))
            {
                activeFlags[index] = 0;
                FreeIndex(playerId, index);
            }
            activeRemotePIPs.Remove(playerId);

            DestroyRemotePIP(playerId);

            OnRemotePIPDestroyed?.Invoke(playerId);
        }
    }

    /// <summary>
    /// Spawn the remote PIP prefab instance for a player.
    /// </summary>
    private static void SpawnRemotePIP(ushort playerId, float3 position, quaternion rotation)
    {
        if (loadedPrefab == null)
        {
            BasisDebug.LogError("Remote PIP prefab not loaded");
            return;
        }

        // Destroy any existing instance for this player
        DestroyRemotePIP(playerId);

        Vector3 pos = new Vector3(position.x, position.y, position.z);
        Quaternion rot = new Quaternion(rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w);

        GameObject instance = UnityEngine.Object.Instantiate(loadedPrefab, pos, rot);
        instance.transform.parent = BasisDeviceManagement.Instance.transform;

        // Apply rotation offset from the metadata component
        if (instance.TryGetComponent<BasisCameraRemotePip>(out BasisCameraRemotePip pipMeta))
        {
            pipMeta.PlayerID = playerId;
        }

        pipInstances[playerId] = instance;
        pipTransforms[playerId] = instance.transform;
    }

    /// <summary>
    /// Destroy the remote PIP prefab instance for a player.
    /// </summary>
    private static void DestroyRemotePIP(ushort playerId)
    {
        pipTransforms.Remove(playerId);

        if (pipInstances.TryGetValue(playerId, out GameObject instance))
        {
            pipInstances.Remove(playerId);
            if (instance != null)
            {
                UnityEngine.Object.Destroy(instance);
            }
        }
    }

    private static int GetOrAllocateIndex(ushort playerId)
    {
        if (playerIdToIndex.TryGetValue(playerId, out int existing))
            return existing;

        int index = recycledIndices.Count > 0 ? recycledIndices.Dequeue() : nextFreeIndex++;
        playerIdToIndex[playerId] = index;
        indexToPlayerId[index] = playerId;
        return index;
    }

    private static void FreeIndex(ushort playerId, int index)
    {
        playerIdToIndex.Remove(playerId);
        indexToPlayerId.Remove(index);
        recycledIndices.Enqueue(index);
    }

    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct PIPSmoothJob : IJobParallelFor
    {
        public float DeltaTime;
        public float LerpSpeed;

        public NativeArray<float3> CurrentPositions;
        [ReadOnly] public NativeArray<float3> TargetPositions;
        public NativeArray<quaternion> CurrentRotations;
        [ReadOnly] public NativeArray<quaternion> TargetRotations;
        [ReadOnly] public NativeArray<byte> ActiveFlags;

        public void Execute(int index)
        {
            if (ActiveFlags[index] == 0)
                return;

            float t = math.saturate(DeltaTime * LerpSpeed);

            CurrentPositions[index] = math.lerp(
                CurrentPositions[index],
                TargetPositions[index],
                t
            );

            CurrentRotations[index] = math.nlerp(
                CurrentRotations[index],
                TargetRotations[index],
                t
            );
        }
    }
}
