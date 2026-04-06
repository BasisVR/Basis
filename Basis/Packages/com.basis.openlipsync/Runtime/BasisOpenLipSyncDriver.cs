using System;
using System.Collections.Generic;
using OpenLipSync.Inference;
using OpenLipSync.Inference.OVRCompat;
using UnityEngine;
using UnityEngine.AddressableAssets;

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
    private static bool _initialized;

    public static bool IsInitialized => _initialized;

    public static void Initialize()
    {
        if (_initialized) return;

        try
        {
            var modelAsset = Addressables.LoadAssetAsync<TextAsset>(ModelAddress).WaitForCompletion();
            var configAsset = Addressables.LoadAssetAsync<TextAsset>(ConfigAddress).WaitForCompletion();

            if (modelAsset == null)
            {
                BasisDebug.Log("[OpenLipSync] No model found at " + ModelAddress + " - OpenLipSync disabled, using uLipSync fallback");
                return;
            }

            _backend = new OpenLipSyncBackend();
            string configJson = configAsset != null ? configAsset.text : null;

            int sampleRate = AudioSettings.outputSampleRate;
            var result = _backend.InitializeFromBytes(sampleRate, modelAsset.bytes, configJson);

            if (result != Result.Success)
            {
                BasisDebug.LogWarning($"[OpenLipSync] Backend initialization failed: {_backend.LastError}");
                _backend.Dispose();
                _backend = null;
                return;
            }

            _initialized = true;
            string slotInfo = UseSlotLimit ? $"{MaxSlots} slots" : "unlimited slots";
            BasisDebug.Log($"[OpenLipSync] Initialized successfully ({slotInfo} available)");
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"[OpenLipSync] Initialization exception: {ex.Message} - falling back to uLipSync");
            Shutdown();
        }
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
        }
        _playerToContext.Clear();

        _backend?.Dispose();
        _backend = null;
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
            _backend?.DestroyContext(ctx);
        }
    }

    public static Result ProcessFrame(uint contextHandle, float[] audioData, Frame frame)
    {
        if (!_initialized || _backend == null) return Result.Unknown;
        return _backend.ProcessFrameFloat(contextHandle, audioData, stereo: false, ref frame);
    }

    /// <summary>
    /// Overload that processes only the first <paramref name="sampleCount"/> samples
    /// from the buffer, avoiding the need to allocate a trimmed copy.
    /// </summary>
    public static Result ProcessFrame(uint contextHandle, float[] audioData, int sampleCount, Frame frame)
    {
        if (!_initialized || _backend == null) return Result.Unknown;
        return _backend.ProcessFrameFloat(contextHandle, new ReadOnlySpan<float>(audioData, 0, sampleCount), stereo: false, ref frame);
    }

    public static Result SendSignal(uint contextHandle, Signals signal, int arg1)
    {
        if (!_initialized || _backend == null) return Result.Unknown;
        return _backend.SendSignal(contextHandle, signal, arg1);
    }

    public static int ActiveSlotCount => _playerToContext.Count;

    // Debug accessors
    public static int DebugMelFramesProduced => _backend?.DebugMelFramesProduced ?? 0;
    public static int DebugInferenceRuns => _backend?.DebugInferenceRuns ?? 0;
    public static float DebugLastInferenceMax => _backend?.DebugLastInferenceMax ?? 0f;
    public static string DebugPipelineStatus => _backend?.DebugPipelineStatus ?? "no backend";
    public static string DebugBackendError => _backend?.LastError ?? "";
    public static string DebugInferenceDetail => _backend?.DebugInferenceDetail ?? "";
}
