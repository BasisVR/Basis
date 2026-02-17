using Basis.BTween;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Debugging;
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
        BasisSceneFactory.Simulate(fixedDeltaTime);
    }

    /// <summary>
    /// LateUpdate step for device management loop, eye simulation, local player late sim,
    /// microphone updates (client), network apply, and JigglePhysics scheduling/pose/render.
    /// </summary>
    public void LateUpdate()
    {
        // Network apply step + gameplay sync
        BasisObjectSyncDriver.TransmitOwnedPickups(TimeAsDouble);//apply latest pickup data
        BasisLocalPlayer.FireJustBeforeNetworkApply(); //hook for network events good for vehicles 
        BasisNetworkManagement.SimulateNetworkApply(); // begin computing player data.
        BasisObjectSyncDriver.CompleteScheduledRemoteLerp(); // apply movement of all pickups
        // Device management tick
        BasisDeviceManagement.OnDeviceManagementLoop?.Invoke(); // useful for manual tracker polling
        BasisRemoteAudioDriver.Simulate(DeltaTime); // computes audio data. (viseme data)
        BasisRemoteNamePlateDriver.ScheduleSimulate(TimeAsDouble);//simulate colors onto nameplates
        BTweenManager.Simulate(realtimeSinceStartupAsDouble); // update menu data.
        // Local player late simulation
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.FacialBlinkDriver.Simulate(TimeAsDouble); //local blink driver updates
            BasisLocalPlayer.Instance.LocalVisemeDriver.Apply(); //local viseme driver
        }
        if (BasisDeviceManagement.HasEvents)
        {
            BasisDeviceManagement.Instance.Simulate(); // poll things like steam audio
        }
        SteamAudioManager.Schedule();//schedule steam audio
        BasisRemoteFaceManagement.Simulate(TimeAsDouble, DeltaTime); // eye blinking

        if (BasisLocalPlayer.PlayerReady)
        {
            // Eye driver (local)
            BasisLocalPlayer.Instance.LocalEyeDriver.Simulate(DeltaTime);//simulate local eye driver
        }
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.LocalEyeDriver.Apply();//local eye driver 

        }
        BasisRemoteAudioDriver.Apply(); //apply visemes
        SteamAudioManager.Apply(); //apply steam audio transforms

        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.Simulate(DeltaTime);//update local player
            BasisLocalCameraDriver.Instance.Simulate();
        }
        // JigglePhysics: schedule/complete passes
        JigglePhysics.ScheduleSimulate(fixedTimeAsDouble, TimeAsDouble, fixedDeltaTime); //schedule jiggles
        // send out avatar
        BasisNetworkTransmitter.AfterAvatarChanges?.Invoke(); //send out local, player network data
        JigglePhysics.SchedulePose(TimeAsDouble);//requires free access to all transform of a player.
#if UNITY_SERVER
#else
        BasisLocalMicrophoneDriver.MicrophoneUpdate(); //microphone Update
#endif

        BasisRemoteNamePlateDriver.CompleteNamePlates();//just colors
        if (SMModuleDebugOptions.UseGizmos)
        {
            JigglePhysics.ScheduleRender();
        }

        if (SMModuleDebugOptions.UseGizmos)
        {
            JigglePhysics.CompleteRender(proceduralMaterial, sphereMesh);//complete rendering of jiggles

        }
        //doing main thread work before this call is ideal for best performance.
        JigglePhysics.CompletePose();
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
            BasisLocalPlayer.Instance.SimulateOnRender();
            BasisRemoteFaceManagement.Apply(); //apply blendshapes
            BasisLocalCameraDriver.Instance.microphoneIconDriver.Simulate(DeltaTime); //update microphone icon
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
