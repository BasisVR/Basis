using Basis.BTween;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Debugging;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Transmitters;
using Basis.BasisUI;
using Basis.Scripts.UI;
using Basis.Scripts.UI.NamePlate;
using GatorDragonGames.JigglePhysics;
using SteamAudio;
using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Central per-frame driver that coordinates device actions, networking compute/apply,
/// physics scheduling for JigglePhysics, and various local simulation hooks.
/// </summary>
[DefaultExecutionOrder(-31950)]
public partial class BasisEventDriver : MonoBehaviour
{
    // ── Platform flag (single #if, used as runtime bool everywhere else) ──
    public static readonly bool IsServer =
#if UNITY_SERVER
        true;
#else
        false;
#endif

    // ── Profile section IDs ─────────────────────────────────────
    const int PROF_NETWORK_APPLY = 0;
    const int PROF_DEVICE_MANAGEMENT = 1;
    const int PROF_REMOTE_AUDIO_SIMULATE = 2;
    const int PROF_NAMEPLATE_SCHEDULE = 3;
    const int PROF_BTWEEN = 4;
    const int PROF_LOCAL_PLAYER = 5;
    const int PROF_REMOTE_FACE_SIMULATE = 6;
    const int PROF_REMOTE_AUDIO_APPLY = 7;
    const int PROF_BLENDSHAPE_SIMULATE = 8;
    const int PROF_BLENDSHAPE_APPLY = 9;
    const int PROF_JIGGLE_SCHEDULE = 10;
    const int PROF_NETWORK_TRANSMIT = 11;
    const int PROF_JIGGLE_POSE = 12;
    const int PROF_MICROPHONE = 13;
    const int PROF_NAMEPLATE_COMPLETE = 14;
    const int PROF_JIGGLE_COMPLETE_POSE = 15;
    const int PROF_SHADOW_CLONE = 16;

    const int PROF_NET_TRANSMIT_PICKUPS = 0;
    const int PROF_NET_FIRE_BEFORE_APPLY = 1;
    const int PROF_NET_SIMULATE_APPLY = 2;
    const int PROF_NET_COMPLETE_REMOTE_LERP = 3;

    // ── Partial method declarations (calls are stripped in non-editor builds) ──
    partial void ProfileLateUpdateInit();
    partial void ProfileBegin(int section);
    partial void ProfileBegin2();
    partial void ProfileEnd(int section);
    partial void ProfileEnd2(int section);
    partial void ProfileLateUpdateFinish();
    partial void ProfileBeforeRenderInit();
    partial void ProfileBeforeRenderFinish();

    // ── Fields ──────────────────────────────────────────────────
    /// <summary>
    /// Accumulator used to track elapsed time since the last interval tick.
    /// </summary>
    public float timeSinceLastUpdate = 0f;

    /// <summary>
    /// Frame delta time (scaled).
    /// </summary>
    public float DeltaTime;

    /// <summary>
    /// Current time as a double (scaled), mirrored from <see cref="Time.timeAsDouble"/>.
    /// </summary>
    public double TimeAsDouble;

    /// <summary>
    /// Fixed-step time as a double, mirrored from <see cref="Time.fixedTimeAsDouble"/>.
    /// </summary>
    public double fixedTimeAsDouble;

    /// <summary>
    /// Fixed-step delta time in seconds.
    /// </summary>
    public float fixedDeltaTime;

    /// <summary>
    /// Unscaled frame delta time in seconds.
    /// </summary>
    public float unscaledDeltaTime;

    /// <summary>
    /// realtimeSinceStartupAsDouble
    /// </summary>
    public double realtimeSinceStartupAsDouble;
    /// <summary>
    /// material we use to display jiggle physics visually
    /// </summary>
    [SerializeField]
    private UnityEngine.Material proceduralMaterial;
    /// <summary>
    /// mesh we use to display around the jiggle physics
    /// </summary>
    [SerializeField]
    private Mesh sphereMesh;
    /// <summary>
    /// Instance of Basis Event Driver
    /// </summary>

    public static BasisEventDriver Instance;

    public static bool StateOfOnRenderBefore = false;

    // ── Lifecycle ───────────────────────────────────────────────

    /// <summary>
    /// Unity enable hook. Subscribes render callbacks (client), initializes scene and network drivers.
    /// </summary>
    public void OnEnable()
    {
        Instance = this;
        if (!IsServer)
            Application.onBeforeRender += OnBeforeRender;
        BasisOpenLipSyncDriver.Initialize();
        BasisSceneFactory.Initalize();
        BasisObjectSyncDriver.Initalization();
        RemoteBoneJobSystem.Initialize();
    }

    /// <summary>
    /// Unity destroy hook. Cleans up network/physics resources and unsubscribes callbacks.
    /// </summary>
    public void OnDestroy()
    {
        BasisOpenLipSyncDriver.Shutdown();
        BasisObjectSyncDriver.OnDestroy();
        Application.onBeforeRender -= OnBeforeRender;
        RemoteBoneJobSystem.Dispose();
        BasisAvatarBufferPool.Deinitialize();
    }

    /// <summary>
    /// Unity disable hook. Unsubscribes from the before-render callback on clients.
    /// </summary>
    public void OnDisable()
    {
        if (!IsServer)
            Application.onBeforeRender -= OnBeforeRender;
    }

    // ── Update ──────────────────────────────────────────────────

    /// <summary>
    /// Unity update loop. Drains main-thread actions, advances network simulation (compute),
    /// schedules remote interpolation, updates input on clients, and runs periodic tasks.
    /// </summary>
    public void Update()
    {
        DeltaTime = Time.deltaTime;
        unscaledDeltaTime = Time.unscaledDeltaTime;
        realtimeSinceStartupAsDouble = Time.realtimeSinceStartupAsDouble;
        TimeAsDouble = Time.timeAsDouble;

        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.LocalVisemeDriver.Simulate(DeltaTime);
        }
        // Drain everything that arrived from worker threads
        while (BasisDeviceManagement.mainThreadActions.TryDequeue(out System.Action action))
        {
            try { action.Invoke(); }
            catch (Exception ex) { Debug.LogError($"MainThread action failed: {ex}"); }
        }
        BasisNetworkManagement.SimulateNetworkCompute(unscaledDeltaTime);
        BasisObjectSyncDriver.ScheduleRemoteLerp(DeltaTime);
        if (!IsServer)
            InputSystem.Update();
        timeSinceLastUpdate += DeltaTime;
    }

    /// <summary>
    /// Fixed-step simulation used for scene-level processing.
    /// </summary>
    public void FixedUpdate()
    {
        fixedDeltaTime = Time.fixedDeltaTime;
        fixedTimeAsDouble = Time.fixedTimeAsDouble;
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisSceneFactory.Simulate(fixedDeltaTime);
        }
    }

    // ── LateUpdate ──────────────────────────────────────────────

    /// <summary>
    /// LateUpdate step for device management loop, eye simulation, local player late sim,
    /// microphone updates (client), network apply, and JigglePhysics scheduling/pose/render.
    /// </summary>
    public void LateUpdate()
    {
        ProfileLateUpdateInit();

        if (StateOfOnRenderBefore)
        {
            OnBeforeRender();
        }

        // ── Network apply group (sub-timed) ──
        ProfileBegin(PROF_NETWORK_APPLY);
        ProfileBegin2();
        BasisObjectSyncDriver.TransmitOwnedPickups(TimeAsDouble);
        ProfileEnd2(PROF_NET_TRANSMIT_PICKUPS);
        ProfileBegin2();
        BasisLocalPlayer.FireJustBeforeNetworkApply();
        ProfileEnd2(PROF_NET_FIRE_BEFORE_APPLY);
        ProfileBegin2();
        BasisNetworkManagement.SimulateNetworkApply();
        ProfileEnd2(PROF_NET_SIMULATE_APPLY);
        ProfileBegin2();
        BasisObjectSyncDriver.CompleteScheduledRemoteLerp();
        ProfileEnd2(PROF_NET_COMPLETE_REMOTE_LERP);
        ProfileEnd(PROF_NETWORK_APPLY);

        // ── Device management ──
        ProfileBegin(PROF_DEVICE_MANAGEMENT);
        BasisDeviceManagement.OnDeviceManagementLoop?.Invoke();
        if (BasisDeviceManagement.HasEvents)
        {
            BasisDeviceManagement.Instance.Simulate();
        }
        ProfileEnd(PROF_DEVICE_MANAGEMENT);

        // ── Remote audio simulate ──
        ProfileBegin(PROF_REMOTE_AUDIO_SIMULATE);
        BasisRemoteAudioDriver.Simulate(DeltaTime);
        ProfileEnd(PROF_REMOTE_AUDIO_SIMULATE);

        // ── Nameplate schedule ──
        ProfileBegin(PROF_NAMEPLATE_SCHEDULE);
        BasisRemoteNamePlateDriver.ScheduleSimulate(TimeAsDouble);
        ProfileEnd(PROF_NAMEPLATE_SCHEDULE);

        // ── BTween ──
        ProfileBegin(PROF_BTWEEN);
        BTweenManager.Simulate(realtimeSinceStartupAsDouble);
        ProfileEnd(PROF_BTWEEN);

        // ── Local player ──
        ProfileBegin(PROF_LOCAL_PLAYER);
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.FacialBlinkDriver.Simulate(TimeAsDouble);
            BasisLocalPlayer.Instance.LocalVisemeDriver.Apply();
        }
#if STEAMAUDIO_ENABLED
        SteamAudioManager.Schedule();
#endif
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.Simulate(DeltaTime);
            BasisLocalCameraDriver.Instance.Simulate();
            BasisLocalPlayer.Instance.LocalHandDriver.Apply();
            BasisLocalPlayer.Instance.LocalEyeDriver.Simulate(DeltaTime);
            BasisLocalPlayer.Instance.LocalEyeDriver.Apply();
        }
        ProfileEnd(PROF_LOCAL_PLAYER);

        // ── Remote face simulate (job schedule) ──
        ProfileBegin(PROF_REMOTE_FACE_SIMULATE);
        BasisRemoteFaceManagement.Simulate(TimeAsDouble, DeltaTime);
        ProfileEnd(PROF_REMOTE_FACE_SIMULATE);

        // ── Remote audio apply ──
        ProfileBegin(PROF_REMOTE_AUDIO_APPLY);
        BasisRemoteAudioDriver.Apply();
#if STEAMAUDIO_ENABLED
        SteamAudioManager.Apply();
#endif
        ProfileEnd(PROF_REMOTE_AUDIO_APPLY);

        // ── BlendShape simulate ──
        ProfileBegin(PROF_BLENDSHAPE_SIMULATE);
        BasisBlendShapeDriver.Simulate();
        ProfileEnd(PROF_BLENDSHAPE_SIMULATE);

        // ── BlendShape apply ──
        ProfileBegin(PROF_BLENDSHAPE_APPLY);
        BasisBlendShapeDriver.Apply();
        BasisAvatarDriver.ScheduleReadBlendShapes();
        ProfileEnd(PROF_BLENDSHAPE_APPLY);

        // ── JigglePhysics schedule ──
        ProfileBegin(PROF_JIGGLE_SCHEDULE);
        JigglePhysics.ScheduleSimulate(fixedTimeAsDouble, TimeAsDouble, fixedDeltaTime);
        ProfileEnd(PROF_JIGGLE_SCHEDULE);

        // ── Network transmit (reads bone results via GetOutGoingMouth) ──
        ProfileBegin(PROF_NETWORK_TRANSMIT);
        BasisNetworkTransmitter.AfterAvatarChanges?.Invoke();
        ProfileEnd(PROF_NETWORK_TRANSMIT);

        // ── JigglePhysics pose ──
        ProfileBegin(PROF_JIGGLE_POSE);
        JigglePhysics.SchedulePose(TimeAsDouble);
        ProfileEnd(PROF_JIGGLE_POSE);

        // ── Microphone ──
        ProfileBegin(PROF_MICROPHONE);
#if !UNITY_SERVER && !BASIS_DISABLE_MICROPHONE
        BasisLocalMicrophoneDriver.MicrophoneUpdate();
#endif
        ProfileEnd(PROF_MICROPHONE);

        // ── Nameplate complete ──
        ProfileBegin(PROF_NAMEPLATE_COMPLETE);
        BasisRemoteNamePlateDriver.CompleteNamePlates();
        ProfileEnd(PROF_NAMEPLATE_COMPLETE);

        BasisJoinLeaveNotification.Simulate(TimeAsDouble);
        IndividualPlayerProvider.SimulateBeacon(DeltaTime);

        if (SMModuleDebugOptions.UseGizmos)
        {
            JigglePhysics.ScheduleRender();
        }
        if (SMModuleDebugOptions.UseGizmos)
        {
            JigglePhysics.CompleteRender(proceduralMaterial, sphereMesh);
        }

        // ── JigglePhysics complete pose ──
        ProfileBegin(PROF_JIGGLE_COMPLETE_POSE);
        JigglePhysics.CompletePose();
        ProfileEnd(PROF_JIGGLE_COMPLETE_POSE);

        // ── Shadow clone blendshapes ──
        ProfileBegin(PROF_SHADOW_CLONE);
        BasisAvatarDriver.ApplyShadowCloneBlendShapes();
        ProfileEnd(PROF_SHADOW_CLONE);

        StateOfOnRenderBefore = true;
        if (IsServer)
        {
            OnBeforeRender();
        }

        ProfileLateUpdateFinish();
    }

    // ── OnBeforeRender ──────────────────────────────────────────

    /// <summary>
    /// Callback invoked before rendering each frame (client), used to run final local player
    /// render-time simulation and to publish avatar changes.
    /// </summary>
    private void OnBeforeRender()
    {
        ProfileBeforeRenderInit();

        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.SimulateOnRender();
            BasisRemoteFaceManagement.Apply();
#if !BASIS_DISABLE_MICROPHONE
            BasisLocalCameraDriver.Instance.microphoneIconDriver.Simulate(DeltaTime);
#endif
        }
        StateOfOnRenderBefore = false;

        ProfileBeforeRenderFinish();
    }

    /// <summary>
    /// Application quit hook. Disposes physics and stops microphone processing.
    /// </summary>
    public async void OnApplicationQuit()
    {
        JigglePhysics.Dispose();
#if !BASIS_DISABLE_MICROPHONE
        BasisLocalMicrophoneDriver.StopProcessingThread();
#endif
        BasisRemoteNamePlateDriver.Dispose();
        await BasisPlayerSettingsManager.FlushAllNow();
    }

    /// <summary>
    /// Renders Gizmos for debugging JigglePhysics when enabled.
    /// </summary>
    public void OnDrawGizmos()
    {
        if (!IsServer && BasisLocalPlayer.PlayerReady)
        {
            BasisHintOffsetGizmos.DrawAll();
        }
    }

    public void OnDrawGizmosSelected()
    {
        if (IsServer) return;
        JigglePhysics.OnDrawGizmos();
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisPlayerInteract.DrawAll();
        }
    }

    // ── Editor-only profiling implementation ────────────────────
    // Partial methods with no implementation are stripped by the compiler,
    // so all Profile*() calls above become zero-cost no-ops in non-editor builds.
#if UNITY_EDITOR
    private bool _profiling;
    private System.Diagnostics.Stopwatch _lateUpdateSW;
    private System.Diagnostics.Stopwatch _beforeRenderSW;

    partial void ProfileLateUpdateInit()
    {
        _profiling = BasisEventDriverProfilerData.Enabled;
        if (_profiling)
            _lateUpdateSW = System.Diagnostics.Stopwatch.StartNew();
    }

    partial void ProfileBegin(int section)
    {
        if (!_profiling) return;
        switch (section)
        {
            case PROF_REMOTE_AUDIO_SIMULATE:
                BasisEventDriverProfilerData.RemoteAudioDriverCount = BasisRemoteAudioDriver.Drivers.Count;
                break;
            case PROF_NAMEPLATE_COMPLETE:
                BasisEventDriverProfilerData.NamePlateJobWasIncomplete = !BasisRemoteNamePlateDriver.handle.IsCompleted;
                break;
        }
        BasisEventDriverProfilerData.Begin();
    }

    partial void ProfileBegin2()
    {
        if (_profiling)
            BasisEventDriverProfilerData.Begin2();
    }

    partial void ProfileEnd(int section)
    {
        if (!_profiling) return;
        double ms = BasisEventDriverProfilerData.End();
        switch (section)
        {
            case PROF_NETWORK_APPLY:         BasisEventDriverProfilerData.NetworkApplyMs = ms; break;
            case PROF_DEVICE_MANAGEMENT:     BasisEventDriverProfilerData.DeviceManagementMs = ms; break;
            case PROF_REMOTE_AUDIO_SIMULATE: BasisEventDriverProfilerData.RemoteAudioSimulateMs = ms; break;
            case PROF_NAMEPLATE_SCHEDULE:    BasisEventDriverProfilerData.NamePlateScheduleMs = ms; break;
            case PROF_BTWEEN:                BasisEventDriverProfilerData.BTweenMs = ms; break;
            case PROF_LOCAL_PLAYER:          BasisEventDriverProfilerData.LocalPlayerMs = ms; break;
            case PROF_REMOTE_FACE_SIMULATE:
                BasisEventDriverProfilerData.RemoteFaceSimulateMs = ms;
                BasisEventDriverProfilerData.RemoteFace_Count = BasisRemoteFaceManagement.count;
                break;
            case PROF_REMOTE_AUDIO_APPLY:    BasisEventDriverProfilerData.RemoteAudioApplyMs = ms; break;
            case PROF_BLENDSHAPE_SIMULATE:   BasisEventDriverProfilerData.BlendShapeSimulateMs = ms; break;
            case PROF_BLENDSHAPE_APPLY:      BasisEventDriverProfilerData.BlendShapeApplyMs = ms; break;
            case PROF_JIGGLE_SCHEDULE:       BasisEventDriverProfilerData.JiggleScheduleMs = ms; break;
            case PROF_NETWORK_TRANSMIT:      BasisEventDriverProfilerData.NetworkTransmitMs = ms; break;
            case PROF_JIGGLE_POSE:           BasisEventDriverProfilerData.JigglePoseMs = ms; break;
            case PROF_MICROPHONE:            BasisEventDriverProfilerData.MicrophoneMs = ms; break;
            case PROF_NAMEPLATE_COMPLETE:    BasisEventDriverProfilerData.NamePlateCompleteMs = ms; break;
            case PROF_JIGGLE_COMPLETE_POSE:  BasisEventDriverProfilerData.JiggleCompletePoseMs = ms; break;
            case PROF_SHADOW_CLONE:          BasisEventDriverProfilerData.ShadowCloneMs = ms; break;
        }
    }

    partial void ProfileEnd2(int section)
    {
        if (!_profiling) return;
        double ms = BasisEventDriverProfilerData.End2();
        switch (section)
        {
            case PROF_NET_TRANSMIT_PICKUPS:     BasisEventDriverProfilerData.Net_TransmitPickupsMs = ms; break;
            case PROF_NET_FIRE_BEFORE_APPLY:    BasisEventDriverProfilerData.Net_FireBeforeApplyMs = ms; break;
            case PROF_NET_SIMULATE_APPLY:       BasisEventDriverProfilerData.Net_SimulateNetworkApplyMs = ms; break;
            case PROF_NET_COMPLETE_REMOTE_LERP: BasisEventDriverProfilerData.Net_CompleteRemoteLerpMs = ms; break;
        }
    }

    partial void ProfileLateUpdateFinish()
    {
        if (!_profiling) return;
        _lateUpdateSW.Stop();
        BasisEventDriverProfilerData.LateUpdateTotalMs = _lateUpdateSW.Elapsed.TotalMilliseconds;
        BasisEventDriverProfilerData.PushHistory();
    }

    partial void ProfileBeforeRenderInit()
    {
        _profiling = BasisEventDriverProfilerData.Enabled;
        if (_profiling)
        {
            BasisEventDriverProfilerData.RemoteFaceJobWasIncomplete =
                BasisRemoteFaceManagement.HasJob && !BasisRemoteFaceManagement.handle.IsCompleted;
            _beforeRenderSW = System.Diagnostics.Stopwatch.StartNew();
        }
    }

    partial void ProfileBeforeRenderFinish()
    {
        if (!_profiling || _beforeRenderSW == null) return;
        _beforeRenderSW.Stop();
        BasisEventDriverProfilerData.OnBeforeRenderMs = _beforeRenderSW.Elapsed.TotalMilliseconds;
    }
#endif
}
