using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices.OpenVR.Structs;
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
        public float[] FingerSplays = new float[5];
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
                        UpdateFingerPercentages(ref BasisLocalPlayer.Instance.LocalMuscleDriver.LeftFinger);

                        // Apply additional position offset
                        BasisOpenVRInputController.AvatarPositionOffset = skeletonAction.bonePositions[1] + additionalPositionOffsetLeft;

                        // Apply additional rotation offset by converting to Quaternion and adding
                        BasisOpenVRInputController.AvatarRotationOffset = (skeletonAction.boneRotations[1] * additionalRotation).eulerAngles;
                        break;
                    }

                case SteamVR_Input_Sources.RightHand:
                    {
                        UpdateFingerPercentages(ref BasisLocalPlayer.Instance.LocalMuscleDriver.RightFinger);

                        // Apply additional position offset
                        BasisOpenVRInputController.AvatarPositionOffset = skeletonAction.bonePositions[1] + additionalPositionOffsetRight;

                        // Apply additional rotation offset by converting to Quaternion and adding
                        Quaternion baseRotation = skeletonAction.boneRotations[1];
                        BasisOpenVRInputController.AvatarRotationOffset = (baseRotation * additionalRotation).eulerAngles;
                        break;
                    }
            }
        }

        private void UpdateFingerPercentages(ref BasisFingerPose fingerDriver)
        {
            ConvertFingerSplays();
            fingerDriver.ThumbPercentage = GetFingerPercentage(0);
            fingerDriver.IndexPercentage = GetFingerPercentage(1);
            fingerDriver.MiddlePercentage = GetFingerPercentage(2);
            fingerDriver.RingPercentage = GetFingerPercentage(3);
            fingerDriver.LittlePercentage = GetFingerPercentage(4);
        }
        /// <summary>
        /// this is where it all goes wrong
        /// valve use splay in a funky way 
        /// or in a way my dumbass does not understand
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        private Vector2 GetFingerPercentage(int index)
        {
            float flippedCurl = 1 - skeletonAction.fingerCurls[index];
            float flippedSplay = 1 - FingerSplays[index];

            return new Vector2(
                BasisBaseMuscleDriver.MapValue(flippedCurl, 0, 1, -1f, 0.7f),
                flippedSplay
            );
        }
        public void ConvertFingerSplays()
        {
            FingerSplays[0] = 0;
            for (int Index = 1; Index < 5; Index++)
            {
                FingerSplays[Index] = BasisBaseMuscleDriver.MapValue(skeletonAction.fingerSplays[Index-1], 0, 1, -1f, 1f);
            }

        }
        public void DeInitalize()
        {
            SteamVR_Input.onSkeletonsUpdated -= SteamVR_Input_OnSkeletonsUpdated;
        }
    }
}
