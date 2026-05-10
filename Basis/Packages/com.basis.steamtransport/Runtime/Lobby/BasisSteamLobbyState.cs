using System;

namespace Basis.Scripts.Networking.Steam
{
    [Serializable]
    public class BasisSteamLobbyState
    {
        public ulong LobbyId;
        public ulong HostSteamId;
        public string LobbyName = string.Empty;
        public string WorldUrl = string.Empty;
        public string WorldName = string.Empty;
        public int VirtualPort;
        public bool UseRelay = true;
        public bool IsHost;

        public void Reset()
        {
            LobbyId = 0;
            HostSteamId = 0;
            LobbyName = string.Empty;
            WorldUrl = string.Empty;
            WorldName = string.Empty;
            VirtualPort = 0;
            UseRelay = true;
            IsHost = false;
        }
    }
}
