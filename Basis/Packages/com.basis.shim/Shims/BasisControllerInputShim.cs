using Basis.BasisUI;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Shims
{
	public enum BasisControllerHand
	{
		Left = 0,
		Right = 1,
	}

	public enum BasisControllerButton
	{
		Trigger = 0,
		Grip = 1,
		Primary = 2,
		Secondary = 3,
		StickClick = 4,
		Menu = 5,
	}

	/// <summary>
	/// Read-only VR controller input for world scripts. Reads the same per-hand state that drives the
	/// player (BasisActionDriver.TryGetRoleState: every device holding the hand, merged), so device
	/// overrides that ignore buttons or sticks apply here too. Everything reads released and centred
	/// while the menu is open or a pointer is over UI, so clicking the menu never fires the world.
	/// Desktop players have no hands: IsAvailable is false and every read is neutral.
	/// </summary>
	public static class BasisControllerInputShim
	{
		public const float TriggerPressThreshold = 0.5f;

		private sealed class HandState
		{
			public readonly BasisInputState Current = new BasisInputState();
			public readonly BasisInputState Last = new BasisInputState();
			public bool Held;
			public int Frame = -1;
		}

		[NoAutoStaticsCleanup] private static readonly HandState[] Hands = { new HandState(), new HandState() };

		public static bool IsBlocked => BasisMainMenu.Instance != null || (BasisInputModuleHandler.Instance != null && BasisInputModuleHandler.Instance.HasHoverONInput);

		public static bool IsAvailable(BasisControllerHand hand) => Read(hand) != null;

		public static float GetTrigger(BasisControllerHand hand)
		{
			HandState state = Live(hand);
			return state != null ? Mathf.Clamp01(state.Current.Trigger) : 0f;
		}

		public static Vector2 GetStick(BasisControllerHand hand)
		{
			HandState state = Live(hand);
			return state != null ? state.Current.Primary2DAxisDeadZoned : Vector2.zero;
		}

		public static bool GetButton(BasisControllerHand hand, BasisControllerButton button)
		{
			HandState state = Live(hand);
			return state != null && Pressed(state.Current, button);
		}

		public static bool GetButtonDown(BasisControllerHand hand, BasisControllerButton button)
		{
			HandState state = Live(hand);
			return state != null && Pressed(state.Current, button) && !Pressed(state.Last, button);
		}

		public static bool GetButtonUp(BasisControllerHand hand, BasisControllerButton button)
		{
			HandState state = Live(hand);
			return state != null && !Pressed(state.Current, button) && Pressed(state.Last, button);
		}

		private static HandState Live(BasisControllerHand hand) => IsBlocked ? null : Read(hand);

		private static HandState Read(BasisControllerHand hand)
		{
			if (hand < BasisControllerHand.Left || hand > BasisControllerHand.Right) return null;
			HandState state = Hands[(int)hand];
			int frame = Time.frameCount;
			if (state.Frame != frame)
			{
				state.Frame = frame;
				BasisBoneTrackedRole role = hand == BasisControllerHand.Left ? BasisBoneTrackedRole.LeftHand : BasisBoneTrackedRole.RightHand;
				state.Held = BasisActionDriver.TryGetRoleState(role, state.Current, state.Last);
			}
			return state.Held ? state : null;
		}

		private static bool Pressed(BasisInputState input, BasisControllerButton button)
		{
			switch (button)
			{
				case BasisControllerButton.Trigger: return input.Trigger >= TriggerPressThreshold;
				case BasisControllerButton.Grip: return input.GripButton;
				case BasisControllerButton.Primary: return input.PrimaryButtonGetState;
				case BasisControllerButton.Secondary: return input.SecondaryButtonGetState;
				case BasisControllerButton.StickClick: return input.Primary2DAxisClick;
				case BasisControllerButton.Menu: return input.SystemOrMenuButton;
				default: return false;
			}
		}
	}
}
