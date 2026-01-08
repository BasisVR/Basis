using Basis.BTween;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Transmitters;
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
    /// Interval (seconds) for periodic tasks such as log display refreshes.
    /// </summary>
    public float updateInterval = 0.1f; // 100 milliseconds

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
        BasisSceneFactory.Initalize();
        BasisObjectSyncDriver.Initalization();
        RemoteBoneJobSystem.Initialize();
    }

    /// <summary>
    /// Unity destroy hook. Cleans up network/physics resources and unsubscribes callbacks.
    /// </summary>
    public void OnDestroy()
    {
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
        TimeAsDouble = Time.timeAsDouble;
        unscaledDeltaTime = Time.unscaledDeltaTime;

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

        // Periodic updates at a stable cadence
        if (timeSinceLastUpdate >= updateInterval) // Use '>=' to avoid small errors
        {
            timeSinceLastUpdate -= updateInterval; // Subtract interval instead of resetting to zero
            BasisConsoleLogger.QueryLogDisplay();
        }
    }

    /// <summary>
    /// Fixed-step simulation used for scene-level processing.
    /// </summary>
    public void FixedUpdate()
    {
        TimeAsDouble = Time.timeAsDouble;
        BasisSceneFactory.Simulate();
    }

    /// <summary>
    /// LateUpdate step for device management loop, eye simulation, local player late sim,
    /// microphone updates (client), network apply, and JigglePhysics scheduling/pose/render.
    /// </summary>
    public void LateUpdate()
    {
        fixedTimeAsDouble = Time.fixedTimeAsDouble;
        fixedDeltaTime = Time.fixedDeltaTime;
        realtimeSinceStartupAsDouble = Time.realtimeSinceStartupAsDouble;

        // Network apply step + gameplay sync
        BasisObjectSyncDriver.TransmitOwnedPickups(TimeAsDouble);
        BasisNetworkManagement.SimulateNetworkApply();

        // JigglePhysics: schedule/complete passes
        JigglePhysics.ScheduleSimulate(fixedTimeAsDouble, TimeAsDouble, fixedDeltaTime);
        BasisObjectSyncDriver.CompleteScheduledRemoteLerp();

        JigglePhysics.SchedulePose(TimeAsDouble);//requires free access to all transform of a player.

        // Device management tick
        BasisDeviceManagement.OnDeviceManagementLoop?.Invoke();
        BasisRemoteAudioDriver.Simulate(DeltaTime);
#if UNITY_SERVER
#else
        BasisLocalMicrophoneDriver.MicrophoneUpdate();
#endif
        BasisRemoteNamePlateDriver.ScheduleSimulate(TimeAsDouble);//simulate colors onto nameplates
        BTweenManager.Simulate(realtimeSinceStartupAsDouble);
        // Local player late simulation
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.FacialBlinkDriver.Simulate(TimeAsDouble);
        }
        if (SMModuleDebugOptions.UseGizmos)
        {
            JigglePhysics.ScheduleRender();
        }
        BasisRemoteAudioDriver.Apply();
        BasisRemoteNamePlateDriver.CompleteNamePlates();
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.LocalVisemeDriver.Apply();

        }
        //doing main thread work before this call is ideal for best performance.
        JigglePhysics.CompletePose();
        SteamAudioManager.Schedule();
        BasisRemoteFaceManagement.Simulate(TimeAsDouble, DeltaTime);

        if (BasisLocalPlayer.PlayerReady)
        {
            // Eye driver (local)
            BasisLocalPlayer.Instance.LocalEyeDriver.Simulate(DeltaTime);
        }

        if (SMModuleDebugOptions.UseGizmos)
        {
            JigglePhysics.CompleteRender(proceduralMaterial, sphereMesh);
        }
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.LocalEyeDriver.Apply();

        }
        SteamAudioManager.Apply();
#if UNITY_SERVER
        OnBeforeRender();
#endif
    }

    /// <summary>
    /// Callback invoked before rendering each frame (client), used to run final local player
    /// render-time simulation and to publish avatar changes.
    /// </summary>
    private void OnBeforeRender()
    {
        if (BasisLocalPlayer.PlayerReady)
        {

            BasisLocalPlayer.Instance.SimulateOnRender(DeltaTime);
            // send out avatar
            BasisNetworkTransmitter.AfterAvatarChanges?.Invoke();
            BasisRemoteFaceManagement.Apply();
            BasisLocalCameraDriver.Instance.microphoneIconDriver.Simulate(DeltaTime);
        }
    }

    /// <summary>
    /// Application quit hook. Disposes physics and stops microphone processing.
    /// </summary>
    public async void OnApplicationQuit()
    {
        JigglePhysics.Dispose();
        BasisLocalMicrophoneDriver.StopProcessingThread();
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
        JigglePhysics.OnDrawGizmos();
        //    BasisLocalPlayer.Instance.BasisLocalFootDriver.DrawGizmos();
#endif
    }
}
