#nullable enable

using System;

namespace Basis.Network.Core
{
	public sealed class BasisReplayWindow
	{
		public const int Size = 1024;
		private const int Words = Size / 64;

		private readonly ulong[] _seen = new ulong[Words];
		private readonly object _lock = new object();
		private long _highest;

		public bool CouldAccept(long counter)
		{
			if (counter <= 0) return false;
			lock (_lock)
			{
				return counter > _highest || (_highest - counter < Size && !IsSet(counter));
			}
		}

		public bool Accept(long counter)
		{
			// Senders start at 1; 0 and anything that wrapped negative never came from Seal.
			if (counter <= 0) return false;
			lock (_lock)
			{
				if (counter > _highest)
				{
					Clear(_highest + 1, counter);
					_highest = counter;
					SetBit(counter);
					return true;
				}

				if (_highest - counter >= Size) return false;
				if (IsSet(counter)) return false;
				SetBit(counter);
				return true;
			}
		}

		private void Clear(long from, long until)
		{
			if (until - from >= Size)
			{
				Array.Clear(_seen, 0, Words);
				return;
			}
			long counter = from;
			while (counter < until)
			{
				int slot = (int)(counter % Size);
				int bit = slot & 63;
				int count = (int)Math.Min(64 - bit, until - counter);
				ulong mask = count == 64 ? ulong.MaxValue : ((1UL << count) - 1) << bit;
				_seen[slot >> 6] &= ~mask;
				counter += count;
			}
		}

		private bool IsSet(long counter)
		{
			int slot = (int)(counter % Size);
			return (_seen[slot >> 6] & (1UL << (slot & 63))) != 0;
		}

		private void SetBit(long counter)
		{
			int slot = (int)(counter % Size);
			_seen[slot >> 6] |= 1UL << (slot & 63);
		}
	}
}
