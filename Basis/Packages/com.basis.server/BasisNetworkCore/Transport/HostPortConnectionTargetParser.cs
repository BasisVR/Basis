using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Basis.Network.Core
{
    public sealed class HostPortConnectionTargetParser : IConnectionTargetParser
    {
        public const ushort DefaultPort = 4296;

        public void Parse(ConnectionTarget target)
        {
            if (target == null) return;
            if (TryParseConnectionString(target.Raw, out string address, out ushort port, out _, out string password))
            {
                target.Set(ConnectionTarget.Keys.Address, address);
                target.Set(ConnectionTarget.Keys.Port, port.ToString(CultureInfo.InvariantCulture));
                target.Set(ConnectionTarget.Keys.Password, password ?? string.Empty);
            }
        }

        public string Format(ConnectionTarget target)
        {
            if (target == null) return string.Empty;
            string address = target.Get(ConnectionTarget.Keys.Address, string.Empty);
            string portString = target.Get(ConnectionTarget.Keys.Port, DefaultPort.ToString(CultureInfo.InvariantCulture));
            string password = target.Get(ConnectionTarget.Keys.Password, string.Empty);

            bool isIPv6 = IPAddress.TryParse(address, out IPAddress ip)
                && ip.AddressFamily == AddressFamily.InterNetworkV6;
            string s = isIPv6 ? $"[{address}]:{portString}" : $"{address}:{portString}";
            if (!string.IsNullOrEmpty(password)) s += "#" + password;
            return s;
        }

        public static bool TryParseConnectionString(
            string raw, out string address, out ushort port, out bool portProvided, out string password)
        {
            address = string.Empty;
            port = DefaultPort;
            portProvided = false;
            password = string.Empty;
            if (string.IsNullOrEmpty(raw)) return false;

            string left = raw;
            int hashIdx = raw.IndexOf('#');
            if (hashIdx >= 0)
            {
                password = raw.Substring(hashIdx + 1);
                left = raw.Substring(0, hashIdx);
            }

            if (left.Length > 0 && left[0] == '[')
            {
                // Bracketed IPv6 literal: [addr]:port or [addr]
                int closeBracket = left.IndexOf(']');
                if (closeBracket > 0)
                {
                    address = left.Substring(1, closeBracket - 1).Trim();
                    string afterBracket = left.Substring(closeBracket + 1);
                    if (afterBracket.Length > 1 && afterBracket[0] == ':')
                    {
                        if (ushort.TryParse(afterBracket.Substring(1), out ushort parsedPort) && parsedPort > 0)
                        {
                            port = parsedPort;
                            portProvided = true;
                        }
                    }
                }
                else
                {
                    address = left.Trim(); // malformed bracket — treat whole string as address
                }
            }
            else
            {
                int colonIdx = left.LastIndexOf(':');
                if (colonIdx > 0
                    && colonIdx < left.Length - 1
                    && ushort.TryParse(left.Substring(colonIdx + 1), out ushort parsedPort)
                    && parsedPort > 0)
                {
                    string candidateAddress = left.Substring(0, colonIdx).Trim();
                    // If the candidate address still contains a colon it is a bare IPv6
                    // literal (e.g. "::1", "2001:db8::1") being misread as host:port.
                    // Leave it unsplit; the user must use [addr]:port notation for
                    // an IPv6 address with an explicit port.
                    if (candidateAddress.IndexOf(':') < 0)
                    {
                        address = candidateAddress;
                        port = parsedPort;
                        portProvided = true;
                    }
                    else
                    {
                        address = left.Trim();
                    }
                }
                else
                {
                    address = left.Trim();
                }
            }

            return !string.IsNullOrEmpty(address);
        }
    }
}
