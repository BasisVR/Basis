using System;
using System.Collections.Generic;
using OpenLipSync.Inference;
using OpenLipSync.Inference.OVRCompat;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public static class BasisOpenLipSyncDriver
{
    /// <summary>
    /// When true, <see cref="MaxSlots"/> is enforced as a hard cap.
    /// When false, slot count is unlimited (bounded only by players in viseme range).
    /// Controlled by the settings toggle "Limit OpenLipSync Slots".
    /// </summary>
    public static bool UseSlotLimit = false;

    /// <summary>
    /// Maximum concurrent OpenLipSync contexts (only enforced when <see cref="UseSlotLimit"/> is true).
    /// Controlled by the settings slider "OpenLipSync Max Slots".
    /// </summary>
    public static int MaxSlots = 30;

    public const string ModelAddress = "Packages/com.basisvr.openlipsync/OpenLipSync/model.onnx.bytes";
    public const string ConfigAddress = "Packages/com.basisvr.openlipsync/OpenLipSync/config.json";

    private static OpenLipSyncBackend _backend;
    private static readonly Dictionary<EntityId, uint> _playerToContext = new Dictionary<EntityId, uint>();
    private static readonly Dictionary<EntityId, Action> _slotRevokedCallbacks = new Dictionary<EntityId, Action>();
    private static readonly Stack<uint> _contextPool = new Stack<uint>();
    private static bool _initialized;

    public static bool IsInitialized => _initialized;

    public static AsyncOperationHandle<TextAsset> modelAsset;
    public static AsyncOperationHandle<TextAsset> configAsset;
    public static void BeginInitialize()
    {
        if (_initialized || modelAsset.IsValid()) return;

        modelAsset = Addressables.LoadAssetAsync<TextAsset>(ModelAddress);
        configAsset = Addressables.LoadAssetAsync<TextAsset>(ConfigAddress);
    }

    public static void EndInitialize()
    {
        if (_initialized) return;

        try
        {
            if (!modelAsset.IsValid())
            {
                BeginInitialize();
            }

            modelAsset.WaitForCompletion();
            if (configAsset.IsValid())
            {
                configAsset.WaitForCompletion();
            }

            if (!modelAsset.IsValid() || modelAsset.Status != AsyncOperationStatus.Succeeded || modelAsset.Result == null)
            {
                BasisDebug.Log("[OpenLipSync] No model found at " + ModelAddress + " - OpenLipSync disabled");
                ReleaseHandles();
                return;
            }

            _backend = new OpenLipSyncBackend();
            string configJson = configAsset.IsValid() && configAsset.Status == AsyncOperationStatus.Succeeded && configAsset.Result != null
                ? configAsset.Result.text
                : null;

            int sampleRate = AudioSettings.outputSampleRate;
            var result = _backend.InitializeFromBytes(sampleRate, modelAsset.Result.bytes, configJson);

            if (result != Result.Success)
            {
                BasisDebug.LogWarning($"[OpenLipSync] Backend initialization failed: {_backend.LastError}");
                _backend.Dispose();
                _backend = null;
                ReleaseHandles();
                return;
            }

            _initialized = true;
            string slotInfo = UseSlotLimit ? $"{MaxSlots} slots" : "unlimited slots";
            BasisDebug.Log($"[OpenLipSync] Initialized successfully ({slotInfo} available)");
            ReleaseHandles();
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"[OpenLipSync] Initialization exception: {ex.Message}");
            Shutdown();
        }
    }

    public static void Initialize()
    {
        BeginInitialize();
        EndInitialize();
    }

    private static void ReleaseHandles()
    {
        if (modelAsset.IsValid())
        {
            Addressables.Release(modelAsset);
        }
        if (configAsset.IsValid())
        {
            Addressables.Release(configAsset);
        }
        modelAsset = default;
        configAsset = default;
    }

    public static void Shutdown()
    {
        _initialized = false;

        if (_backend != null)
        {
            foreach (var kvp in _playerToContext)
            {
                _backend.DestroyContext(kvp.Value);
            }
            while (_contextPool.Count > 0)
            {
                _backend.DestroyContext(_contextPool.Pop());
            }
        }
        _playerToContext.Clear();
        _contextPool.Clear();

        _backend?.Dispose();
        _backend = null;
        ReleaseHandles();
    }

    public static bool TryAcquireSlot(EntityId playerInstanceId, out uint contextHandle)
    {
        contextHandle = 0;
        if (!_initialized || _backend == null) return false;

        if (_playerToContext.TryGetValue(playerInstanceId, out contextHandle))
        {
            return true;
        }

        if (UseSlotLimit && _playerToContext.Count >= MaxSlots) return false;

        // Reuse a pooled context if available
        if (_contextPool.Count > 0)
        {
            contextHandle = _contextPool.Pop();
            _playerToContext[playerInstanceId] = contextHandle;
            return true;
        }

        uint ctx = 0;
        var result = _backend.CreateContext(ref ctx);
        if (result != Result.Success)
        {
            BasisDebug.LogWarning($"[OpenLipSync] Failed to create context: {_backend.LastError}");
            return false;
        }

        _playerToContext[playerInstanceId] = ctx;
        contextHandle = ctx;
        return true;
    }

    public static void ReleaseSlot(EntityId playerInstanceId)
    {
        if (_playerToContext.TryGetValue(playerInstanceId, out uint ctx))
        {
            _playerToContext.Remove(playerInstanceId);
            _contextPool.Push(ctx);
        }
    }

    /// <summary>
    /// Test seam. When non-null this stands in for the ONNX session, so the scheduling around
    /// inference — batching, result publication, how many frames the mouth ends up behind the
    /// audio — can be measured offline with a controllable inference cost and no model load.
    /// Null in normal play; the read costs one static load per inference.
    /// </summary>
    public static Func<uint, float[], int, Frame, Result> ProcessFrameOverride;

    /// <summary>
    /// Overload that processes only the first <paramref name="sampleCount"/> samples
    /// from the buffer, avoiding the need to allocate a trimmed copy.
    /// </summary>
    public static Result ProcessFrame(uint contextHandle, float[] audioData, int sampleCount, Frame frame)
    {
        var over = ProcessFrameOverride;
        if (over != null)
        {
            return over(contextHandle, audioData, sampleCount, frame);
        }

        return !_initialized || _backend == null ? Result.Unknown : _backend.ProcessFrameFloat(contextHandle, new ReadOnlySpan<float>(audioData, 0, sampleCount), stereo: false, ref frame);
    }

    public static Result SendSignal(uint contextHandle, Signals signal, int arg1)
    {
        return !_initialized || _backend == null ? Result.Unknown : _backend.SendSignal(contextHandle, signal, arg1);
    }

    /// <summary>
    /// Registers a per-entity callback fired when that entity's slot is forcefully revoked
    /// (e.g. MaxSlots was lowered). The callback should dispose its BasisOpenLipSyncContext and
    /// stop producing visemes. The backend context is already destroyed before this fires — do
    /// NOT call ReleaseSlot. Re-registering the same entity overwrites the previous callback.
    /// </summary>
    public static void RegisterSlotRevokedCallback(EntityId entityId, Action onRevoked)
    {
        _slotRevokedCallbacks[entityId] = onRevoked;
    }

    public static void UnregisterSlotRevokedCallback(EntityId entityId)
    {
        _slotRevokedCallbacks.Remove(entityId);
    }

    /// <summary>
    /// Evicts excess contexts when UseSlotLimit is enabled and the active count exceeds MaxSlots.
    /// Call after changing UseSlotLimit or MaxSlots.
    /// </summary>
    public static void EnforceSlotLimit()
    {
        if (!UseSlotLimit || !_initialized || _backend == null)
        {
            return;
        }

        // Evict active contexts that exceed the new limit
        while (_playerToContext.Count > MaxSlots)
        {
            var enumerator = _playerToContext.GetEnumerator();
            if (!enumerator.MoveNext())
            {
                break;
            }

            var entityId = enumerator.Current.Key;
            var ctx = enumerator.Current.Value;
            _playerToContext.Remove(entityId);
            _contextPool.Push(ctx);
            if (_slotRevokedCallbacks.TryGetValue(entityId, out var onRevoked))
            {
                onRevoked?.Invoke();
            }
        }
        // Trim pooled contexts so total (active + pooled) doesn't exceed MaxSlots
        int maxPooled = Math.Max(0, MaxSlots - _playerToContext.Count);
        while (_contextPool.Count > maxPooled)
        {
            _backend.DestroyContext(_contextPool.Pop());
        }
    }
    public static int ActiveSlotCount => _playerToContext.Count;
    public static int DebugMelFramesProduced => _backend?.DebugMelFramesProduced ?? 0;
    public static int DebugInferenceRuns => _backend?.DebugInferenceRuns ?? 0;
    public static float DebugLastInferenceMax => _backend?.DebugLastInferenceMax ?? 0f;
    public static string DebugPipelineStatus => _backend?.DebugPipelineStatus ?? "no backend";
    public static string DebugBackendError => _backend?.LastError ?? "";
    public static string DebugInferenceDetail => _backend?.DebugInferenceDetail ?? "";
}
