using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Eye_Follow;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Transmitters;
using UnityEngine;
using UnityEngine.InputSystem;
public class BasisEventDriver : MonoBehaviour
{
    public float updateInterval = 0.1f; // 100 milliseconds
    public float timeSinceLastUpdate = 0f;
    public void OnEnable()
    {
        BasisSceneFactory.Initalize();
        BasisObjectSyncDriver.Initialize();
        Application.onBeforeRender += OnBeforeRender;
    }
    public void OnDisable()
    {
        BasisObjectSyncDriver.Deinitialize();
        Application.onBeforeRender -= OnBeforeRender;
    }
    public void Update()
    {
        BasisNetworkManagement.SimulateNetworkCompute();
        InputSystem.Update();
        float DeltaTime = Time.deltaTime;
        timeSinceLastUpdate += DeltaTime;

        if (timeSinceLastUpdate >= updateInterval) // Use '>=' to avoid small errors
        {
            timeSinceLastUpdate -= updateInterval; // Subtract interval instead of resetting to zero
            BasisConsoleLogger.QueryLogDisplay();
        }
        BasisObjectSyncDriver.Update(DeltaTime);


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
        BasisLocalMicrophoneDriver.MicrophoneUpdate();
        BasisNetworkManagement.SimulateNetworkApply();
        BasisObjectSyncDriver.LateUpdate();
    }
    private void OnBeforeRender()
    {
        if (BasisLocalPlayer.PlayerReady)
        {
            BasisLocalPlayer.Instance.SimulateOnRender();
            //send out avatar
            BasisNetworkTransmitter.AfterAvatarChanges?.Invoke();
        }
    }
    public void OnApplicationQuit()
    {
        BasisLocalMicrophoneDriver.StopProcessingThread();
    }
}
