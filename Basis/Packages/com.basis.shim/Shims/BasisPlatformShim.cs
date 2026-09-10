using Basis.Scripts.Device_Management;

namespace Basis.Shims
{
	public static class BasisPlatformShim
	{
		public static string CurrentMode => BasisPlatformDetection.CurrentMode;
		public static string Platform => BasisPlatformDetection.Platform;
		public static bool IsVR => BasisPlatformDetection.IsVR;
		public static bool IsDesktop => BasisPlatformDetection.IsDesktop;
		public static bool IsMobileGpu => BasisPlatformDetection.IsMobileGpu;
		public static bool IsHeadsetWorn => BasisPlatformDetection.IsHeadsetWorn;
		public static string[] Conditions => (string[])BasisPlatformDetection.ConditionNames.Clone();
		public static bool IsDetected( string condition ) => BasisPlatformDetection.TryParse( condition, out BasisPlatformCondition parsed ) && BasisPlatformDetection.IsDetected( parsed );
		public static bool IsStaticCondition( string condition ) => BasisPlatformDetection.TryParse( condition, out BasisPlatformCondition parsed ) && BasisPlatformDetection.IsStatic( parsed );
	}
}
