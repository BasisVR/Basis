using UnityEngine;

namespace Basis.Scripts.BasisSdk.Players
{
    /// <summary>
    /// Serializable container for player/avatar height metrics and scale ratios.
    /// Tracks measured eye heights and arm spans for both the user (player) and the avatar,
    /// along with precomputed scale ratios and the currently selected height/scale pair.
    /// </summary>
    /// <remarks>
    /// Use <see cref="PickHeightMode(BasisSelectedHeightMode)"/> to populate the
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
        private float eyeRatioPlayerToDefaultScale = 1f;

        /// <summary>
        /// Ratio mapping the avatar's measured eye height to the avatar's default reference scale.
        /// </summary>
        private float eyeRatioAvatarToAvatarDefaultScale = 1f; // should be used for the player

        /// <summary>
        /// Ratio mapping the player's measured arm span to a default reference scale.
        /// </summary>
        private float armRatioPlayerToDefaultScale = 1f;

        /// <summary>
        /// Ratio mapping the avatar's measured arm span to the avatar's default reference scale.
        /// </summary>
        private float armRatioAvatarToAvatarDefaultScale = 1f; // should be used for the player

        /// <summary>
        /// The player height (meters) currently selected by <see cref="PickHeightMode(BasisSelectedHeightMode)"/>.
        /// </summary>
        private float selectedPlayerHeight = BasisLocalPlayer.FallbackSize;

        /// <summary>
        /// The avatar height (meters) currently selected by <see cref="PickHeightMode(BasisSelectedHeightMode)"/>.
        /// </summary>
        private float selectedAvatarHeight = BasisLocalPlayer.FallbackSize;

        /// <summary>
        /// The player-to-default scale currently selected by <see cref="PickHeightMode(BasisSelectedHeightMode)"/>.
        /// </summary>
        private float selectedPlayerToDefaultScale = 1f;

        /// <summary>
        /// The avatar-to-avatar-default scale currently selected by <see cref="PickHeightMode(BasisSelectedHeightMode)"/>.
        /// </summary>
        private float selectedAvatarToAvatarDefaultScale = 1f;

        public float SelectedAvatarToAvatarDefaultScale { get => selectedAvatarToAvatarDefaultScale; private set => selectedAvatarToAvatarDefaultScale = value; }
        public float SelectedPlayerToDefaultScale { get => selectedPlayerToDefaultScale; private set => selectedPlayerToDefaultScale = value; }
        public float SelectedAvatarHeight { get => selectedAvatarHeight; private set => selectedAvatarHeight = value; }
        public float SelectedPlayerHeight { get => selectedPlayerHeight; private set => selectedPlayerHeight = value; }
        public float ArmRatioAvatarToAvatarDefaultScale { get => armRatioAvatarToAvatarDefaultScale; private set => armRatioAvatarToAvatarDefaultScale = value; }
        public float ArmRatioPlayerToDefaultScale { get => armRatioPlayerToDefaultScale; private set => armRatioPlayerToDefaultScale = value; }
        public float EyeRatioAvatarToAvatarDefaultScale { get => eyeRatioAvatarToAvatarDefaultScale; private set => eyeRatioAvatarToAvatarDefaultScale = value; }
        public float EyeRatioPlayerToDefaultScale { get => eyeRatioPlayerToDefaultScale; private set => eyeRatioPlayerToDefaultScale = value; }

        /// <summary>
        /// Chooses the active height metrics and scale ratios based on the provided mode.
        /// </summary>
        /// <param name="Height">Selection mode: <see cref="BasisSelectedHeightMode.ArmSpan"/>,
        /// <see cref="BasisSelectedHeightMode.EyeHeight"/>, or <see cref="BasisSelectedHeightMode.Custom"/>.</param>
        public void PickHeightMode(BasisSelectedHeightMode Height)
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
            BasisDebug.Log($"Height Mode is {Height} with height {SelectedPlayerHeight} with avatar height {SelectedAvatarHeight} with selected player to default scale {SelectedPlayerToDefaultScale} select avatar to avatar scale {SelectedAvatarToAvatarDefaultScale}", BasisDebug.LogTag.System);
        }
        public static void ComputeRatios()
        {
            BasisDebug.Log($"Computed Ratios", BasisDebug.LogTag.System);
            var localPlayer = BasisLocalPlayer.Instance;
            // ---- compute ratios safely ----
            localPlayer.CurrentHeight.EyeRatioAvatarToAvatarDefaultScale = localPlayer.CurrentHeight.AvatarEyeHeight / Mathf.Max(0.0001f, BasisLocalPlayer.DefaultAvatarEyeHeight);

            localPlayer.CurrentHeight.EyeRatioPlayerToDefaultScale = localPlayer.CurrentHeight.PlayerEyeHeight / Mathf.Max(0.0001f, BasisLocalPlayer.DefaultPlayerEyeHeight);

            localPlayer.CurrentHeight.ArmRatioAvatarToAvatarDefaultScale = localPlayer.CurrentHeight.AvatarArmSpan / Mathf.Max(0.0001f, BasisLocalPlayer.DefaultAvatarArmSpan);

            localPlayer.CurrentHeight.ArmRatioPlayerToDefaultScale = localPlayer.CurrentHeight.PlayerArmSpan / Mathf.Max(0.0001f, BasisLocalPlayer.DefaultPlayerArmSpan);
        }
    }
}
