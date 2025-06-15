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
                    {
                        BasisFingerPoseParams[] LeftHandParams = BasisLocalPlayer.Instance.LocalMuscleDriver.LeftHandParams;
                        //values need to be moved from 0 to 1 to -0.9 to 0.9f
                        LeftHandParams[0].Stretch = Remap01ToMinus1To1(skeletonAction.fingerCurls[0]);
                        LeftHandParams[1].Stretch = Remap01ToMinus1To1(skeletonAction.fingerCurls[1]);
                        LeftHandParams[2].Stretch = Remap01ToMinus1To1(skeletonAction.fingerCurls[2]);
                        LeftHandParams[3].Stretch = Remap01ToMinus1To1(skeletonAction.fingerCurls[3]);
                        LeftHandParams[4].Stretch = Remap01ToMinus1To1(skeletonAction.fingerCurls[4]);

                        LeftHandParams[1].Spread = Remap01ToMinus1To1(skeletonAction.fingerSplays[0]);
                        LeftHandParams[2].Spread = Remap01ToMinus1To1(skeletonAction.fingerSplays[1]);
                        LeftHandParams[3].Spread = Remap01ToMinus1To1(skeletonAction.fingerSplays[2]);
                        LeftHandParams[4].Spread = Remap01ToMinus1To1(skeletonAction.fingerSplays[3]);
                        // Apply additional position offset
                        BasisOpenVRInputController.AvatarPositionOffset = skeletonAction.bonePositions[1] + additionalPositionOffsetLeft;

                        // Apply additional rotation offset by converting to Quaternion and adding
                        BasisOpenVRInputController.AvatarRotationOffset = (skeletonAction.boneRotations[1] * additionalRotation).eulerAngles;
                        break;
                    }

                case SteamVR_Input_Sources.RightHand:
                    {
                        var RightHandParams = BasisLocalPlayer.Instance.LocalMuscleDriver.RightHandParams;
                        RightHandParams[0].Stretch = Remap01ToMinus1To1(skeletonAction.fingerCurls[0]);
                        RightHandParams[1].Stretch = Remap01ToMinus1To1(skeletonAction.fingerCurls[1]);
                        RightHandParams[2].Stretch = Remap01ToMinus1To1(skeletonAction.fingerCurls[2]);
                        RightHandParams[3].Stretch = Remap01ToMinus1To1(skeletonAction.fingerCurls[3]);
                        RightHandParams[4].Stretch = Remap01ToMinus1To1(skeletonAction.fingerCurls[4]);

                        RightHandParams[1].Spread = Remap01ToMinus1To1(skeletonAction.fingerSplays[0]);
                        RightHandParams[2].Spread = Remap01ToMinus1To1(skeletonAction.fingerSplays[1]);
                        RightHandParams[3].Spread = Remap01ToMinus1To1(skeletonAction.fingerSplays[2]);
                        RightHandParams[4].Spread = Remap01ToMinus1To1(skeletonAction.fingerSplays[3]);
                        // Apply additional position offset
                        BasisOpenVRInputController.AvatarPositionOffset = skeletonAction.bonePositions[1] + additionalPositionOffsetRight;

                        // Apply additional rotation offset by converting to Quaternion and adding
                        Quaternion baseRotation = skeletonAction.boneRotations[1];
                        BasisOpenVRInputController.AvatarRotationOffset = (baseRotation * additionalRotation).eulerAngles;
                        break;
                    }
            }
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
