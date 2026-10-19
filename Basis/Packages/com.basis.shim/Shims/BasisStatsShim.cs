using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using Basis.Network.Core;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Cilbox;
using Basis.Shims;

namespace Basis.Shims
{
	public static class BasisStatsShim
	{
		public const int BottleneckMeasuring = 0;
		public const int BottleneckCpu = 1;
		public const int BottleneckGpu = 2;
		public const int BottleneckBalanced = 3;
		public const int BottleneckFrameCap = 4;
		public const int BottleneckUnknown = 5;
		public static float FramesPerSecond => CilboxStats.FramesPerSecond();
		public static float FrameTimeMs => CilboxStats.FrameTimeMs();
		public static float TargetFramesPerSecond => CilboxStats.TargetFramesPerSecond();
		public static float CpuFrameTimeMs => (float)CilboxStats.Timing().CpuBusyMs;
		public static float GpuFrameTimeMs => (float)CilboxStats.Timing().GpuMs;
		public static int Bottleneck => CilboxStats.Bottleneck();
		public static bool IsConnected => CilboxStats.Peer() != null;
		public static int RoundTripTimeMs => CilboxStats.Peer() is NetPeer peer ? peer.RoundTripTime : -1;
		public static float SecondsSinceLastPacket => CilboxStats.Peer() is NetPeer peer ? peer.TimeSinceLastPacket : -1f;
		public static int PlayerLimit => CilboxStats.Peer() != null ? BasisNetworkManagement.ServerMetaDataMessage.PeerLimit : 0;
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxStats
	{
		internal const double IdleSeconds = 5d;
		private static bool leased;
		private static int tickFrame = int.MinValue, timingFrame = int.MinValue;
		private static double clockRead = double.NegativeInfinity, timingRead = double.NegativeInfinity;
		private static BasisFrameBottleneckReading timing;
		internal static float FramesPerSecond()
		{
			clockRead = Lease();
			if (tickFrame >= Time.frameCount - 1) return BasisFrameClock.SmoothedFramesPerSecond;
			float delta = Time.unscaledDeltaTime;
			return delta > 0f ? 1f / delta : 0f;
		}
		internal static float FrameTimeMs()
		{
			float fps = FramesPerSecond();
			return fps > 0f ? 1000f / fps : 0f;
		}
		internal static float TargetFramesPerSecond()
		{
			double target = Timing().TargetMs;
			return target > 0d ? (float)(1000d / target) : 0f;
		}
		internal static int Bottleneck() => Timing().Kind switch
		{
			BasisFrameBottleneckKind.Cpu => BasisStatsShim.BottleneckCpu,
			BasisFrameBottleneckKind.Gpu => BasisStatsShim.BottleneckGpu,
			BasisFrameBottleneckKind.Balanced => BasisStatsShim.BottleneckBalanced,
			BasisFrameBottleneckKind.FrameCap => BasisStatsShim.BottleneckFrameCap,
			BasisFrameBottleneckKind.NoGpuTimer => BasisStatsShim.BottleneckUnknown,
			_ => BasisStatsShim.BottleneckMeasuring,
		};
		internal static BasisFrameBottleneckReading Timing()
		{
			timingRead = Lease();
			if (timingFrame < Time.frameCount - 1) Sample();
			return timing;
		}
		internal static NetPeer Peer() => BasisNetworkConnection.LocalPlayerIsConnected ? BasisNetworkConnection.LocalPlayerPeer : null;
		internal static void Release()
		{
			if (!leased) return;
			leased = false;
			BasisFrameClock.OnTick -= Tick;
			BasisFrameClock.RemoveRequest();
		}
		private static double Lease()
		{
			if (!leased)
			{
				leased = true;
				BasisFrameClock.OnTick += Tick;
				BasisFrameClock.AddRequest();
			}
			return Time.realtimeSinceStartupAsDouble;
		}
		private static void Sample()
		{
			timingFrame = Time.frameCount;
			BasisFrameBottleneck.Sample();
			timing = BasisFrameBottleneck.Read(timing.Kind);
		}
		private static void Tick()
		{
			tickFrame = Time.frameCount;
			double now = Time.realtimeSinceStartupAsDouble;
			bool timed = now - timingRead < IdleSeconds;
			if (timed) Sample();
			else if (now - clockRead >= IdleSeconds) Release();
		}
	}
}
