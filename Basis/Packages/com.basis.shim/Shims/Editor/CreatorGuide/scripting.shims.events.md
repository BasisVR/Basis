Every event shim works the same way: call `GetComponent<T>()` once in `Start` (Cilbox adds it for you), then declare a method with the shim's callback name. Basis calls it directly. There is nothing to subscribe to or unsubscribe from.

> [!info]
> Most event shims call you once straight after you opt in, with the current value. You do not need separate start up code.

## BasisPlatformEventShim

```csharp
void Start() { GetComponent<BasisPlatformEventShim>(); }

void OnPlatformChanged(string mode, bool vr, bool headsetWorn)
{
    vrHud.SetActive(vr);
    desktopHud.SetActive(!vr);
}
```

## BasisLanguageEventShim

```csharp
void Start() { GetComponent<BasisLanguageEventShim>(); }

void OnLanguageChanged(string code)
{
    sign.text = code == "de" ? "Willkommen" : "Welcome";
}
```

## BasisGraphicsSettingsEventShim

Coalesced to at most one call per frame.

```csharp
void Start() { GetComponent<BasisGraphicsSettingsEventShim>(); }

void OnGraphicsSettingsChanged(string qualityLevel, int qualityTier)
{
    detail.SetActive(qualityTier >= BasisGraphicsSettingsShim.TierMedium);
}
```

## BasisPermissionEventShim

```csharp
void Start() { GetComponent<BasisPermissionEventShim>(); }

void OnLocalPermissionsChanged(bool isAdmin, bool isModerator)
{
    staffPanel.SetActive(isAdmin || isModerator);
}
```

## BasisJiggleEventShim

Fires when someone grabs or touches this object's jiggle physics. Both callbacks fire once when it starts (`began` true) and once when it ends.

```csharp
BasisJiggleEventShim jiggle;
int tail;

void Start()
{
    jiggle = GetComponent<BasisJiggleEventShim>();
    tail = jiggle.FindJiggleRig("Tail");
}

void OnJiggleGrab(int playerId, bool began, int hand, int rigIndex)
{
    if (rigIndex == tail && began) squeak.Play();
}

void OnJiggleTouch(int playerId, bool began, int hand, int rigIndex) { }
```

`GetJiggleRigCount()` and `GetJiggleRigName(index)` list the rigs. Detection only runs while a script has opted in.

## BasisBeforeRenderShim

Runs after all pose and IK work, right before the frame is drawn. Use it to pin something to a hand or the head without a frame of lag. Keep it short; the render waits for it.

```csharp
void Start() { GetComponent<BasisBeforeRenderShim>(); }

void OnBeforeRender()
{
    IBasisPlayer me = BasisPlayersShim.Local;
    if (me == null) return;
    transform.position = me.GetBonePosition(HumanBodyBones.RightHand);
}
```
