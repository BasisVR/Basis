using UnityEngine;

namespace Basis.Shims
{
	/// <summary>
	/// Mouse and VR controller reads for props and avatars. Pass this: only the player who spawned the
	/// prop or wears the avatar is read, every other copy reads nothing. Worlds may use it too.
	/// </summary>
	public static class BasisLocalInputShim
	{
		public static bool HasMouse => BasisDesktopInputShim.HasMouse;
		public static bool IsBlocked => BasisControllerInputShim.IsBlocked;

		public static Vector2 GetMouseDelta(object self) => Cilbox.CilboxInputRequests.Local(self) ? BasisDesktopInputShim.MouseDelta : Vector2.zero;
		public static Vector2 GetMouseScroll(object self) => Cilbox.CilboxInputRequests.Local(self) ? BasisDesktopInputShim.MouseScroll : Vector2.zero;
		public static Vector2 GetMousePosition(object self) => Cilbox.CilboxInputRequests.Local(self) ? BasisDesktopInputShim.MousePosition : Vector2.zero;
		public static bool GetMouseButton(object self, int button) => Cilbox.CilboxInputRequests.Local(self) && BasisDesktopInputShim.GetMouseButton(button);
		public static bool GetMouseButtonDown(object self, int button) => Cilbox.CilboxInputRequests.Local(self) && BasisDesktopInputShim.GetMouseButtonDown(button);
		public static bool GetMouseButtonUp(object self, int button) => Cilbox.CilboxInputRequests.Local(self) && BasisDesktopInputShim.GetMouseButtonUp(button);

		public static bool IsAvailable(object self, BasisControllerHand hand) => Cilbox.CilboxInputRequests.Local(self) && BasisControllerInputShim.IsAvailable(hand);
		public static float GetTrigger(object self, BasisControllerHand hand) => Cilbox.CilboxInputRequests.Local(self) ? BasisControllerInputShim.GetTrigger(hand) : 0f;
		public static Vector2 GetStick(object self, BasisControllerHand hand) => Cilbox.CilboxInputRequests.Local(self) ? BasisControllerInputShim.GetStick(hand) : Vector2.zero;
		public static bool GetButton(object self, BasisControllerHand hand, BasisControllerButton button) => Cilbox.CilboxInputRequests.Local(self) && BasisControllerInputShim.GetButton(hand, button);
		public static bool GetButtonDown(object self, BasisControllerHand hand, BasisControllerButton button) => Cilbox.CilboxInputRequests.Local(self) && BasisControllerInputShim.GetButtonDown(hand, button);
		public static bool GetButtonUp(object self, BasisControllerHand hand, BasisControllerButton button) => Cilbox.CilboxInputRequests.Local(self) && BasisControllerInputShim.GetButtonUp(hand, button);
	}
}
