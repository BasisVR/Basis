#nullable enable

using Org.BouncyCastle.Crypto.Parameters;
using Debug = System.Diagnostics.Debug;
using Ed25519Signer = Org.BouncyCastle.Crypto.Signers.Ed25519Signer;
using Exception = System.Exception;
using Rfc8032 = Org.BouncyCastle.Math.EC.Rfc8032;

namespace Basis.Contrib.Crypto
{
	/// Ed25519 elliptic-curve signature algorithm.
	public sealed class Ed25519
	{
		public static readonly int PubkeySize = Rfc8032.Ed25519.PublicKeySize;
		public static readonly int PrivkeySize = Rfc8032.Ed25519.SecretKeySize;

		/// Returns `null` when the conversion failed. Should never fail as long
		/// as the privkey is a valid privkey.
		public static PubKey? ConvertPrivkeyToPubkey(PrivKey privkey)
		{
			Ed25519PrivateKeyParameters privParams;
			try
			{
				privParams = new(privkey.V);
			}
			catch
			{
				return null;
			}
			var pubkeyBytes = privParams.GeneratePublicKey().GetEncoded();
			Debug.Assert(pubkeyBytes.Length == Rfc8032.Ed25519.PublicKeySize);
			return new PubKey(pubkeyBytes);
		}

		/// Returns `false` if verification failed.
		public static bool Verify(PubKey pubkey, Signature sig, Payload payload)
		{
			Ed25519PublicKeyParameters ed25519Params;
			try
			{
				ed25519Params = new Ed25519PublicKeyParameters(pubkey.V);
			}
			catch
			{
				return false;
			}
			var signer = new Ed25519Signer();
			signer.Init(false, ed25519Params);
			signer.BlockUpdate(buf: payload.V, off: 0, len: payload.V.Length);
			return signer.VerifySignature(sig.V);
		}

		/// Returns `false` and stores `null` in `sig` if signing failed.
		public static bool Sign(PrivKey privkey, Payload payload, out Signature? sig)
		{
			Ed25519PrivateKeyParameters ed25519Params;
			sig = null;
			try
			{
				ed25519Params = new Ed25519PrivateKeyParameters(privkey.V);
			}
			catch
			{
				return false;
			}
			var signer = new Ed25519Signer();
			signer.Init(true, ed25519Params);
			signer.BlockUpdate(buf: payload.V, off: 0, len: payload.V.Length);
			byte[] sigBytes;
			try
			{
				sigBytes = signer.GenerateSignature();
			}
			catch (Exception e)
			{
				Debug.Fail(
					"it shouldn't be possible for GenerateSignature to fail since we "
						+ "already initialized it, something went very wrong"
				);
				throw e;
			}
			Debug.Assert(sigBytes.Length == Rfc8032.Ed25519.SignatureSize);
			sig = new Signature(sigBytes);
			return true;
		}
	}
}
