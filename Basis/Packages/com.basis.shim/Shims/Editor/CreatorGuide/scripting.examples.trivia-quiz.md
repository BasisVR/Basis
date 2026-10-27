## Questions file

Host a JSON file over https:

```json
{ "questions": [
  { "q": "What does BEE stand for?", "a": ["Basis Encrypted Experience", "Big Event Engine"], "correct": 0 }
] }
```

## Script

```csharp title="TriviaQuiz.cs (Cilbox)"
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Shims;
using Cilbox;
using TMPro;
using UnityEngine;

[Cilboxable]
public class TriviaQuiz : MonoBehaviour
{
    public string url = "https://example.github.io/questions.json";
    public TextMeshPro questionLabel;
    public TextMeshPro[] answerLabels;

    BasisNetworkShim net;
    BasisJsonNode questions;
    int index;

    void Start()
    {
        net = GetComponent<BasisNetworkShim>();
        net.NetworkMessageReceived += OnMessage;
        net.PlayerJoined += OnPlayerJoined;
        questionLabel.text = "Loading...";
        new BasisStringDownloader().DownloadString(url, result =>
        {
            if (!result.Success) { questionLabel.text = "Could not load questions"; return; }
            BasisJsonNode root = BasisJson.Parse(result.Result);
            questions = root != null ? root.Get("questions") : null;
            Show();
        });
    }

    public void NextQuestion()
    {
        if (questions == null || questions.Count == 0) return;
        net.TakeOwnership();
        index = (index + 1) % questions.Count;
        Show();
        if (net.HasNetworkID) net.SendCustomNetworkEvent(new[] { (byte)index }, DeliveryMethod.ReliableOrdered);
    }

    void OnMessage(ushort sender, byte[] data, DeliveryMethod method)
    {
        if (data == null || data.Length < 1) return;
        index = data[0];
        Show();
    }

    void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (net.HasNetworkID && net.IsOwnedLocallyOnServer) net.SendCustomNetworkEvent(new[] { (byte)index }, DeliveryMethod.ReliableOrdered, new[] { player.playerId });
    }

    void Show()
    {
        if (questions == null || index >= questions.Count) return;
        BasisJsonNode q = questions.Get(index);
        questionLabel.text = q.GetString("q", "");
        string[] answers = q.GetStringArray("a");
        for (int i = 0; i < answerLabels.Length; i++) answerLabels[i].text = i < answers.Length ? answers[i] : "";
    }
}
```

- Only the **question index** is synced; every client downloads the same file.
- Players approve the host the first time. Until then the sign says it is loading.
