using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;

public class BasisUnInitalizedPlayer : BasisNetworkPlayer
{
    public BasisUnInitalizedPlayer(ushort PlayerID)
    {
        playerId = PlayerID;
        hasID = true;
    }
    public override void DeInitialize()
    {
    }

    public override void Initialize()
    {

    }
}
