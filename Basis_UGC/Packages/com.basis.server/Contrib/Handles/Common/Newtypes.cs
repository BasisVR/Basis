#nullable enable

// This file contains various wrapper types, to more safely differentiate them
// and help code document itself.

using Generator.Equals;

namespace Basis.Contrib.Auth.Handles.Newtypes
{
	// TODO: Unify with core basis' notion of identity and also DID's identity type
	/// `Identity` is a string that represents the player's machine-readable account
	/// identifier
	[Equatable]
	public sealed partial record Identity(string V);
}
