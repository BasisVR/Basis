using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Basis.Shims
{
	public static class BasisDesktopInputShim
	{
		public static bool HasMouse => Mouse.current != null;
		public static Vector2 MouseDelta => Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
		public static Vector2 MouseScroll => Mouse.current != null ? Mouse.current.scroll.ReadValue() : Vector2.zero;
		public static Vector2 MousePosition => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;

		public static bool GetMouseButton(int button)
		{
			ButtonControl control = Button(button);
			return control != null && control.isPressed;
		}

		public static bool GetMouseButtonDown(int button)
		{
			ButtonControl control = Button(button);
			return control != null && control.wasPressedThisFrame;
		}

		public static bool GetMouseButtonUp(int button)
		{
			ButtonControl control = Button(button);
			return control != null && control.wasReleasedThisFrame;
		}

		public static Vector2 GetMouseDelta(object self) => Cilbox.CilboxInputRequests.Local(self) ? MouseDelta : Vector2.zero;
		public static Vector2 GetMouseScroll(object self) => Cilbox.CilboxInputRequests.Local(self) ? MouseScroll : Vector2.zero;
		public static Vector2 GetMousePosition(object self) => Cilbox.CilboxInputRequests.Local(self) ? MousePosition : Vector2.zero;
		public static bool GetMouseButton(object self, int button) => Cilbox.CilboxInputRequests.Local(self) && GetMouseButton(button);
		public static bool GetMouseButtonDown(object self, int button) => Cilbox.CilboxInputRequests.Local(self) && GetMouseButtonDown(button);
		public static bool GetMouseButtonUp(object self, int button) => Cilbox.CilboxInputRequests.Local(self) && GetMouseButtonUp(button);

		static ButtonControl Button(int button)
		{
			Mouse mouse = Mouse.current;
			if (mouse == null)
			{
				return null;
			}
			switch (button)
			{
				case 0: return mouse.leftButton;
				case 1: return mouse.rightButton;
				case 2: return mouse.middleButton;
				case 3: return mouse.backButton;
				case 4: return mouse.forwardButton;
				default: return null;
			}
		}
	}
}
