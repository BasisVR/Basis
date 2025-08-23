using UnityEngine;

[System.Serializable]
public class BasisPlatformDefault<T>
{
    public T windows;
    public T android;
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
            case RuntimePlatform.LinuxPlayer:
            case RuntimePlatform.LinuxEditor:
                return linux;
            default:
                return other;
        }
    }
}
