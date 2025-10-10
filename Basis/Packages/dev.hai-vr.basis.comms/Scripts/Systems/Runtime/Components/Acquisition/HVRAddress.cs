using System;
using System.Collections.Generic;

namespace HVR.Basis.Comms
{
    public class HVRAddress
    {
        private static readonly Dictionary<string, int> AddressToIdDict = new();
        private static readonly Dictionary<int, string> IdToAddressDict = new(); // TODO: Could probably make a List and stop using _nextId, or make a bidirectional dictionary
        private static int _nextId = 1;

        /// Generates a GUID address. Use this is when the string address doesn't matter, and you need an internal identifier to reference a value.
        /// Please store this address, don't call this over and over.
        /// Valid IDs start at 1.
        public static int NewRandomAddress()
        {
            return AddressToId(Guid.NewGuid().ToString());
        }

        /// Returns an ID for that address, storing that address if it was not seen before.
        /// This ID is only valid for the duration of the app's execution; don't store it across app executions.
        /// Valid IDs start at 1.<br/>
        /// You should store the returned value of this somewhere, the whole point of having addresses represented as strings is to
        /// avoid using string references on frequently invoked methods.
        public static int AddressToId(string address)
        {
            if (AddressToIdDict.TryGetValue(address, out var id)) return id;

            var newId = _nextId;
            AddressToIdDict.Add(address, newId);
            IdToAddressDict.Add(newId, address);
            _nextId++;

            return newId;
        }

        /// Returns the string address for an ID that was returned by any method of this class. Throws an exception if that ID was never seen.
        public static string ResolveKnownAddressFromId(int knownIddress)
        {
            if (IdToAddressDict.TryGetValue(knownIddress, out var id)) return id;
            throw new IndexOutOfRangeException();
        }
    }
}
