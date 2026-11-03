using Basis.Scripts.Common;
using UnityEngine;

namespace Basis.Scripts.Settings
{
[System.Serializable]
public class BasisPlatformDefault<T>
{
    public T windows;
    public T android;
    public T ios;
    public T linux;
    public T other;
    private T lowEndValue;
    private bool hasLowEnd;

    /// <summary>Optional default for desktop-class low-end GPUs (<see cref="BasisGpuDetection.IsLowEndDesktop"/>); unset falls through to the platform column.</summary>
    public T lowEnd { get => lowEndValue; set { lowEndValue = value; hasLowEnd = true; } }

    public bool HasLowEnd => hasLowEnd;

    public T GetDefault()
    {
        switch (Application.platform)
        {
            case RuntimePlatform.Android:
                return android;
            case RuntimePlatform.IPhonePlayer:
                return ios;
        }

        if (BasisGpuDetection.IsMobileGpu)
        {
            return android;
        }

        if (hasLowEnd && BasisGpuDetection.IsLowEndDesktop)
        {
            return lowEndValue;
        }

        switch (Application.platform)
        {
            case RuntimePlatform.WindowsPlayer:
            case RuntimePlatform.WindowsEditor:
                return windows;
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
}
