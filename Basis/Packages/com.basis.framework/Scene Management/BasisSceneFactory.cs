using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering.Universal;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static class BasisSceneFactory
{
    public const string BasisLoadingSceneKey = "BasisLoadingScene";

    public static BasisScene BasisScene;
    private static float timeSinceLastCheck = 0f;
    public static float RespawnCheckTimer = 5f;
    public static float RespawnHeight = -100f;
    public static BasisLocalPlayer BasisLocalPlayer;
    private static bool _isLoadingLoadingScene = false;
    private static AsyncOperationHandle<SceneInstance>? _loadingSceneHandle = null;

    public static void Initalize()
    {
        BasisScene.Ready += Initalize;
        BasisScene.Destroyed += BasisSceneDestroyed;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }
    private static void OnSceneUnloaded(Scene unloadedScene)
    {
        // Check if any BasisScene still exists after the scene was unloaded
        BasisScene[] scenes = Object.FindObjectsByType<BasisScene>(FindObjectsInactive.Exclude);
        if (scenes.Length == 0)
        {
            LoadLoadingScene();
        }
    }
    public static void BasisSceneDestroyed(BasisScene UnloadingScene)
    {
        if(UnloadingScene != BasisScene)
        {
            return;
        }

        BasisScene[] scenes = Object.FindObjectsByType<BasisScene>(FindObjectsInactive.Exclude);
        foreach(BasisScene potentialMainScene in scenes)
        {
            if (potentialMainScene == UnloadingScene)
            {
                continue;
            }

            Initalize(potentialMainScene);
            return;
        }
    }
    private static async void LoadLoadingScene()
    {
        if (_isLoadingLoadingScene)
        {
            BasisDebug.Log("Loading scene load already in progress, skipping.", BasisDebug.LogTag.Scene);
            return;
        }
        _isLoadingLoadingScene = true;
        try
        {
            BasisDebug.Log("No BasisScene found after scene unload. Loading BasisLoadingScene.", BasisDebug.LogTag.Scene);
            BasisLocalPlayer.SpawnPlayerOnSceneLoad = true;
            AsyncOperationHandle<SceneInstance> handle = Addressables.LoadSceneAsync(BasisLoadingSceneKey, LoadSceneMode.Additive, activateOnLoad: true);
            while (!handle.IsDone)
            {
                await Task.Yield();
            }
            await handle.Task;
            _loadingSceneHandle = handle;
            BasisDebug.Log("BasisLoadingScene loaded successfully.", BasisDebug.LogTag.Scene);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error loading BasisLoadingScene: {ex}", BasisDebug.LogTag.Scene);
        }
        finally
        {
            _isLoadingLoadingScene = false;
        }
    }
    private static async void UnloadLoadingScene()
    {
        if (_loadingSceneHandle == null)
        {
            return;
        }
        try
        {
            BasisDebug.Log("Unloading BasisLoadingScene.", BasisDebug.LogTag.Scene);
            AsyncOperationHandle<SceneInstance> handle = _loadingSceneHandle.Value;
            _loadingSceneHandle = null;
            await Addressables.UnloadSceneAsync(handle).Task;
            BasisDebug.Log("BasisLoadingScene unloaded successfully.", BasisDebug.LogTag.Scene);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error unloading BasisLoadingScene: {ex}", BasisDebug.LogTag.Scene);
        }
    }
    public static void Initalize(BasisScene scene)
    {
        // If a loading scene is active and a different BasisScene is now ready, unload the loading scene
        if (_loadingSceneHandle != null && _loadingSceneHandle.Value.IsValid())
        {
            Scene loadingScene = _loadingSceneHandle.Value.Result.Scene;
            if (scene.gameObject.scene != loadingScene)
            {
                UnloadLoadingScene();
            }
        }
        BasisScene = scene;
        AttachMixerToAllSceneAudioSources();
        RespawnCheckTimer = BasisScene.RespawnCheckTimer;
        RespawnHeight = BasisScene.RespawnHeight;
        if (scene.MainCamera != null)
        {
            LoadCameraProperties(scene.MainCamera);
            GameObject.DestroyImmediate(scene.MainCamera.gameObject);
            BasisDebug.Log("Destroying Main Camera Attached To Scene");
        }
        else
        {
            BasisDebug.Log("No attached camera to scene script Found");
        }
        List<GameObject> MainCameras = new List<GameObject>();
        GameObject.FindGameObjectsWithTag("MainCamera", MainCameras);
        int Count = MainCameras.Count;
        for (int Index = 0; Index < Count; Index++)
        {
            GameObject PC = MainCameras[Index];
            if (PC.TryGetComponent(out Camera camera))
            {
                if (camera != BasisLocalCameraDriver.Instance.Camera)
                {
                //    LoadCameraPropertys(camera);
                    GameObject.DestroyImmediate(camera.gameObject);
                }
                else
                {
                  //  BasisDebug.Log("No New main Camera Found");
                }
            }
        }
        if (BasisLocalPlayer.Instance != null)
        {
            BasisLocalPlayer = BasisLocalPlayer.Instance;
        }
        else
        {
            BasisLocalPlayer = GameObject.FindFirstObjectByType<BasisLocalPlayer>(FindObjectsInactive.Exclude);
        }
    }
    public static void LoadCameraProperties(Camera Camera)
    {
        BNL.Log("Loading Camera Propertys From Camera "+ Camera.gameObject.name);
        // Configure the local player's camera mostly based on the scene's placeholder camera.
        Camera RealCamera = BasisLocalCameraDriver.Instance.Camera;
        RealCamera.useOcclusionCulling = Camera.useOcclusionCulling;
        RealCamera.backgroundColor = Camera.backgroundColor;
        RealCamera.clearFlags = Camera.clearFlags;
        RealCamera.barrelClipping = Camera.barrelClipping;
        RealCamera.usePhysicalProperties = Camera.usePhysicalProperties;
        // Note that these are limited by the player's size in BasisLocalCameraDriver.UpdateCameraScale().
        BasisLocalCameraDriver.Instance.SetDesiredClipPlanes(Camera.farClipPlane, Camera.nearClipPlane);
        // Set more camera data from the UniversalAdditionalCameraData component if it exists.
        if (Camera.TryGetComponent(out UniversalAdditionalCameraData AdditionalCameraData))
        {
            UniversalAdditionalCameraData Data = BasisLocalCameraDriver.Instance.CameraData;

            Data.stopNaN = AdditionalCameraData.stopNaN;
            Data.dithering = AdditionalCameraData.dithering;

            Data.volumeTrigger = AdditionalCameraData.volumeTrigger;
        }
    }
    public static void AttachMixerToAllSceneAudioSources()
    {
        // Check if mixerGroup is assigned
        BasisScene.Group = SMModuleAudio.Instance.WorldDefaultMixer;

        // Get all active and inactive AudioSources in the scene
        AudioSource[] sources = GameObject.FindObjectsByType<AudioSource>(FindObjectsInactive.Include);
        int AudioSourceCount = sources.Length;
        // Loop through each AudioSource and assign the mixer group if not already assigned
        for (int Index = 0; Index < AudioSourceCount; Index++)
        {
            AudioSource source = sources[Index];
            if (source != null && source.outputAudioMixerGroup == null)
            {
                source.outputAudioMixerGroup = BasisScene.Group;
            }
        }

        BasisDebug.Log("Mixer group assigned to all scene AudioSources.");
    }
    /// <summary>
    /// Fired after the player has been spawned into the scene.
    /// </summary>
    public static Action OnSpawnedEvent;
    public static void SpawnPlayer(BasisLocalPlayer localPlayer)
    {
        BasisDebug.Log("Spawning Player");
        if (RequestSpawnPoint(out Vector3 position, out Quaternion rotation))
        {
            if (localPlayer != null)
            {
                localPlayer.Teleport(position, rotation);
            }
            else
            {
                BasisDebug.LogError("Missing Local Player!");
            }
            OnSpawnedEvent?.Invoke();
        }
        else
        {
            OnSpawnedEvent?.Invoke();
        }
    }
    public static void Simulate(float FixedDeltaTime)
    {
        timeSinceLastCheck += FixedDeltaTime;
        // Check only if enough time has passed
        if (timeSinceLastCheck > RespawnCheckTimer)
        {
            timeSinceLastCheck = 0f; // Reset timer
            if (BasisLocalPlayer.PlayerSelf.position.y < RespawnHeight)
            {
                SpawnPlayer(BasisLocalPlayer);
            }
        }
    }
    public static bool RequestSpawnPoint(out Vector3 Position, out Quaternion Rotation)
    {
        if (BasisScene != null)
        {
            if (BasisScene.SpawnPoint == null)
            {
                Position = Vector3.zero;
                Rotation = Quaternion.identity;
            }
            else
            {
                BasisScene.SpawnPoint.GetPositionAndRotation(out Position, out Rotation);
            }
            return true;
        }
        else
        {
            BasisDebug.LogError("Missing BasisScene!");
            Position = Vector3.zero;
            Rotation = Quaternion.identity;
            return false;
        }
    }
}
