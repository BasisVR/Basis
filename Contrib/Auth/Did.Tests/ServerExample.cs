#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Basis.Contrib.Auth.DecentralizedIds;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Xunit;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace Basis.Contrib.Auth.DecentralizedIds
{
	class ConnectionState
	{
		readonly Server Server;
		Did? Did;
		Challenge? Challenge = null;

		public ConnectionState(Server server)
		{
			Server = server;
		}

		public Did? Player => Did;

		/// Returns false if connection should be terminated
		public bool RecvDid(Did playerDid)
		{
			Did = playerDid;
			return !Server.BannedDids.Contains(playerDid);
		}

		public Challenge SendChallenge()
		{
			Challenge = Server.DidAuth.MakeChallenge(
				Did ?? throw new Exception("call RecvDid first")
			);
			return Challenge;
		}

		/// Returns false if connection should be terminated
		public async Task<bool> RecvChallengeResponse(Response response)
		{
			if (!response.DidUrlFragment.V.Equals(string.Empty))
			{
				throw new Exception("multiple pubkeys not yet supported");
			}
			var challenge =
				Challenge ?? throw new Exception("call SendChallenge first");
			var result = await Server.DidAuth.VerifyResponse(response, challenge);
			return result.IsOk;
		}
	}

	class Server
	{
		internal readonly DidAuthentication DidAuth;
		internal readonly HashSet<Did> BannedDids = new();
		internal readonly HashSet<IPAddress> BannedIps = new();
		readonly Dictionary<IPAddress, ConnectionState> Connections = new();

		public Server(DidAuthentication didAuth)
		{
			DidAuth = didAuth;
		}

		public void Ban(IPAddress playerIp)
		{
			BannedIps.Add(playerIp);
			if (!Connections.TryGetValue(playerIp, out ConnectionState? conn))
			{
				// No such connection
				return;
			}
			Connections.Remove(playerIp);
			var connDid = conn.Player;
			if (connDid is not null)
			{
				BannedDids.Add(connDid);
			}
		}

		/// Returns a challenge that is sent to player, or else null if player is banned.
		public ConnectionState OnConnection(IPAddress remoteAddr)
		{
			var connectionState = new ConnectionState(this);
			Connections.Add(remoteAddr, connectionState);
			return connectionState;
		}
	}

	public class ServerExample
	{
		static (PubKey, PrivKey) RandomKeyPair(CryptoRng rng)
		{
			var privKeyBytes = new byte[Ed25519.PrivkeySize];
			rng.GetBytes(privKeyBytes);
			var privKey = new PrivKey(privKeyBytes);
			var pubKey =
				Ed25519.ConvertPrivkeyToPubkey(privKey)
				?? throw new Exception("privkey was invalid");
			return (pubKey, privKey);
		}

		[Fact]
		static async Task Main()
		{
			// Client
			var rng = CryptoRng.Create();
			var (pubKey, privKey) = RandomKeyPair(rng);
			var playerDid = DidKeyResolver.EncodePubkeyAsDid(pubKey);
			var playerIp = IPAddress.Loopback;

			// Server
			var cfg = new Config { Rng = rng };
			Server server = new(didAuth: new DidAuthentication(cfg));
			ConnectionState conn = server.OnConnection(playerIp);
			Debug.Assert(conn.RecvDid(playerDid));
			Challenge challenge = conn.SendChallenge();

			// Client
			var payloadToSign = new Payload(challenge.Nonce.V);
			Debug.Assert(
				Ed25519.Sign(privKey, payloadToSign, out Signature? sig),
				"signing with a valid privkey should always succeed"
			);
			Debug.Assert(
				sig is not null,
				"signing with a valid privkey should always succeed"
			);
			Debug.Assert(
				Ed25519.Verify(pubKey, sig, payloadToSign),
				"sanity check: verifying sig"
			);
			// for simplicity, use an empty fragment since the client only has one pubkey
			var response = new Response(sig, new DidUrlFragment(string.Empty));

			// Server
			var isAuthenticated = await conn.RecvChallengeResponse(response);
			Debug.Assert(isAuthenticated, "the response should have been valid");

			// Next we ban the player
			server.Ban(playerIp);

			// Client tries to connect again, but from a different IP
			var bannedConn = server.OnConnection(
				new IPAddress(new byte[] { 192, 168, 1, 1 })
			);
			// Connection terminated when DID matches ban list
			Debug.Assert(!bannedConn.RecvDid(playerDid));
		}
	}
}
