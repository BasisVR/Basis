using Basis.BasisUI;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk;
using Unity.Profiling;
using UnityEngine;

namespace HVR.Basis.Comms
{
    public static class AutomaticFaceTrackingInjector
    {
        private static readonly ProfilerMarker AutoDetectMarker = new ProfilerMarker("BasisFace.AutoDetect");
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            BasisAvatarFactory.OnAvatarPreCalibration -= Inject;
            BasisAvatarFactory.OnAvatarPreCalibration += Inject;
        }

        private static void Inject(BasisAvatar avatar, bool isLocal)
        {
            using (AutoDetectMarker.Auto()) InjectInternal(avatar, isLocal);
        }

        private static void InjectInternal(BasisAvatar avatar, bool isLocal)
        {
            if (avatar == null || !avatar.gameObject.activeInHierarchy) return;
            if (avatar.GetComponentInChildren<AutomaticFaceTracking>(true) != null) return;
            var comms = avatar.GetComponentInChildren<HVRAvatarComms>(true);
            var hasCarrier = avatar.GetComponentInChildren<HVRNetworkingCarrier>(true) != null;
            if ((comms != null) != hasCarrier) return;
            var face = !isLocal || BasisSettingsDefaults.AutoDetectFaceTracking.RawValue;
            var eye = !isLocal || BasisSettingsDefaults.AutoDetectEyeTracking.RawValue;
            if (!face && !eye) return;
            BlendshapeActuationDefinitionFile[] files = null;
            var convention = AutomaticFaceTracking.NamingConvention.Unknown;
            if (face)
            {
                var smrs = avatar.SkinnedMeshRenderers != null && avatar.SkinnedMeshRenderers.Length > 0 ? avatar.SkinnedMeshRenderers : avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                files = AutomaticFaceTracking.ResolveAutoDetectedFilesOrNull(smrs, out convention);
            }
            var useEye = eye && AutomaticFaceTracking.HasEyeBones(avatar);
            if (files == null && !useEye) return;
            GameObject holder;
            if (comms == null) holder = HVRAvatarComms.CreateRuntimeInactive(avatar);
            else
            {
                holder = new GameObject("Generated__AutomaticFaceTracking");
                holder.SetActive(false);
                holder.transform.SetParent(avatar.transform, false);
            }
            var tracking = holder.AddComponent<AutomaticFaceTracking>();
            tracking.autoDetected = true;
            tracking.autoFace = files != null;
            tracking.autoEye = eye;
            tracking.preResolvedFiles = files;
            tracking.preResolvedConvention = convention;
            holder.SetActive(true);
        }
    }
}
