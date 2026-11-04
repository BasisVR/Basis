```csharp title="LocalizedSign.cs (Cilbox)"
using Basis.Shims;
using Cilbox;
using TMPro;
using UnityEngine;

[Cilboxable]
public class LocalizedSign : MonoBehaviour
{
    public TextMeshPro label;
    public string[] languageCodes = { "en", "de", "fr", "ja" };
    public string[] texts = { "Welcome", "Willkommen", "Bienvenue", "ようこそ" };

    void Start()
    {
        GetComponent<BasisLanguageEventShim>();
    }

    void OnLanguageChanged(string code)
    {
        label.text = texts[0];
        for (int i = 0; i < languageCodes.Length; i++)
        {
            if (code.StartsWith(languageCodes[i])) { label.text = texts[i]; return; }
        }
    }
}
```

- `OnLanguageChanged` is called once straight away with the current language.
- Matching with `StartsWith` lets `es` cover `es-MX`.
- Set the arrays in the inspector rather than with an initializer if your sandbox rejects array initializers, and make sure the font has the glyphs you need.
