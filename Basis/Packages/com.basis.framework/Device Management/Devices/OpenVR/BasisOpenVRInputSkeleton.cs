using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.OpenVR.Structs;
using System;
using UnityEngine;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    [System.Serializable]
    public class BasisOpenVRInputSkeleton 
    {
        [SerializeField]
        public OpenVRDevice Device;
        [SerializeField]
        public SteamVR_Action_Skeleton skeletonAction;
        [SerializeField]
        public BasisOpenVRInputController BasisOpenVRInputController;

        public Quaternion additionalRotation;
        public Vector3 additionalPositionOffsetLeft = new Vector3(0, -0.06f, -0.01f);
        public Vector3 additionalPositionOffsetRight = new Vector3(0, -0.06f, -0.01f);

        public void Initalize(BasisOpenVRInputController basisOpenVRInputController)
        {
            BasisOpenVRInputController = basisOpenVRInputController;
            string Action = $"Skeleton{BasisOpenVRInputController.inputSource.ToString()}";
            skeletonAction = SteamVR_Input.GetAction<SteamVR_Action_Skeleton>(Action);
            if (skeletonAction != null)
            {
                SteamVR_Input.onSkeletonsUpdated += SteamVR_Input_OnSkeletonsUpdated;
            }
            else
            {
                BasisDebug.LogError("Missing Skeleton Action for " + Action);
            }
            if(BasisOpenVRInputController.inputSource == SteamVR_Input_Sources.LeftHand)
            {
                 additionalRotation = Quaternion.Euler(new Vector3(25, 180, 45));
            }
            else
            {
                if (BasisOpenVRInputController.inputSource == SteamVR_Input_Sources.RightHand)
                {
                    //45
                     additionalRotation = Quaternion.Euler(new Vector3(25, 180, 315));
                }
            }
        }
        private void SteamVR_Input_OnSkeletonsUpdated(bool skipSendingEvents)
        {
            onTrackingChangedLoop();
        }
        private void onTrackingChangedLoop()
        {
            switch (BasisOpenVRInputController.inputSource)
            {
                case SteamVR_Input_Sources.LeftHand:
                    UpdateHandPose(
                        BasisLocalPlayer.Instance.LocalHandDriver.LeftHand,
                        additionalPositionOffsetLeft,
                        skeletonAction.boneRotations[1] * additionalRotation
                    );
                    break;

                case SteamVR_Input_Sources.RightHand:
                    UpdateHandPose(
                        BasisLocalPlayer.Instance.LocalHandDriver.RightHand,
                        additionalPositionOffsetRight,
                        skeletonAction.boneRotations[1] * additionalRotation
                    );
                    break;
            }
        }

        private void UpdateHandPose(BasisFingerPose hand, Vector3 positionOffset, Quaternion rotation)
        {
            // Finger curls
            hand.ThumbPercentage[0] = Remap01ToMinus1To1(skeletonAction.fingerCurls[0]);
            hand.IndexPercentage[0] = Remap01ToMinus1To1(skeletonAction.fingerCurls[1]);
            hand.MiddlePercentage[0] = Remap01ToMinus1To1(skeletonAction.fingerCurls[2]);
            hand.RingPercentage[0] = Remap01ToMinus1To1(skeletonAction.fingerCurls[3]);
            hand.LittlePercentage[0] = Remap01ToMinus1To1(skeletonAction.fingerCurls[4]);

            // Finger splays
            hand.IndexPercentage[1] = Remap01ToMinus1To1(skeletonAction.fingerSplays[0]);
            hand.MiddlePercentage[1] = Remap01ToMinus1To1(skeletonAction.fingerSplays[1]);
            hand.RingPercentage[1] = Remap01ToMinus1To1(skeletonAction.fingerSplays[2]);
            hand.LittlePercentage[1] = Remap01ToMinus1To1(skeletonAction.fingerSplays[3]);

            // Apply offsets
            BasisOpenVRInputController.AvatarPositionOffset = skeletonAction.bonePositions[1] + positionOffset;
            BasisOpenVRInputController.AvatarRotationOffset = rotation.eulerAngles;
        }
        float Remap01ToMinus1To1(float value)
        {
            return (1f - value) * 2f - 1f;
        }
        public void DeInitalize()
        {
            SteamVR_Input.onSkeletonsUpdated -= SteamVR_Input_OnSkeletonsUpdated;
        }
    }
}
