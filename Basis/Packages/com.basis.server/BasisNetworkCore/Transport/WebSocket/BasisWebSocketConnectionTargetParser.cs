using System;
using System.Globalization;

namespace Basis.Network.Core
{
    public sealed class BasisWebSocketConnectionTargetParser : IConnectionTargetParser
    {
        public void Parse(ConnectionTarget target)
        {
            if (target == null) return;
            if (TryParse(target.Raw, out string address, out ushort port, out string password))
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
            string password = target.Get(ConnectionTarget.Keys.Password, string.Empty);
            if (IsUrl(address))
            {
                return string.IsNullOrEmpty(password) ? address : address + "#" + password;
            }
            return new LNLConnectionTargetParser().Format(target);
        }

        public static bool IsUrl(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string trimmed = value.TrimStart();
            return trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryParse(string raw, out string address, out ushort port, out string password)
        {
            address = string.Empty;
            port = LNLConnectionTargetParser.DefaultPort;
            password = string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string text = raw.Trim();
            if (!IsUrl(text))
            {
                return LNLConnectionTargetParser.TryParseConnectionString(text, out address, out port, out _, out password);
            }
            int hash = text.IndexOf('#');
            if (hash >= 0)
            {
                password = text.Substring(hash + 1);
                text = text.Substring(0, hash);
            }
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) text = "ws://" + text.Substring(7);
            else if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) text = "wss://" + text.Substring(8);
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri) || string.IsNullOrEmpty(uri.Host)) return false;
            bool secure = string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
            int parsedPort = uri.Port > 0 ? uri.Port : (secure ? 443 : 80);
            port = (ushort)parsedPort;
            address = text;
            return true;
        }
    }
}
