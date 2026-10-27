## Setup

1. Create an empty GameObject and add **Basis Scene** (for a world) or **Basis Prop** (for a prop).
2. Add **Cilbox Scene Basis** (world) or the matching Cilbox component for your content type. This is the sandbox all scripts in the content run in.
3. Add your scripts to objects under it.

> [!info]
> If your editor cannot find the `Cilbox` namespace, add a reference to the Cilbox assembly in your own `.asmdef`, or turn on **Auto Referenced** on the Cilbox assembly.

## A spinning object

```csharp title="Spinner.cs"
using UnityEngine;
using Cilbox;

[Cilboxable]
public class Spinner : MonoBehaviour
{
    public Vector3 degreesPerSecond = new Vector3(0f, 90f, 0f);

    void Update()
    {
        transform.Rotate(degreesPerSecond * Time.deltaTime);
    }
}
```

Public fields and `[SerializeField]` fields are saved with the content, so you can set them in the inspector.

## A button that changes text

1. Create a world space **Canvas** with a **Button** and a **TextMeshPro - Text (UI)**.
2. Add this script to the canvas and assign the button and the text.

```csharp title="ClickCounter.cs"
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Cilbox;

[Cilboxable]
public class ClickCounter : MonoBehaviour
{
    public Button button;
    public TextMeshProUGUI label;
    int clicks;

    void Start()
    {
        button.onClick.AddListener(OnClick);
        label.text = "Click me";
    }

    void OnDestroy()
    {
        button.onClick.RemoveAllListeners();
    }

    void OnClick()
    {
        clicks++;
        label.text = $"Clicked {clicks} times";
    }
}
```

This counter is local: every player has their own count. The [Synced Toggle](/en/docs/scripting/examples/synced-toggle) example makes a button everyone shares.

## Testing

Enter play mode in your scene. Basis swaps each `[Cilboxable]` script for a `CilboxProxy`, so you are testing exactly what runs after upload. Then build the content and load it in the client as described in [Building](/en/docs/world/building).
