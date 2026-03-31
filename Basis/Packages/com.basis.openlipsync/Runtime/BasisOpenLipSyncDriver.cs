using System;
using System.Collections.Generic;
using OpenLipSync.Inference;
using OpenLipSync.Inference.OVRCompat;
using UnityEngine;
using UnityEngine.AddressableAssets;

public static class BasisOpenLipSyncDriver
{
    public const int MaxSlots = 30;

    public const string ModelAddress = "Packages/com.basisvr.openlipsync/OpenLipSync/model.onnx.bytes";
    public const string ConfigAddress = "Packages/com.basisvr.openlipsync/OpenLipSync/config.json";

    private static OpenLipSyncBackend _backend;
    private static readonly Dictionary<int, uint> _playerToContext = new Dictionary<int, uint>();
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
            BasisDebug.Log($"[OpenLipSync] Initialized successfully ({MaxSlots} slots available)");
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

    public static bool TryAcquireSlot(int playerInstanceId, out uint contextHandle)
    {
        contextHandle = 0;
        if (!_initialized || _backend == null) return false;

        if (_playerToContext.TryGetValue(playerInstanceId, out contextHandle))
        {
            return true;
        }

        if (_playerToContext.Count >= MaxSlots) return false;

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

    public static void ReleaseSlot(int playerInstanceId)
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
