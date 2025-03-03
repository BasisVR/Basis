#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Contrib.Auth.DecentralizedIds.Newtypes;
using Basis.Contrib.Crypto;
using Did = Basis.Contrib.Auth.DecentralizedIds.Newtypes.Did;

namespace Basis.Contrib.Auth.DecentralizedIds
{
	/// The functionality that all DID methods implement
	public interface IDidMethod
	{
		/// Resolves to a map of DID Url fragments to a Json Web Key. This method
		/// resolves a DID to its DID Document, and inspects the `verificationMethods`
		/// field to extract a dictionary of public keys.
		///
		/// Even though json is not what all DID methods will use to represent keys,
		/// we standardize the api to return JsonWebKey because it documents its
		/// own key algorithms and is a reasonably portable format.
		public Task<DidDocument> ResolveDocument(Did did);

		public DidMethodKind Kind { get; }
	}

	/// The different supported DidMethods.
	public enum DidMethodKind
	{
		Key,
		// TODO: Web
	}
}
