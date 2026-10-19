using System;

namespace Basis.Shims
{
	/// <summary>
	/// Random numbers every client agrees on without sending a message. Value(seed, index) is a pure
	/// hash, so the same seed and index give the same number on every machine. The Now* helpers use
	/// the server clock cut into windows of periodSeconds, so all players roll the same value for the
	/// same window. Clocks differ by a few milliseconds, so two players can briefly disagree right at
	/// a window edge; pick a period well above that. Not for anything that must resist prediction.
	/// </summary>
	public static class BasisSharedRandomShim
	{
		public const double MinPeriodSeconds = 0.001d;

		/// <summary>0 (inclusive) to 1 (exclusive).</summary>
		public static float Value(int seed, long index) => (Hash(seed, index) >> 40) * (1f / (1 << 24));

		public static float Range(int seed, long index, float min, float max) => min + (max - min) * Value(seed, index);

		/// <summary>min inclusive, max exclusive; returns min when max is not above it.</summary>
		public static int RangeInt(int seed, long index, int min, int max)
		{
			if (max <= min) return min;
			ulong span = (ulong)((long)max - min);
			return (int)(min + (long)(Hash(seed, index) % span));
		}

		/// <summary>Which window of periodSeconds the server clock is in now; 0 for a bad period.</summary>
		public static long WindowIndex(double periodSeconds)
		{
			if (double.IsNaN(periodSeconds) || double.IsInfinity(periodSeconds) || periodSeconds < MinPeriodSeconds) return 0;
			return (long)Math.Floor(BasisNetworkTimeShim.ServerTimeSeconds / periodSeconds);
		}

		public static float Now(int seed, double periodSeconds) => Value(seed, WindowIndex(periodSeconds));

		public static float NowRange(int seed, double periodSeconds, float min, float max) => Range(seed, WindowIndex(periodSeconds), min, max);

		public static int NowRangeInt(int seed, double periodSeconds, int min, int max) => RangeInt(seed, WindowIndex(periodSeconds), min, max);

		// SplitMix64 over the seed and index, so neighbouring seeds and indices are uncorrelated.
		private static ulong Hash(int seed, long index)
		{
			ulong z = unchecked((ulong)index + 0x9E3779B97F4A7C15UL * ((ulong)(uint)seed + 1UL));
			z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
			z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
			return z ^ (z >> 31);
		}
	}
}
