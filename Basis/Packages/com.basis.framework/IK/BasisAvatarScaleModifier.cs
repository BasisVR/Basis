using Basis.Scripts.BasisSdk.Players;
using System;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    [Serializable]
    public class BasisAvatarScaleModifier
    {
        /// <summary>
        /// set during calibration
        /// </summary>
        public Vector3 DuringCalibrationScale = Vector3.one;
        /// <summary>
        /// Set Scale
        /// </summary>
        public float ApplyScale;
        /// <summary>
        /// Final Scale is Set Scale * DuringCalibrationScale
        /// </summary>
        public Vector3 FinalScale = Vector3.one;
        public void ReInitalize(Animator Animator)
        {
            DuringCalibrationScale = Animator.transform.localScale;
            ApplyScale = 1;
            FinalScale = DuringCalibrationScale;
        }
        public void SetAvatarheightOverride(float Scale)
        {
            ApplyScale = Scale;
            // Final scale = Default scale * Override scale (component-wise)
            FinalScale = DuringCalibrationScale * Scale;
            if (BasisLocalPlayer.Instance.BasisAvatar != null)
            {
                BasisLocalPlayer.Instance.BasisAvatar.transform.localScale = FinalScale;
            }
        }
    }
}
