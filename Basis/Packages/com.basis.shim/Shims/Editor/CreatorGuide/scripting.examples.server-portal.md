```csharp title="ServerPortal.cs (Cilbox)"
using Basis.Shims;
using Cilbox;
using TMPro;
using UnityEngine;

[Cilboxable]
public class ServerPortal : MonoBehaviour
{
    public string host = "play.example.com";
    public int port = 4296;
    public TextMeshPro sign;

    void Start()
    {
        GetComponent<BasisInteractableShim>().ButtonDown += OnPressed;
        sign.text = host;
    }

    void OnPressed()
    {
        BasisConnectionShim.RequestConnect(this, host, port, null, result =>
        {
            if (result == BasisConnectionResult.Declined) sign.text = "Maybe next time";
            else if (result != BasisConnectionResult.Accepted) sign.text = $"Can't connect: {result}";
        });
    }
}
```

- The request must come from the player's own press, and the player always sees the address and confirms.
- Pass a password as the fourth argument for private servers.
