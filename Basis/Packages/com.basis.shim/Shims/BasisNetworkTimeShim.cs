using System;
using Basis.Scripts.Networking;

namespace Basis.Shims
{
	public static class BasisNetworkTimeShim
	{
		public static DateTime ServerUtcNow => BasisNetworkManagement.RemoteUtcTime();
		public static long ServerUtcTicks => BasisNetworkManagement.RemoteUtcTime().Ticks;
		public static double ServerTimeSeconds => BasisNetworkManagement.RemoteUtcTime().Ticks / (double)TimeSpan.TicksPerSecond;
		public static int ServerTimeMilliseconds => unchecked((int)(BasisNetworkManagement.RemoteUtcTime().Ticks / TimeSpan.TicksPerMillisecond));
	}
}
