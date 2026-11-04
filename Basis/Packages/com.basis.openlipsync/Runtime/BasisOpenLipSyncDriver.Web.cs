#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using OpenLipSync.Inference;
using OpenLipSync.Inference.OVRCompat;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public static partial class BasisOpenLipSyncDriver
{
    private const float WebStatsInterval = 30f;
    [NoAutoStaticsCleanup] private static int _webStage;
    [NoAutoStaticsCleanup] private static float _webStatsTimer, _webFramesSeen, _webBusySeen;
    [NoAutoStaticsCleanup] private static readonly float[] _webStats = new float[4];

    public static int InputSampleRate
    {
        get
        {
            OpenLipSyncBackend backend = _backend;
            return backend != null ? backend.SampleRate : 0;
        }
    }

    public static void WebTick(int sampleRate, float deltaTime)
    {
        if (_webStage == 3 && (!_initialized || _backend == null)) _webStage = 0;
        switch (_webStage)
        {
            case 0:
                if (_initialized || sampleRate <= 0) return;
                modelAsset = Addressables.LoadAssetAsync<TextAsset>(ModelAddress);
                configAsset = Addressables.LoadAssetAsync<TextAsset>(ConfigAddress);
                _webStage = 1;
                return;
            case 1:
                if (!modelAsset.IsValid() || !configAsset.IsValid())
                {
                    _webStage = 0;
                    return;
                }
                if (modelAsset.IsDone && configAsset.IsDone) StartWebBackend(sampleRate);
                return;
            case 2:
                int state = BasisWebLipSync.BasisWebLipSync_State();
                if (state == BasisWebLipSync.Loading) return;
                if (state != BasisWebLipSync.Ready)
                {
                    FailWeb("[OpenLipSync] The web inference worker did not start - OpenLipSync disabled");
                    return;
                }
                _initialized = true;
                _webStage = 3;
                BasisDebug.Log($"[OpenLipSync] Initialized on web, inference runs on a worker at {sampleRate} Hz input ({(UseSlotLimit ? $"{MaxSlots} slots" : "unlimited slots")})");
                return;
            case 3:
                if ((_webStatsTimer += deltaTime) < WebStatsInterval) return;
                _webStatsTimer = 0f;
                if (BasisWebLipSync.BasisWebLipSync_Stats(_webStats) != BasisWebLipSync.Ready)
                {
                    FailWeb("[OpenLipSync] The web inference worker stopped - OpenLipSync disabled");
                    return;
                }
                float frames = _webStats[0] - _webFramesSeen, busy = _webStats[1] - _webBusySeen;
                _webFramesSeen = _webStats[0];
                _webBusySeen = _webStats[1];
                if (frames > 0f) BasisDebug.Log($"[OpenLipSync] web worker: {frames / WebStatsInterval:F0} frames/s, {busy / frames:F3} ms per frame, {_webStats[2]:F0} dropped, {_webStats[3]:F0} in flight, {ActiveSlotCount} slots");
                return;
        }
    }

    public static void FlushWeb()
    {
        if (_initialized) BasisWebLipSync.BasisWebLipSync_Flush();
    }

    private static void StartWebBackend(int sampleRate)
    {
        try
        {
            if (modelAsset.Status != AsyncOperationStatus.Succeeded || modelAsset.Result == null)
            {
                BasisDebug.Log("[OpenLipSync] No model found at " + ModelAddress + " - OpenLipSync disabled");
                ReleaseHandles();
                _webStage = 4;
                return;
            }
            string configJson = configAsset.Status == AsyncOperationStatus.Succeeded && configAsset.Result != null ? configAsset.Result.text : null;
            _backend = new OpenLipSyncBackend();
            Result result = _backend.InitializeFromBytes(sampleRate, modelAsset.Result.bytes, configJson);
            ReleaseHandles();
            if (result != Result.Success)
            {
                BasisDebug.LogWarning($"[OpenLipSync] Backend initialization failed: {_backend.LastError}");
                _backend.Dispose();
                _backend = null;
                _webStage = 4;
                return;
            }
            _webStage = 2;
        }
        catch (Exception ex)
        {
            FailWeb($"[OpenLipSync] Initialization exception: {ex.Message}");
        }
    }

    private static void FailWeb(string message)
    {
        BasisDebug.LogWarning(message);
        List<Action> revoked = new List<Action>();
        foreach (KeyValuePair<EntityId, uint> slot in _playerToContext)
        {
            if (_slotRevokedCallbacks.TryGetValue(slot.Key, out Action onRevoked) && onRevoked != null) revoked.Add(onRevoked);
        }
        Shutdown();
        for (int index = 0; index < revoked.Count; index++) revoked[index]();
        _webStage = 4;
    }
}
#endif
