using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.OpenVR.Structs;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    [DefaultExecutionOrder(15001)]
    public class BasisOpenVRInputController : BasisInput
    {
        public OpenVRDevice Device;
        public SteamVR_Input_Sources inputSource;
        public SteamVR_Action_Pose DeviceposeAction = SteamVR_Input.GetAction<SteamVR_Action_Pose>("Pose");
        public BasisOpenVRInputSkeleton SkeletonHandInput = null;
        public bool HasOnUpdate = false;
        public void Initialize(OpenVRDevice device, string UniqueID, string UnUniqueID, string subSystems, bool AssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole, SteamVR_Input_Sources SteamVR_Input_Sources)
        {
            if (HasOnUpdate && DeviceposeAction != null)
            {
                DeviceposeAction[inputSource].onUpdate -= SteamVR_Behavior_Pose_OnUpdate;
                HasOnUpdate = false;
            }
            inputSource = SteamVR_Input_Sources;
            Device = device;
            InitalizeTracking(UniqueID, UnUniqueID, subSystems, AssignTrackedRole, basisBoneTrackedRole);
            if (DeviceposeAction != null)
            {
                if (HasOnUpdate == false)
                {
                    DeviceposeAction[inputSource].onUpdate += SteamVR_Behavior_Pose_OnUpdate;
                    HasOnUpdate = true;
                }
            }
            if (inputSource == SteamVR_Input_Sources.LeftHand || inputSource == SteamVR_Input_Sources.RightHand)
            {
                SkeletonHandInput = new BasisOpenVRInputSkeleton();
                SkeletonHandInput.Initalize(this);
            }
            BasisDebug.Log("set Controller to inputSource " + inputSource + " bone role " + basisBoneTrackedRole);
        }
        public new void OnDestroy()
        {
            if (DeviceposeAction != null)
            {
                DeviceposeAction[inputSource].onUpdate -= SteamVR_Behavior_Pose_OnUpdate;
                HasOnUpdate = false;
            }
            if (SkeletonHandInput != null)
            {
                SkeletonHandInput.DeInitalize();
            }
            historyBuffer.Clear();
            base.OnDestroy();
        }
        public override void DoPollData()
        {
            if (SteamVR.active)
            {
                CurrentInputState.GripButton = SteamVR_Actions._default.Grip.GetState(inputSource);
                CurrentInputState.SystemOrMenuButton = SteamVR_Actions._default.System.GetState(inputSource);
                CurrentInputState.PrimaryButtonGetState = SteamVR_Actions._default.A_Button.GetState(inputSource);
                CurrentInputState.SecondaryButtonGetState = SteamVR_Actions._default.B_Button.GetState(inputSource);
                CurrentInputState.Primary2DAxisClick = SteamVR_Actions._default.JoyStickClick.GetState(inputSource);
                CurrentInputState.Primary2DAxis = SteamVR_Actions._default.Joystick.GetAxis(inputSource);
                CurrentInputState.Trigger = SteamVR_Actions._default.Trigger.GetAxis(inputSource);
                CurrentInputState.SecondaryTrigger = SteamVR_Actions._default.HandTrigger.GetAxis(inputSource);
                CurrentInputState.Secondary2DAxis = SteamVR_Actions._default.TrackPad.GetAxis(inputSource);
                CurrentInputState.Secondary2DAxisClick = SteamVR_Actions._default.TrackPadTouched.GetState(inputSource);
                UpdatePlayerControl();
            }
        }
        public Vector3 ControllerPosition;
        public Quaternion ControllerRotation;
        private void SteamVR_Behavior_Pose_OnUpdate(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            UpdateHistoryBuffer();
            if (HasOnUpdate)
            {
                ControllerPosition = DeviceposeAction[inputSource].localPosition;
                ControllerRotation = DeviceposeAction[inputSource].localRotation;
            }
            TransformFinalPosition = LocalRawPosition * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;
            TransformFinalRotation = LocalRawRotation;
            if (hasRoleAssigned)
            {
                if (Control.HasTracked != BasisHasTracked.HasNoTracker)
                {
                    // Apply position offset using math.mul for quaternion-vector multiplication
                    Control.IncomingData.position = TransformFinalPosition;

                    // Apply rotation offset using math.mul for quaternion multiplication
                    Control.IncomingData.rotation = TransformFinalRotation;
                }
            }
        }
        #region Mostly Unused Steam
        protected SteamVR_HistoryBuffer historyBuffer = new SteamVR_HistoryBuffer(30);
        protected int lastFrameUpdated;
        protected void UpdateHistoryBuffer()
        {
            int currentFrame = Time.frameCount;
            if (lastFrameUpdated != currentFrame)
            {
                historyBuffer.Update(DeviceposeAction[inputSource].localPosition, DeviceposeAction[inputSource].localRotation, DeviceposeAction[inputSource].velocity, DeviceposeAction[inputSource].angularVelocity);
                lastFrameUpdated = currentFrame;
            }
        }
        public Vector3 GetVelocity()
        {
            return DeviceposeAction[inputSource].velocity;
        }
        public Vector3 GetAngularVelocity()
        {
            return DeviceposeAction[inputSource].angularVelocity;
        }
        public bool GetVelocitiesAtTimeOffset(float secondsFromNow, out Vector3 velocity, out Vector3 angularVelocity)
        {
            return DeviceposeAction[inputSource].GetVelocitiesAtTimeOffset(secondsFromNow, out velocity, out angularVelocity);
        }
        public void GetEstimatedPeakVelocities(out Vector3 velocity, out Vector3 angularVelocity)
        {
            int top = historyBuffer.GetTopVelocity(10, 1);

            historyBuffer.GetAverageVelocities(out velocity, out angularVelocity, 2, top);
        }
        public bool isValid { get { return DeviceposeAction[inputSource].poseIsValid; } }
        public bool isActive { get { return DeviceposeAction[inputSource].active; } }
        #endregion
        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker == null && LoadedDeviceRequest == null)
            {
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
        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
            SteamVR_Actions.default_Haptic.Execute(0, duration, frequency, amplitude, inputSource);
        }
    }
}
