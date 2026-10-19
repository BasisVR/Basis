using System;
using System.Collections.Generic;

namespace Basis.Scripts.BasisSdk.Players
{
    public sealed class BasisChatHistory
    {
        public const int Capacity = 15;

        public readonly struct Entry
        {
            public readonly string Message;
            public readonly DateTime ReceivedUtc;

            public Entry(string message, DateTime receivedUtc)
            {
                Message = message;
                ReceivedUtc = receivedUtc;
            }
        }

        private readonly List<Entry> entries = new List<Entry>(Capacity);

        public int Count => entries.Count;

        public int Version { get; private set; }

        public Entry this[int index] => entries[index];

        public void Add(string message, DateTime receivedUtc)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (entries.Count == Capacity)
            {
                entries.RemoveAt(0);
            }

            entries.Add(new Entry(message, receivedUtc));
            Version++;
        }
    }
}
