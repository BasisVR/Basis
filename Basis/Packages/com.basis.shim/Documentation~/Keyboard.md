# Keyboard (BasisKeyboardShim)

Status: implemented in `Shims/BasisKeyboardShim.cs`, on top of the text request mode in `com.basis.framework/BasisUI/BasisMenuVirtualKeyboardTextRequest.cs`. Compiled only, not run.

## Why

A script with no UI input field of its own (a sign, a name tag, a search box drawn on a mesh) had no way to ask the player for text. The Basis keyboard only opened when the player selected a Unity input field.

## API

```csharp
namespace Basis.Shims
{
    public enum BasisKeyboardContent { Standard = 0, Integer = 1, Decimal = 2, Alphanumeric = 3 }

    public static class BasisKeyboardShim
    {
        public static bool Open(object self, Action<string> onSubmit);
        public static bool Open(object self, string text, Action<string> onSubmit);
        public static bool Open(object self, string text, string prompt, Action<string> onSubmit, Action onCancel);
        public static bool Open(object self, string text, string prompt, int characterLimit, BasisKeyboardContent content, Action<string> onSubmit, Action onCancel);
        public static bool IsOpen(object self);
        public static string GetText(object self); // null when this script's request is not open
        public static void Close(object self);
    }
}
```

Pass `this`. `text` is what the field starts with, `prompt` shows as the placeholder while it is empty (128 characters at most), and `characterLimit` 0 means the 4096 cap.

```csharp
void OnSignClicked() => BasisKeyboardShim.Open(this, signText, "Write on the sign", OnSignWritten, null);
void OnSignWritten(string text) { signText = text; }
```

Pass a method, not a lambda.

## Behaviour

- The keyboard opens in the main menu (opening the menu if needed) with the title "Text request from <content name>". For someone else's prop the owner's name is added.
- A yellow warning row sits under the text for the whole request: the named content is asking, not Basis, and the player should never type or paste a password there. Scripts cannot hide or restyle it.
- Enter calls `onSubmit` with the text and closes the keyboard. Close, closing the menu, or the player selecting a different input field calls `onCancel`.
- Another script opening the keyboard cancels the current request first. `Close` from the script that opened it closes without calling `onCancel`.
- On desktop the field takes focus, so the physical keyboard types into it and Enter submits.
- Callbacks are dropped once the script is disabled or destroyed.

## Who can open it

Worlds, props, and the avatar the local player is wearing. Someone else's avatar gets `false`. A script that opened the keyboard less than 2 seconds ago gets `false`, unless its request is still open.
