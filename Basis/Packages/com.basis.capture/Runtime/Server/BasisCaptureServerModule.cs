using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using static BasisPermissions.PermissionManager;
[assembly: BasisNetworkPackage(typeof(Basis.Network.Server.Capture.BasisCaptureServerModule))]
namespace Basis.Network.Server.Capture
{
    public sealed class BasisCaptureServerModule : IBasisNetworkPackage
    {
        public static Func<NetPeer, bool> Authorize = peer => PermissionIntegration.PeerHas(peer, BasisCaptureProtocol.PermissionNode);
        public static Func<NetPeer, bool> MayRecordWhileLocked = peer => PermissionIntegration.PeerHas(peer, BasisCaptureProtocol.RecordNode);
        public static Func<NetPeer, bool> MayToggleLock = peer => PermissionIntegration.PeerHas(peer, BasisCaptureProtocol.LockNode);
        public static bool Locked { get; private set; } = true;
        public static string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "capture.xml");
        public void Initialize()
        {
            Load();
            BasisServerMessageRegistry.RegisterServerPlugin(BasisCaptureProtocol.MessageName, DeliveryMethod.ReliableOrdered, Handle);
            BasisServerMessageRegistry.PeerSubscribed -= SendState;
            BasisServerMessageRegistry.PeerSubscribed += SendState;
        }
        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    XElement root = XDocument.Load(ConfigPath).Root;
                    string value = root?.Element("Locked")?.Value;
                    Locked = value == null || !bool.TryParse(value.Trim(), out bool parsed) || parsed;
                }
                else
                {
                    Locked = true;
                    Save();
                }
            }
            catch (Exception e)
            {
                Locked = true;
                BNL.LogError($"Capture lock config could not be read, staying locked: {e.Message}");
            }
        }
        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                new XDocument(new XComment(" Scene capture lock. Only players with the basis.capture.collect permission can record on this server, and while this is true they also need basis.capture.record. Every recording asks the other players, and only those who allow are recorded. Admins can change this from the capture panel. "), new XElement("Capture", new XElement("Locked", Locked ? "true" : "false"))).Save(ConfigPath);
            }
            catch (Exception e)
            {
                BNL.LogError($"Capture lock config could not be saved: {e.Message}");
            }
        }
        public static void SetLocked(bool locked)
        {
            if (Locked == locked) return;
            Locked = locked;
            Save();
            foreach (NetPeer peer in NetworkServer.AuthenticatedPeers.Values) if (peer != null) SendState(peer);
            BNL.Log($"Scene capture {(locked ? "locked" : "unlocked")}");
        }
        private static bool Check(Func<NetPeer, bool> check, NetPeer peer)
        {
            try { return check(peer); }
            catch (Exception e) { BNL.LogError($"Capture permission check failed: {e.Message}"); return false; }
        }
        public static void SendState(NetPeer peer)
        {
            long flags = (Check(MayRecordWhileLocked, peer) ? 1L : 0L) | (Check(MayToggleLock, peer) ? 2L : 0L) | (Check(Authorize, peer) ? 4L : 0L);
            BasisServerMessageRegistry.SendToPeer(peer, BasisCaptureProtocol.MessageName, w => { w.Put(BasisCaptureProtocol.OpLockState); w.Put(Locked ? 1u : 0u); w.Put((ushort)peer.Id); w.Put(flags); });
        }
        private static readonly Dictionary<(int, int), long> lastAsk = new Dictionary<(int, int), long>();
        private static bool MayAsk(int requester, int target)
        {
            long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            lock (lastAsk)
            {
                if (lastAsk.TryGetValue((requester, target), out long last) && now - last < BasisCaptureProtocol.AskIntervalMs) return false;
                if (lastAsk.Count > 4096) lastAsk.Clear();
                lastAsk[(requester, target)] = now;
            }
            return true;
        }
        private static void Deny(NetPeer peer, uint session) => BasisServerMessageRegistry.SendToPeer(peer, BasisCaptureProtocol.MessageName, w => { w.Put(BasisCaptureProtocol.OpDenied); w.Put(session); w.Put((ushort)peer.Id); w.Put(0L); });
        private static void Handle(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                if (!reader.TryGetByte(out byte op) || !reader.TryGetUInt(out uint session)) return;
                long ticks = reader.TryGetLong(out long t) ? t : 0;
                if (op == BasisCaptureProtocol.OpSetLock)
                {
                    if (Check(MayToggleLock, peer)) SetLocked(session != 0);
                    else SendState(peer);
                    return;
                }
                if (op == BasisCaptureProtocol.OpQueryLock)
                {
                    SendState(peer);
                    return;
                }
                bool targeted = reader.TryGetUShort(out ushort only);
                ushort requester = (ushort)peer.Id;
                if (op == BasisCaptureProtocol.OpConsent)
                {
                    if (targeted && only != requester && NetworkServer.AuthenticatedPeers.TryGetValue(only, out NetPeer recorder) && recorder != null) BasisServerMessageRegistry.SendToPeer(recorder, BasisCaptureProtocol.MessageName, w => { w.Put(BasisCaptureProtocol.OpConsent); w.Put(session); w.Put(requester); w.Put(ticks != 0 ? 1L : 0L); });
                    return;
                }
                if (op != BasisCaptureProtocol.OpStart && op != BasisCaptureProtocol.OpStop && op != BasisCaptureProtocol.OpCollect) return;
                if (!Check(Authorize, peer) || (op == BasisCaptureProtocol.OpStart && Locked && !Check(MayRecordWhileLocked, peer)))
                {
                    Deny(peer, session);
                    return;
                }
                byte forward = op == BasisCaptureProtocol.OpStart ? BasisCaptureProtocol.OpAsk : op;
                if (forward == BasisCaptureProtocol.OpAsk && !MayAsk(requester, targeted ? only : -1)) return;
                foreach (NetPeer target in NetworkServer.AuthenticatedPeers.Values)
                {
                    if (target == null || target.Id == peer.Id || (targeted && target.Id != only)) continue;
                    BasisServerMessageRegistry.SendToPeer(target, BasisCaptureProtocol.MessageName, w => { w.Put(forward); w.Put(session); w.Put(requester); w.Put(ticks); });
                }
                BNL.Log($"Capture {(forward == BasisCaptureProtocol.OpAsk ? "consent request" : op == BasisCaptureProtocol.OpStop ? "stop" : "collect")} {session} from {requester} forwarded{(targeted ? " to " + only : "")}");
            }
            finally
            {
                reader.Recycle();
            }
        }
    }
}
