This page collects the things almost every world or prop script needs. Each section shows the call you write and the rule that usually trips people up. Networking has its own guide: see [Networking](/en/docs/scripting/networking).

> [!info]
> Examples marked **Cilbox** run inside the sandbox, so they also work in content uploaded as a `.bee` file. Examples marked **Native** use the full Unity and Basis API and only work in code compiled into the client. Most Cilbox examples also compile natively.

## Writing a script

A Cilbox script is an ordinary `MonoBehaviour` with the `[Cilboxable]` attribute. At build time Basis replaces it with a `CilboxProxy` that runs your code inside the sandbox of the content it belongs to (Scene, Prop or Avatar).

```csharp title="Spinner.cs (Cilbox)"
using UnityEngine;
using Cilbox;

[Cilboxable]
public class Spinner : MonoBehaviour
{
    public float degreesPerSecond = 90f;

    void Update()
    {
        transform.Rotate(0f, degreesPerSecond * Time.deltaTime, 0f);
    }
}
```

## Event execution order

The proxy forwards these Unity messages to a Cilbox script:

| Message | Notes |
| --- | --- |
| `Awake`, `Start` | Do one time setup in `Start`. |
| `Update`, `FixedUpdate` | There is no `LateUpdate`. |
| `OnEnable`, `OnDisable` | The **first** `OnEnable` never arrives, so never subscribe there. |
| `OnDestroy` | Unsubscribe and clean up here. |
| `OnTriggerEnter/Stay/Exit`, `OnCollisionEnter/Stay/Exit` | Need a collider, and a Rigidbody on one side. |

Not forwarded: `LateUpdate`, `OnGUI`, `OnAnimatorIK`, `OnParticleCollision` and coroutines. Use delayed events instead of coroutines (see [Timers and delayed events](#timers-and-delayed-events)).

```csharp
// Wrong: the first OnEnable is dropped, so this never registers on the first activation.
void OnEnable() { manager.Register(this); }

// Right.
void Start() { manager.Register(this); }
void OnDestroy() { manager.Unregister(this); }
```

## Players

### The local player

`BasisLocalPlayer.Instance` is the player on this machine. Teleport them, change their movement, or freeze them.

```csharp title="Cilbox"
using Basis.Scripts.BasisSdk.Players;

BasisLocalPlayer local = BasisLocalPlayer.Instance;
local.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
local.Teleport(spawnPoint.position, spawnPoint.rotation, BasisTeleportMode.Instant);
local.SetDefaultMovementSpeed(3.5f);
local.SetJumpHeight(1.2f);
local.Immobilize(true);
```

For a zone that changes movement only while the player is inside, use overrides under your own key. Clearing the key restores whatever was underneath, so two zones never fight over the original value.

```csharp title="Cilbox"
// Mode: 0 = Walk, 1 = Fly, 2 = NoClip.
local.SetWalkSpeedOverride("LowGravityZone", 1.2f);
local.SetJumpHeightOverride("LowGravityZone", 2.5f);
local.SetMovementModeOverride("LowGravityZone", 1);
local.ClearLocomotionOverride("LowGravityZone");
```

> [!info]
> In client builds, the **Basis Locomotion Zone** component (com.basis.examples) does this without code. It is not on the default content allowlists, so uploaded worlds use the overrides above instead.

### Everyone in the instance

`BasisPlayersShim` returns players as `IBasisPlayer`, which is read only. Poses come back as copied vectors, never as a `Transform`.

```csharp title="Cilbox"
using Basis.Shims;
using Basis.Scripts.BasisSdk.Players;

int count = BasisPlayersShim.Count;

IBasisPlayer nearest = BasisPlayersShim.Nearest(transform.position);
if (nearest != null)
{
    Debug.Log($"{nearest.DisplayName} is {nearest.DistanceTo(transform.position):F1}m away");
}

foreach (IBasisPlayer player in BasisPlayersShim.Within(transform.position, 5f))
{
    Vector3 head = player.GetBonePosition(HumanBodyBones.Head);
}
```

Useful reads on `IBasisPlayer`: `IsLocal`, `DisplayName`, `SafeDisplayName` (use this when showing names on signs), and `UUID`, which is the same on every visit and every server.

### Launching the player

`BasisVelocityShim` pushes the local player in world space. It is for worlds only. Gravity takes the vertical part.

```csharp title="Cilbox"
using Basis.Shims;

// A trampoline: keep the run, replace the fall.
Vector3 velocity = BasisVelocityShim.Velocity;
BasisVelocityShim.SetVelocity(new Vector3(velocity.x, 9f, velocity.z));

// A boost pad.
BasisVelocityShim.AddVelocity(transform.forward * 6f);
```

## Input

### Interacting with objects

A **Basis Pickup Interactable** raises events when a player grabs it and presses use. For something that is pressed but not picked up, add a **Basis Interactable Shim** and handle `ButtonDown` and `ButtonUp`.

```csharp title="Cilbox"
using Basis.Shims;

void Start()
{
    GetComponent<BasisInteractableShim>().ButtonDown += OnPressed;
}

void OnPressed() { door.SetActive(!door.activeSelf); }
```

```csharp title="Cilbox"
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;

public BasisPickupInteractable pickup;

void Start()
{
    pickup.OnInteractStartEvent.AddListener(OnGrab);
    pickup.OnInteractEndEvent.AddListener(OnDrop);
    pickup.OnPickupUse.AddListener(OnUse);
}

void OnGrab(BasisInput input) { }
void OnDrop(BasisInput input) { }

void OnUse(BasisPickUpUseMode mode)
{
    if (mode == BasisPickUpUseMode.OnPickUpUseDown) Fire();
}
```

### Controller buttons

`BasisControllerInputShim` reads the same hand state that drives the player. Worlds only. Everything reads as released while the menu is open or a pointer is over UI.

```csharp title="Cilbox"
using Basis.Shims;

void Update()
{
    if (!BasisControllerInputShim.IsAvailable(BasisControllerHand.Right)) return; // desktop

    if (BasisControllerInputShim.GetButtonDown(BasisControllerHand.Right, BasisControllerButton.Trigger))
        Fire();

    float squeeze = BasisControllerInputShim.GetTrigger(BasisControllerHand.Left);
    Vector2 stick = BasisControllerInputShim.GetStick(BasisControllerHand.Left);
}
```

### Haptics

```csharp title="Cilbox"
// Strength 0..1, at most 1 second. Returns false when nothing played.
BasisHapticsShim.Play(this, BasisHapticHand.Right);
BasisHapticsShim.Play(this, BasisHapticHand.Both, 0.8f, 0.2f);
```

## UI events

World space canvases work with the Basis pointer. Wire buttons from code so the listener lives in the sandbox. See [UI Canvas](/en/docs/world/ui-canvas) for setting up the canvas itself.

```csharp title="Cilbox"
using UnityEngine.UI;
using TMPro;

public Button button;
public TextMeshProUGUI label;

void Start()
{
    button.onClick.AddListener(OnClick);
    label.text = "Ready";
}

void OnDestroy() { button.onClick.RemoveAllListeners(); }

void OnClick() { label.text = "Clicked!"; }
```

Allowed in Cilbox: `UnityEngine.UI.*` types, `UnityEvent.AddListener` (including `UnityEvent<T>`), creating a `UnityAction`, and `RemoveAllListeners`. Two limits:

- `RemoveListener(OnClick)` does not remove the listener, because each use of `OnClick` as a delegate is a new wrapper. Use `RemoveAllListeners`.
- UI components (`Button`, `Toggle`, `Canvas`) are allowed in worlds and props, not avatars.

## Timers and delayed events

Cilbox scripts have no coroutines. A networked script can schedule a callback through its network component instead.

```csharp title="Cilbox"
using Basis;

BasisNetworkShim net;

void Start()
{
    net = GetComponent<BasisNetworkShim>();
    net.SendCustomEventDelayedSeconds(CloseDoor, 3f);
    net.SendCustomEventDelayedFrames(Rebuild, 2);
}
```

For a timer that does not need a network component, count down in `Update`:

```csharp
float remaining = 3f;

void Update()
{
    if (remaining <= 0f) return;
    remaining -= Time.deltaTime;
    if (remaining <= 0f) CloseDoor();
}
```

## Platform checks

```csharp title="Cilbox"
using Basis.Shims;

bool vr = BasisPlatformShim.IsVR;
bool mobile = BasisPlatformShim.IsMobileGpu;
bool quest = vr && BasisPlatformShim.IsDetected("Android");
```

To react when a player switches between desktop and VR, add a `BasisPlatformEventShim` and define the callback by name. Adding the component is the opt in; the callback fires once straight away and again on every change.

```csharp title="Cilbox"
void Start() { GetComponent<BasisPlatformEventShim>(); }

void OnPlatformChanged(string mode, bool vr, bool headsetWorn)
{
    vrControls.SetActive(vr);
    desktopHud.SetActive(!vr);
}
```

> [!info]
> No code needed: **Basis Platform Switch** (com.basis.examples) enables and disables objects and components per platform from a list of rules.

The same opt in pattern exists for other changes: `BasisLanguageEventShim` calls `OnLanguageChanged(string code)`, `BasisGraphicsSettingsEventShim` calls `OnGraphicsSettingsChanged(string qualityLevel, int qualityTier)`, and `BasisPermissionEventShim` calls `OnLocalPermissionsChanged(bool isAdmin, bool isModerator)`.

## Loading images and text

Downloads check the address before any request goes out, and refuse redirects. Players approve new hosts the first time.

```csharp title="Cilbox"
using Basis;

var images = new BasisImageDownloader();
images.DownloadImage("https://example.com/sign.png", result =>
{
    if (result.Success) signMaterial.mainTexture = result.Result;
    else Debug.LogWarning(result.Error);
});

var strings = new BasisStringDownloader();
strings.DownloadString("https://example.github.io/questions.json", result =>
{
    if (result.Success) LoadQuestions(result.Result);
});
```

Parse JSON with `BasisJson`, which runs on the host so the script only sees plain values:

```csharp title="Cilbox"
using Basis.Shims;

BasisJsonNode root = BasisJson.Parse(text);
if (root == null) return;
string title = root.GetString("title", "Untitled");
BasisJsonNode questions = root.Get("questions");
for (int i = 0; i < (questions != null ? questions.Count : 0); i++)
{
    string prompt = questions.Get(i).GetString("q", "");
}
```

For REST APIs use `BasisWebRequestShim.GetText` and `PostText` (https only, worlds and props only).

## External links

Open a website from a player's own press. The player sees the address and confirms it.

```csharp title="Cilbox"
using Basis.Shims;

public void OnWebsiteButtonPressed()
{
    BasisExternalLinkShim.Open(this, "https://example.com/community", result =>
    {
        if (result != BasisExternalLinkResult.Opened) addressLabel.text = "example.com/community";
    });
}
```

`BasisConnectionShim.RequestConnect(this, host, port, password, callback)` works the same way and moves the player to another server after they accept.

## Saving data (persistence)

`BasisSaveShim` stores bytes on the player's machine, filed under your content's ID. Until the player agrees, `Save` returns false and `Load` returns null, and the rest of your script keeps running.

```csharp title="Cilbox"
using Basis.Shims;

bool saved = BasisSaveShim.Save(this, "progress", bytes);
byte[] loaded = BasisSaveShim.Load(this, "progress");
BasisSaveShim.Delete(this, "progress");

// Name a store to share saves between a world and its props.
BasisSaveShim.Save(this, "my-game", "progress", bytes);
```

Limits: 256 KB per slot, 32 slots and 1 MB per store, 8 writes a second. See the [persistence example](/en/docs/scripting/examples/persistence) for packing values into bytes.

## Menus

Add a tab to the player's settings, or a whole panel with its own button in the menu.

```csharp title="Cilbox"
using Basis.Shims;

BasisMenuPage page;

void Start()
{
    page = BasisMenuShim.AddSettingsTab(this, "Race Track");
    if (page == null) return; // refused
    page.AddHeader("Race");
    page.AddToggle("Ghost car", true, OnGhost);
    page.AddSlider("Laps", 1, 10, 3, true, OnLaps);
    page.AddButton("Reset race", OnReset);
}
```

See the [settings tab example](/en/docs/scripting/examples/settings-tab).

## Audio

```csharp title="Cilbox"
var source = GetComponent<AudioSource>();
source.clip = clip;
source.spatialBlend = 1f;
source.Play();
```

To let a speaker carry a player's voice, see `BasisVoiceRoutingShim` in the [Shim Reference](/en/docs/scripting/shims/rendering-and-audio).

## Performance and quality

Read the player's quality tier and frame rate to scale your content down when it is struggling.

```csharp title="Cilbox"
using Basis.Shims;

bool cheap = BasisGraphicsSettingsShim.QualityTier <= BasisGraphicsSettingsShim.TierLow;
backgroundDetail.SetActive(!cheap);

if (BasisStatsShim.Bottleneck == BasisStatsShim.BottleneckGpu) mirror.SetActive(false);
```

## Debugging

`Debug.Log` works as written; Basis tags each message with the content it came from. In the editor, **Basis > Tools > Cilbox Permissions** shows what each sandbox allows, and its **Script Check** tab scans your scripts for calls that will be blocked. The **Basis > Creator Guide** window has this guide and the full Cilbox API reference offline.

> [!warn]
> Blocked calls do not throw at build time. A call outside the whitelist fails when it runs, so scan scripts before uploading.
