#nullable enable

using System;
using System.Collections.Generic;
using Generator.Equals;

namespace Basis.Contrib.Crypto
{
	// TODO: Switch to record struct when they land in Unity to be zero-cost.

	[Equatable]
	public sealed partial record Payload([property: OrderedEquality] byte[] V);

	[Equatable]
	public sealed partial record Signature([property: OrderedEquality] byte[] V);

	/// Public asymmetric key
	[Equatable]
	public sealed partial record PubKey([property: OrderedEquality] byte[] V);

	/// Private (secret) asymmetric key
	[Equatable]
	public sealed partial record PrivKey([property: OrderedEquality] byte[] V);

	/// Private (secret) symmetric key
	[Equatable]
	public sealed partial record SharedSecretKey([property: OrderedEquality] byte[] V);

	/// The full set of SigningAlgorithms we support
	public enum SigningAlgorithm
	{
		Ed25519,
	}
}
