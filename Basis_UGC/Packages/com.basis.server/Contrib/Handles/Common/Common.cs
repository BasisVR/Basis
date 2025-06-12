#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Contrib.Auth.Handles.Newtypes;

namespace Basis.Contrib.Auth.Handles
{
	/// Resolves whether a handle points to a given identity.
	public interface IHandleVerifier
	{
		/// For documentation on this function, see `HandleVerifier`.
		public Task<bool> HandlePointsToIdentity(IHandle handle, Identity identity);

		/// The particular kind of handle
		public HandleKind Kind { get; }

		public HandleProperties Properties { get; }
	}

	/// All handle types implement `IHandle`
	public interface IHandle
	{
		/// Which type of handle?
		public HandleKind Kind { get; }

		public HandleProperties Properties { get; }

		/// Gets the display name to show.
		public string DisplayName { get; }
	}

	/// Information inherent to a particular `HandleKind` kind/type of handle.
	// TODO: Does it make sense to switch to a record struct?
	public record HandleProperties(
		HandleKind Kind,
		HandleMutability Mutability,
		bool IsGloballyUnique
	);

	/// The degree to which the set of identities that a handle points to can be
	/// changed.
	public enum HandleMutability
	{
		/// Handles always point to the same set of identities.
		Immutable,

		/// Once an identity is added to the set it always remains, but new identities
		/// can also be added.
		AppendOnly,

		/// Identities can be added and deleted from the set at will.
		Mutable,
	}

	/// The different supported DidMethods.
	public enum HandleKind
	{
		Local,
		Dns,
		// TODO: HttpWellKnown
		// TODO: Steam
	}
}
