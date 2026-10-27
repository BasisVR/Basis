`BasisMenuShim` adds UI to the player's own menu. Both calls return null when refused, so always check.

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

`Open()` only works within 5 seconds of the player pressing something in your content, so content cannot pop menus open by itself.

```csharp
BasisMenuPage arcade;

void Start() { arcade = BasisMenuShim.AddPanel(this, "Arcade"); }

public void OnKioskPressed() { arcade?.Open(); }
```

Limits: 64 items per page, 32 dropdown options, 48 character titles. More: [Settings Tab example](/en/docs/scripting/examples/settings-tab).
