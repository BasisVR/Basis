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
public class BasisEventDriver : MonoBehaviour
{
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
    /// <summary>
    /// Unity enable hook. Subscribes render callbacks (client), initializes scene and network drivers.
    /// </summary>
    public void OnEnable()
    {
        Instance = this;
#if UNITY_SERVER
#else
        Application.onBeforeRender += OnBeforeRender;
#endif
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
#if UNITY_SERVER
#else
        Application.onBeforeRender -= OnBeforeRender;
#endif
    }

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
#if UNITY_SERVER
#else
        InputSystem.Update();
#endif
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

    /// <summary>
    /// LateUpdate step for device management loop, eye simulation, local player late sim,
    /// microphone updates (client), network apply, and JigglePhysics scheduling/pose/render.
    /// </summary>
    public void LateUpdate()
    {
#if UNITY_EDITOR
        bool profiling = BasisEventDriverProfilerData.Enabled;
        System.Diagnostics.Stopwatch totalSW = null;
        if (profiling) totalSW = System.Diagnostics.Stopwatch.StartNew();
#endif

        if (StateOfOnRenderBefore)
        {
            OnBeforeRender();
        }

        // ── Network apply group (sub-timed) ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
        if (profiling) BasisEventDriverProfilerData.Begin2();
#endif
        BasisObjectSyncDriver.TransmitOwnedPickups(TimeAsDouble);
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Net_TransmitPickupsMs = BasisEventDriverProfilerData.End2();
        if (profiling) BasisEventDriverProfilerData.Begin2();
#endif
        BasisLocalPlayer.FireJustBeforeNetworkApply();
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Net_FireBeforeApplyMs = BasisEventDriverProfilerData.End2();
        if (profiling) BasisEventDriverProfilerData.Begin2();
#endif
        BasisNetworkManagement.SimulateNetworkApply();
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Net_SimulateNetworkApplyMs = BasisEventDriverProfilerData.End2();
        if (profiling) BasisEventDriverProfilerData.Begin2();
#endif
        BasisObjectSyncDriver.CompleteScheduledRemoteLerp();
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Net_CompleteRemoteLerpMs = BasisEventDriverProfilerData.End2();
        if (profiling) BasisEventDriverProfilerData.NetworkApplyMs = BasisEventDriverProfilerData.End();
#endif

        // ── Device management ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        BasisDeviceManagement.OnDeviceManagementLoop?.Invoke();
        if (BasisDeviceManagement.HasEvents)
        {
            BasisDeviceManagement.Instance.Simulate();
        }
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.DeviceManagementMs = BasisEventDriverProfilerData.End();
#endif

        // ── Remote audio simulate ──
#if UNITY_EDITOR
        if (profiling)
        {
            BasisEventDriverProfilerData.RemoteAudioDriverCount = BasisRemoteAudioDriver.Drivers.Count;
            BasisEventDriverProfilerData.Begin();
        }
#endif
        BasisRemoteAudioDriver.Simulate(DeltaTime);
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.RemoteAudioSimulateMs = BasisEventDriverProfilerData.End();
#endif

        // ── Nameplate schedule ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        BasisRemoteNamePlateDriver.ScheduleSimulate(TimeAsDouble);
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.NamePlateScheduleMs = BasisEventDriverProfilerData.End();
#endif

        // ── BTween ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        BTweenManager.Simulate(realtimeSinceStartupAsDouble);
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.BTweenMs = BasisEventDriverProfilerData.End();
#endif

        // ── Local player ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
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
            BasisLocalPlayer.Instance.LocalEyeDriver.Simulate(DeltaTime);
            BasisLocalPlayer.Instance.LocalEyeDriver.Apply();
        }
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.Simulate(DeltaTime);
            BasisLocalCameraDriver.Instance.Simulate();
        }
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.LocalHandDriver.Apply();
        }
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.LocalPlayerMs = BasisEventDriverProfilerData.End();
#endif

        // ── Remote face simulate (job schedule) ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        BasisRemoteFaceManagement.Simulate(TimeAsDouble, DeltaTime);
#if UNITY_EDITOR
        if (profiling)
        {
            BasisEventDriverProfilerData.RemoteFaceSimulateMs = BasisEventDriverProfilerData.End();
            BasisEventDriverProfilerData.RemoteFace_Count = BasisRemoteFaceManagement.count;
        }
#endif

        // ── Remote audio apply ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        BasisRemoteAudioDriver.Apply();
#if STEAMAUDIO_ENABLED
        SteamAudioManager.Apply();
#endif
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.RemoteAudioApplyMs = BasisEventDriverProfilerData.End();
#endif

        // ── BlendShape simulate ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        BasisBlendShapeDriver.Simulate();
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.BlendShapeSimulateMs = BasisEventDriverProfilerData.End();
#endif

        // ── BlendShape apply ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        BasisBlendShapeDriver.Apply();
        BasisAvatarDriver.ScheduleReadBlendShapes();
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.BlendShapeApplyMs = BasisEventDriverProfilerData.End();
#endif

        // ── JigglePhysics schedule ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        JigglePhysics.ScheduleSimulate(fixedTimeAsDouble, TimeAsDouble, fixedDeltaTime);
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.JiggleScheduleMs = BasisEventDriverProfilerData.End();
#endif

        // ── Network transmit (reads bone results via GetOutGoingMouth) ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        BasisNetworkTransmitter.AfterAvatarChanges?.Invoke();
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.NetworkTransmitMs = BasisEventDriverProfilerData.End();
#endif

        // ── JigglePhysics pose ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        JigglePhysics.SchedulePose(TimeAsDouble);
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.JigglePoseMs = BasisEventDriverProfilerData.End();
#endif

        // ── Microphone ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
#if !UNITY_SERVER && !BASIS_DISABLE_MICROPHONE
        BasisLocalMicrophoneDriver.MicrophoneUpdate();
#endif
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.MicrophoneMs = BasisEventDriverProfilerData.End();
#endif

        // ── Nameplate complete ──
#if UNITY_EDITOR
        if (profiling)
        {
            BasisEventDriverProfilerData.NamePlateJobWasIncomplete = !BasisRemoteNamePlateDriver.handle.IsCompleted;
            BasisEventDriverProfilerData.Begin();
        }
#endif
        BasisRemoteNamePlateDriver.CompleteNamePlates();
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.NamePlateCompleteMs = BasisEventDriverProfilerData.End();
#endif

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
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        JigglePhysics.CompletePose();
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.JiggleCompletePoseMs = BasisEventDriverProfilerData.End();
#endif

        // ── Shadow clone blendshapes ──
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.Begin();
#endif
        BasisAvatarDriver.ApplyShadowCloneBlendShapes();
#if UNITY_EDITOR
        if (profiling) BasisEventDriverProfilerData.ShadowCloneMs = BasisEventDriverProfilerData.End();
#endif

        StateOfOnRenderBefore = true;
#if UNITY_SERVER
        OnBeforeRender();
#endif

#if UNITY_EDITOR
        if (profiling)
        {
            totalSW.Stop();
            BasisEventDriverProfilerData.LateUpdateTotalMs = totalSW.Elapsed.TotalMilliseconds;
            BasisEventDriverProfilerData.PushHistory();
        }
#endif
    }

    /// <summary>
    /// Callback invoked before rendering each frame (client), used to run final local player
    /// render-time simulation and to publish avatar changes.
    /// </summary>
    private void OnBeforeRender()
    {
#if UNITY_EDITOR
        bool profiling = BasisEventDriverProfilerData.Enabled;
        if (profiling)
        {
            BasisEventDriverProfilerData.RemoteFaceJobWasIncomplete = BasisRemoteFaceManagement.HasJob && !BasisRemoteFaceManagement.handle.IsCompleted;
        }
        System.Diagnostics.Stopwatch sw = null;
        if (profiling) sw = System.Diagnostics.Stopwatch.StartNew();
#endif

        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.SimulateOnRender();
            BasisRemoteFaceManagement.Apply();
#if !BASIS_DISABLE_MICROPHONE
            BasisLocalCameraDriver.Instance.microphoneIconDriver.Simulate(DeltaTime);
#endif
        }
        StateOfOnRenderBefore = false;

#if UNITY_EDITOR
        if (profiling && sw != null)
        {
            sw.Stop();
            BasisEventDriverProfilerData.OnBeforeRenderMs = sw.Elapsed.TotalMilliseconds;
        }
#endif
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
#if UNITY_SERVER
#else
        //    BasisLocalPlayer.Instance.BasisLocalFootDriver.DrawGizmos();
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisHintOffsetGizmos.DrawAll();
        }
#endif
    }
    public void OnDrawGizmosSelected()
    {
#if UNITY_SERVER
#else
        JigglePhysics.OnDrawGizmos();
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisPlayerInteract.DrawAll();
        }
#endif
    }
}
