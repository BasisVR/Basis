`BasisMenuShim` adds a tab to the player's settings while your content is loaded. The tab disappears when the world unloads.

## Script

```csharp title="WorldOptions.cs (Cilbox)"
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class WorldOptions : MonoBehaviour
{
    public GameObject rain;
    public GameObject ambientMusic;
    public Light sun;

    BasisMenuPage page;
    BasisMenuItem brightness;

    void Start()
    {
        page = BasisMenuShim.AddSettingsTab(this, "Island");
        if (page == null) return;

        page.AddHeader("Weather");
        page.AddToggle("Rain", rain.activeSelf, on => rain.SetActive(on));
        page.AddDropdown("Time of day", new[] { "Morning", "Noon", "Dusk" }, 1, OnTimeOfDay);

        page.AddHeader("Audio");
        page.AddToggle("Music", ambientMusic.activeSelf, on => ambientMusic.SetActive(on));

        page.AddHeader("Lighting");
        brightness = page.AddSlider("Sun brightness", 0, 2, 1, false, value => sun.intensity = value);
        page.AddButton("Reset", OnReset);
    }

    void OnTimeOfDay(int index)
    {
        float angle = index == 0 ? 20f : index == 1 ? 70f : 170f;
        sun.transform.rotation = Quaternion.Euler(angle, -30f, 0f);
    }

    void OnReset()
    {
        brightness.SetValue(1);
    }
}
```

## A whole panel

For a game menu rather than options, add a panel. It gets its own button in the menu, and can have several tabs.

```csharp title="Cilbox"
BasisMenuPage game = BasisMenuShim.AddPanel(this, "Arcade");
if (game == null) return;
BasisMenuItem score = game.AddLabel("Score: 0");
game.AddButton("New game", OnNewGame);
BasisMenuPage rules = game.AddTab("Rules");
rules.AddLabel("Hit every target before the timer runs out.");
```

`game.Open()` only works within 5 seconds of the player pressing something in your content. Content cannot pop menus open on its own.

## Rules

- Who may add a tab: worlds, props the local player spawned, and the local player's own avatar. `null` means refused; check it.
- Options are local. Each player sets their own. Sync a choice only if it should change the world for everyone.
- Remember choices with [BasisSaveShim](/en/docs/scripting/examples/persistence) if they should survive the next visit.

> [!info]
> On an avatar, add the tab from `BasisAvatarShim.OnAvatarReady` when `isOwner` is true. Only the wearer gets it.
