using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.OpenVR;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SpatialTracking;
using Valve.VR;
namespace Basis.Scripts.Device_Management.Devices.Unity_Spatial_Tracking
{
    [DefaultExecutionOrder(15001)]
    public class BasisOpenVRInputSpatial : BasisInput
    {
        public TrackedPoseDriver.TrackedPose TrackedPose = TrackedPoseDriver.TrackedPose.Center;
        public BasisOpenVRInputEye BasisOpenVRInputEye;
        public BasisLocalVirtualSpineDriver BasisVirtualSpine = new BasisLocalVirtualSpineDriver();
        public void Initialize(TrackedPoseDriver.TrackedPose trackedPose, string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole, SteamVR_Input_Sources SteamVR_Input_Sources)
        {
            TrackedPose = trackedPose;
            InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole);
            if (basisBoneTrackedRole == BasisBoneTrackedRole.CenterEye)
            {
                BasisOpenVRInputEye = gameObject.AddComponent<BasisOpenVRInputEye>();
                BasisOpenVRInputEye.Initalize();
                BasisVirtualSpine.Initialize();
            }
        }
        public new void OnDestroy()
        {
            BasisVirtualSpine.DeInitialize();
            BasisOpenVRInputEye.Shutdown();
            base.OnDestroy();
        }
        public override void DoPollData()
        {
            if (PoseDataSource.TryGetDataFromSource(TrackedPose, out Pose resultPose))
            {
                UnscaledDeviceCoord.rotation = resultPose.rotation;
                UnscaledDeviceCoord.position = (float3)resultPose.position;

                ConvertToScaledDeviceCoord();
                if (TryGetRole(out var CurrentRole) && CurrentRole == BasisBoneTrackedRole.CenterEye)
                {
                    BasisOpenVRInputEye.Simulate();
                }
            }
            ControlOnlyAsDevice();
            UpdatePlayerControl();
        }
        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker == null && LoadedDeviceRequest == null)
            {
                DeviceSupportInformation Match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
                if (Match.CanDisplayPhysicalTracker)
                {
                    LoadModelWithKey(Match.DeviceID);
                }
                else
                {
                    if (UseFallbackModel())
                    {
                        LoadModelWithKey(FallbackDeviceID);
                    }
                }
            }
        }
        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
            BasisDebug.LogError("Spatial does not support Haptics Playback");
        }
        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }
    }
}
