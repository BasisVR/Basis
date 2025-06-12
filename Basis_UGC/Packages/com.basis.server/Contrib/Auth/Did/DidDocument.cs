#nullable enable

using System.Collections.ObjectModel;
using Generator.Equals;
using DidUrlFragment = Basis.Contrib.Auth.DecentralizedIds.Newtypes.DidUrlFragment;

namespace Basis.Contrib.Auth.DecentralizedIds
{
	/// Contains the info that we care about in the DID Document.
	/// A DID Document is what a DID is resolved into. See
	/// https://www.w3.org/TR/did-core/#did-resolution
	[Equatable]
	public sealed partial record DidDocument(
		[property: UnorderedEquality]
			ReadOnlyDictionary<DidUrlFragment, JsonWebKey> Pubkeys
	);
}
