using System;
using System.Diagnostics;
using System.Text;
using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Basis.Network.Core;
#if UNITY_2017_1_OR_NEWER
using UnityEngine;
#endif
using static Basis.Network.Core.Serializable.SerializableBasis;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace BasisNetworkClient
{
    public static class BasisDIDAuthIdentityClient
    {
        private static (PubKey, PrivKey) Key;
        private static Did DID;
        private const string PrivateKeyDID = "PrivateKeyDID";
        private const string PublicKeyDID = "PublicKeyDID";
        private const string DIDID = "DIDID";
        public static string GetOrSaveDID()
        {
#if UNITY_2017_1_OR_NEWER
            string privateKeyBase64 = PlayerPrefs.GetString(PrivateKeyDID, string.Empty);
            string publicKeyBase = PlayerPrefs.GetString(PublicKeyDID, string.Empty);
            string didId = PlayerPrefs.GetString(DIDID, string.Empty);

            if (string.IsNullOrEmpty(privateKeyBase64) || string.IsNullOrEmpty(publicKeyBase) || string.IsNullOrEmpty(didId))
            {
                ClientKeyCreation(out Key, out DID);
                PlayerPrefs.SetString(PrivateKeyDID, Convert.ToBase64String(Key.Item2.V));
                PlayerPrefs.SetString(PublicKeyDID, Convert.ToBase64String(Key.Item1.V));
                PlayerPrefs.SetString(DIDID, DID.V);
                PlayerPrefs.Save();
            }
            else
            {
                DID = new Did(didId);
                byte[] publicKeyBytes = Convert.FromBase64String(publicKeyBase);
                byte[] privateKeyBytes = Convert.FromBase64String(privateKeyBase64);

                PubKey pubKey = new PubKey(publicKeyBytes);
                PrivKey privKey = new PrivKey(privateKeyBytes);

                Key = (pubKey, privKey);
            }
            return DID.V;
#else
            return string.Empty;
#endif
        }

        public static bool IdentityMessage(NetworkClient Client, NetPacketReader Reader, out NetDataWriter Writer)
        {
            return RespondToChallenge(Client, Key.Item2, DID?.V, Reader, out Writer);
        }

        /// <summary>
        /// Answers the server's login challenge: signs <see cref="BasisDidLoginTranscript"/> with the
        /// DID key, then seals everything the client sends from here on with the connection key.
        /// A relay that passes the signature on can only use it with this client's connection key and
        /// connect payload, so it can neither send as this client nor change how it appears.
        /// </summary>
        public static bool RespondToChallenge(NetworkClient Client, PrivKey PrivateKey, string Did, NetPacketReader Reader, out NetDataWriter Writer)
        {
            Writer = new NetDataWriter();
            if (Client == null || PrivateKey == null || string.IsNullOrEmpty(Did))
            {
                BNL.LogError("No identity to answer the login challenge with");
                return false;
            }
            if (Client.LegacyLogin)
            {
                return RespondToLegacyChallenge(PrivateKey, Reader, Writer);
            }

            BytesMessage ChallengeBytes = new BytesMessage();
            if (!ChallengeBytes.Deserialize(Reader, out byte[] Nonce) || Nonce.Length < BasisDidLoginTranscript.MinNonceSize ||
                !ChallengeBytes.Deserialize(Reader, out byte[] ServerEphemeralPublic) || ServerEphemeralPublic.Length != BasisCryptoHandshake.PublicKeySize ||
                !ChallengeBytes.Deserialize(Reader, out byte[] ServerIdBytes))
            {
                BNL.LogError("Malformed auth challenge from server");
                return false;
            }
            string ServerId = Encoding.UTF8.GetString(ServerIdBytes);
            if (Client.ConnectionPublicKey == null || Client.ConnectionPayloadHash == null)
            {
                BNL.LogError("This connection was not opened for a DID login");
                return false;
            }

            Payload payloadToSign = new Payload(BasisDidLoginTranscript.Build(Did, ServerId, Nonce, Client.ConnectionPublicKey, ServerEphemeralPublic, Client.ConnectionPayloadHash));
            if (Ed25519.Sign(PrivateKey, payloadToSign, out Signature sig) == false || sig == null)
            {
                BNL.LogError("Unable to sign Key");
                return false;
            }
            if (!Client.TrySealToServer(ServerEphemeralPublic))
            {
                BNL.LogError("Unable to set up the connection key");
                return false;
            }
            WriteResponse(Writer, sig);
            return true;
        }
        private static bool RespondToLegacyChallenge(PrivKey PrivateKey, NetPacketReader Reader, NetDataWriter Writer)
        {
            BytesMessage ChallengeBytes = new BytesMessage();
            if (!ChallengeBytes.Deserialize(Reader, out byte[] Nonce) || !BasisDidLoginTranscript.IsLegacyNonce(Nonce))
            {
                BNL.LogError("Malformed auth challenge from server");
                return false;
            }
            if (Ed25519.Sign(PrivateKey, new Payload(Nonce), out Signature sig) == false || sig == null)
            {
                BNL.LogError("Unable to sign Key");
                return false;
            }
            WriteResponse(Writer, sig);
            return true;
        }
        private static void WriteResponse(NetDataWriter Writer, Signature sig)
        {
            // for simplicity, use an empty fragment since the client only has one pubkey
            BytesMessage SignatureBytes = new BytesMessage();
            BytesMessage FragmentBytes = new BytesMessage();
            SignatureBytes.Serialize(Writer, sig.V);
            FragmentBytes.Serialize(Writer, Encoding.UTF8.GetBytes("N/A"));
        }
        public static (PubKey, PrivKey) RandomKeyPair(CryptoRng rng)
        {
            var privKeyBytes = new byte[Ed25519.PrivkeySize];
            rng.GetBytes(privKeyBytes);
            var privKey = new PrivKey(privKeyBytes);
            var pubKey = Ed25519.ConvertPrivkeyToPubkey(privKey) ?? throw new Exception("privkey was invalid");
            return (pubKey, privKey);
        }
        public static void ClientKeyCreation(out (PubKey, PrivKey) Keys, out Did Did)
        {
            // Client
            CryptoRng rng = CryptoRng.Create();
            Keys = RandomKeyPair(rng);
            Did = DidKeyResolver.EncodePubkeyAsDid(Keys.Item1);
        }
    }
}
