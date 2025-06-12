namespace Basis.Scripts.BasisSdk.Players
{
    [System.Serializable]
    public partial class BasisLocalHeightInformation
    {
        public string AvatarName;

        public float PlayerEyeHeight = BasisLocalPlayer.FallbackSize;
        public float AvatarEyeHeight = BasisLocalPlayer.FallbackSize;

        public float PlayerArmSpan = BasisLocalPlayer.FallbackSize;
        public float AvatarArmSpan = BasisLocalPlayer.FallbackSize;

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
            switch(Height)
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
            }
        }
        public void CopyTo(BasisLocalHeightInformation target)
        {
            if (target == null)
            {
                return;
            }
            else
            {
                BasisDebug.Log("Missing Target Height Information");
            }

            target.AvatarName = this.AvatarName;
            target.PlayerEyeHeight = this.PlayerEyeHeight;
            target.AvatarEyeHeight = this.AvatarEyeHeight;
            target.EyeRatioPlayerToDefaultScale = this.EyeRatioPlayerToDefaultScale;
            target.EyeRatioAvatarToAvatarDefaultScale = this.EyeRatioAvatarToAvatarDefaultScale;
            target.ArmRatioPlayerToDefaultScale = this.ArmRatioPlayerToDefaultScale;
            target.ArmRatioAvatarToAvatarDefaultScale = this.ArmRatioAvatarToAvatarDefaultScale;
            target.SelectedAvatarHeight = this.SelectedAvatarHeight;
            target.SelectedPlayerHeight = this.SelectedPlayerHeight;
        }
    }
}
