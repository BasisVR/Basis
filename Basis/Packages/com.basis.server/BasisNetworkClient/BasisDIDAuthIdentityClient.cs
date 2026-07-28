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
        private static DidUrlFragment DidUrlFragment;
        private const string PrivateKeyDID = "PrivateKeyDID";
        private const string PublicKeyDID = "PublicKeyDID";
        private const string DIDID = "DIDID";

        /// <summary>
        /// Optional per-identity namespace. When set (e.g. to an SSO subject id), the DID keypair
        /// is stored under namespaced PlayerPrefs keys so multiple signed-in users on one machine
        /// keep separate DIDs. Empty/null preserves the original global keys — existing installs
        /// are unaffected. Set this before the DID provider resolves (see BasisSsoIdentityBinding).
        /// </summary>
        public static string IdentityNamespace;

        private static string NamespacedKey(string baseKey) =>
            string.IsNullOrEmpty(IdentityNamespace) ? baseKey : baseKey + "::" + IdentityNamespace;

        public static string GetOrSaveDID()
        {
#if UNITY_2017_1_OR_NEWER
            DidUrlFragment = new DidUrlFragment(string.Empty);

            string privateKeyKey = NamespacedKey(PrivateKeyDID);
            string publicKeyKey = NamespacedKey(PublicKeyDID);
            string didIdKey = NamespacedKey(DIDID);

            string privateKeyBase64 = PlayerPrefs.GetString(privateKeyKey, string.Empty);
            string publicKeyBase = PlayerPrefs.GetString(publicKeyKey, string.Empty);
            string didId = PlayerPrefs.GetString(didIdKey, string.Empty);

            if (string.IsNullOrEmpty(privateKeyBase64) || string.IsNullOrEmpty(publicKeyBase) || string.IsNullOrEmpty(didId))
            {
                ClientKeyCreation(out Key, out DID);
                PlayerPrefs.SetString(privateKeyKey, Convert.ToBase64String(Key.Item2.V));
                PlayerPrefs.SetString(publicKeyKey, Convert.ToBase64String(Key.Item1.V));
                PlayerPrefs.SetString(didIdKey, DID.V);
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

        public static bool IdentityMessage(NetPeer peer, NetPacketReader Reader, out NetDataWriter Writer)
        {
            Writer = new NetDataWriter();
            BytesMessage ChallengeBytes = new BytesMessage();

            ChallengeBytes.Deserialize(Reader, out byte[] PayloadBytes);
            // Client
            Payload payloadToSign = new Payload(PayloadBytes);
            if (Ed25519.Sign(Key.Item2, payloadToSign, out Signature sig) == false)
            {
                BNL.LogError("Unable to sign Key");
                return false;
            }
            if (Ed25519.Verify(Key.Item1, sig, payloadToSign) == false)
            {
                BNL.LogError("Unable to Very Key");
                return false;
            }
            // for simplicity, use an empty fragment since the client only has one pubkey
            Response response = new Response(sig, DidUrlFragment);
            BytesMessage SignatureBytes = new BytesMessage();
            BytesMessage FragmentBytes = new BytesMessage();
            SignatureBytes.Serialize(Writer, response.Signature.V);
            string Fragment = response.DidUrlFragment.V;
            if (string.IsNullOrEmpty(Fragment))
            {
                Fragment = "N/A";
            }
            FragmentBytes.Serialize(Writer, Encoding.UTF8.GetBytes(Fragment));
            return true;
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
