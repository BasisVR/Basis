using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Valve.VR;

namespace Unity.XR.OpenVR
{
    public class OpenVREvent : UnityEvent<VREvent_t> { }
    public class OpenVREvents
    {
        private static OpenVREvents instance;

        //dictionaries are slow/allocate in mono for some reason. So we just allocate a bunch at the beginning.
        private OpenVREvent[] events;
        private int[] eventIndicies;
        private VREvent_t vrEvent;
        private uint vrEventSize;

        private bool preloadedEvents = false;

        private const int maxEventsPerUpdate = 64;
        private static bool debugLogAllEvents = false;

        private static bool enabled = true;

        public static void Initialize(bool lazyLoadEvents = false)
        {
            instance = new OpenVREvents(lazyLoadEvents);
        }

        public bool IsInitialized()
        {
            return instance != null;
        }

        public OpenVREvents(bool lazyLoadEvents = false)
        {
            if (OpenVRHelpers.IsUsingSteamVRInput())
            {
                enabled = false; //let the steamvr plugin handle events
                return;
            }

            instance = this;
            events = new OpenVREvent[(int)EVREventType.VREvent_VendorSpecific_Reserved_End];

            vrEvent = new VREvent_t();
            vrEventSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VREvent_t));

            if (lazyLoadEvents == false)
            {
                for (int eventIndex = 0; eventIndex < events.Length; eventIndex++)
                {
                    events[eventIndex] = new OpenVREvent();
                }
            }
            else
            {
                preloadedEvents = true;
            }

            RegisterDefaultEvents();
        }

        public void RegisterDefaultEvents()
        {
            AddListener(EVREventType.VREvent_Quit, On_VREvent_Quit);
        }

        public static void AddListener(EVREventType eventType, UnityAction<VREvent_t> action, bool removeOtherListeners = false)
        {
            instance.Add(eventType, action, removeOtherListeners);
        }
        public void Add(EVREventType eventType, UnityAction<VREvent_t> action, bool removeOtherListeners = false)
        {
            if (!enabled)
            {
                Debug.LogError("[OpenVR XR Plugin] This events class is currently not enabled, please use SteamVR_Events instead.");
                return;
            }

            int eventIndex = (int)eventType;
            if (preloadedEvents == false && events[eventIndex] == null)
            {
                events[eventIndex] = new OpenVREvent();
            }

            if (removeOtherListeners)
            {
                events[eventIndex].RemoveAllListeners();
            }

            events[eventIndex].AddListener(action);
        }

        public static void RemoveListener(EVREventType eventType, UnityAction<VREvent_t> action)
        {
            instance.Remove(eventType, action);
        }
        public void Remove(EVREventType eventType, UnityAction<VREvent_t> action)
        {
            int eventIndex = (int)eventType;
            if (preloadedEvents || events[eventIndex] != null)
            {
                events[eventIndex].RemoveListener(action);
            }
        }

        public static void Update()
        {
            instance.PollEvents();
        }

        public void PollEvents()
        {
            if (Valve.VR.OpenVR.System != null && enabled)
            {
                for (int eventIndex = 0; eventIndex < maxEventsPerUpdate; eventIndex++)
                {
                    if (Valve.VR.OpenVR.System == null || !Valve.VR.OpenVR.System.PollNextEvent(ref vrEvent, vrEventSize))
                        break;

                    int uEventType = (int)vrEvent.eventType;

                    if (debugLogAllEvents)
                    {
                        EVREventType eventType = (EVREventType)uEventType;
                        Debug.Log(string.Format("[{0}] {1}", Time.frameCount, eventType.ToString()));
                    }

                    if (events[uEventType] != null)
                    {
                        events[uEventType].Invoke(vrEvent);
                    }
                }
            }
        }

        private bool exiting = false;

        #region DefaultEvents
        private void On_VREvent_Quit(VREvent_t pEvent)
        {
            if (exiting == true)
            {
                return;
            }
            exiting = true;

            if (Valve.VR.OpenVR.System != null)
            {
                Valve.VR.OpenVR.System.AcknowledgeQuit_Exiting();
            }

#if UNITY_EDITOR
            Debug.Log("<b>[OpenVR]</b> Quit requested from OpenVR. Exiting application via EditorApplication.isPlaying = false");
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Debug.Log("<b>[OpenVR]</b> Quit requested from OpenVR. Exiting application via Application.Quit");
            Application.Quit();
#endif
        }
        #endregion
    }
}