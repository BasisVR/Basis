using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.Shims
{
	public static class BasisVelocityShim
	{
		public static Vector3 Velocity => BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.LocalCharacterDriver.Velocity : Vector3.zero;
		public static void SetVelocity(Vector3 velocity)
		{
			if (BasisLocalPlayer.Instance != null) BasisLocalPlayer.Instance.LocalCharacterDriver.SetVelocity(velocity);
		}
		public static void AddVelocity(Vector3 velocity)
		{
			if (BasisLocalPlayer.Instance != null) BasisLocalPlayer.Instance.LocalCharacterDriver.AddVelocity(velocity);
		}
	}
}
