namespace Basis.Scripts.Device_Management.EyeTracking
{
    public interface IBasisEyeTrackingProvider
    {
        BasisEyeSource Source { get; }
        string DisplayName { get; }
        bool IsActive { get; }
        bool TryGetEyeData(ref BasisEyeTrackingData data);
    }
}
