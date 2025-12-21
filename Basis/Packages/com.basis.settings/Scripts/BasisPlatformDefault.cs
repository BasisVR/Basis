using UnityEngine;

[System.Serializable]
public class BasisPlatformDefault<T>
{
    public T windows;
    public T android;
    public T ios;
    public T linux;
    public T other;

    public T GetDefault()
    {
        switch (Application.platform)
        {
            case RuntimePlatform.WindowsPlayer:
            case RuntimePlatform.WindowsEditor:
                return windows;
            case RuntimePlatform.Android:
                return android;
            case RuntimePlatform.IPhonePlayer:
                return ios;
            case RuntimePlatform.LinuxPlayer:
            case RuntimePlatform.LinuxEditor:
                return linux;
            default:
                return other;
        }
    }

    public BasisPlatformDefault()
    {
    }

    public BasisPlatformDefault(T defaultAll)
    {
        windows = defaultAll;
        android = defaultAll;
        ios = defaultAll;
        linux = defaultAll;
        other = defaultAll;
    }

}
