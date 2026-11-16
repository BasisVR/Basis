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

Binds keys, mouse, gamepad and VR controls once and hands them back as numbered slots. Works in worlds, props and avatars. A prop reads the local player on every client. On an avatar only the wearer's copy reads anything; other copies get a request that stays empty.

```csharp
using Basis.Shims;
using UnityEngine.InputSystem;

BasisInputRequest input;
int jump, fire, steer;

void Start()
{
    input = BasisInputShim.Request(this);
    if (input == null) return;
    jump = input.AddKey(Key.Space);
    fire = input.AddController(BasisControllerHand.Right, BasisControllerButton.Trigger);
    steer = input.AddGamepad(BasisInputGamepadControl.LeftStickX);
}

void OnInputChanged(int down, int up)
{
    if ((down & (1 << jump)) != 0) Jump();
    if ((down & (1 << fire)) != 0) Shoot();
    float x = input.Get(steer);
}

void OnDestroy() { input?.Release(); }
```

Each `Add` returns a slot, or -1 when the control is not allowed or the request is full (32 slots, 64 requests per piece of content). Buttons read 0 or 1 and count as held from 0.5. `OnInputChanged()` with no arguments also works; `down` and `up` are bitmasks of slots pressed and released since the last call. Without the callback, poll `Get(slot)`, `Held`, `Changed`, `TakeDown()` and `TakeUp()`, or pass an array to `Target(values)` and Basis keeps it filled. `WakeOn(mask)` picks which slots wake you.

Also available: `AddMouseButton(button)` for buttons 0 to 4, `AddMouseAxis(BasisInputMouseAxis.DeltaX)`, `AddController(hand, BasisInputControllerAxis.StickY)`, `AddControl("<Keyboard>/space")` for any Input System path, and `IsBound(slot)` to see if the device is there.

Everything reads 0 while the menu is open. Keys and gamepad also stop while the player is typing, mouse and controllers while the pointer is over UI. A button held through a block has to be let go before it counts again.

## BasisLocalInputShim

The reads from `BasisDesktopInputShim` and `BasisControllerInputShim`, open to props and avatars as well. Pass `this`. A prop reads the local player on every client; an avatar only reads on the wearer's copy.

```csharp
if (BasisLocalInputShim.GetMouseButtonDown(this, 0)) Shoot();
Vector2 delta = BasisLocalInputShim.GetMouseDelta(this);
if (BasisLocalInputShim.GetButtonDown(this, BasisControllerHand.Right, BasisControllerButton.Primary)) Jump();
float trigger = BasisLocalInputShim.GetTrigger(this, BasisControllerHand.Right);
string label = BasisLocalInputShim.GetButtonName(this, BasisControllerHand.Right, BasisControllerButton.Primary);
```

## BasisKeyboardShim

Opens the Basis virtual keyboard and gives you the text when the player submits. Worlds and props can open it, avatars only for the wearer. When it comes from someone else's prop, the keyboard shows their name. Returns false when it did not open, and a script can open it at most once every 2 seconds.

```csharp
string petName = "Rex";

void Rename()
{
    BasisKeyboardShim.Open(this, petName, "Name your pet", 20, BasisKeyboardContent.Standard, text =>
    {
        petName = text;
    }, null);
}
```

Shorter overloads take only the callback, or the starting text and the callback. Content types are `Standard`, `Integer`, `Decimal` and `Alphanumeric`. `IsOpen(this)`, `GetText(this)` and `Close(this)` manage an open keyboard.

## BasisHapticsShim

Vibrates the local player's controllers. Strength 0 to 1, at most 1 second, 10 pulses a second per content. Returns false when nothing played.

```csharp
BasisHapticsShim.Play(this, BasisHapticHand.Right);
BasisHapticsShim.Play(this, BasisHapticHand.Both, 0.8f, 0.2f);
```

## BasisHandBlockShim

Stops the local player's hands pointing (`Raycast`), picking things up (`Pickup`) or touching (`Touch`). Worlds, props the local player spawned and the wearer's avatar can block. A block belongs to the script that set it and lifts when that script is disabled or the player respawns.

```csharp
BasisHandBlockShim.Block(this, BasisHandBlockShim.Right, BasisHandBlock.Pickup | BasisHandBlock.Raycast);

bool mine = BasisHandBlockShim.IsBlocking(this, BasisHandBlockShim.Right, BasisHandBlock.Pickup);
bool anyone = BasisHandBlockShim.IsBlocked(this, BasisHandBlockShim.Left, BasisHandBlock.Touch);

BasisHandBlockShim.Unblock(this, BasisHandBlockShim.Both, BasisHandBlock.All);
```

Hands are `BasisHandBlockShim.Left`, `Right` and `Both`. `UnblockAll(this)` drops everything the script holds.

## BasisGrabShim

What the local player's hands are holding, jiggle bones or pickups. Worlds, props the local player spawned and the wearer's avatar. Hands are `BasisGrabShim.Left` and `BasisGrabShim.Right`.

```csharp
if (BasisGrabShim.IsHoldingJiggle(this, BasisGrabShim.Left))
{
    int owner = BasisGrabShim.GetHeldJigglePlayerId(this, BasisGrabShim.Left);
    HumanBodyBones near = BasisGrabShim.GetHeldJiggleAttachedBone(this, BasisGrabShim.Left);
    string bone = BasisGrabShim.GetHeldJiggleBoneName(this, BasisGrabShim.Left);
}

if (BasisGrabShim.IsHolding(this, BasisGrabShim.Right, sword)) swordGlow.SetActive(true);
GameObject held = BasisGrabShim.GetHeldPickup(this, BasisGrabShim.Right);

BasisGrabShim.BlockJiggle(this, BasisGrabShim.Right, true);
```

`GetHeldPickup` only returns objects from your own content; `GetHeldPickupName` and `TryGetHeldPickupPose` work on anything. `TryGetHeldJiggleBone` gives the grabbed bone's position and rotation, and `ReleaseJiggle` lets go. `BlockJiggle` stops that hand grabbing jiggle bones until you pass false.

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
