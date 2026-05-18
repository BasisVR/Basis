using Basis.Network.Core;
using System;
using System.Globalization;
using UnityEngine;

namespace Basis.Scripts.Networking.Steam
{
    [Serializable]
    public class BasisSteamTransportConfig
    {
        public ulong LobbyId;
        public ulong HostSteamId;
        public int VirtualPort;
        public bool UseSteamRelay = true;

        public void Clear()
        {
            LobbyId = 0;
            HostSteamId = 0;
            VirtualPort = 0;
            UseSteamRelay = true;
        }
    }

    public static class BasisSteamNetworkStack
    {
        public const string StackId = "steam";
        public const string DisplayName = "Steam";
        public const string HostSteamIdKey = "hostSteamId";
        public const string VirtualPortKey = "virtualPort";
        public const string UseRelayKey = "useRelay";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Initialize()
        {
            Register();
        }

        public static void Register()
        {
            BasisNetworkStackRegistry.Register(StackId, DisplayName, Create);
            BasisNetworkStackRegistry.RegisterParser(StackId, new BasisSteamConnectionTargetParser());
            BasisNetworkStackRegistry.RegisterTick(StackId, SteamNetManager.PollActiveManagers);
            BasisTransportConfigStore.RegisterType(StackId, typeof(BasisSteamTransportConfig));
        }

        public static NetManager Create(EventBasedNetListener listener, Configuration configuration)
        {
            return new SteamNetManager(listener, configuration);
        }

        public static BasisSteamTransportConfig GetConfig()
        {
            return BasisTransportConfigStore.Get<BasisSteamTransportConfig>(StackId);
        }
    }

    internal sealed class BasisSteamConnectionTargetParser : IConnectionTargetParser
    {
        public void Parse(ConnectionTarget target)
        {
            if (target == null)
            {
                return;
            }

            string raw = target.Raw ?? string.Empty;
            if (raw.StartsWith("steam://lobby/", StringComparison.OrdinalIgnoreCase))
            {
                target.Set(ConnectionTarget.Keys.LobbyId, raw.Substring("steam://lobby/".Length));
                return;
            }

            if (raw.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            {
                target.Set(ConnectionTarget.Keys.LobbyId, raw.Substring("steam://".Length));
                return;
            }

            if (raw.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
            {
                target.Set(ConnectionTarget.Keys.LobbyId, raw.Substring("steam:".Length));
                return;
            }

            int separator = raw.IndexOf(':');
            if (separator > 0)
            {
                target.Set(BasisSteamNetworkStack.HostSteamIdKey, raw.Substring(0, separator));
                target.Set(BasisSteamNetworkStack.VirtualPortKey, raw.Substring(separator + 1));
                return;
            }

            if (!string.IsNullOrWhiteSpace(raw))
            {
                target.Set(BasisSteamNetworkStack.HostSteamIdKey, raw);
            }
        }

        public string Format(ConnectionTarget target)
        {
            if (target == null)
            {
                return string.Empty;
            }

            string lobbyId = target.Get(ConnectionTarget.Keys.LobbyId);
            if (!string.IsNullOrWhiteSpace(lobbyId))
            {
                return "steam://lobby/" + lobbyId;
            }

            string hostSteamId = target.Get(BasisSteamNetworkStack.HostSteamIdKey);
            if (string.IsNullOrWhiteSpace(hostSteamId))
            {
                hostSteamId = target.Get(ConnectionTarget.Keys.Address);
            }

            string virtualPort = target.Get(BasisSteamNetworkStack.VirtualPortKey);
            if (string.IsNullOrWhiteSpace(virtualPort))
            {
                virtualPort = target.Get(ConnectionTarget.Keys.Port);
            }

            if (string.IsNullOrWhiteSpace(hostSteamId))
            {
                return target.Raw ?? string.Empty;
            }

            if (int.TryParse(virtualPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort))
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}:{1}", hostSteamId, parsedPort);
            }

            return hostSteamId;
        }
    }
}
