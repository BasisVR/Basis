using System;
using Basis.Scripts.Networking;
using Cilbox;
using Basis.Shims;

namespace Basis.Shims
{
	public enum BasisConnectionResult
	{
		Accepted = 0,
		Declined = 1,
		InvalidAddress = 2,
		Blocked = 3,
		NoInteraction = 4,
		Busy = 5,
		Unsupported = 6,
		NotConnected = 7,
	}

	public static class BasisConnectionShim
	{
		public static void RequestConnect(object self, string address, ushort port) => CilboxConnection.Connect(self, address, port, null, null);
		public static void RequestConnect(object self, string address, ushort port, string password) => CilboxConnection.Connect(self, address, port, password, null);
		public static void RequestConnect(object self, string address, ushort port, string password, Action<BasisConnectionResult> onResult) => CilboxConnection.Connect(self, address, port, password, onResult);
		public static void RequestDisconnect(object self) => CilboxConnection.Disconnect(self, null);
		public static void RequestDisconnect(object self, Action<BasisConnectionResult> onResult) => CilboxConnection.Disconnect(self, onResult);
	}
}

namespace Cilbox
{
	internal static class CilboxConnection
	{
		internal static bool IsEntryPoint(string name) => name == nameof(BasisConnectionShim.RequestConnect) || name == nameof(BasisConnectionShim.RequestDisconnect);
		internal static void Connect(object self, string address, ushort port, string password, Action<BasisConnectionResult> onResult)
		{
			CilboxProxy proxy = Running(self);
			BasisServerRequests.RequestConnect(address, port, password, proxy != null ? proxy.gameObject : null, Reply(proxy, onResult));
		}
		internal static void Disconnect(object self, Action<BasisConnectionResult> onResult)
		{
			CilboxProxy proxy = Running(self);
			BasisServerRequests.RequestDisconnect(proxy != null ? proxy.gameObject : null, Reply(proxy, onResult));
		}
		private static Action<BasisServerRequests.Result> Reply(CilboxProxy proxy, Action<BasisConnectionResult> onResult)
		{
			if (onResult == null) return null;
			bool running = proxy != null;
			return result =>
			{
				if (running && proxy == null) return;
				onResult((BasisConnectionResult)(int)result);
			};
		}
		private static CilboxProxy Running(object self)
		{
			if (!(self is CilboxProxy proxy) || proxy == null) return null;
			if (!(proxy.box is CilboxBasisCommon box) || box == null || box is CilboxAvatarBasis) return null;
			return box.interpreterAccountingDepth > 0 ? proxy : null;
		}
	}
}
