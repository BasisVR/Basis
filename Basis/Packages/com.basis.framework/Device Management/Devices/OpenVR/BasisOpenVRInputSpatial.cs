using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.OpenVR;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SpatialTracking;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.Unity_Spatial_Tracking
{
    [DefaultExecutionOrder(15001)]
    public class BasisOpenVRInputSpatial : BasisInput
    {
        public TrackedPoseDriver.TrackedPose TrackedPose = TrackedPoseDriver.TrackedPose.Center;
        public BasisOpenVRInputEye BasisOpenVRInputEye;
        public BasisVirtualSpineDriver BasisVirtualSpine = new BasisVirtualSpineDriver();
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
                LocalRawPosition = (float3)resultPose.position;
                LocalRawRotation = resultPose.rotation;
                if (hasRoleAssigned)
                {
                    if (Control.HasTracked != BasisHasTracked.HasNoTracker)
                    {
                        Control.IncomingData.position = TransformFinalPosition - math.mul(TransformFinalRotation, AvatarPositionOffset * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale);
                        Control.IncomingData.rotation = math.mul(TransformFinalRotation, Quaternion.Euler(AvatarRotationOffset));
                    }
                }
                if (TryGetRole(out var CurrentRole))
                {
                    if (CurrentRole == BasisBoneTrackedRole.CenterEye)
                    {
                        BasisOpenVRInputEye.Simulate();
                    }
                }
            }
            TransformFinalPosition = LocalRawPosition * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;
            TransformFinalRotation = LocalRawRotation;
            UpdatePlayerControl();
        }
        public override void ShowTrackedVisual()
		{
			if (BasisVisualTracker != null || LoadedDeviceRequest != null) return;

			BasisDeviceMatchSettings Match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
			if (Match.CanDisplayPhysicalTracker)
			{
				var op = Addressables.LoadAssetAsync<GameObject>(Match.DeviceID);
				GameObject go = op.WaitForCompletion();
				GameObject gameObject = Object.Instantiate(go);
				gameObject.name = CommonDeviceIdentifier;
				gameObject.transform.parent = this.transform;
				if (gameObject.TryGetComponent(out BasisVisualTracker))
				{
					BasisVisualTracker.Initialization(this);
				}
			}
			else
			{
				if (UseFallbackModel())
				{
					UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op = Addressables.LoadAssetAsync<GameObject>(FallbackDeviceID);
					GameObject go = op.WaitForCompletion();
					GameObject gameObject = Object.Instantiate(go);
					gameObject.name = CommonDeviceIdentifier;
					gameObject.transform.parent = this.transform;
					if (gameObject.TryGetComponent(out BasisVisualTracker))
					{
						BasisVisualTracker.Initialization(this);
					}
				}
			}
		}
	}
}
