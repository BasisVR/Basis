#nullable enable
using System;

namespace Basis.Contrib.Auth.DecentralizedIds.Result
{
	public readonly struct Success { }

	/// Analagous to rust's Result type.
	public abstract record Result<T, E>
	{
		private Result() { }

		public sealed record Ok(T Ok) : Result<T, E> { }

		public sealed record Err(E Err) : Result<T, E> { }
	}
}
