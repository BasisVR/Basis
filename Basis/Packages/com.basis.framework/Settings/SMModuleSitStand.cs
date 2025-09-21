using Basis.Scripts.BasisSdk.Players;

public class SMModuleSitStand : BasisSettingsBase
{
    public static bool IsSteatedMode = false;
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        switch (optionValue)
        {
            case "Seated Mode":
                if (IsSteatedMode == false)
                {
                    BasisHeightDriver.ChangeEyeHeightMode(BasisLocalPlayer.Instance, BasisSelectedHeightMode.EyeHeight);
                    IsSteatedMode = true;
                }
                break;
            case "Standing Mode":
                if (IsSteatedMode == true)
                {
                    BasisHeightDriver.ChangeEyeHeightMode(BasisLocalPlayer.Instance, BasisSelectedHeightMode.EyeHeight);
                    IsSteatedMode = false;
                }
                break;
        }
    }
}
