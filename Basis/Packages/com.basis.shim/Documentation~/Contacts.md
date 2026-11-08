# Contacts and body haptics

Contacts let avatars, props and worlds react to touch without writing a script. A `Basis Contact Receiver` measures how close a sender is and writes the result to a parameter, a script callback, or both. Every avatar already carries senders on its body, so a receiver works against any player without that player setting anything up.

## Components

`Basis Contact Sender` is a sphere or capsule with a list of tags. Use it for things that are not part of a body, such as a wand tip or a prop that should press a world button.

`Basis Contact Receiver` is a sphere or capsule that looks for senders whose tags overlap its own. An empty tag list accepts every sender. Its fields:

| Field | Meaning |
| --- | --- |
| `allowSelf` | The wearer (or, on a world or prop, the local player) can trigger it. |
| `allowOthers` | Other players can trigger it. |
| `allowWorld` | Senders on props and worlds can trigger it. |
| `mode` | `Constant` is 1 while touched. `Proximity` goes from 0 at the edge to 1 at the centre. `OnEnter` is 1 for `pulseSeconds` when a touch starts. |
| `parameter` | Name written on the wearer's side. Leave empty for script-only receivers. |

Capsules run along the object's local Y axis, `height` is the full length including the end caps, and both shapes scale with the transform.

## Built-in body senders

Every loaded avatar has these senders, sized from the avatar's own proportions:

| Tag | Where |
| --- | --- |
| `Head` | Top of the head |
| `Torso` | Hips to chest |
| `Hand`, `HandLeft`, `HandRight` | Palm |
| `Finger`, `FingerLeft`, `FingerRight` | Index fingertip |
| `Foot`, `FootLeft`, `FootRight` | Foot to toe |

A receiver tagged `Hand` on top of your head is a headpat. A small receiver on the nose tagged `Finger` is a boop.

## Driving Vixxy

Receivers on an avatar are evaluated only by the wearer. When the value changes, the `parameter` is written into the avatar's comms variable store under that address, which is what Vixxy controls read. Give a `HVR Vixxy Control` an explicit address path, put the same string in the receiver's `parameter`, and the control follows the contact. Vixxy networks the value to everyone else as usual. A control that also has a menu item will be overwritten by whichever wrote last, so use a separate control for contact-driven states.

With OSC turned on, the same value is also sent as `/avatar/parameters/<parameter>`, so OSC tools that read avatar parameters see contacts too.

## Scripts

Add `BasisContactEventShim` to the object that holds the receivers (or a parent of them) and declare the callbacks on your script:

```csharp
void Start() { GetComponent<BasisContactEventShim>(); }
void OnContactEnter(int receiverIndex, int playerId) { }
void OnContactExit(int receiverIndex, int playerId) { }
```

`FindReceiver(name)`, `GetValue(index)`, `IsTouched(index)` and `GetTouchingPlayer(index)` read the current state. `playerId` is -1 for a world or prop sender. Callbacks are capped at 32 per object per frame.

## Body haptics

Settings > General > Interactions > Body Haptics adds 17 zones to your own body: head, chest, stomach, upper back and lower back (each left and right), upper arms, forearms, thighs and shins. Other players' body senders and any sender on a prop or world drive them. Each zone value is a proximity from 0 to 1, multiplied by the intensity setting.

The zones go out over OSC as `/avatar/parameters/BasisHaptics/<Zone>`, for example `/avatar/parameters/BasisHaptics/ChestLeft`. A bridge program for your vest or suit maps those to motors. Native code can also implement `IBasisBodyHapticsOutput` and call `BasisBodyHaptics.RegisterOutput` to receive the array directly.

Haptics from other players can be turned off on their own, your own hands can be allowed separately (a hand never triggers its own arm), and blocked players or players whose interactions you disabled never trigger anything.

## Cost

Nothing runs while there are no receivers and body haptics are off. When something is listening, the main thread only packs the shape list. Transform reads happen in a parallel transform job, and shape building and contact tests run as Burst jobs, culled per player before testing individual shapes. Results are applied on the following frame.
