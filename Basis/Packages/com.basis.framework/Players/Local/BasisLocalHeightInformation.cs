namespace Basis.Scripts.BasisSdk.Players
{
    /// <summary>
    /// Serializable container for player/avatar height metrics and scale ratios.
    /// Tracks measured eye heights and arm spans for both the user (player) and the avatar,
    /// along with precomputed scale ratios and the currently selected height/scale pair.
    /// </summary>
    /// <remarks>
    /// Use <see cref="PickRatio(BasisSelectedHeightMode)"/> to populate the
    /// <see cref="SelectedPlayerHeight"/>, <see cref="SelectedAvatarHeight"/>,
    /// <see cref="SelectedPlayerToDefaultScale"/>, and <see cref="SelectedAvatarToAvatarDefaultScale"/> fields
    /// based on the chosen mode (ArmSpan, EyeHeight, or Custom).
    /// <para>
    /// Note on the <c>Custom</c> branch: the scale computation uses
    /// <see cref="BasisLocalPlayer.DefaultAvatarEyeHeight"/> for the player scale and
    /// <see cref="BasisLocalPlayer.DefaultPlayerEyeHeight"/> for the avatar scale. Verify this is intentional.
    /// </para>
    /// </remarks>
    [System.Serializable]
    public class BasisLocalHeightInformation
    {
        /// <summary>
        /// Human-readable name of the avatar these measurements are associated with.
        /// </summary>
        public string AvatarName;

        /// <summary>
        /// Measured eye height for the player (meters). Defaults to <see cref="BasisLocalPlayer.FallbackSize"/>.
        /// </summary>
        public float PlayerEyeHeight = BasisLocalPlayer.FallbackSize;

        /// <summary>
        /// Measured eye height for the avatar (meters). Defaults to <see cref="BasisLocalPlayer.FallbackSize"/>.
        /// </summary>
        public float AvatarEyeHeight = BasisLocalPlayer.FallbackSize;

        /// <summary>
        /// Measured arm span for the player (meters). Defaults to <see cref="BasisLocalPlayer.FallbackSize"/>.
        /// </summary>
        public float PlayerArmSpan = BasisLocalPlayer.FallbackSize;

        /// <summary>
        /// Measured arm span for the avatar (meters). Defaults to <see cref="BasisLocalPlayer.FallbackSize"/>.
        /// </summary>
        public float AvatarArmSpan = BasisLocalPlayer.FallbackSize;

        /// <summary>
        /// Custom player eye height (meters) supplied by user or calibration UI.
        /// </summary>
        public float CustomPlayerEyeHeight = BasisLocalPlayer.FallbackSize;

        /// <summary>
        /// Custom avatar eye height (meters) supplied by user or calibration UI.
        /// </summary>
        public float CustomAvatarEyeHeight = BasisLocalPlayer.FallbackSize;

        /// <summary>
        /// Ratio mapping the player's measured eye height to a default reference scale.
        /// </summary>
        public float EyeRatioPlayerToDefaultScale = 1f;

        /// <summary>
        /// Ratio mapping the avatar's measured eye height to the avatar's default reference scale.
        /// </summary>
        public float EyeRatioAvatarToAvatarDefaultScale = 1f; // should be used for the player

        /// <summary>
        /// Ratio mapping the player's measured arm span to a default reference scale.
        /// </summary>
        public float ArmRatioPlayerToDefaultScale = 1f;

        /// <summary>
        /// Ratio mapping the avatar's measured arm span to the avatar's default reference scale.
        /// </summary>
        public float ArmRatioAvatarToAvatarDefaultScale = 1f; // should be used for the player

        /// <summary>
        /// The player height (meters) currently selected by <see cref="PickRatio(BasisSelectedHeightMode)"/>.
        /// </summary>
        public float SelectedPlayerHeight = BasisLocalPlayer.FallbackSize;

        /// <summary>
        /// The avatar height (meters) currently selected by <see cref="PickRatio(BasisSelectedHeightMode)"/>.
        /// </summary>
        public float SelectedAvatarHeight = BasisLocalPlayer.FallbackSize;

        /// <summary>
        /// The player-to-default scale currently selected by <see cref="PickRatio(BasisSelectedHeightMode)"/>.
        /// </summary>
        public float SelectedPlayerToDefaultScale = 1f;

        /// <summary>
        /// The avatar-to-avatar-default scale currently selected by <see cref="PickRatio(BasisSelectedHeightMode)"/>.
        /// </summary>
        public float SelectedAvatarToAvatarDefaultScale = 1f;

        /// <summary>
        /// Chooses the active height metrics and scale ratios based on the provided mode.
        /// </summary>
        /// <param name="Height">Selection mode: <see cref="BasisSelectedHeightMode.ArmSpan"/>,
        /// <see cref="BasisSelectedHeightMode.EyeHeight"/>, or <see cref="BasisSelectedHeightMode.Custom"/>.</param>
        public void PickRatio(BasisSelectedHeightMode Height)
        {
            switch (Height)
            {
                case BasisSelectedHeightMode.ArmSpan:
                    SelectedPlayerHeight = PlayerArmSpan;
                    SelectedAvatarHeight = AvatarArmSpan;

                    SelectedPlayerToDefaultScale = ArmRatioPlayerToDefaultScale;
                    SelectedAvatarToAvatarDefaultScale = ArmRatioAvatarToAvatarDefaultScale;
                    break;

                case BasisSelectedHeightMode.EyeHeight:
                    SelectedPlayerHeight = PlayerEyeHeight;
                    SelectedAvatarHeight = AvatarEyeHeight;

                    SelectedPlayerToDefaultScale = EyeRatioPlayerToDefaultScale;
                    SelectedAvatarToAvatarDefaultScale = EyeRatioAvatarToAvatarDefaultScale;
                    break;

                case BasisSelectedHeightMode.Custom:
                    SelectedPlayerHeight = CustomPlayerEyeHeight;
                    SelectedAvatarHeight = CustomAvatarEyeHeight;

                    // Uses DefaultAvatarEyeHeight for player, DefaultPlayerEyeHeight for avatar.
                    SelectedPlayerToDefaultScale = SelectedPlayerHeight / BasisLocalPlayer.DefaultAvatarEyeHeight;
                    SelectedAvatarToAvatarDefaultScale = SelectedAvatarHeight / BasisLocalPlayer.DefaultPlayerEyeHeight;
                    break;
            }
        }

        /// <summary>
        /// Copies the core height fields and ratios from this instance into the target.
        /// </summary>
        /// <param name="ApplyTo">Target instance to receive copied values.</param>
        public void CopyTo(ref BasisLocalHeightInformation ApplyTo)
        {
            if (ApplyTo == null)
            {
                BasisDebug.Log("Missing Target Height Information");
                return;
            }

            ApplyTo.AvatarName = this.AvatarName;
            ApplyTo.PlayerEyeHeight = this.PlayerEyeHeight;
            ApplyTo.AvatarEyeHeight = this.AvatarEyeHeight;
            ApplyTo.EyeRatioPlayerToDefaultScale = this.EyeRatioPlayerToDefaultScale;
            ApplyTo.EyeRatioAvatarToAvatarDefaultScale = this.EyeRatioAvatarToAvatarDefaultScale;
            ApplyTo.ArmRatioPlayerToDefaultScale = this.ArmRatioPlayerToDefaultScale;
            ApplyTo.ArmRatioAvatarToAvatarDefaultScale = this.ArmRatioAvatarToAvatarDefaultScale;
            ApplyTo.SelectedAvatarHeight = this.SelectedAvatarHeight;
            ApplyTo.SelectedPlayerHeight = this.SelectedPlayerHeight;
            ApplyTo.CustomPlayerEyeHeight = this.CustomPlayerEyeHeight;
            ApplyTo.CustomAvatarEyeHeight = this.CustomAvatarEyeHeight;
        }
    }
}
