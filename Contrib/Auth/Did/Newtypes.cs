#nullable enable

// This file contains various wrapper types, to more safely differentiate them
// and help code document itself.

namespace Basis.Contrib.Auth.DecentralizedIds.Newtypes
{
	/// A DID. DIDs do *not* contain any fragment portion. See
	/// https://www.w3.org/TR/did-core/#did-syntax
	public sealed record Did(string V);

	/// A full DID Url, which is a did along with an optional path query and
	/// fragment. See
	/// https://www.w3.org/TR/did-core/#did-url-syntax
	public sealed record DidUrl(string V);

	/// A DID Url Fragment. Does not include the `#` part. Can be empty.
	public sealed record DidUrlFragment(string V);

	/// A signature.
	public sealed record Signature(byte[] V);

	/// A random nonce.
	public sealed record Nonce(byte[] V);
}
