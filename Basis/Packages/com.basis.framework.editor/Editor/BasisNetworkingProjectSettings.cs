#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[FilePath("ProjectSettings/BasisNetworkingSettings.asset", FilePathAttribute.Location.ProjectFolder)]
public class BasisNetworkingProjectSettings : ScriptableSingleton<BasisNetworkingProjectSettings>
{
    public const string DefaultLocalNetworkUsageDescription =
        "Basis connects to servers, avatars and media hosted on your local network.";

    [SerializeField] private bool allowLocalHttp = true;
    [SerializeField] private string localNetworkUsageDescription = DefaultLocalNetworkUsageDescription;

    public bool AllowLocalHttp => allowLocalHttp;

    public string LocalNetworkUsageDescription =>
        string.IsNullOrWhiteSpace(localNetworkUsageDescription)
            ? DefaultLocalNetworkUsageDescription
            : localNetworkUsageDescription;

    public InsecureHttpOption DesiredInsecureHttpOption =>
        allowLocalHttp ? InsecureHttpOption.AlwaysAllowed : InsecureHttpOption.NotAllowed;

    public void SetAllowLocalHttp(bool value)
    {
        if (allowLocalHttp == value) return;
        allowLocalHttp = value;
        Save(true);
    }

    public void SetLocalNetworkUsageDescription(string value)
    {
        if (localNetworkUsageDescription == value) return;
        localNetworkUsageDescription = value;
        Save(true);
    }

    public void ApplyToPlayerSettings()
    {
        InsecureHttpOption desired = DesiredInsecureHttpOption;
        if (PlayerSettings.insecureHttpOption == desired) return;
        PlayerSettings.insecureHttpOption = desired;
    }
}
#endif
