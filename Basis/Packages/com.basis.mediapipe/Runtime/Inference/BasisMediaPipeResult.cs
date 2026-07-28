using UnityEngine;

namespace Basis.MediaPipe
{
    /// <summary>One inferred frame of webcam tracking data in source/normalized space.</summary>
    public struct BasisMediaPipeResult
    {
        public bool HasFace;
        public bool HasLeftHand;
        public bool HasRightHand;
        public bool HasPose;

        public Matrix4x4 FaceTransform;
        public float[] FaceBlendshapes;
        public float TongueOut;

        public Vector2 HeadImagePosition;
        public float FaceImageSize;

        public Vector2 LeftEyeGaze;
        public Vector2 RightEyeGaze;

        public Vector3[] LeftHandLandmarks;
        public Vector3[] RightHandLandmarks;

        /// <summary>Metric hand landmarks in metres, wrist-relative. Orientation and finger curl come from these.</summary>
        public Vector3[] LeftHandWorldLandmarks;
        public Vector3[] RightHandWorldLandmarks;

        public Vector3[] PoseLandmarks;

        /// <summary>Metric body landmarks in metres. The arm retarget runs off these, not the image landmarks.</summary>
        public Vector3[] PoseWorldLandmarks;

        /// <summary>Per-landmark visibility. The pose model extrapolates limbs it cannot see, so this is the
        /// only thing separating a tracked arm from confident-looking phantom data.</summary>
        public float[] PoseVisibility;

        public bool PoseSidesSwapped;

        public double TimestampMs;
    }
}
