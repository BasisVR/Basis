#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Contrib.Auth.Handles.Newtypes;

namespace Basis.Contrib.Auth.Handles
{
	/// Configuration for [`HandleVerifier`].
	///
	/// Be sure to populate the `Verifiers`.
	public sealed record Config
	{
		public Dictionary<HandleKind, IHandleVerifier> Verifiers { get; init; } = new();
	}

	/// Resolves whether a handle points to a given identity.
	public class HandleVerifier
	{
		private readonly Dictionary<HandleKind, IHandleVerifier> verifiers;

		public HandleVerifier(Config cfg)
		{
			verifiers = cfg.Verifiers;
		}

		/// Checks if the given `Handle` "points to" the given `Identity`.
		///
		/// SECURITY NOTE:
		///
		/// All of the following must be true to consider a handle associated with a
		/// given peer:
		/// * `HandlePointsToIdentity(handle, identity)` returns `true`
		/// * `identity` points to `handle` through some other mechanism (for example,
		///   a peer is authenticated on `identity` and has requested `handle` to be
		///   displayed to other players).
		///
		/// If you only establish that `handle` -> `identity` without also ensuring
		/// ensuring that `identity` -> `handle`, then its possible for Bob to
		/// point `bob.com` to Alice's handle, and make Alice appear as `bob.com`
		/// without Alice's consent. Likewise if you *only* establish that
		/// `identity` -> `handle`, then bob could point their identity to `alice.com`
		/// and masquerade/spoof themselves as alice. This is why its *very* important
		/// to establish a bidirectional mapping: `handle` <-> `identity`.
		public async Task<bool> HandlePointsToIdentity(
			IHandle handle,
			Identity identity
		)
		{
			if (!verifiers.TryGetValue(handle.Kind, out IHandleVerifier? verifier))
			{
				return false;
			}
			return await verifier.HandlePointsToIdentity(handle, identity);
		}
	}
}
