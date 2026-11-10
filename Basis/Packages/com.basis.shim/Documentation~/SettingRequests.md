# Setting requests (BasisSettingRequestShim)

Status: implemented in `Shims/BasisSettingRequestShim.cs`. The prompt is `com.basis.framework/BasisUI/BasisSettingRequests.cs` and the jump is `SettingsProvider.RevealSetting`. No tests yet.

## Why

Content sometimes only works well with one of the player's own settings on. A world built around jiggle grabbing, a prop that needs passthrough. Content must not change those settings itself, so it asks: the player gets a prompt, and if they agree, Settings opens on that option with the row highlighted. The player still changes it themselves.

## API

```csharp
namespace Basis.Shims
{
    public enum BasisSettingRequestResult { Shown = 0, Declined = 1, Blocked = 2, Busy = 3, Unsupported = 4, NotFound = 5, Throttled = 6 }

    public static class BasisSettingRequestShim
    {
        public static bool Request(object self, string settingKey);
        public static bool Request(object self, string settingKey, string message);
        public static bool Request(object self, string settingKey, string message, Action<BasisSettingRequestResult> onResult);
        public static bool IsAsking(object self); // a setting prompt is open right now
    }
}
```

Pass `this`. `Request` returns `true` while the prompt is open, and `false` when it was refused on the spot. `onResult` fires once with the outcome.

```csharp
BasisSettingRequestShim.Request(this, "jigglegrabinteractions", "Turn this on to grab the rope.", result =>
{
    if (result == BasisSettingRequestResult.Shown) waitingForPlayer = true;
});
```

## Setting keys

The key is the setting's binding key, the lowercase name a `BasisSettingsBinding` is created with in `BasisSettingsDefaults` (or a package's own settings class). Examples: `jigglegrabinteractions`, `disableproppickup`, `enablepassthrough`, `menuteleport`, `showframetimems`. Keys are letters, digits, `_`, `.` and `-`, up to 128 characters.

The key is only looked up after the player accepts. A key with no row in Settings gives `NotFound` and the player sees "Setting Not Found".

## The prompt

The title is "Show a Setting?". The body names the content, its kind (world, prop or avatar), and for someone else's prop or avatar, the player it belongs to. An optional message from the script goes underneath as a quote. It is cut to 140 characters, with markup and line breaks removed. The setting itself is not named in the prompt. The player sees exactly which row it is once Settings opens.

The accept button arms after a moment. With Do Not Disturb on, or when the menu can't open, the prompt goes to notifications instead. Dismissing it there counts as a decline.

## Results

| Result | Meaning |
| --- | --- |
| `Shown` | The player accepted and Settings is open on the row. |
| `Declined` | The player said no or dismissed the notification. |
| `Blocked` | Invalid key, unknown owner, or this asker is cooling down or muted. |
| `Busy` | Another setting prompt is open. |
| `Unsupported` | Headless server, or the prompt could not be shown. |
| `NotFound` | The player accepted, but no row in Settings is bound to that key, or Settings closed before it was found. |
| `Throttled` | This script asked less than 5 seconds ago. |

## Who can ask

Any world, prop or avatar script. Unlike locks, content the local player owns is asked about too.

Declines count against the asker: the owner player for someone else's content, the content itself for the player's own world, props and avatar. After a decline that asker is blocked for 30 seconds, and after 3 declines for the rest of the session. An accept clears the count.

## From Basis code

Without a prompt:

```csharp
PanelComponent row = await SettingsProvider.RevealSetting("jigglegrabinteractions");
if (row == null) { /* no such row, or Settings closed first */ }
```

It opens the main menu first, then the Settings provider on it, then navigates. It looks through the tabs that are already built, then builds the rest one per frame, opening collapsed sections as it goes. It switches to the tab that holds the row, scrolls it into view and bounces it. Permission-gated tabs the player can't see are skipped. The returned task finishes once the row is on screen.

With the prompt, from Basis code that is not a script: `BasisSettingRequests.Request(askerId, contentName, contentKind, ownerName, settingKey, message, onResult)`.

## Permissions

Avatar boxes whitelist `BasisSettingRequestShim` and `BasisSettingRequestResult` by name. Prop and world boxes get them through `Basis.Shims.*`. It has no entry in the script consent prompt, because every request asks the player itself.
