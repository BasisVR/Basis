using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.EventDriver;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI;
using Basis.Shims;
using Cilbox;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Basis.Shims
{
	public enum BasisInputMouseAxis
	{
		DeltaX = 0,
		DeltaY = 1,
		ScrollX = 2,
		ScrollY = 3,
		PositionX = 4,
		PositionY = 5,
	}

	public enum BasisInputGamepadControl
	{
		South = 0,
		East = 1,
		West = 2,
		North = 3,
		LeftShoulder = 4,
		RightShoulder = 5,
		LeftStickPress = 6,
		RightStickPress = 7,
		Start = 8,
		Select = 9,
		DpadUp = 10,
		DpadDown = 11,
		DpadLeft = 12,
		DpadRight = 13,
		LeftTrigger = 14,
		RightTrigger = 15,
		LeftStickX = 16,
		LeftStickY = 17,
		RightStickX = 18,
		RightStickY = 19,
	}

	public enum BasisInputControllerAxis
	{
		Trigger = 0,
		StickX = 1,
		StickY = 2,
	}

	public static class BasisInputShim
	{
		public const int MaxSlots = 32;
		public const int MaxRequestsPerContent = 64;
		public const float PressThreshold = 0.5f;
		public static BasisInputRequest Request(object self) => CilboxInputRequests.Request(self);
	}

	public sealed class BasisInputRequest
	{
		private readonly CilboxInputLease lease;
		internal BasisInputRequest(CilboxInputLease lease) { this.lease = lease; }
		public int Count => lease.Count;
		public bool IsReleased => lease.Released;
		public bool Changed => lease.Changed;
		public int Held => lease.Held;
		public int AddKey(Key key) => lease.AddKey(key);
		public int AddMouseButton(int button) => lease.AddMouseButton(button);
		public int AddMouseAxis(BasisInputMouseAxis axis) => lease.AddMouseAxis(axis);
		public int AddGamepad(BasisInputGamepadControl control) => lease.AddGamepad(control);
		public int AddController(BasisControllerHand hand, BasisControllerButton button) => lease.AddController(hand, button);
		public int AddController(BasisControllerHand hand, BasisInputControllerAxis axis) => lease.AddController(hand, axis);
		public int AddControl(string path) => lease.AddControl(path);
		public bool IsBound(int slot) => lease.IsBound(slot);
		public float Get(int slot) => lease.Get(slot);
		public int Read(float[] values) => lease.Read(values);
		public int TakeDown() => lease.TakeDown();
		public int TakeUp() => lease.TakeUp();
		public void Release() => CilboxInputRequests.Release(lease);
	}
}

namespace Cilbox
{
	internal sealed class CilboxInputChannel
	{
		internal const int KindKey = 0, KindMouseButton = 1, KindMouseAxis = 2, KindGamepad = 3, KindHandButton = 4, KindHandAxis = 5, KindPath = 6;
		internal const int GateFocus = 0, GatePointer = 1, GateHand = 2;
		internal int kind, code, gate, index;
		internal string path;
		internal InputControl<float> control;
		internal bool isButton, held, waitRelease;
		internal float value;
		internal readonly List<CilboxInputLease> leases = new List<CilboxInputLease>();
		internal readonly List<int> slots = new List<int>();
		internal long Key => ((long)kind << 32) | (uint)code;
	}

	internal sealed class CilboxInputLease
	{
		internal readonly CilboxProxy owner;
		internal readonly CilboxBasisCommon box;
		internal readonly CilboxMethod callback;
		internal readonly BasisInputRequest handle;
		private readonly float[] values = new float[BasisInputShim.MaxSlots];
		private readonly CilboxInputChannel[] channels = new CilboxInputChannel[BasisInputShim.MaxSlots];
		private int pendingDown, pendingUp;
		internal int Count { get; private set; }
		internal int Held { get; private set; }
		internal bool Changed { get; private set; }
		internal bool Released { get; private set; }
		internal bool Queued;
		internal CilboxInputLease(CilboxProxy owner, CilboxBasisCommon box, CilboxMethod callback)
		{
			this.owner = owner;
			this.box = box;
			this.callback = callback;
			handle = new BasisInputRequest(this);
		}
		internal int AddKey(Key key)
		{
			if (key == UnityEngine.InputSystem.Key.None || !Enum.IsDefined(typeof(Key), key)) return -1;
			return Add(CilboxInputChannel.KindKey, (int)key, null);
		}
		internal int AddMouseButton(int button) => button < 0 || button > 4 ? -1 : Add(CilboxInputChannel.KindMouseButton, button, null);
		internal int AddMouseAxis(BasisInputMouseAxis axis) => axis < BasisInputMouseAxis.DeltaX || axis > BasisInputMouseAxis.PositionY ? -1 : Add(CilboxInputChannel.KindMouseAxis, (int)axis, null);
		internal int AddGamepad(BasisInputGamepadControl control) => control < BasisInputGamepadControl.South || control > BasisInputGamepadControl.RightStickY ? -1 : Add(CilboxInputChannel.KindGamepad, (int)control, null);
		internal int AddController(BasisControllerHand hand, BasisControllerButton button)
		{
			if (hand < BasisControllerHand.Left || hand > BasisControllerHand.Right || button < BasisControllerButton.Trigger || button > BasisControllerButton.Menu) return -1;
			return Add(CilboxInputChannel.KindHandButton, ((int)hand << 8) | (int)button, null);
		}
		internal int AddController(BasisControllerHand hand, BasisInputControllerAxis axis)
		{
			if (hand < BasisControllerHand.Left || hand > BasisControllerHand.Right || axis < BasisInputControllerAxis.Trigger || axis > BasisInputControllerAxis.StickY) return -1;
			return Add(CilboxInputChannel.KindHandAxis, ((int)hand << 8) | (int)axis, null);
		}
		internal int AddControl(string path) => string.IsNullOrEmpty(path) || path.Length > CilboxInputRequests.MaxPathLength ? -1 : Add(CilboxInputChannel.KindPath, 0, path);
		internal bool IsBound(int slot) => slot >= 0 && slot < Count && CilboxInputRequests.IsBound(channels[slot]);
		internal float Get(int slot) => slot >= 0 && slot < Count ? values[slot] : 0f;
		internal int Read(float[] into)
		{
			Changed = false;
			if (into == null) return Count;
			int copied = Math.Min(into.Length, Count);
			Array.Copy(values, into, copied);
			return copied;
		}
		internal int TakeDown()
		{
			int taken = pendingDown;
			pendingDown = 0;
			return taken;
		}
		internal int TakeUp()
		{
			int taken = pendingUp;
			pendingUp = 0;
			return taken;
		}
		internal void Publish(int slot, float value, bool held, bool flipped)
		{
			values[slot] = value;
			Changed = true;
			if (!flipped) return;
			int bit = 1 << slot;
			if (held)
			{
				Held |= bit;
				pendingDown |= bit;
			}
			else
			{
				Held &= ~bit;
				pendingUp |= bit;
			}
		}
		internal void DetachAll()
		{
			Released = true;
			for (int i = 0; i < Count; i++) CilboxInputRequests.Unsubscribe(channels[i], this, i);
			Count = 0;
			Held = pendingDown = pendingUp = 0;
			Changed = false;
			Array.Clear(values, 0, values.Length);
			Array.Clear(channels, 0, channels.Length);
		}
		private int Add(int kind, int code, string path)
		{
			if (Released) return -1;
			CilboxInputChannel channel = CilboxInputRequests.Channel(kind, code, path);
			for (int i = 0; i < Count; i++) if (ReferenceEquals(channels[i], channel)) return i;
			if (Count >= BasisInputShim.MaxSlots)
			{
				CilboxInputRequests.DropIfUnused(channel);
				return -1;
			}
			int slot = Count++;
			channels[slot] = channel;
			channel.leases.Add(this);
			channel.slots.Add(slot);
			values[slot] = channel.value;
			if (channel.held) Held |= 1 << slot;
			Changed = true;
			return slot;
		}
	}

	[AutoStaticsCleanup]
	internal static partial class CilboxInputRequests
	{
		internal const int MaxPathLength = 128;
		internal const float ChangeEpsilon = 0.0005f;
		internal const string CallbackName = "OnInputChanged";
		private static readonly List<CilboxInputLease> leases = new List<CilboxInputLease>();
		private static readonly List<CilboxInputChannel> channels = new List<CilboxInputChannel>();
		private static readonly Dictionary<long, CilboxInputChannel> byKey = new Dictionary<long, CilboxInputChannel>();
		private static readonly Dictionary<string, CilboxInputChannel> byPath = new Dictionary<string, CilboxInputChannel>();
		private static readonly List<CilboxInputLease> notify = new List<CilboxInputLease>();
		[NoAutoStaticsCleanup] private static readonly BasisInputState[] handCurrent = { new BasisInputState(), new BasisInputState() };
		[NoAutoStaticsCleanup] private static readonly BasisInputState[] handLast = { new BasisInputState(), new BasisInputState() };
		[NoAutoStaticsCleanup] private static readonly int[] handTick = new int[2];
		[NoAutoStaticsCleanup] private static readonly bool[] handHeld = new bool[2];
		[NoAutoStaticsCleanup] private static readonly Action tickHandler = Tick;
		[NoAutoStaticsCleanup] private static readonly Action<InputDevice, InputDeviceChange> deviceHandler = DeviceChanged;
		private static Keyboard keyboard;
		private static Mouse mouse;
		private static Gamepad gamepad;
		private static int tick;
		private static bool hooked, bindingsDirty;
		internal static int ChannelCount => channels.Count;
		internal static int LeaseCount => leases.Count;
		internal static BasisInputRequest Request(object self)
		{
			if (!(self is CilboxProxy proxy) || proxy == null || proxy.disabled || !(proxy.box is CilboxBasisCommon box) || box == null || box.disabled || box.interpreterAccountingDepth == 0) return null;
			int held = 0;
			for (int i = 0; i < leases.Count; i++) if (ReferenceEquals(leases[i].box, box)) held++;
			if (held >= BasisInputShim.MaxRequestsPerContent) return null;
			CilboxInputLease lease = new CilboxInputLease(proxy, box, FindCallback(proxy));
			leases.Add(lease);
			Hook();
			return lease.handle;
		}
		internal static void Release(CilboxInputLease lease)
		{
			if (lease == null || lease.Released) return;
			lease.DetachAll();
			leases.Remove(lease);
			if (leases.Count == 0) Unhook();
		}
		internal static bool IsBound(CilboxInputChannel channel)
		{
			switch (channel.kind)
			{
				case CilboxInputChannel.KindHandButton:
				case CilboxInputChannel.KindHandAxis:
					int hand = channel.code >> 8;
					if (handTick[hand] != tick)
					{
						handTick[hand] = tick;
						handHeld[hand] = BasisActionDriver.TryGetRoleState(hand == 0 ? BasisBoneTrackedRole.LeftHand : BasisBoneTrackedRole.RightHand, handCurrent[hand], handLast[hand]);
					}
					return handHeld[hand];
				default:
					return channel.control != null;
			}
		}
		internal static CilboxInputChannel Channel(int kind, int code, string path)
		{
			CilboxInputChannel channel;
			if (path != null)
			{
				if (byPath.TryGetValue(path, out channel)) return channel;
			}
			else if (byKey.TryGetValue(((long)kind << 32) | (uint)code, out channel)) return channel;
			channel = new CilboxInputChannel { kind = kind, code = code, path = path, index = channels.Count };
			channels.Add(channel);
			if (path != null) byPath[path] = channel;
			else byKey[channel.Key] = channel;
			Bind(channel);
			channel.waitRelease = channel.isButton;
			return channel;
		}
		internal static void DropIfUnused(CilboxInputChannel channel)
		{
			if (channel.leases.Count > 0) return;
			int last = channels.Count - 1;
			CilboxInputChannel moved = channels[last];
			channels[channel.index] = moved;
			moved.index = channel.index;
			channels.RemoveAt(last);
			if (channel.path != null) byPath.Remove(channel.path);
			else byKey.Remove(channel.Key);
		}
		internal static void Unsubscribe(CilboxInputChannel channel, CilboxInputLease lease, int slot)
		{
			for (int i = channel.leases.Count - 1; i >= 0; i--)
			{
				if (!ReferenceEquals(channel.leases[i], lease) || channel.slots[i] != slot) continue;
				channel.leases.RemoveAt(i);
				channel.slots.RemoveAt(i);
				break;
			}
			DropIfUnused(channel);
		}
		internal static void Poll(bool menu, bool hover, bool typing, bool focused)
		{
			tick++;
			Keyboard currentKeyboard = Keyboard.current;
			Mouse currentMouse = Mouse.current;
			Gamepad currentGamepad = Gamepad.current;
			if (bindingsDirty || !ReferenceEquals(currentKeyboard, keyboard) || !ReferenceEquals(currentMouse, mouse) || !ReferenceEquals(currentGamepad, gamepad))
			{
				keyboard = currentKeyboard;
				mouse = currentMouse;
				gamepad = currentGamepad;
				bindingsDirty = false;
				for (int i = 0; i < channels.Count; i++) Bind(channels[i]);
			}
			bool focusBlocked = menu || typing || !focused, pointerBlocked = menu || hover || !focused, handBlocked = menu || hover;
			for (int i = 0; i < channels.Count; i++)
			{
				CilboxInputChannel channel = channels[i];
				bool blocked = channel.gate == CilboxInputChannel.GateFocus ? focusBlocked : channel.gate == CilboxInputChannel.GatePointer ? pointerBlocked : handBlocked;
				float raw = 0f;
				if (blocked)
				{
					if (channel.isButton) channel.waitRelease = true;
				}
				else
				{
					Sample(channel, out raw);
					if (channel.waitRelease)
					{
						if (Math.Abs(raw) < BasisInputShim.PressThreshold) channel.waitRelease = false;
						else raw = 0f;
					}
				}
				float difference = raw - channel.value;
				if (difference < ChangeEpsilon && difference > -ChangeEpsilon && (raw != 0f || channel.value == 0f)) continue;
				bool held = Math.Abs(raw) >= BasisInputShim.PressThreshold, flipped = held != channel.held;
				channel.value = raw;
				channel.held = held;
				for (int s = 0; s < channel.leases.Count; s++)
				{
					CilboxInputLease lease = channel.leases[s];
					lease.Publish(channel.slots[s], raw, held, flipped);
					if (lease.callback != null && !lease.Queued)
					{
						lease.Queued = true;
						notify.Add(lease);
					}
				}
			}
			Notify();
		}
		private static void Tick()
		{
			for (int i = leases.Count - 1; i >= 0; i--)
			{
				CilboxInputLease lease = leases[i];
				if (lease.owner == null || lease.owner.disabled || lease.box == null || lease.box.disabled) Release(lease);
			}
			if (!hooked) return;
			BasisInputModuleHandler handler = BasisInputModuleHandler.Instance;
			Poll(BasisMainMenu.Instance != null, handler != null && handler.HasHoverONInput, handler != null && handler.IsTyping(), Application.isFocused);
		}
		private static void Notify()
		{
			for (int i = notify.Count - 1; i >= 0; i--)
			{
				CilboxInputLease lease = notify[i];
				if (lease.Released)
				{
					notify.RemoveAt(i);
					continue;
				}
				if (lease.owner == null || !lease.owner.isActiveAndEnabled) continue;
				lease.Queued = false;
				notify.RemoveAt(i);
				try
				{
					lease.callback.Interpret(lease.owner, null);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
		}
		private static void Sample(CilboxInputChannel channel, out float value)
		{
			value = 0f;
			switch (channel.kind)
			{
				case CilboxInputChannel.KindHandButton:
					if (IsBound(channel)) value = HandButton(handCurrent[channel.code >> 8], (BasisControllerButton)(channel.code & 0xFF));
					break;
				case CilboxInputChannel.KindHandAxis:
					if (IsBound(channel)) value = HandAxis(handCurrent[channel.code >> 8], (BasisInputControllerAxis)(channel.code & 0xFF));
					break;
				default:
					if (channel.control != null) value = channel.control.ReadValue();
					break;
			}
			if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
		}
		private static float HandButton(BasisInputState input, BasisControllerButton button)
		{
			switch (button)
			{
				case BasisControllerButton.Trigger: return input.Trigger >= BasisControllerInputShim.TriggerPressThreshold ? 1f : 0f;
				case BasisControllerButton.Grip: return input.GripButton ? 1f : 0f;
				case BasisControllerButton.Primary: return input.PrimaryButtonGetState ? 1f : 0f;
				case BasisControllerButton.Secondary: return input.SecondaryButtonGetState ? 1f : 0f;
				case BasisControllerButton.StickClick: return input.Primary2DAxisClick ? 1f : 0f;
				case BasisControllerButton.Menu: return input.SystemOrMenuButton ? 1f : 0f;
				default: return 0f;
			}
		}
		private static float HandAxis(BasisInputState input, BasisInputControllerAxis axis)
		{
			switch (axis)
			{
				case BasisInputControllerAxis.Trigger: return Mathf.Clamp01(input.Trigger);
				case BasisInputControllerAxis.StickX: return input.Primary2DAxisDeadZoned.x;
				case BasisInputControllerAxis.StickY: return input.Primary2DAxisDeadZoned.y;
				default: return 0f;
			}
		}
		private static void Bind(CilboxInputChannel channel)
		{
			channel.control = null;
			switch (channel.kind)
			{
				case CilboxInputChannel.KindKey:
					channel.gate = CilboxInputChannel.GateFocus;
					channel.isButton = true;
					if (Keyboard.current != null) channel.control = Keyboard.current[(Key)channel.code];
					break;
				case CilboxInputChannel.KindMouseButton:
					channel.gate = CilboxInputChannel.GatePointer;
					channel.isButton = true;
					channel.control = MouseButton(Mouse.current, channel.code);
					break;
				case CilboxInputChannel.KindMouseAxis:
					channel.gate = CilboxInputChannel.GatePointer;
					channel.control = MouseAxis(Mouse.current, (BasisInputMouseAxis)channel.code);
					break;
				case CilboxInputChannel.KindGamepad:
					channel.gate = CilboxInputChannel.GateFocus;
					channel.isButton = channel.code <= (int)BasisInputGamepadControl.RightTrigger;
					channel.control = GamepadControl(Gamepad.current, (BasisInputGamepadControl)channel.code);
					break;
				case CilboxInputChannel.KindHandButton:
					channel.gate = CilboxInputChannel.GateHand;
					channel.isButton = true;
					break;
				case CilboxInputChannel.KindHandAxis:
					channel.gate = CilboxInputChannel.GateHand;
					channel.isButton = (channel.code & 0xFF) == (int)BasisInputControllerAxis.Trigger;
					break;
				case CilboxInputChannel.KindPath:
					InputControl found = null;
					try
					{
						found = InputSystem.FindControl(channel.path);
					}
					catch (Exception)
					{
						found = null;
					}
					channel.control = found as InputControl<float>;
					channel.isButton = found is ButtonControl;
					channel.gate = found != null && found.device is Mouse ? CilboxInputChannel.GatePointer : CilboxInputChannel.GateFocus;
					break;
			}
		}
		private static InputControl<float> MouseButton(Mouse device, int button)
		{
			if (device == null) return null;
			switch (button)
			{
				case 0: return device.leftButton;
				case 1: return device.rightButton;
				case 2: return device.middleButton;
				case 3: return device.backButton;
				case 4: return device.forwardButton;
				default: return null;
			}
		}
		private static InputControl<float> MouseAxis(Mouse device, BasisInputMouseAxis axis)
		{
			if (device == null) return null;
			switch (axis)
			{
				case BasisInputMouseAxis.DeltaX: return device.delta.x;
				case BasisInputMouseAxis.DeltaY: return device.delta.y;
				case BasisInputMouseAxis.ScrollX: return device.scroll.x;
				case BasisInputMouseAxis.ScrollY: return device.scroll.y;
				case BasisInputMouseAxis.PositionX: return device.position.x;
				case BasisInputMouseAxis.PositionY: return device.position.y;
				default: return null;
			}
		}
		private static InputControl<float> GamepadControl(Gamepad device, BasisInputGamepadControl control)
		{
			if (device == null) return null;
			switch (control)
			{
				case BasisInputGamepadControl.South: return device.buttonSouth;
				case BasisInputGamepadControl.East: return device.buttonEast;
				case BasisInputGamepadControl.West: return device.buttonWest;
				case BasisInputGamepadControl.North: return device.buttonNorth;
				case BasisInputGamepadControl.LeftShoulder: return device.leftShoulder;
				case BasisInputGamepadControl.RightShoulder: return device.rightShoulder;
				case BasisInputGamepadControl.LeftStickPress: return device.leftStickButton;
				case BasisInputGamepadControl.RightStickPress: return device.rightStickButton;
				case BasisInputGamepadControl.Start: return device.startButton;
				case BasisInputGamepadControl.Select: return device.selectButton;
				case BasisInputGamepadControl.DpadUp: return device.dpad.up;
				case BasisInputGamepadControl.DpadDown: return device.dpad.down;
				case BasisInputGamepadControl.DpadLeft: return device.dpad.left;
				case BasisInputGamepadControl.DpadRight: return device.dpad.right;
				case BasisInputGamepadControl.LeftTrigger: return device.leftTrigger;
				case BasisInputGamepadControl.RightTrigger: return device.rightTrigger;
				case BasisInputGamepadControl.LeftStickX: return device.leftStick.x;
				case BasisInputGamepadControl.LeftStickY: return device.leftStick.y;
				case BasisInputGamepadControl.RightStickX: return device.rightStick.x;
				case BasisInputGamepadControl.RightStickY: return device.rightStick.y;
				default: return null;
			}
		}
		private static CilboxMethod FindCallback(CilboxProxy proxy)
		{
			CilboxClass cls = proxy.cls;
			if (cls == null || cls.methodNameToIndex == null || !cls.methodNameToIndex.TryGetValue(CallbackName, out uint index)) return null;
			CilboxMethod method = cls.methods[index];
			if (method.isStatic || (method.signatureParameters != null && method.signatureParameters.Length != 0)) return null;
			return method;
		}
		private static void DeviceChanged(InputDevice device, InputDeviceChange change)
		{
			bindingsDirty = true;
		}
		private static void Hook()
		{
			if (hooked) return;
			hooked = true;
			bindingsDirty = true;
			BasisEventDriver.OnUpdate += tickHandler;
			InputSystem.onDeviceChange -= deviceHandler;
			InputSystem.onDeviceChange += deviceHandler;
		}
		private static void Unhook()
		{
			if (!hooked) return;
			hooked = false;
			BasisEventDriver.OnUpdate -= tickHandler;
			InputSystem.onDeviceChange -= deviceHandler;
		}
	}
}
