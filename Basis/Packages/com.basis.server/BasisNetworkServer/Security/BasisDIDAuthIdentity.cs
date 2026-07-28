using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
using Basis.Network.Server.Auth;
using BasisNetworkServer.Security;
using BasisServerHandle;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static BasisNetworkServer.Security.BasisPlayerModeration;
using static SerializableBasis;
using Challenge = Basis.Contrib.Auth.DecentralizedIds.Challenge;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace BasisDidLink
{
    public class BasisDIDAuthIdentity : IAuthIdentity
    {
        internal readonly DidAuthentication DidAuth;
        public ConcurrentDictionary<int, OnAuth> AuthIdentity = new ConcurrentDictionary<int, OnAuth>();
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _timeouts = new ConcurrentDictionary<int, CancellationTokenSource>();
        private readonly ConcurrentDictionary<string, int> _didCounts = new ConcurrentDictionary<string, int>();
        public ConcurrentDictionary<string, byte> Admins = new ConcurrentDictionary<string, byte>();
        public static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName, "admins.xml");
        public BasisDIDAuthIdentity()
        {
            string[] LoadedAdmins = LoadAdmins(FilePath);
            if (LoadedAdmins != null)
            {
                foreach (var admin in LoadedAdmins)
                {
                    Admins.TryAdd(admin, 0);
                }
            }
            else
            {
                Admins = new ConcurrentDictionary<string, byte>();
            }
            string adminsList = string.Join(", ", Admins);
            BNL.Log($"Loaded Admins {Admins.Count} {adminsList}");
            CryptoRng rng = CryptoRng.Create();
            Config cfg = new Config { Rng = rng };
            DidAuth = new DidAuthentication(cfg);
            BasisServerHandleEvents.OnAuthReceived += OnAuthReceived;
            BNL.Log("DidAuthIdentity initialized.");
        }

        public void DeInitialize()
        {
            BasisServerHandleEvents.OnAuthReceived -= OnAuthReceived;
            BNL.Log("DidAuthIdentity deinitialized.");
        }

        public static string UnpackString(byte[] compressedBytes)
        {
            return Encoding.UTF8.GetString(compressedBytes, 0, compressedBytes.Length);
        }

        public struct OnAuth
        {
            public ReadyMessage ReadyMessage;
            public Challenge Challenge;
            public Did Did;
        }
        public int CheckForDuplicates(Did Did)
        {
            return _didCounts.TryGetValue(Did.V, out int Count) ? Count : 0;
        }

        private void RetainDid(Did Did)
        {
            _didCounts.AddOrUpdate(Did.V, 1, static (_, Existing) => Existing + 1);
        }

        private void ReleaseDid(Did Did)
        {
            string Key = Did.V;
            if (string.IsNullOrEmpty(Key))
            {
                return;
            }
            while (_didCounts.TryGetValue(Key, out int Current))
            {
                if (Current > 1)
                {
                    if (_didCounts.TryUpdate(Key, Current - 1, Current))
                    {
                        return;
                    }
                }
                else if (((ICollection<KeyValuePair<string, int>>)_didCounts).Remove(new KeyValuePair<string, int>(Key, Current)))
                {
                    return;
                }
            }
        }
        public void ProcessConnection(Configuration Configuration, ConnectionRequest ConnectionRequest, NetPeer newPeer)
        {
            try
            {
                BNL.Log($"Processing connection from peer {newPeer.Id}.");
                ReadyMessage readyMessage = new ReadyMessage();
                readyMessage.Deserialize(ConnectionRequest.Data);

                if (readyMessage.WasDeserializedCorrectly())
                {
                    if (BasisServerHandleEvents.IsHeadlessDisallowed(readyMessage.playerMetaDataMessage, out string reason))
                    {
                        BasisServerHandleEvents.RejectWithReason(newPeer, reason);
                        return;
                    }

                    string UUID = readyMessage.playerMetaDataMessage.playerUUID;
                    Did playerDid = new Did(UUID);
                    if (BasisPlayerModeration.IsBanned(UUID))
                    {
                        if (BasisPlayerModeration.GetBannedReason(UUID, out string Reason))
                        {
                            BasisServerHandleEvents.RejectWithReason(newPeer, "Banned User!  Reason " + Reason);

                        }
                        else
                        {
                            BasisServerHandleEvents.RejectWithReason(newPeer, " Banned User!");
                        }
                        return;
                    }
                    if (Configuration.HowManyDuplicateAuthCanExist <= CheckForDuplicates(playerDid))
                    {
                        BasisServerHandleEvents.RejectWithReason(newPeer, "To Many Auths From this DID!");
                        return;
                    }

                    OnAuth OnAuth = new OnAuth
                    {
                        Did = playerDid,
                        Challenge = MakeChallenge(playerDid),
                        ReadyMessage = readyMessage
                    };

                    if (AuthIdentity.TryAdd(newPeer.Id, OnAuth))
                    {
                        RetainDid(playerDid);
                        readyMessage.playerMetaDataMessage.playerUUID = playerDid.V;
                        NetDataWriter Writer = NetworkServer.RentWriter();
                        BytesMessage NetworkMessage = new BytesMessage();
                        NetworkMessage.Serialize(Writer, OnAuth.Challenge.Nonce.V);
                        BNL.Log("Sending out Writer with size : " + Writer.Length);
                        NetworkServer.TrySend(newPeer, Writer, BasisNetworkCommons.AuthIdentityChannel, DeliveryMethod.ReliableOrdered);
                        NetworkServer.ReturnWriter(Writer);

                        CancellationTokenSource cts = new CancellationTokenSource();
                        _timeouts[newPeer.Id] = cts;
                        Task.Run(async () =>
                        {
                            await TimeOut(newPeer, UUID, cts);
                        });
                        //   BasisServerHandleEvents.OnNetworkAccepted(newPeer, readyMessage, playerDid.V);
                    }
                    else
                    {
                        BasisServerHandleEvents.RejectWithReason(newPeer, "Payload Provided was invalid! potential Duplication");
                    }
                }
                else
                {
                    BasisServerHandleEvents.RejectWithReason(newPeer, "Invalid ReadyMessage received.");
                }
            }
            catch (Exception e)
            {
                BNL.Log($"Error processing connection: {e.Message} {e.StackTrace}");
                BasisServerHandleEvents.RejectWithReason(newPeer, "Connection could not be processed.");
            }
        }
        // The handshake round trip degrades with how many peers are already on the server: measured
        // on a 32-core box it runs under 50 ms into a near-empty instance and 7.7 s at ~2,400 peers,
        // while verification itself stays at 0.16 ms. A flat window therefore stops being a
        // liveness check during a mass join and starts evicting peers whose reply is merely queued.
        // Scale the allowance with population so a lone unresponsive peer is still cut at the
        // configured value, and cap it so a dead peer can never linger indefinitely.
        private const int AuthTimeoutPerPeerMs = 12;
        private const int AuthTimeoutMaxExtraMs = 45000;

        public static int GetAuthTimeoutMs(int population)
        {
            int Configured = NetworkServer.Configuration.AuthValidationTimeOutMiliseconds;
            if (population <= 0)
            {
                return Configured;
            }
            long Extra = Math.Min((long)population * AuthTimeoutPerPeerMs, AuthTimeoutMaxExtraMs);
            return (int)Math.Min(Configured + Extra, int.MaxValue);
        }

        public async Task TimeOut(NetPeer newPeer, string UUID, CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(GetAuthTimeoutMs(NetworkServer.Server?.ConnectedPeersCount ?? 0), cts.Token);
                if (!_timeouts.ContainsKey(newPeer.Id)) return;
                if (AuthIdentity.TryRemove(newPeer.Id, out OnAuth TimedOut))
                {
                    ReleaseDid(TimedOut.Did);
                }
                _timeouts.TryRemove(newPeer.Id, out _);
                cts.Dispose();
                BNL.Log($"Authentication timeout for {UUID}.");
                BasisServerHandleEvents.RejectWithReason(newPeer, "Authentication timeout");
                newPeer.Disconnect();
            }
            catch (TaskCanceledException) { }
        }

        private async void OnAuthReceived(NetPacketReader reader, NetPeer newPeer)
        {
            try
            {
                //     BNL.Log($"Authentication response received from {newPeer.Id}.");
                if (_timeouts.TryRemove(newPeer.Id, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }

                BytesMessage SignatureBytes = new BytesMessage();
                if (!SignatureBytes.Deserialize(reader, out byte[] SigBytes))
                {
                    BNL.LogError($"Malformed auth response from peer {newPeer.Id}: bad signature data");
                    BasisServerHandleEvents.RejectWithReason(newPeer, "Malformed auth response: bad signature data");
                    return;
                }
                BytesMessage FragmentBytes = new BytesMessage();
                if (!FragmentBytes.Deserialize(reader, out byte[] FragBytes))
                {
                    BNL.LogError($"Malformed auth response from peer {newPeer.Id}: bad fragment data");
                    BasisServerHandleEvents.RejectWithReason(newPeer, "Malformed auth response: bad fragment data");
                    return;
                }

                Signature Sig = new Signature(SigBytes);
                string FragmentAsString = UnpackString(FragBytes);
                if (FragmentAsString == "N/A")
                {
                    FragmentAsString = string.Empty;
                }
                DidUrlFragment Fragment = new DidUrlFragment(FragmentAsString);
                Response response = new Response(Sig, Fragment);

                if (AuthIdentity.TryGetValue(newPeer.Id, out OnAuth authIdentity))
                {
                    Challenge challenge = authIdentity.Challenge;
                    bool isAuthenticated = await RecvChallengeResponse(response, challenge);

                    if (isAuthenticated)
                    {
                        BasisServerHandleEvents.OnNetworkAccepted(newPeer, authIdentity.ReadyMessage, authIdentity.Did.V);
                    }
                    else
                    {
                        BNL.LogError($"Authentication failed for {authIdentity.Did.V}.");
                        BasisServerHandleEvents.RejectWithReason(newPeer, "was unable to authenticate!");
                    }
                }
            }
            catch (Exception e)
            {
                BNL.Log($"Error during authentication: {e.Message} {e.StackTrace}");
                BasisServerHandleEvents.RejectWithReason(newPeer, "Authentication failed.");
            }
        }
        public Challenge MakeChallenge(Did ChallengingDID)
        {
            return DidAuth.MakeChallenge(ChallengingDID ?? throw new Exception("call RecvDid first"));
        }

        public async Task<bool> RecvChallengeResponse(Response response, Challenge Challenge)
        {
            if (!response.DidUrlFragment.V.Equals(string.Empty))
            {
                throw new Exception("multiple pubkeys not yet supported");
            }
            var challenge = Challenge ?? throw new Exception("call SendChallenge first");
            var result = await DidAuth.VerifyResponse(response, challenge);
            return result.IsOk;
        }

        public void RemoveConnection(int NetPeer)
        {
            if (AuthIdentity.TryRemove(NetPeer, out var authIdentity))
            {
                ReleaseDid(authIdentity.Did);
            }
            if (_timeouts.TryRemove(NetPeer, out var cts))
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
        }
        public bool IsNetPeerAdmin(string UUID)
        {
            if (Admins.ContainsKey(UUID))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public bool AddNetPeerAsAdmin(string UUID)
        {
            if (string.IsNullOrEmpty(UUID))
            {
                BNL.Log($"can't add was empty or null! {UUID}");
                return false;
            }
            else
            {
                BNL.Log($"AddNetPeerAsAdmin {UUID}");
                Admins.TryAdd(UUID, 0);
                SaveAdmins(Admins.Keys.ToArray(), FilePath);
                return true;
            }
        }
        static void SaveAdmins(string[] admins, string filePath)
        {
            if (!IAuthIdentity.HasFileSupport)
            {
                return;
            }
            admins ??= new string[0]; // Ensure it's not null

            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(string[]));
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    serializer.Serialize(writer, admins);
                }
            }
            catch (Exception ex)
            {
                BNL.LogError($"Error saving admins: {ex.Message} {ex.StackTrace}");
            }
        }

        static string[] LoadAdmins(string filePath)
        {
            if (IAuthIdentity.HasFileSupport)
            {
                if (File.Exists(filePath))
                {
                    try
                    {
                        XmlSerializer serializer = new XmlSerializer(typeof(string[]));
                        using (StreamReader reader = new StreamReader(filePath))
                        {
                            return (string[])serializer.Deserialize(reader);
                        }
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"Error loading admins (possibly corrupted file), deleting and recreating: {ex.Message}");
                        File.Delete(filePath);
                    }
                }

                // If file is missing or corrupted, create a new one
                BNL.Log("Creating a new admin list...");
                string[] newAdmins = new string[0];
                SaveAdmins(newAdmins, filePath);
                return newAdmins;
            }
            else
            {
                string[] newAdmins = new string[0];
                return newAdmins;
            }
        }


        public bool NetIDToUUID(NetPeer Peer, out string UUID)
        {
            if (AuthIdentity.TryGetValue(Peer.Id, out OnAuth OnAuth))
            {
                UUID = OnAuth.Did.V;
                return true;
            }
            UUID = string.Empty;
            return false;
        }

        public bool UUIDToNetID(string UUID, out int Peer)
        {
            foreach (KeyValuePair<int, OnAuth> Pair in AuthIdentity)
            {
                if (Pair.Value.Did.V == UUID)
                {
                    Peer = Pair.Key;
                    return true;
                }
            }
            Peer = 0;
            return false;
        }

        public bool RemoveNetPeerAsAdmin(string UUID)
        {
            BNL.Log($"RemoveNetPeerAsAdmin {UUID}");
            if (Admins.TryRemove(UUID, out _))
            {
                SaveAdmins(Admins.Keys.ToArray(), FilePath);
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}
