using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Eye_Follow;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.UI.NamePlate;
using GatorDragonGames.JigglePhysics;
using System;
using UnityEngine;
using UnityEngine.InputSystem;
public class BasisEventDriver : MonoBehaviour
{
    public float updateInterval = 0.1f; // 100 milliseconds
    public float timeSinceLastUpdate = 0f;
    public float DeltaTime;
    public double TimeAsDouble;
    public double fixedTimeAsDouble;
    public float fixedDeltaTime;
    public float unscaledDeltaTime;
    [SerializeField]
    private Material proceduralMaterial;
    [SerializeField]
    private Mesh sphereMesh;
    public void OnEnable()
    {
#if UNITY_SERVER
#else
        Application.onBeforeRender += OnBeforeRender;
#endif
        BasisSceneFactory.Initalize();
        BasisObjectSyncDriver.Initalization();
        RemoteBoneJobSystem.Initialize();
    }
    public void OnDestroy()
    {
        BasisObjectSyncDriver.OnDestroy();
        Application.onBeforeRender -= OnBeforeRender;
        RemoteBoneJobSystem.Dispose();
        BasisAvatarBufferPool.DeInitalize();
    }
    public void OnDisable()
    {
#if UNITY_SERVER
#else
        Application.onBeforeRender -= OnBeforeRender;
#endif
    }
    public void Update()
    {
        DeltaTime = Time.deltaTime;
        TimeAsDouble = Time.timeAsDouble;
        unscaledDeltaTime = Time.unscaledDeltaTime;
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

        if (timeSinceLastUpdate >= updateInterval) // Use '>=' to avoid small errors
        {
            timeSinceLastUpdate -= updateInterval; // Subtract interval instead of resetting to zero
            BasisConsoleLogger.QueryLogDisplay();
        }
    }
    public void FixedUpdate()
    {
        TimeAsDouble = Time.timeAsDouble;
        BasisSceneFactory.Simulate();
    }
    public void LateUpdate()
    {
        fixedTimeAsDouble = Time.fixedTimeAsDouble;
        fixedDeltaTime = Time.fixedDeltaTime;
        BasisDeviceManagement.OnDeviceManagementLoop?.Invoke();
        if (BasisLocalEyeDriver.RequiresUpdate())
        {
            BasisLocalEyeDriver.Instance.Simulate();
        }
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.SimulateOnLateUpdate();
        }
#if UNITY_SERVER
#else
        BasisLocalMicrophoneDriver.MicrophoneUpdate();
#endif
        BasisObjectSyncDriver.TransmitOwnedPickups(TimeAsDouble);
        BasisNetworkManagement.SimulateNetworkApply();

        JigglePhysics.ScheduleSimulate(fixedTimeAsDouble, TimeAsDouble, Time.fixedDeltaTime);

        BasisObjectSyncDriver.CompleteScheduledRemoteLerp();
        JigglePhysics.SchedulePose(TimeAsDouble);
        if (SMModuleDebugOptions.UseGizmos)
        {
            JigglePhysics.ScheduleRender();
        }
        JigglePhysics.CompletePose();
        if (SMModuleDebugOptions.UseGizmos)
        {
            JigglePhysics.CompleteRender(proceduralMaterial, sphereMesh);
        }
#if UNITY_SERVER
        OnBeforeRender();
#endif
    }
    private void OnBeforeRender()
    {
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.SimulateOnRender(DeltaTime);
            //send out avatar
            BasisNetworkTransmitter.AfterAvatarChanges?.Invoke();
        }
    }
    public void OnApplicationQuit()
    {
        JigglePhysics.Dispose();
        BasisLocalMicrophoneDriver.StopProcessingThread();
    }
    public void OnDrawGizmos()
    {
        JigglePhysics.OnDrawGizmos();
    }
}
