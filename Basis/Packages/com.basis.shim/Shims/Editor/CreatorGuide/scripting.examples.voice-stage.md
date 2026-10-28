```csharp title="VoiceStage.cs (Cilbox, world)"
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class VoiceStage : MonoBehaviour
{
    public Collider stageArea;
    public Transform speaker;
    public float range = 60f;

    int route = -1;
    ushort routedPlayer;
    float nextCheck;

    void Update()
    {
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + 0.5f;

        BasisNetworkPlayer onStage = null;
        foreach (BasisNetworkPlayer p in BasisNetworkPlayer.GetAllPlayers())
        {
            if (p.Player != null && stageArea.bounds.Contains(p.Player.GetPosition())) { onStage = p; break; }
        }

        if (onStage != null && route >= 0 && onStage.playerId == routedPlayer) return;
        Stop();
        if (onStage == null || !BasisVoiceRoutingShim.HasConsent(onStage)) return;
        route = BasisVoiceRoutingShim.RouteVoiceToObject(onStage, speaker, 2f, range);
        routedPlayer = onStage.playerId;
    }

    void Stop()
    {
        if (route >= 0) BasisVoiceRoutingShim.StopVoiceRoute(route);
        route = -1;
    }

    void OnDestroy() => Stop();
}
```

- Every client routes the voice locally from positions it already has. There is no extra network traffic.
- The speaker's consent is checked first. Players who have not allowed voice routing are heard normally.
- Check in **Basis > Tools > Cilbox Permissions** that `BasisNetworkPlayer.GetAllPlayers` is allowed in your sandbox.
