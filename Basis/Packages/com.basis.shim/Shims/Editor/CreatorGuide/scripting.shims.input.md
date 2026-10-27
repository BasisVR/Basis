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
