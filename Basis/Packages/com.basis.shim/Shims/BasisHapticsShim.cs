using Basis.BasisUI;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using Cilbox;
using Basis.Shims;

namespace Basis.Shims
{
	public enum BasisHapticHand
	{
		Left = 0,
		Right = 1,
		Both = 2,
	}

	/// <summary>
	/// Vibrates the local player's controllers. Pass <c>this</c>. Nothing else can be reached: no
	/// other player's controllers, no trackers. Strength is clamped to 0..1, a pulse lasts at most
	/// 1 s, and each content gets 10 pulses a second. Avatars may only buzz the player wearing
	/// them. Returns false when nothing was played, including when the player has UI Haptics off.
	/// </summary>
	public static class BasisHapticsShim
	{
		public const float MaxSeconds = 1f;
		public const int MaxPulsesPerSecond = 10;
		public static bool Play(object self, BasisHapticHand hand) => CilboxHaptics.Play(self, hand, 0.5f, 0.1f);
		public static bool Play(object self, BasisHapticHand hand, float strength, float seconds) => CilboxHaptics.Play(self, hand, strength, seconds);
	}
}

namespace Cilbox
{
	internal static class CilboxHaptics
	{
		private const float MinSeconds = 0.01f;
		internal static bool IsEntryPoint(string name) => name == nameof(BasisHapticsShim.Play);
		internal static bool Play(object self, BasisHapticHand hand, float strength, float seconds)
		{
			if (!(self is CilboxProxy proxy) || proxy == null || !(proxy.box is CilboxBasisCommon box) || box == null || box.disabled || box.interpreterAccountingDepth == 0) return false;
			if (hand < BasisHapticHand.Left || hand > BasisHapticHand.Both || float.IsNaN(strength) || float.IsNaN(seconds) || strength <= 0f || seconds <= 0f) return false;
			if (!BasisSettingsDefaults.UIHaptics.RawValue || !CilboxBasisConsent.ForLocalPlayer(box, proxy.gameObject) || !Spend(box)) return false;
			BasisDeviceManagement devices = BasisDeviceManagement.Instance;
			if (devices == null) return false;
			float amplitude = Mathf.Clamp01(strength), duration = Mathf.Clamp(seconds, MinSeconds, BasisHapticsShim.MaxSeconds);
			bool played = false;
			if (hand != BasisHapticHand.Right) played |= Pulse(devices, BasisBoneTrackedRole.LeftHand, amplitude, duration);
			if (hand != BasisHapticHand.Left) played |= Pulse(devices, BasisBoneTrackedRole.RightHand, amplitude, duration);
			return played;
		}
		private static bool Pulse(BasisDeviceManagement devices, BasisBoneTrackedRole role, float amplitude, float duration)
		{
			if (!devices.FindDevice(out BasisInput input, role) || input == null) return false;
			input.PlayHaptic(duration, amplitude);
			return true;
		}
		private static bool Spend(CilboxBasisCommon box)
		{
			double now = Time.realtimeSinceStartupAsDouble;
			if (now - box.hapticWindowStart >= 1d)
			{
				box.hapticWindowStart = now;
				box.hapticWindowPulses = 0;
			}
			if (box.hapticWindowPulses >= BasisHapticsShim.MaxPulsesPerSecond) return false;
			box.hapticWindowPulses++;
			return true;
		}
	}
}
