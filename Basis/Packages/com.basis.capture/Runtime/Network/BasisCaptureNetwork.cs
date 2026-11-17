using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
namespace Basis.Capture
{
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
    public static class BasisCaptureNetwork
    {
        public sealed class PeerStatus
        {
            public ushort netId;
            public string uuid, name, state;
            public long received, total;
        }
        private sealed class Outgoing
        {
            public uint session;
            public ushort requester;
            public string packPath;
            public FileStream stream;
            public long total;
            public int chunkCount, nextChunk, acked;
        }
        private sealed class Incoming
        {
            public ushort sender;
            public string uuid, partPath;
            public FileStream stream;
            public long total, received;
            public int chunkCount, nextChunk;
        }
        public static event Action Changed;
        public static readonly Dictionary<ushort, PeerStatus> Peers = new Dictionary<ushort, PeerStatus>();
        public static uint CollectSession { get; private set; }
        public static string CollectFolder { get; private set; }
        public static bool Collecting => CollectSession != 0;
        public static string LastDenied { get; set; }
        public static bool ServerLocked { get; private set; } = true;
        public static bool MayBypassLock { get; private set; }
        public static bool CanToggleLock { get; private set; }
        public static bool HaveLockState { get; private set; }
        public static bool ConsentMode { get; private set; }
        public static readonly Dictionary<ushort, bool> Consent = new Dictionary<ushort, bool>();
        public static bool NeedsConsent => BasisNetworkConnection.LocalPlayerIsConnected;
        public static bool IsCaptureAdmin { get; private set; }
        public static bool HasConsent(ushort netId) => Consent.TryGetValue(netId, out bool allowed) && allowed;
        public static void ResetConsent() => Consent.Clear();
        public static int CountConsent(bool? state)
        {
            int n = 0;
            foreach (KeyValuePair<ushort, BasisRemotePlayer> pair in BasisNetworkPlayers.RemotePlayers)
            {
                bool known = Consent.TryGetValue(pair.Key, out bool allowed);
                if (state == null ? !known : known && allowed == state.Value) n++;
            }
            return n;
        }
        private static readonly Dictionary<string, float> declinedAt = new Dictionary<string, float>();
        public static bool MayRecord => !BasisNetworkConnection.LocalPlayerIsConnected || (HaveLockState && LocalMayCollect && (!ServerLocked || MayBypassLock));
        public static void SetServerLock(bool locked)
        {
            if (CanToggleLock) SendControl(BasisCaptureProtocol.OpSetLock, locked ? 1u : 0u, 0);
        }
        public static BasisCaptureSession PeerSession { get; private set; }
        private static ushort dataId, peerRequester;
        private static bool hasDataId, initialized;
        private static uint peerSessionId;
        private static Outgoing outgoing;
        private static readonly Dictionary<ushort, Incoming> incoming = new Dictionary<ushort, Incoming>();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            BasisClientMessageRegistry.RegisterClientPlugin(BasisCaptureProtocol.MessageName, OnControl);
            BasisNetworkPlayer.OnLocalPlayerJoined -= OnLocalJoined;
            BasisNetworkPlayer.OnLocalPlayerJoined += OnLocalJoined;
            BasisNetworkPlayer.OnLocalPlayerLeft -= OnLocalLeft;
            BasisNetworkPlayer.OnLocalPlayerLeft += OnLocalLeft;
            BasisNetworkPlayer.OnRemotePlayerJoined -= OnRemoteJoined;
            BasisNetworkPlayer.OnRemotePlayerJoined += OnRemoteJoined;
            BasisNetworkPlayer.OnRemotePlayerLeft -= OnRemoteLeft;
            BasisNetworkPlayer.OnRemotePlayerLeft += OnRemoteLeft;
            Application.quitting -= OnQuit;
            Application.quitting += OnQuit;
            if (BasisNetworkPlayer.LocalPlayer != null) _ = ResolveDataId();
        }
        private static void OnQuit()
        {
            Application.quitting -= OnQuit;
            CancelAll();
            initialized = false;
        }
        public static bool ServerSupportsCollection => BasisClientMessageRegistry.IsPluginSupplied(BasisCaptureProtocol.MessageName);
        public static bool LocalMayCollect => IsCaptureAdmin || BasisNetworkModeration.LocalPlayerHasNode(BasisCaptureProtocol.PermissionNode);
        private static void OnLocalJoined(BasisNetworkPlayer player, BasisLocalPlayer local) => _ = ResolveDataId();
        private static async Task ResolveDataId()
        {
            BasisIdResolutionResult result = await BasisNetworkIdResolver.ResolveAsync(BasisCaptureProtocol.DataMessageId);
            if (!result.Success) { BasisDebug.LogWarning("Capture could not resolve its data channel.", BasisDebug.LogTag.Networking); return; }
            dataId = result.Id;
            hasDataId = true;
            BasisNetworkGenericMessages.RegisterHandler(dataId, OnData);
            BasisNetworkGenericMessages.RegisterDirectHandler(dataId, OnData);
            Changed?.Invoke();
        }
        private static void OnLocalLeft(BasisNetworkPlayer player, BasisLocalPlayer local)
        {
            hasDataId = false;
            HaveLockState = false;
            ServerLocked = true;
            MayBypassLock = false;
            CanToggleLock = false;
            IsCaptureAdmin = false;
            CancelAll();
        }
        private static void OnRemoteJoined(BasisNetworkPlayer player, BasisRemotePlayer remote)
        {
            if (!Collecting || BasisCaptureSession.Current == null || BasisCaptureSession.Current.options.sessionId != CollectSession) return;
            ushort id = player.playerId;
            SendControl(BasisCaptureProtocol.OpStart, CollectSession, BasisCaptureSession.Current.StartServerTicks, id);
            if (ConsentMode) SetStatus(id, "asked");
        }
        private static void OnRemoteLeft(BasisNetworkPlayer player, BasisRemotePlayer remote)
        {
            ushort id = player.playerId;
            Consent.Remove(id);
            if (PeerSession != null && peerRequester == id) StopPeerSession();
            if (outgoing != null && outgoing.requester == id) CloseOutgoing();
            if (incoming.TryGetValue(id, out Incoming inc)) { CloseIncoming(inc, true); SetStatus(id, "left before sending"); }
        }
        private static void CancelAll()
        {
            StopPeerSession();
            CloseOutgoing();
            foreach (Incoming inc in new List<Incoming>(incoming.Values)) CloseIncoming(inc, true);
            CollectSession = 0;
            Changed?.Invoke();
        }
        public static bool BeginCollection(BasisCaptureSession session)
        {
            if (session == null || !ServerSupportsCollection) return false;
            CollectSession = session.options.sessionId;
            CollectFolder = session.Folder;
            ConsentMode = true;
            LastDenied = null;
            Peers.Clear();
            foreach (ushort id in BasisNetworkPlayers.RemotePlayers.Keys) SetStatus(id, "asked");
            bool sent = SendControl(BasisCaptureProtocol.OpStart, CollectSession, session.StartServerTicks);
            Changed?.Invoke();
            return sent;
        }
        public static void EndCollection()
        {
            if (!Collecting) return;
            SendControl(BasisCaptureProtocol.OpStop, CollectSession, BasisCaptureSession.ServerTicksNow());
            SendControl(BasisCaptureProtocol.OpCollect, CollectSession, BasisCaptureSession.ServerTicksNow());
            Changed?.Invoke();
        }
        public static void FinishCollection()
        {
            foreach (Incoming inc in new List<Incoming>(incoming.Values)) CloseIncoming(inc, true);
            CollectSession = 0;
            ConsentMode = false;
            Changed?.Invoke();
        }
        private static bool SendControl(byte op, uint session, long ticks, int target = -1) => BasisClientMessageRegistry.Send(BasisCaptureProtocol.MessageName, w => { w.Put(op); w.Put(session); w.Put(ticks); if (target >= 0) w.Put((ushort)target); });
        private static void OnControl(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                if (!reader.TryGetByte(out byte op) || !reader.TryGetUInt(out uint session) || !reader.TryGetUShort(out ushort requester) || !reader.TryGetLong(out long ticks)) return;
                BasisDeviceManagement.EnqueueOnMainThread(() => HandleControl(op, session, requester, ticks));
            }
            finally
            {
                reader.Recycle();
            }
        }
        private static void HandleControl(byte op, uint session, ushort requester, long ticks)
        {
            switch (op)
            {
                case BasisCaptureProtocol.OpLockState:
                    ServerLocked = session != 0;
                    MayBypassLock = (ticks & 1) != 0;
                    CanToggleLock = (ticks & 2) != 0;
                    IsCaptureAdmin = (ticks & 4) != 0;
                    HaveLockState = true;
                    if (!MayRecord && BasisCapture.Recording)
                    {
                        BasisCapture.Stop();
                        LastDenied = "This server locked scene capture, so the recording was stopped.";
                    }
                    Changed?.Invoke();
                    break;
                case BasisCaptureProtocol.OpDenied:
                    if (session == CollectSession) { LastDenied = ServerLocked ? "Scene capture is locked on this server, so players were not asked." : "The server did not allow asking other players."; CollectSession = 0; ConsentMode = false; Changed?.Invoke(); }
                    break;
                case BasisCaptureProtocol.OpAsk:
                    AskConsent(session, requester, ticks);
                    break;
                case BasisCaptureProtocol.OpConsent:
                    if (session != CollectSession || !ConsentMode) break;
                    Consent[requester] = ticks != 0;
                    SetStatus(requester, ticks != 0 ? "allowed" : "declined");
                    break;
                case BasisCaptureProtocol.OpStop:
                    if (PeerSession != null && peerSessionId == session && peerRequester == requester) PeerSession.Stop();
                    break;
                case BasisCaptureProtocol.OpCollect:
                    Collect(session, requester);
                    break;
            }
        }
        private static void AskConsent(uint session, ushort requester, long ticks)
        {
            if (BasisNetworkPlayer.LocalPlayer != null && requester == BasisNetworkPlayer.LocalPlayer.playerId) return;
            string uuid = UuidOf(requester);
            string policy = BasisCaptureSettings.ConsentPolicy.RawValue;
            void Reply(bool allow, bool asked)
            {
                if (!allow && asked) declinedAt[uuid] = Time.unscaledTime;
                SendControl(BasisCaptureProtocol.OpConsent, session, allow ? 1 : 0, requester);
                if (allow) StartPeerSession(session, requester, ticks);
                else if (!asked) BasisNotificationCenter.LogResolved(BasisLocalization.Get("settings.developer.capture.consent.title"), BasisLocalization.Get("settings.developer.capture.consent.autoDeclined", NameOf(requester)), null, BasisNotificationStatus.Denied, BasisNotificationCategory.Player);
            }
            if (policy == "allow") { Reply(true, false); return; }
            if (policy == "decline" || (declinedAt.TryGetValue(uuid, out float at) && Time.unscaledTime - at < 120f)) { Reply(false, false); return; }
            BasisCaptureConsentPrompt.Show(NameOf(requester), allow => Reply(allow, true));
        }
        private static void StartPeerSession(uint session, ushort requester, long ticks)
        {
            if (BasisNetworkPlayer.LocalPlayer != null && requester == BasisNetworkPlayer.LocalPlayer.playerId) return;
            if (PeerSession != null && PeerSession.State == BasisCaptureSession.SessionState.Recording) { SendStatus(requester, session, BasisCaptureProtocol.StatusFailed); return; }
            if (BasisCaptureSession.Current != null && BasisCaptureSession.Current.State == BasisCaptureSession.SessionState.Recording) { SendStatus(requester, session, BasisCaptureProtocol.StatusFailed); return; }
            if (BasisLocalPlayer.Instance == null || BasisLocalPlayer.Instance.BasisAvatar == null) { SendStatus(requester, session, BasisCaptureProtocol.StatusNoAvatar); return; }
            BasisCaptureSession.Options o = new BasisCaptureSession.Options { peerMode = true, microphone = BasisCaptureSettings.PeerMicrophone.RawValue, rateHz = BasisCapture.RateFromSetting(), sessionId = session, requester = requester, requesterStartTicks = ticks, maxSeconds = 3600f, folder = Path.Combine(BasisCaptureSession.DefaultRoot, "peer_" + session.ToString("x8") + "_" + Safe(BasisLocalPlayer.Instance.UUID)) };
            BasisCaptureSession s = BasisCaptureSession.Start(o);
            if (s == null) { SendStatus(requester, session, BasisCaptureProtocol.StatusFailed); return; }
            PeerSession = s;
            peerSessionId = session;
            peerRequester = requester;
            SendStatus(requester, session, BasisCaptureProtocol.StatusRecording);
            string who = NameOf(requester);
            BasisNotificationCenter.LogResolved(BasisLocalization.Get("settings.developer.capture.consent.title"), BasisLocalization.Get("settings.developer.capture.consent.recording", who), null, BasisNotificationStatus.Accepted, BasisNotificationCategory.Player);
            BasisDebug.Log($"Capture: recording locally for admin {who} (session {session:x8}).", BasisDebug.LogTag.Networking);
            Changed?.Invoke();
        }
        private static void StopPeerSession()
        {
            if (PeerSession != null && PeerSession.State == BasisCaptureSession.SessionState.Recording) PeerSession.Stop();
            PeerSession = null;
            peerSessionId = 0;
            peerRequester = 0;
        }
        private static void Collect(uint session, ushort requester)
        {
            if (PeerSession == null || peerSessionId != session || peerRequester != requester) { if (BasisNetworkPlayer.LocalPlayer == null || requester != BasisNetworkPlayer.LocalPlayer.playerId) SendStatus(requester, session, BasisCaptureProtocol.StatusNothing); return; }
            BasisCaptureSession s = PeerSession;
            if (s.State == BasisCaptureSession.SessionState.Recording) s.Stop();
            SendStatus(requester, session, BasisCaptureProtocol.StatusPacking);
            WaitThenSend(s, session, requester);
        }
        private static async void WaitThenSend(BasisCaptureSession s, uint session, ushort requester)
        {
            for (int i = 0; i < 600 && s.State == BasisCaptureSession.SessionState.Finalizing; i++) await Task.Yield();
            string pack = s.Folder + ".pack";
            try { await Task.Run(() => BasisCapturePack.Pack(s.Folder, pack)); }
            catch (Exception e) { BasisDebug.LogError("Capture pack failed: " + e.Message); SendStatus(requester, session, BasisCaptureProtocol.StatusFailed); return; }
            CloseOutgoing();
            FileStream fs = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.Read);
            outgoing = new Outgoing { session = session, requester = requester, packPath = pack, stream = fs, total = fs.Length, chunkCount = (int)((fs.Length + BasisCaptureProtocol.ChunkBytes - 1) / BasisCaptureProtocol.ChunkBytes) };
            byte[] offer = new byte[1 + 4 + 8 + 4];
            offer[0] = BasisCaptureProtocol.DataOffer;
            BinaryPrimitives.WriteUInt32LittleEndian(offer.AsSpan(1), session);
            BinaryPrimitives.WriteInt64LittleEndian(offer.AsSpan(5), outgoing.total);
            BinaryPrimitives.WriteInt32LittleEndian(offer.AsSpan(13), outgoing.chunkCount);
            SendData(requester, offer);
            Pump();
        }
        private static void Pump()
        {
            Outgoing o = outgoing;
            if (o == null) return;
            while (o.nextChunk < o.chunkCount && o.nextChunk < o.acked + BasisCaptureProtocol.WindowChunks)
            {
                long offset = (long)o.nextChunk * BasisCaptureProtocol.ChunkBytes;
                int size = (int)Math.Min(BasisCaptureProtocol.ChunkBytes, o.total - offset);
                byte[] msg = new byte[9 + size];
                msg[0] = BasisCaptureProtocol.DataChunk;
                BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(1), o.session);
                BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(5), o.nextChunk);
                o.stream.Position = offset;
                int read = 0;
                while (read < size) { int n = o.stream.Read(msg, 9 + read, size - read); if (n <= 0) break; read += n; }
                SendData(o.requester, msg);
                o.nextChunk++;
            }
            if (o.acked >= o.chunkCount)
            {
                byte[] done = new byte[5];
                done[0] = BasisCaptureProtocol.DataDone;
                BinaryPrimitives.WriteUInt32LittleEndian(done.AsSpan(1), o.session);
                SendData(o.requester, done);
                BasisDebug.Log($"Capture: sent {o.total} bytes to {NameOf(o.requester)}.", BasisDebug.LogTag.Networking);
                CloseOutgoing();
                PeerSession = null;
            }
        }
        private static void CloseOutgoing()
        {
            if (outgoing == null) return;
            outgoing.stream?.Dispose();
            try { File.Delete(outgoing.packPath); } catch { }
            outgoing = null;
        }
        private static void SendStatus(ushort requester, uint session, byte status)
        {
            byte[] msg = new byte[6];
            msg[0] = BasisCaptureProtocol.DataStatus;
            BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(1), session);
            msg[5] = status;
            SendData(requester, msg);
        }
        private static void SendData(ushort target, byte[] payload)
        {
            if (!hasDataId) return;
            BasisNetworkGenericMessages.OnNetworkMessageSendDirect(dataId, payload, DeliveryMethod.ReliableOrdered, new[] { target }, true);
        }
        private static void OnData(ushort sender, byte[] payload, DeliveryMethod method)
        {
            if (payload == null || payload.Length < 5) return;
            byte[] copy = (byte[])payload.Clone();
            BasisDeviceManagement.EnqueueOnMainThread(() => HandleData(sender, copy));
        }
        private static void HandleData(ushort sender, byte[] p)
        {
            byte op = p[0];
            uint session = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(1));
            switch (op)
            {
                case BasisCaptureProtocol.DataAck:
                    if (outgoing != null && outgoing.session == session && outgoing.requester == sender && p.Length >= 9) { outgoing.acked = Math.Max(outgoing.acked, BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(5))); Pump(); }
                    break;
                case BasisCaptureProtocol.DataCancel:
                    if (outgoing != null && outgoing.session == session && outgoing.requester == sender) CloseOutgoing();
                    break;
                case BasisCaptureProtocol.DataStatus:
                    if (session != CollectSession || p.Length < 6) return;
                    if (ConsentMode && !HasConsent(sender)) return;
                    SetStatus(sender, p[5] switch { BasisCaptureProtocol.StatusRecording => "recording", BasisCaptureProtocol.StatusNoAvatar => "no avatar loaded", BasisCaptureProtocol.StatusFailed => "could not record", BasisCaptureProtocol.StatusNothing => "has nothing for this take", BasisCaptureProtocol.StatusPacking => "packing", _ => "unknown" });
                    break;
                case BasisCaptureProtocol.DataOffer:
                    if (session != CollectSession || p.Length < 17 || CollectFolder == null) return;
                    long total = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(5));
                    if (total <= 0 || total > BasisCaptureProtocol.MaxTakeBytes) { SendCancel(sender, session); return; }
                    if (incoming.TryGetValue(sender, out Incoming old)) CloseIncoming(old, true);
                    string peers = Path.Combine(CollectFolder, BasisCaptureFormat.PeersFolder);
                    Directory.CreateDirectory(peers);
                    Incoming inc = new Incoming { sender = sender, uuid = UuidOf(sender), total = total, chunkCount = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(13)) };
                    inc.partPath = Path.Combine(peers, Safe(inc.uuid) + ".part");
                    inc.stream = new FileStream(inc.partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    incoming[sender] = inc;
                    SetStatus(sender, "receiving", 0, total);
                    break;
                case BasisCaptureProtocol.DataChunk:
                    if (!incoming.TryGetValue(sender, out Incoming c) || session != CollectSession || p.Length < 9) return;
                    int index = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(5));
                    if (index != c.nextChunk) { SendCancel(sender, session); CloseIncoming(c, true); SetStatus(sender, "transfer out of order"); return; }
                    c.stream.Write(p, 9, p.Length - 9);
                    c.received += p.Length - 9;
                    c.nextChunk++;
                    if (c.nextChunk % (BasisCaptureProtocol.WindowChunks / 2) == 0 || c.nextChunk == c.chunkCount) SendAck(sender, session, c.nextChunk);
                    SetStatus(sender, "receiving", c.received, c.total);
                    break;
                case BasisCaptureProtocol.DataDone:
                    if (!incoming.TryGetValue(sender, out Incoming d) || session != CollectSession) return;
                    incoming.Remove(sender);
                    d.stream.Dispose();
                    if (d.received != d.total) { SetStatus(sender, "incomplete"); try { File.Delete(d.partPath); } catch { } return; }
                    string target = Path.Combine(CollectFolder, BasisCaptureFormat.PeersFolder, Safe(d.uuid));
                    SetStatus(sender, "unpacking", d.received, d.total);
                    Task.Run(() => { BasisCapturePack.Unpack(d.partPath, target); File.Delete(d.partPath); }).ContinueWith(t => BasisDeviceManagement.EnqueueOnMainThread(() => SetStatus(sender, t.Exception == null ? "received" : "unpack failed: " + t.Exception.GetBaseException().Message, d.received, d.total)));
                    break;
            }
        }
        private static void SendAck(ushort target, uint session, int count)
        {
            byte[] msg = new byte[9];
            msg[0] = BasisCaptureProtocol.DataAck;
            BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(1), session);
            BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(5), count);
            SendData(target, msg);
        }
        private static void SendCancel(ushort target, uint session)
        {
            byte[] msg = new byte[5];
            msg[0] = BasisCaptureProtocol.DataCancel;
            BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(1), session);
            SendData(target, msg);
        }
        private static void CloseIncoming(Incoming inc, bool delete)
        {
            incoming.Remove(inc.sender);
            inc.stream?.Dispose();
            if (delete) try { File.Delete(inc.partPath); } catch { }
        }
        private static void SetStatus(ushort id, string state, long received = -1, long total = -1)
        {
            if (!Peers.TryGetValue(id, out PeerStatus s)) Peers[id] = s = new PeerStatus { netId = id, uuid = UuidOf(id), name = NameOf(id) };
            s.state = state;
            if (received >= 0) s.received = received;
            if (total >= 0) s.total = total;
            Changed?.Invoke();
        }
        private static string UuidOf(ushort id) => BasisNetworkPlayers.RemotePlayers.TryGetValue(id, out BasisRemotePlayer rp) && !string.IsNullOrEmpty(rp.UUID) ? rp.UUID : "net" + id;
        private static string NameOf(ushort id) => BasisNetworkPlayers.RemotePlayers.TryGetValue(id, out BasisRemotePlayer rp) ? rp.SafeDisplayName ?? rp.DisplayName : "player " + id;
        private static string Safe(string s) => BasisCaptureFormat.SafeFileName(s);
    }
}
