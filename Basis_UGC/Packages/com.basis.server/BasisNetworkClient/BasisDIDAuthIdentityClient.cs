using System;
using System.Diagnostics;
using System.Text;
using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using LiteNetLib;
using LiteNetLib.Utils;
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
        public static string GetOrSaveDID()
        {
#if UNITY_2017_1_OR_NEWER
            DidUrlFragment = new DidUrlFragment(string.Empty);

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
