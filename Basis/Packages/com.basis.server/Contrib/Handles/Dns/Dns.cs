#nullable enable

using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Basis.Contrib.Auth.Handles.Newtypes;
using LookupClient = DnsClient.LookupClient;
using QueryType = DnsClient.QueryType;

namespace Basis.Contrib.Auth.Handles.Dns
{
	public sealed class DnsHandleResolver : IHandleVerifier
	{
		public HandleKind Kind => DnsHandle.KIND;
		public HandleProperties Properties => DnsHandle.PROPERTIES;

		private const string TXT_RECORD_PREFIX = "_nexus-handles";
		private readonly LookupClient Client;

		public DnsHandleResolver(LookupClient client)
		{
			Client = client;
		}

		public async Task<bool> HandlePointsToIdentity(
			IHandle handle,
			Identity identity
		)
		{
			var sb = new StringBuilder(TXT_RECORD_PREFIX);
			sb.Append(".");
			sb.Append(handle.DisplayName);
			var lookupResult = await Client.QueryAsync(sb.ToString(), QueryType.TXT);

			var txtAttrs = lookupResult.Answers.TxtRecords().FirstOrDefault()?.Text;
			if (txtAttrs == null)
			{
				return false;
			}

			foreach (var attr in txtAttrs)
			{
				Console.WriteLine(attr);
				var parts = attr.Split(
					separator: "=",
					count: 2,
					StringSplitOptions.RemoveEmptyEntries
				);
				var prefix = parts[0];
				var suffix = parts[1];
				if (prefix != "did")
				{
					// TODO: introduce custom exception type
					throw new Exception(
						"dns txt record did not match expected format 2"
					);
				}

				if (suffix == identity.V)
				{
					return true;
				}
			}

			return false;
		}
	}

	/// Essentially just a string.
	public readonly struct DnsHandle : IHandle
	{
		public static readonly HandleProperties PROPERTIES = new(
			Kind: HandleKind.Dns,
			Mutability: HandleMutability.Mutable,
			IsGloballyUnique: true
		);
		public static readonly HandleKind KIND = HandleKind.Dns;

		public readonly HandleKind Kind => KIND;
		public readonly HandleProperties Properties => PROPERTIES;

		public readonly string DisplayName { get; init; }
	}
}
