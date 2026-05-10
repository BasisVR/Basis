using HVR.Basis.Comms;
using UnityEngine;
#if HVR_VIXXY_IS_IN_BASIS
using Basis.Scripts.BasisSdk;
using HVR.Basis.Comms;
using HVR.Basis.Comms.HVRUtility;
#endif

namespace HVR.Vixxy
{
    public class VixxySetup
    {
        public static HVRVixxyOrchestrator EnsureInitialized(Component comp, bool isWearer)
        {
#if HVR_VIXXY_IS_IN_BASIS
            var avatar = HVRCommsUtil.GetAvatar(comp);
            if (avatar == null)
            {
                return EnsureSceneHasNonAvatarOrchestrator();
            }

            var existingOrchestrator = avatar.GetComponentInChildren<HVRVixxyOrchestrator>(true);
            if (existingOrchestrator != null) return existingOrchestrator;

            var orchestrator = CreateOrchestrator(avatar.transform, "Generated__VixxyAvatar", avatar.transform, isWearer);
            return orchestrator;
#else
            return EnsureSceneHasNonAvatarOrchestrator();
#endif
        }

        private static HVRVixxyOrchestrator EnsureSceneHasNonAvatarOrchestrator()
        {
            var existingOrchestrators = Object.FindObjectsByType<HVRVixxyOrchestrator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var existingOrchestrator in existingOrchestrators)
            {
#if HVR_VIXXY_IS_IN_BASIS
                var isNotInsideAvatar = HVRCommsUtil.GetAvatar(existingOrchestrator) == null;
                if (isNotInsideAvatar)
                {
                    return existingOrchestrator;
                }
#else
                    return existingOrchestrator;
#endif
            }

            var receiveEventsFromAcquisitionService = true;
            var sceneOrchestrator = CreateOrchestrator(null, "Generated__VixxyScene", null, receiveEventsFromAcquisitionService);
            return sceneOrchestrator;
        }

        private static HVRVixxyOrchestrator CreateOrchestrator(Transform contextNullable, string name, Transform parentNullable, bool isWearer)
        {
            var go = new GameObject(name);
            if (parentNullable != null)
            {
                go.transform.SetParent(parentNullable);
            }
            go.SetActive(false);
            var orchestrator = go.AddComponent<HVRVixxyOrchestrator>();
            orchestrator.context = contextNullable;
            if (contextNullable != null)
            {
#if HVR_VIXXY_IS_IN_BASIS
                var commsNullable = HVRCommsUtil.GetComms(contextNullable);
                if (commsNullable != null)
                {
                    orchestrator.VariableStore = commsNullable.VariableStore;
                }
                else
                {
                    orchestrator.VariableStore = AcquisitionService.SceneInstance.VariableStore;
                }
#endif
            }
            go.SetActive(true);
            return orchestrator;
        }
    }
}
