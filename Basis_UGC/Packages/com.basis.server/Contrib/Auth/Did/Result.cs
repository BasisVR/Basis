#nullable enable

namespace Basis.Contrib.Auth.DecentralizedIds
{
	public readonly struct Result<T, E>
	{
		private readonly bool isOk;
		private readonly T? ok;
		private readonly E? err;

		private Result(T? ok, E? err, bool isOk)
		{
			this.ok = ok;
			this.err = err;
			this.isOk = isOk;
		}

		public bool IsOk => isOk;
		public bool IsErr => !isOk;

		public T GetOk => ok ?? throw new InvalidVariantExeption();

		public E GetErr => err ?? throw new InvalidVariantExeption();

		public static Result<T, E> Ok(T v)
		{
			return new(v, default, true);
		}

		public static Result<T, E> Err(E e)
		{
			return new(default, e, false);
		}

		public static implicit operator Result<T, E>(T v) => new(v, default, true);

		public static implicit operator Result<T, E>(E e) => new(default, e, false);
	}

	public class InvalidVariantExeption : System.Exception
	{
		public InvalidVariantExeption()
			: base("wrong result variant") { }
	}
}
