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
                        UpdateFingerPercentages( BasisLocalPlayer.Instance.LocalMuscleDriver.LeftHandPoses);

                        // Apply additional position offset
                        BasisOpenVRInputController.AvatarPositionOffset = skeletonAction.bonePositions[1] + additionalPositionOffsetLeft;

                        // Apply additional rotation offset by converting to Quaternion and adding
                        BasisOpenVRInputController.AvatarRotationOffset = (skeletonAction.boneRotations[1] * additionalRotation).eulerAngles;
                        break;
                    }

                case SteamVR_Input_Sources.RightHand:
                    {
                        UpdateFingerPercentages( BasisLocalPlayer.Instance.LocalMuscleDriver.RightHandPoses);
                        // Apply additional position offset
                        BasisOpenVRInputController.AvatarPositionOffset = skeletonAction.bonePositions[1] + additionalPositionOffsetRight;

                        // Apply additional rotation offset by converting to Quaternion and adding
                        Quaternion baseRotation = skeletonAction.boneRotations[1];
                        BasisOpenVRInputController.AvatarRotationOffset = (baseRotation * additionalRotation).eulerAngles;
                        break;
                    }
            }
        }

        private void UpdateFingerPercentages( BasisFingerPose fingerDriver)
        {

        }
        public void DeInitalize()
        {
            SteamVR_Input.onSkeletonsUpdated -= SteamVR_Input_OnSkeletonsUpdated;
        }
    }
}
