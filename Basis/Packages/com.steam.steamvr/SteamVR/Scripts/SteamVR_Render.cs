//======= Copyright (c) Valve Corporation, All rights reserved. ===============
//
// Purpose: Handles rendering of all SteamVR_Cameras
//
//=============================================================================
using UnityEngine;
using System.Collections;
using UnityEngine.XR;
namespace Valve.VR
{
    public class SteamVR_Render : MonoBehaviour
    {
        public TrackedDevicePose_t[] poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        public TrackedDevicePose_t[] gamePoses = new TrackedDevicePose_t[0];
        private WaitForEndOfFrame waitForEndOfFrame = new WaitForEndOfFrame();
        private EVRScreenshotType[] screenshotTypes = new EVRScreenshotType[] { EVRScreenshotType.StereoPanorama };
        public VREvent_t vrEvent;
        public uint size;
        private const string openVRDeviceName = "OpenVR";
        [HideInInspector]
        public static SteamVR_Render steamvr_render;

        internal static bool isPlaying = false;
        private Coroutine initializeCoroutine;
        private bool loadedOpenVRDeviceSuccess = false;
        private IEnumerator RenderLoop()
        {
            while (Application.isPlaying)
            {
                yield return waitForEndOfFrame;

                var compositor = OpenVR.Compositor;
                if (compositor != null)
                {
                    if (!compositor.CanRenderScene())
                    {
                        continue;
                    }

                    compositor.SetTrackingSpace(SteamVR.settings.trackingSpace);
                }
            }
        }
        private void OnInputFocus(bool hasFocus)
        {
        }
        private void OnRequestScreenshot(VREvent_t vrEvent)
        {
        }
        private void OnEnable()
        {
            SteamVR_Events.System(EVREventType.VREvent_Quit).Listen(OnQuit);
            vrEvent = new VREvent_t();
            size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VREvent_t));
            StartCoroutine(RenderLoop());
            SteamVR_Events.InputFocus.Listen(OnInputFocus);
            SteamVR_Events.System(EVREventType.VREvent_RequestScreenshot).Listen(OnRequestScreenshot);
            if (SteamVR.initializedState == SteamVR.InitializedStates.InitializeSuccess)
            {
                OpenVR.Screenshots.HookScreenshot(screenshotTypes);
            }
            else
            {
                SteamVR_Events.Initialized.AddListener(OnSteamVRInitialized);
            }
        }

        private void OnSteamVRInitialized(bool success)
        {
            if (success)
            {
                OpenVR.Screenshots.HookScreenshot(screenshotTypes);
            }
        }

        private void OnDisable()
        {
            SteamVR_Events.System(EVREventType.VREvent_Quit).Remove(OnQuit);
            StopAllCoroutines();
            SteamVR_Events.InputFocus.Remove(OnInputFocus);
            SteamVR_Events.System(EVREventType.VREvent_RequestScreenshot).Remove(OnRequestScreenshot);
            if (SteamVR.initializedState != SteamVR.InitializedStates.InitializeSuccess)
            {
                SteamVR_Events.Initialized.RemoveListener(OnSteamVRInitialized);
            }
        }
        public void Simulate()
        {
            if (OpenVR.Input != null)
            {
                SteamVR_Input.LateUpdate();
            }
            if (SteamVR.active == false)
            {
                return;
            }

            // Dispatch any OpenVR events.
            var system = OpenVR.System;
            if (system == null)
            {
                return;
            }

            var compositor = OpenVR.Compositor;
            if (compositor != null)
            {
                compositor.GetLastPoses(poses, gamePoses);
                SteamVR_Events.NewPoses.Send(poses);
                SteamVR_Events.NewPosesApplied.Send();
            }
            for (int Index = 0; Index < 64; Index++)
            {
                if (!system.PollNextEvent(ref vrEvent, size))
                {
                    break;
                }

                switch ((EVREventType)vrEvent.eventType)
                {
                    case EVREventType.VREvent_InputFocusCaptured: // another app has taken focus (likely dashboard)
                        if (vrEvent.data.process.oldPid == 0)
                        {
                            SteamVR_Events.InputFocus.Send(false);
                        }
                        break;
                    case EVREventType.VREvent_InputFocusReleased: // that app has released input focus
                        if (vrEvent.data.process.pid == 0)
                        {
                            SteamVR_Events.InputFocus.Send(true);
                        }
                        break;
                    case EVREventType.VREvent_ShowRenderModels:
                        SteamVR_Events.HideRenderModels.Send(false);
                        break;
                    case EVREventType.VREvent_HideRenderModels:
                        SteamVR_Events.HideRenderModels.Send(true);
                        break;
                    default:
                        SteamVR_Events.System((EVREventType)vrEvent.eventType).Send(vrEvent);
                        break;
                }
            }
        }
        public static void Initialize(SteamVR_Render renderInstance, bool forceUnityVRToOpenVR = false)
        {
            steamvr_render = renderInstance;
        }

        protected void Awake()
        {
            steamvr_render = this;
            isPlaying = true;
            InitializeSteamVR(steamvr_render, this);
        }

        public void InitializeSteamVR(SteamVR_Render renderInstance, bool forceUnityVRToOpenVR = false)
        {
            if (forceUnityVRToOpenVR)
            {
                if (initializeCoroutine != null)
                {
                    StopCoroutine(initializeCoroutine);
                }

                if (XRSettings.loadedDeviceName == openVRDeviceName)
                {
                    EnableOpenVR();
                }
                else
                {
                    initializeCoroutine = StartCoroutine(DoInitializeSteamVR(forceUnityVRToOpenVR));
                }
            }
            else
            {
                SteamVR.Initialize(renderInstance, false);
            }
        }
        void OnApplicationQuit()
        {
            SteamVR.SafeDispose();
        }

        private IEnumerator DoInitializeSteamVR(bool forceUnityVRToOpenVR = false)
        {
            XRDevice.deviceLoaded += XRDevice_deviceLoaded;
            XRSettings.LoadDeviceByName(new string[1] { openVRDeviceName });
            while (loadedOpenVRDeviceSuccess == false)
            {
                yield return null;
            }
            XRDevice.deviceLoaded -= XRDevice_deviceLoaded;
            EnableOpenVR();
        }

        private void XRDevice_deviceLoaded(string deviceName)
        {
            if (deviceName == openVRDeviceName)
            {
                loadedOpenVRDeviceSuccess = true;
            }
            else
            {
                Debug.LogError("<b>[SteamVR]</b> Tried to async load: " + openVRDeviceName + ". Loaded: " + deviceName, this);
                loadedOpenVRDeviceSuccess = true; //try anyway
            }
        }

        private void EnableOpenVR()
        {
            SteamVR.Initialize(steamvr_render, false);
            initializeCoroutine = null;
        }

#if UNITY_EDITOR
        //only stop playing if the unity editor is running
        private void OnDestroy()
        {
            isPlaying = false;
        }
#endif
        protected void OnQuit(VREvent_t vrEvent)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
		    Application.Quit();
#endif
        }
    }
}
