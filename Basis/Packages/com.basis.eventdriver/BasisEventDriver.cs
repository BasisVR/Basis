using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Eye_Follow;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Transmitters;
using GatorDragonGames.JigglePhysics;
using UnityEngine;
using UnityEngine.InputSystem;
public class BasisEventDriver : MonoBehaviour
{
    public float updateInterval = 0.1f; // 100 milliseconds
    public float timeSinceLastUpdate = 0f;
    public float DeltaTime;
    public double TimeAsDouble;
    public void OnEnable()
    {
#if UNITY_SERVER
#else
        Application.onBeforeRender += OnBeforeRender;
#endif
        BasisSceneFactory.Initalize();
        BasisObjectSyncDriver.Initalization();
    }
    public void OnDestroy()
    {
        BasisObjectSyncDriver.OnDestroy();
        Application.onBeforeRender -= OnBeforeRender;
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
        BasisNetworkManagement.SimulateNetworkCompute();
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
        if (!BasisDeviceManagement.hasPendingActions) return;

        while (BasisDeviceManagement.mainThreadActions.TryDequeue(out System.Action action))
        {
            action.Invoke();
        }

        // Reset flag once all actions are executed
        BasisDeviceManagement.hasPendingActions = !BasisDeviceManagement.mainThreadActions.IsEmpty;
    }
    public void FixedUpdate()
    {
        BasisSceneFactory.Simulate();
        JigglePhysics.ScheduleSimulate(Time.timeAsDouble, Time.fixedTimeAsDouble, Time.fixedDeltaTime);
    }
    public void LateUpdate()
    {
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
        BasisObjectSyncDriver.CompleteScheduledRemoteLerp();
#if UNITY_SERVER
        OnBeforeRender();
#endif
        if(BasisLocalAvatarDriver.IsNormalHead == false)
        {
            BasisLocalAvatarDriver.ScaleHeadToNormal();
            JigglePhysics.SchedulePose(TimeAsDouble);
            JigglePhysics.CompletePose();
            BasisLocalAvatarDriver.ScaleheadToZero();
        }
        else
        {
            //if the local head is good already just continue on.
            JigglePhysics.SchedulePose(TimeAsDouble);
            JigglePhysics.CompletePose();
        }
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
