using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Shims;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Scripting;

namespace Cilbox
{
	/// <summary>
	/// Prop and avatar scripts built before the self overloads call the desktop and controller
	/// shims without passing this. Those calls resolve here instead and read nothing unless the
	/// box running the call belongs to the local player (spawned the prop, wears the avatar).
	/// </summary>
	[AutoStaticsCleanup]
	internal static partial class CilboxInputLegacy
	{
		private const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;
		private static readonly List<CilboxBasisCommon> boxes = new List<CilboxBasisCommon>();
		private static bool warned;

		internal static void Note(CilboxBasisCommon box)
		{
			if (box.inputNoted) return;
			box.inputNoted = true;
			boxes.Add(box);
		}

		internal static void Forget(CilboxBasisCommon box)
		{
			if (!box.inputNoted) return;
			box.inputNoted = false;
			boxes.Remove(box);
		}

		internal static MethodInfo Resolve(CilboxUsage usage, Type declaringType, string name, SerializedTypeDescriptor[] parametersIn)
		{
			Type host = declaringType == typeof(BasisDesktopInputShim) ? typeof(Desktop) : declaringType == typeof(BasisControllerInputShim) ? typeof(Controller) : null;
			if (host == null) return null;
			try
			{
				Type[] parameters = usage.DescriptorsToArrayOfNativeTypes(parametersIn);
				for (int i = 0; i < parameters.Length; i++)
				{
					if (parameters[i] == null) return null;
				}
				return host.GetMethod(name, Statics, null, parameters, null);
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static bool Local()
		{
			CilboxBasisCommon running = null;
			for (int i = boxes.Count - 1; i >= 0; i--)
			{
				CilboxBasisCommon box = boxes[i];
				if (box == null)
				{
					boxes.RemoveAt(i);
					continue;
				}
				if (box.disabled || box.interpreterAccountingDepth == 0) continue;
				if (running != null)
				{
					Refused(null, "two scripts were running at once, so Basis could not tell which one asked");
					return false;
				}
				running = box;
			}
			if (running == null)
			{
				Refused(null, "no running script was found for the call");
				return false;
			}
			if (CilboxBasisConsent.ForLocalPlayer(running, running.gameObject)) return true;
			Refused(running, CilboxBasisConsent.TryOwner(running, running.gameObject, out BasisContentBase content) ? $"its content ({content.GetType().Name} {content.name}) does not belong to the local player" : "Basis could not find the content it belongs to");
			return false;
		}

		private static void Refused(CilboxBasisCommon box, string reason)
		{
			if (box != null ? box.inputWarned : warned) return;
			if (box != null) box.inputWarned = true;
			else warned = true;
			Debug.LogWarning($"[Cilbox] Input read without passing this returned nothing for {(box != null ? CilboxBasisConsent.NameOf(box) : "a script")}: {reason}.");
		}

		[Preserve]
		private static class Desktop
		{
			[Preserve] internal static Vector2 get_MouseDelta() => Local() ? BasisDesktopInputShim.MouseDelta : Vector2.zero;
			[Preserve] internal static Vector2 get_MouseScroll() => Local() ? BasisDesktopInputShim.MouseScroll : Vector2.zero;
			[Preserve] internal static Vector2 get_MousePosition() => Local() ? BasisDesktopInputShim.MousePosition : Vector2.zero;
			[Preserve] internal static bool GetMouseButton(int button) => Local() && BasisDesktopInputShim.GetMouseButton(button);
			[Preserve] internal static bool GetMouseButtonDown(int button) => Local() && BasisDesktopInputShim.GetMouseButtonDown(button);
			[Preserve] internal static bool GetMouseButtonUp(int button) => Local() && BasisDesktopInputShim.GetMouseButtonUp(button);
		}

		[Preserve]
		private static class Controller
		{
			[Preserve] internal static bool IsAvailable(BasisControllerHand hand) => Local() && BasisControllerInputShim.IsAvailable(hand);
			[Preserve] internal static float GetTrigger(BasisControllerHand hand) => Local() ? BasisControllerInputShim.GetTrigger(hand) : 0f;
			[Preserve] internal static Vector2 GetStick(BasisControllerHand hand) => Local() ? BasisControllerInputShim.GetStick(hand) : Vector2.zero;
			[Preserve] internal static bool GetButton(BasisControllerHand hand, BasisControllerButton button) => Local() && BasisControllerInputShim.GetButton(hand, button);
			[Preserve] internal static bool GetButtonDown(BasisControllerHand hand, BasisControllerButton button) => Local() && BasisControllerInputShim.GetButtonDown(hand, button);
			[Preserve] internal static bool GetButtonUp(BasisControllerHand hand, BasisControllerButton button) => Local() && BasisControllerInputShim.GetButtonUp(hand, button);
		}
	}
}
