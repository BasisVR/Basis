using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Basis.Network.Core
{
    public enum BasisPortMappingMethod : byte
    {
        None,
        Pcp,
        NatPmp,
        Upnp
    }
    public sealed class BasisPortMapper : IDisposable
    {
        public const int LeaseSeconds = 3600, GatewayPort = 5351, SsdpPort = 1900, SearchMs = 2500, IdleRetryMs = 600000, PermanentRenewMs = 1800000;
        public static bool Enabled = true;
        public readonly int InternalPort;
        public readonly bool Udp;
        public readonly string Description;
        public BasisPortMappingMethod Method { get; private set; }
        public IPAddress ExternalAddress { get; private set; }
        public int ExternalPort { get; private set; }
        public bool ExternalIsPublic => ExternalAddress != null && !IsPrivate(ExternalAddress);
        public event Action<BasisPortMapper> Changed;
        private static readonly string[] searchTargets = { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2", "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANIPConnection:2", "urn:schemas-upnp-org:service:WANPPPConnection:1" };
        private static readonly Regex locationRegex = new Regex(@"^location:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline), serviceRegex = new Regex(@"<service>(.*?)</service>", RegexOptions.IgnoreCase | RegexOptions.Singleline), errorRegex = new Regex(@"<errorCode>\s*(\d+)\s*</errorCode>", RegexOptions.IgnoreCase);
        private static readonly IPAddress routeProbe = new IPAddress(new byte[] { 198, 51, 100, 1 }), ssdpGroup = new IPAddress(new byte[] { 239, 255, 255, 250 });
        private readonly object gate = new object();
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly byte[] nonce = new byte[12];
        private readonly Thread worker;
        private IPAddress gateway, local;
        private string upnpControl, upnpService;
        private int lifetime;
        private bool reportedNone;
        private volatile bool disposed;
        private BasisPortMapper(int internalPort, string description, bool udp)
        {
            InternalPort = internalPort;
            Description = string.IsNullOrWhiteSpace(description) ? "Basis" : description;
            Udp = udp;
            using (System.Security.Cryptography.RandomNumberGenerator rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(nonce);
            }
            worker = new Thread(Run) { IsBackground = true, Name = "Basis Port Mapper " + internalPort };
        }
        public static BasisPortMapper Start(int internalPort, string description, bool udp = true)
        {
            if (!Enabled || internalPort <= 0 || internalPort > ushort.MaxValue)
            {
                return null;
            }
            BasisPortMapper mapper = new BasisPortMapper(internalPort, description, udp);
            try
            {
                mapper.worker.Start();
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"[PortMap] Could not start the port mapper: {ex.Message}");
                return null;
            }
            return mapper;
        }
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            stop.Set();
            if (worker.IsAlive && Thread.CurrentThread != worker)
            {
                worker.Join(1500);
            }
            lock (gate)
            {
                Remove();
            }
        }
        private void Run()
        {
            int failures = 0;
            while (!disposed)
            {
                int wait;
                bool ok;
                try
                {
                    ok = TryMap();
                }
                catch (Exception ex)
                {
                    BNL.LogWarning($"[PortMap] {Description}: mapping attempt failed: {ex.Message}");
                    ok = false;
                }
                if (ok)
                {
                    failures = 0;
                    wait = lifetime > 0 ? Math.Max(30, lifetime / 2) * 1000 : PermanentRenewMs;
                }
                else
                {
                    failures++;
                    wait = failures < 3 ? 30000 : IdleRetryMs;
                }
                if (stop.WaitOne(wait))
                {
                    break;
                }
            }
        }
        private bool TryMap()
        {
            IPAddress gw = FindGateway(out IPAddress lan);
            bool ok = Method == BasisPortMappingMethod.Upnp && TryUpnp(lan);
            if (!ok && gw != null && !disposed)
            {
                int pcp = TryPcp(gw, lan, LeaseSeconds);
                ok = pcp > 0 || (pcp == 0 && TryPmp(gw, LeaseSeconds));
            }
            if (!ok && Method != BasisPortMappingMethod.Upnp && !disposed)
            {
                ok = TryUpnp(lan);
            }
            if (ok)
            {
                lock (gate)
                {
                    gateway = gw;
                    local = lan;
                }
                return true;
            }
            if (Method != BasisPortMappingMethod.None)
            {
                BNL.LogWarning($"[PortMap] {Description}: the router stopped renewing the forward for {Proto} {InternalPort}.");
                Apply(BasisPortMappingMethod.None, null, 0, 0);
            }
            else if (!reportedNone && !disposed)
            {
                reportedNone = true;
                BNL.Log($"[PortMap] {Description}: no router answered PCP, NAT-PMP or UPnP; forward {Proto} port {InternalPort} by hand if players outside this network cannot reach it.");
            }
            return false;
        }
        private string Proto => Udp ? "UDP" : "TCP";
        private void Apply(BasisPortMappingMethod method, IPAddress address, int port, int life)
        {
            bool changed;
            lock (gate)
            {
                if (disposed && method != BasisPortMappingMethod.None)
                {
                    return;
                }
                changed = Method != method || ExternalPort != port || !Equals(ExternalAddress, address);
                Method = method;
                ExternalAddress = address;
                ExternalPort = port;
                lifetime = life;
            }
            if (!changed)
            {
                return;
            }
            if (method != BasisPortMappingMethod.None)
            {
                reportedNone = false;
                string where = address == null ? "port " + port : address + ":" + port;
                BNL.Log($"[PortMap] {Description}: {Proto} {InternalPort} forwarded by the router as {where} via {method}.");
                if (address != null && IsPrivate(address))
                {
                    BNL.LogWarning($"[PortMap] {Description}: the router's outside address {address} is itself private (double NAT or carrier-grade NAT), so the forward only reaches the next network up.");
                }
            }
            try
            {
                Changed?.Invoke(this);
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"[PortMap] Changed handler threw: {ex.Message}");
            }
        }
        private void Remove()
        {
            BasisPortMappingMethod method = Method;
            try
            {
                if (method == BasisPortMappingMethod.Pcp && gateway != null)
                {
                    byte[] req = PcpRequest(local, 0);
                    SendOnly(gateway, GatewayPort, req);
                }
                else if (method == BasisPortMappingMethod.NatPmp && gateway != null)
                {
                    byte[] req = new byte[12];
                    req[1] = (byte)(Udp ? 1 : 2);
                    WriteU16(req, 4, InternalPort);
                    SendOnly(gateway, GatewayPort, req);
                }
                else if (method == BasisPortMappingMethod.Upnp && upnpControl != null)
                {
                    Soap("DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + ExternalPort + "</NewExternalPort><NewProtocol>" + Proto + "</NewProtocol>", 1000, out _);
                }
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"[PortMap] {Description}: removing the forward failed: {ex.Message}");
            }
            if (method != BasisPortMappingMethod.None)
            {
                BNL.Log($"[PortMap] {Description}: removed the {method} forward for {Proto} {InternalPort}.");
            }
            Method = BasisPortMappingMethod.None;
            ExternalAddress = null;
            ExternalPort = 0;
        }
        private byte[] PcpRequest(IPAddress lan, int life)
        {
            byte[] req = new byte[60];
            req[0] = 2;
            req[1] = 1;
            WriteU32(req, 4, (uint)life);
            WriteMapped(req, 8, lan ?? IPAddress.Any);
            Buffer.BlockCopy(nonce, 0, req, 24, 12);
            req[36] = (byte)(Udp ? 17 : 6);
            WriteU16(req, 40, InternalPort);
            WriteU16(req, 42, ExternalPort > 0 ? ExternalPort : InternalPort);
            WriteMapped(req, 44, IPAddress.Any);
            return req;
        }
        private int TryPcp(IPAddress gw, IPAddress lan, int life)
        {
            if (lan == null)
            {
                return 0;
            }
            byte[] res = Exchange(gw, GatewayPort, PcpRequest(lan, life), out int len);
            if (res == null)
            {
                return -1;
            }
            if (len < 60 || res[0] != 2 || res[1] != 0x81 || res[3] != 0)
            {
                return 0;
            }
            for (int i = 0; i < 12; i++)
            {
                if (res[24 + i] != nonce[i])
                {
                    return 0;
                }
            }
            Apply(BasisPortMappingMethod.Pcp, ReadMapped(res, 44), ReadU16(res, 42), (int)Math.Min(ReadU32(res, 4), int.MaxValue));
            return 1;
        }
        private bool TryPmp(IPAddress gw, int life)
        {
            IPAddress external = null;
            byte[] res = Exchange(gw, GatewayPort, new byte[2], out int len);
            if (res != null && len >= 12 && res[0] == 0 && res[1] == 128 && ReadU16(res, 2) == 0)
            {
                external = new IPAddress(new[] { res[8], res[9], res[10], res[11] });
            }
            byte op = (byte)(Udp ? 1 : 2);
            byte[] req = new byte[12];
            req[1] = op;
            WriteU16(req, 4, InternalPort);
            WriteU16(req, 6, ExternalPort > 0 ? ExternalPort : InternalPort);
            WriteU32(req, 8, (uint)life);
            res = Exchange(gw, GatewayPort, req, out len);
            if (res == null || len < 16 || res[0] != 0 || res[1] != 128 + op || ReadU16(res, 2) != 0 || ReadU16(res, 8) != InternalPort)
            {
                return false;
            }
            Apply(BasisPortMappingMethod.NatPmp, external, ReadU16(res, 10), (int)Math.Min(ReadU32(res, 12), int.MaxValue));
            return true;
        }
        private bool TryUpnp(IPAddress lan)
        {
            for (int attempt = 0; attempt < 2 && !disposed; attempt++)
            {
                bool cached = upnpControl != null;
                if (!cached && !DiscoverUpnp(lan))
                {
                    return false;
                }
                IPAddress client = UpnpClientAddress(lan);
                if (client == null)
                {
                    return false;
                }
                int status = AddUpnp(client, out int external, out int life);
                if (status == 200)
                {
                    Apply(BasisPortMappingMethod.Upnp, UpnpExternalAddress(), external, life);
                    return true;
                }
                upnpControl = null;
                if (status != 0 || !cached)
                {
                    return false;
                }
            }
            return false;
        }
        private IPAddress UpnpClientAddress(IPAddress lan)
        {
            if (Uri.TryCreate(upnpControl, UriKind.Absolute, out Uri uri) && IPAddress.TryParse(uri.Host, out IPAddress host))
            {
                return LocalFor(host) ?? lan;
            }
            return lan;
        }
        private int AddUpnp(IPAddress client, out int external, out int life)
        {
            external = 0;
            life = LeaseSeconds;
            int port = ExternalPort > 0 ? ExternalPort : InternalPort;
            for (int tries = 0; tries < 6 && !disposed; tries++)
            {
                string args = "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>" + Proto + "</NewProtocol><NewInternalPort>" + InternalPort + "</NewInternalPort><NewInternalClient>" + client + "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>" + Escape(Description) + "</NewPortMappingDescription><NewLeaseDuration>" + life + "</NewLeaseDuration>";
                string body = Soap("AddPortMapping", args, 3000, out int status);
                if (status == 200)
                {
                    external = port;
                    return 200;
                }
                if (status != 500 || body == null)
                {
                    return status;
                }
                Match error = errorRegex.Match(body);
                int code = error.Success && int.TryParse(error.Groups[1].Value, out int parsed) ? parsed : 0;
                if (code == 725 && life != 0)
                {
                    life = 0;
                }
                else if (code == 718 || code == 724)
                {
                    port = port >= ushort.MaxValue ? 1024 : port + 1;
                }
                else
                {
                    BNL.LogWarning($"[PortMap] {Description}: the router refused the UPnP forward for {Proto} {InternalPort} (error {code}).");
                    return 500;
                }
            }
            return 500;
        }
        private IPAddress UpnpExternalAddress()
        {
            string body = Soap("GetExternalIPAddress", string.Empty, 3000, out int status);
            string value = status == 200 ? Tag(body, "NewExternalIPAddress") : null;
            return value != null && IPAddress.TryParse(value.Trim(), out IPAddress address) ? address : null;
        }
        private bool DiscoverUpnp(IPAddress lan)
        {
            List<string> locations = new List<string>();
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Bind(new IPEndPoint(lan ?? IPAddress.Any, 0));
                socket.ReceiveTimeout = 250;
                IPEndPoint group = new IPEndPoint(ssdpGroup, SsdpPort);
                foreach (string target in searchTargets)
                {
                    byte[] search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + target + "\r\n\r\n");
                    socket.SendTo(search, group);
                }
                byte[] buffer = new byte[4096];
                long deadline = NowMs() + SearchMs;
                while (NowMs() < deadline && !disposed)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int read;
                    try
                    {
                        read = socket.ReceiveFrom(buffer, ref from);
                    }
                    catch (SocketException)
                    {
                        continue;
                    }
                    Match match = locationRegex.Match(Encoding.ASCII.GetString(buffer, 0, read));
                    if (!match.Success || !Uri.TryCreate(match.Groups[1].Value.Trim(), UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttp)
                    {
                        continue;
                    }
                    if (!IPAddress.TryParse(uri.Host, out IPAddress host) || !host.Equals(((IPEndPoint)from).Address) || !IsPrivate(host))
                    {
                        continue;
                    }
                    string location = uri.ToString();
                    if (!locations.Contains(location))
                    {
                        locations.Add(location);
                    }
                }
            }
            int bestRank = int.MaxValue;
            foreach (string location in locations)
            {
                Uri locationUri = new Uri(location);
                string xml = Http(locationUri, "GET", null, null, 3000, out int status);
                if (status != 200 || xml == null)
                {
                    continue;
                }
                string urlBase = Tag(xml, "URLBase");
                Uri baseUri = urlBase != null && Uri.TryCreate(urlBase.Trim(), UriKind.Absolute, out Uri parsedBase) && parsedBase.Host == locationUri.Host ? parsedBase : locationUri;
                foreach (Match service in serviceRegex.Matches(xml))
                {
                    string type = Tag(service.Value, "serviceType"), control = Tag(service.Value, "controlURL");
                    if (type == null || control == null)
                    {
                        continue;
                    }
                    type = type.Trim();
                    int rank = type.Contains(":WANIPConnection:2") ? 0 : type.Contains(":WANIPConnection:1") ? 1 : type.Contains(":WANPPPConnection:1") ? 2 : int.MaxValue;
                    if (rank >= bestRank || !Uri.TryCreate(baseUri, control.Trim(), out Uri controlUri) || controlUri.Host != locationUri.Host)
                    {
                        continue;
                    }
                    bestRank = rank;
                    upnpService = type;
                    upnpControl = controlUri.ToString();
                }
                if (bestRank == 0)
                {
                    break;
                }
            }
            return upnpControl != null;
        }
        private string Soap(string action, string args, int timeoutMs, out int status)
        {
            string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" + action + " xmlns:u=\"" + upnpService + "\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
            return Http(new Uri(upnpControl), "POST", upnpService + "#" + action, body, timeoutMs, out status);
        }
        private static string Http(Uri uri, string method, string soapAction, string body, int timeoutMs, out int status)
        {
            status = 0;
            try
            {
                using (TcpClient tcp = new TcpClient(AddressFamily.InterNetwork))
                {
                    if (!tcp.ConnectAsync(uri.Host, uri.Port).Wait(timeoutMs))
                    {
                        return null;
                    }
                    tcp.ReceiveTimeout = tcp.SendTimeout = timeoutMs;
                    byte[] payload = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
                    StringBuilder head = new StringBuilder();
                    head.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.0\r\nHost: ").Append(uri.Host).Append(':').Append(uri.Port).Append("\r\nConnection: close\r\n");
                    if (soapAction != null)
                    {
                        head.Append("Content-Type: text/xml; charset=\"utf-8\"\r\nSOAPAction: \"").Append(soapAction).Append("\"\r\n");
                    }
                    head.Append("Content-Length: ").Append(payload.Length).Append("\r\n\r\n");
                    NetworkStream stream = tcp.GetStream();
                    byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
                    stream.Write(headBytes, 0, headBytes.Length);
                    if (payload.Length > 0)
                    {
                        stream.Write(payload, 0, payload.Length);
                    }
                    MemoryStream received = new MemoryStream();
                    byte[] buffer = new byte[4096];
                    try
                    {
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0 && received.Length < 262144)
                        {
                            received.Write(buffer, 0, read);
                        }
                    }
                    catch (IOException)
                    {
                    }
                    string text = Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
                    int lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal), split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (lineEnd < 0 || split < 0)
                    {
                        return null;
                    }
                    string[] statusLine = text.Substring(0, lineEnd).Split(' ');
                    if (statusLine.Length < 2 || !int.TryParse(statusLine[1], out status))
                    {
                        status = 0;
                        return null;
                    }
                    return text.Substring(split + 4);
                }
            }
            catch (Exception)
            {
                status = 0;
                return null;
            }
        }
        private byte[] Exchange(IPAddress target, int port, byte[] request, out int length)
        {
            length = 0;
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(target, port);
                    byte[] buffer = new byte[1100];
                    for (int timeout = 250; timeout <= 2000 && !disposed; timeout *= 2)
                    {
                        socket.Send(request);
                        socket.ReceiveTimeout = timeout;
                        try
                        {
                            length = socket.Receive(buffer);
                            return buffer;
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
        private static void SendOnly(IPAddress target, int port, byte[] request)
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Connect(target, port);
                socket.Send(request);
                socket.Send(request);
            }
        }
        private static IPAddress FindGateway(out IPAddress lan)
        {
            lan = LocalFor(routeProbe);
            IPAddress fallback = null, fallbackLan = null;
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }
                    IPInterfaceProperties props = nic.GetIPProperties();
                    IPAddress found = null;
                    foreach (GatewayIPAddressInformation info in props.GatewayAddresses)
                    {
                        if (info.Address.AddressFamily == AddressFamily.InterNetwork && !info.Address.Equals(IPAddress.Any))
                        {
                            found = info.Address;
                            break;
                        }
                    }
                    if (found == null)
                    {
                        continue;
                    }
                    foreach (UnicastIPAddressInformation unicast in props.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }
                        if (lan != null && unicast.Address.Equals(lan))
                        {
                            return found;
                        }
                        if (fallback == null)
                        {
                            fallback = found;
                            fallbackLan = unicast.Address;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            if (fallback != null)
            {
                lan = LocalFor(fallback) ?? fallbackLan;
                return fallback;
            }
            if (lan != null && IsPrivate(lan) && !IPAddress.IsLoopback(lan))
            {
                byte[] guess = lan.GetAddressBytes();
                guess[3] = 1;
                return new IPAddress(guess);
            }
            return null;
        }
        private static IPAddress LocalFor(IPAddress target)
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(target, GatewayPort);
                    IPAddress address = ((IPEndPoint)socket.LocalEndPoint).Address;
                    return address.Equals(IPAddress.Any) ? null : address;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
        public static bool IsPrivate(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }
            byte[] b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return b[0] == 0 || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && (b[1] & 0xC0) == 64) || (b[0] == 169 && b[1] == 254);
            }
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }
        private static string Tag(string xml, string name)
        {
            if (xml == null)
            {
                return null;
            }
            Match match = Regex.Match(xml, "<(?:[A-Za-z0-9_]+:)?" + name + @"\b[^>]*>(.*?)</(?:[A-Za-z0-9_]+:)?" + name + ">", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value : null;
        }
        private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        private static long NowMs() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        private static void WriteU16(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 8);
            b[o + 1] = (byte)v;
        }
        private static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }
        private static int ReadU16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
        private static uint ReadU32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        private static void WriteMapped(byte[] b, int o, IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                Buffer.BlockCopy(address.GetAddressBytes(), 0, b, o, 16);
                return;
            }
            Array.Clear(b, o, 10);
            b[o + 10] = 0xFF;
            b[o + 11] = 0xFF;
            Buffer.BlockCopy(address.GetAddressBytes(), 0, b, o + 12, 4);
        }
        private static IPAddress ReadMapped(byte[] b, int o)
        {
            byte[] raw = new byte[16];
            Buffer.BlockCopy(b, o, raw, 0, 16);
            IPAddress address = new IPAddress(raw);
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }
    }
}
