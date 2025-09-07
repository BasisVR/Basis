namespace Basis.Scripts.BasisSdk.Players
{
    [System.Serializable]
    public class BasisLocalHeightInformation
    {
        public string AvatarName;

        public float PlayerEyeHeight = BasisLocalPlayer.FallbackSize;
        public float AvatarEyeHeight = BasisLocalPlayer.FallbackSize;

        public float PlayerArmSpan = BasisLocalPlayer.FallbackSize;
        public float AvatarArmSpan = BasisLocalPlayer.FallbackSize;

        public float CustomPlayerEyeHeight = BasisLocalPlayer.FallbackSize;
        public float CustomAvatarEyeHeight = BasisLocalPlayer.FallbackSize;

        public float EyeRatioPlayerToDefaultScale = 1f;
        public float EyeRatioAvatarToAvatarDefaultScale = 1f; // should be used for the player

        public float ArmRatioPlayerToDefaultScale = 1f;
        public float ArmRatioAvatarToAvatarDefaultScale = 1f; // should be used for the player

        public float SelectedPlayerHeight = BasisLocalPlayer.FallbackSize;
        public float SelectedAvatarHeight = BasisLocalPlayer.FallbackSize;

        public float SelectedPlayerToDefaultScale = 1f;
        public float SelectedAvatarToAvatarDefaultScale = 1f;
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

                    SelectedPlayerToDefaultScale = SelectedPlayerHeight / BasisLocalPlayer.DefaultAvatarEyeHeight;
                    SelectedAvatarToAvatarDefaultScale = SelectedAvatarHeight / BasisLocalPlayer.DefaultPlayerEyeHeight;
                    break;
            }
        }
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
