#nullable enable

// This file contains various wrapper types, to more safely differentiate them
// and help code document itself.

using Generator.Equals;

namespace Basis.Contrib.Auth.DecentralizedIds.Newtypes
{
	/// A DID. DIDs do *not* contain any fragment portion. See
	/// https://www.w3.org/TR/did-core/#did-syntax
	[Equatable]
	public sealed partial record Did([property: OrderedEquality] string V);

	/// A full DID Url, which is a did along with an optional path query and
	/// fragment. See
	/// https://www.w3.org/TR/did-core/#did-url-syntax
	[Equatable]
	public sealed partial record DidUrl([property: OrderedEquality] string V);

	/// A DID Url Fragment. Does not include the `#` part. Can be empty.
	[Equatable]
	public sealed partial record DidUrlFragment([property: OrderedEquality] string V);

	/// A random nonce.
	[Equatable]
	public sealed partial record Nonce([property: OrderedEquality] byte[] V);
}
