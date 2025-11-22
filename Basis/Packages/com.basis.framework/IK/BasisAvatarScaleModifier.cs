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
        public void ReInitalize(Animator animator)
        {
            DuringCalibrationScale = animator.transform.localScale;
            ApplyScale = 1;
            FinalScale = DuringCalibrationScale;
        }
        /// <summary>
        /// Use <see cref="BasisLocalHeight.SetPlayerSize"/> instead of calling this directly.
        /// </summary>
        public void SetAvatarScale(float scale)
        {
            ApplyScale = scale;
            FinalScale = DuringCalibrationScale * scale;
            if (BasisLocalPlayer.Instance.BasisAvatar != null)
            {
                BasisLocalPlayer.Instance.BasisAvatar.transform.localScale = FinalScale;
            }
        }
    }
}
