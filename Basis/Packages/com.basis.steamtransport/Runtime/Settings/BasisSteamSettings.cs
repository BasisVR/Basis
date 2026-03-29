using UnityEngine;

namespace Basis.Scripts.Networking.Steam
{
    [CreateAssetMenu(fileName = "BasisSteamSettings", menuName = "Basis/Steam Settings")]
    public class BasisSteamSettings : ScriptableObject
    {
        public const string DefaultResourcesPath = "BasisSteamSettings";

        [Header("App")]
        public uint AppId = 480;

        [Header("Initialization")]
        public bool RestartAppIfNecessary = false;
        public bool AutoInitialize = true;
        public bool RunCallbacksManually = true;

        [Header("Lobby Defaults")]
        [Range(2, 250)]
        public int DefaultMaxLobbyMembers = 32;
        public int RelayVirtualPort = 0;
        public bool UseRelayByDefault = true;
        public bool CreateFriendsOnlyByDefault = true;

        [Header("Debug")]
        public bool EnableTransportTrace = false;
    }
}
