## BasisControllerInputShim

Worlds only. VR controller state. Everything reads as released while the menu is open or the pointer is over UI.

```csharp
using Basis.Shims;

void Update()
{
    if (!BasisControllerInputShim.IsAvailable(BasisControllerHand.Right)) return;

    if (BasisControllerInputShim.GetButtonDown(BasisControllerHand.Right, BasisControllerButton.Primary)) Jump();
    bool holding = BasisControllerInputShim.GetButton(BasisControllerHand.Left, BasisControllerButton.Grip);
    float trigger = BasisControllerInputShim.GetTrigger(BasisControllerHand.Right);
    Vector2 stick = BasisControllerInputShim.GetStick(BasisControllerHand.Left);
}
```

Buttons: `Trigger`, `Grip`, `Primary`, `Secondary`, `StickClick`, `Menu`.

## BasisDesktopInputShim

Mouse input on desktop. Button 0 is left, 1 right, 2 middle.

```csharp
if (!BasisDesktopInputShim.HasMouse) return;
if (BasisDesktopInputShim.GetMouseButtonDown(0)) Shoot();
Vector2 delta = BasisDesktopInputShim.MouseDelta;
float scroll = BasisDesktopInputShim.MouseScroll.y;
Vector2 screen = BasisDesktopInputShim.MousePosition;
```

## BasisInputShim

Worlds only. The cheapest way to read input from any device when a script watches more than a few buttons. Ask once for the inputs you need, and Basis polls each one once a frame no matter how many scripts share it. Your script does nothing until something changes.

```csharp
using Basis.Shims;
using UnityEngine.InputSystem;

BasisInputRequest input;
float[] values = new float[BasisInputShim.MaxSlots];
int jump, fire, stickX;

void Start()
{
    input = BasisInputShim.Request(this);
    jump = input.AddKey(Key.Space);
    fire = input.AddController(BasisControllerHand.Right, BasisControllerButton.Trigger);
    stickX = input.AddGamepad(BasisInputGamepadControl.LeftStickX);
    input.Target(values);
}

void OnInputChanged(int down, int up)
{
    if ((down & (1 << jump)) != 0) Jump();
    if ((down & (1 << fire)) != 0) Fire();
    speed = values[stickX];
}

void OnDestroy() { input.Release(); }
```

Each `Add` call returns a slot number, or -1 if it was refused. A request holds up to 32 slots and can be null, so check it if your script might hold more than 64 requests.

| Call | Reads |
| --- | --- |
| `AddKey(Key.Space)` | A keyboard key |
| `AddMouseButton(0)` | A mouse button. 0 is left, 1 right, 2 middle, 3 back, 4 forward |
| `AddMouseAxis(BasisInputMouseAxis.DeltaX)` | Mouse movement, scroll or position |
| `AddGamepad(BasisInputGamepadControl.South)` | A gamepad button, trigger or stick axis |
| `AddController(hand, BasisControllerButton.Grip)` | A VR controller button |
| `AddController(hand, BasisInputControllerAxis.StickX)` | A VR controller trigger or stick axis |
| `AddControl("<Joystick>/trigger")` | Any other button or axis, by Input System control path. Name one axis, such as `<Pen>/position/x` |

Scripts can read results in two ways.

- Add an `OnInputChanged(int down, int up)` method. Basis calls it at most once a frame, only when a value changed, and passes the presses and releases since the last call. A method with no parameters, `OnInputChanged()`, also works, with `TakeDown()` and `TakeUp()` to get the presses.
- Check `input.Changed` from your own `Update`. `values` keeps its contents, so a held stick can keep moving something every frame.

`Target(values)` makes Basis write each new value straight into your array, which is the fastest way to read. Without it, call `Read(values)` to copy them over. Slots past the end of the array are skipped.

`WakeOn(mask)` picks which slots wake the script, one bit per slot. The others still update `values`, `Held` and the presses and releases, but on their own they don't call `OnInputChanged` or set `Changed`. Use it when you read something that changes every frame, like mouse movement, but only need to act on a button. For example, `input.WakeOn(1 << fire)`.

`values` holds one number per slot. Buttons are 0 or 1, triggers run from 0 to 1, and sticks from -1 to 1. `Held`, `TakeDown()` and `TakeUp()` hold one bit per slot, set at 0.5 and above. `TakeDown` and `TakeUp` give you each press and release once and clear it, so a quick tap between two reads still arrives, and they work from `FixedUpdate`.

Everything reads 0 while the menu is open. Keyboards and gamepads also read 0 while the player types in a text field or the game window is out of focus. Mice and VR controllers also read 0 while a pointer is over UI, and mice while the window is out of focus. A button held when reading resumes stays at 0 until it is let go.

`IsBound(slot)` tells you whether a device is there to read, for example false for `AddController` on desktop. Requests end on their own when the script is destroyed. Call `Release()` sooner if you stop needing them.

## BasisHapticsShim

Vibrates the local player's controllers. Strength 0 to 1, at most 1 second, 10 pulses a second per content. Returns false when nothing played.

```csharp
BasisHapticsShim.Play(this, BasisHapticHand.Right);
BasisHapticsShim.Play(this, BasisHapticHand.Both, 0.8f, 0.2f);
```

## BasisInteractableShim

Makes an object pressable with the pointer, with hover and press colours on its renderer.

```csharp
BasisInteractableShim button;

void Start()
{
    button = GetComponent<BasisInteractableShim>();
    button.ButtonDown += OnDown;
    button.ButtonUp += OnUp;
}

void OnDown() { lamp.SetActive(!lamp.activeSelf); }
void OnUp() { }
```

Inspector fields: `isEnabled`, `Color`, `HoverColor`, `InteractColor`, `DisabledColor`, `PropertyName` (the material colour property).

## BasisActionTrigger

Forwards Unity UI pointer events to your script. Add it to a UI element.

```csharp
using UnityEngine.EventSystems;

void Start()
{
    var trigger = GetComponent<BasisActionTrigger>();
    trigger.PointerEnter += e => highlight.SetActive(true);
    trigger.PointerExit += e => highlight.SetActive(false);
    trigger.PointerClick += e => Select();
}
```

Also available: `PointerDown`, `PointerUp`, `BeginDrag`, `Drag`, `EndDrag`, `Drop`, `Scroll`.

## BasisUIExtensions

`Toggle.onValueChanged` takes a `UnityAction<bool>`. This extension lets you pass a method instead.

```csharp
using Basis.Shims;
using UnityEngine.UI;

public Toggle musicToggle;

void Start() { musicToggle.AddListener(OnMusic); }

void OnMusic(bool on) { music.SetActive(on); }
```
