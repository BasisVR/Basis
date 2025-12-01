using Basis.Scripts.BasisSdk.Players;

public class SMModuleSitStand : BasisSettingsBase
{
    public static bool IsSteatedMode = false;
    public static float MissingHeightDelta = 0;
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        switch (optionValue)
        {
            case "Seated Mode":
                if (IsSteatedMode == false)
                {
                    BasisHeightDriver.CapturePlayerHeight(BasisLocalPlayer.Instance);
                    MissingHeightDelta = BasisLocalHeight.DefaultPlayerEyeHeight - BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight;
                    IsSteatedMode = true;
                }
                break;
            case "Standing Mode":
                MissingHeightDelta = 0;
                IsSteatedMode = false;
                break;
        }
    }
}
