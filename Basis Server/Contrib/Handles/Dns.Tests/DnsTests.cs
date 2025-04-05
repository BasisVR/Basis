#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;
using Identity = Basis.Contrib.Auth.Handles.Newtypes.Identity;
using LookupClient = DnsClient.LookupClient;
using LookupClientOptions = DnsClient.LookupClientOptions;
using NameServer = DnsClient.NameServer;

namespace Basis.Contrib.Auth.Handles.Dns
{
	public class DnsTests
	{
		[Fact]
		public async Task KnownExample()
		{
			var opts = new LookupClientOptions(NameServer.Cloudflare);
			var dnsClient = new LookupClient(opts);
			var dnsVerifier = new DnsHandleResolver(client: dnsClient);
			var verifier = new HandleVerifier(
				new Config()
				{
					Verifiers = new Dictionary<HandleKind, IHandleVerifier>()
					{
						{ HandleKind.Dns, dnsVerifier },
					},
				}
			);

			var handle = new DnsHandle() { DisplayName = "example.socialvr.net" };
			var identity = new Identity("did:web:example.socialvr.net");
			Debug.Assert(
				await dnsVerifier.HandlePointsToIdentity(
					handle: handle,
					identity: identity
				),
				"should match the known, did:web that have been set in socialvr.net's DNS record"
			);
			Debug.Assert(
				await verifier.HandlePointsToIdentity(
					handle: handle,
					identity: identity
				),
				"should match the known, did:web that have been set in socialvr.net's DNS record"
			);
		}
	}
}
