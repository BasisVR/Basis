`BasisMenuShim` adds UI to the player's own menu. Both calls return null when refused. Always check.

| Call | Adds |
| --- | --- |
| `BasisMenuShim.AddSettingsTab(this, title)` | A tab in the player's settings (up to 4 per content). |
| `BasisMenuShim.AddPanel(this, title)` | A panel with its own menu button (one per content, up to 8 tabs). |

## Building a page

```csharp
using Basis.Shims;

BasisMenuPage page;
BasisMenuItem status, volume, mode;

void Start()
{
    page = BasisMenuShim.AddSettingsTab(this, "Club");
    if (page == null) return;

    page.AddHeader("Music");
    status = page.AddLabel("Playing: nothing");
    volume = page.AddSlider("Volume", 0f, 1f, 0.6f, OnVolume);
    page.AddToggle("Lights", true, on => lights.SetActive(on));
    mode = page.AddDropdown("Mode", new[] { "Chill", "Dance", "Off" }, 0, OnMode);
    page.AddButton("Skip track", OnSkip);
}

void OnVolume(float v) { music.volume = v; }
void OnMode(int index) { }
void OnSkip() { status.SetText("Playing: next"); }
```

## Changing items later

```csharp
volume.SetValue(0.3f);
float current = volume.Value;
mode.SetOptions(new[] { "Chill", "Dance" });
status.SetTooltip("What the DJ is playing");
volume.SetInteractable(isOwner);
status.SetVisible(false);
```

Pages: `SetTitle`, `SetVisible`, `Clear`, `Remove`, `AddTab` (panels), `IsOpen`, and `Open()`.

## Opening a panel

`Open()` only works within 5 seconds of the player pressing something in your content, and content cannot pop menus open by itself.

```csharp
BasisMenuPage arcade;

void Start() { arcade = BasisMenuShim.AddPanel(this, "Arcade"); }

public void OnKioskPressed() { arcade?.Open(); }
```

Limits: 64 items per page, 32 dropdown options, 48 character titles. More: [Settings Tab example](/en/docs/scripting/examples/settings-tab).

## BasisSettingRequestShim

Asks the player to open one of their own settings, such as turning on OSC for a world that needs it. Worlds, props and avatars can ask. The player sees who is asking and can say no. If they agree, Settings opens on that row and they change it themselves.

```csharp
public void OnOscHelpPressed()
{
    BasisSettingRequestShim.Request(this, "enableosc", "The light show listens for OSC.", result =>
    {
        if (result != BasisSettingRequestResult.Shown) hint.text = "OSC is under Settings";
    });
}
```

The key is the setting's binding key and the message is cut to 140 characters. Each script can ask once every 5 seconds. A decline blocks the asker for 30 seconds, and three declines block it for the rest of the session. `IsAsking(this)` is true while any request is on screen. Results are `Shown`, `Declined`, `Blocked`, `Busy`, `Unsupported`, `NotFound` and `Throttled`.

## BasisSettingFavouritesShim

Reads and drives the settings the player has added to their favourites, for building your own quick menu. Only the world, props the player spawned and the avatar they wear get anything back. Favourites are read by index, and `GetVersion` changes whenever the list or a value changes.

```csharp
int seen = -1;

void Update()
{
    int version = BasisSettingFavouritesShim.GetVersion(this);
    if (version == seen) return;
    seen = version;

    for (int i = 0; i < BasisSettingFavouritesShim.GetCount(this); i++)
    {
        string title = BasisSettingFavouritesShim.GetTitle(this, i);
        int kind = BasisSettingFavouritesShim.GetKind(this, i);
        if (kind == BasisSettingFavouritesShim.Toggle) ShowToggle(i, title, BasisSettingFavouritesShim.IsOn(this, i));
        if (kind == BasisSettingFavouritesShim.Slider) ShowSlider(i, title, BasisSettingFavouritesShim.GetNormalized(this, i), BasisSettingFavouritesShim.GetValueText(this, i));
    }
}

public void OnToggle(int i, bool on) => BasisSettingFavouritesShim.SetOn(this, i, on);
public void OnSlider(int i, float t) => BasisSettingFavouritesShim.SetNormalized(this, i, t);
public void OnButton(int i) => BasisSettingFavouritesShim.Press(this, i);
```

Choices use `GetOptionCount`, `GetOptionLabel`, `GetSelected` and `Select`. Sliders also have `GetValue`, `SetValue`, `GetMin`, `GetMax` and `FormatValue`.
