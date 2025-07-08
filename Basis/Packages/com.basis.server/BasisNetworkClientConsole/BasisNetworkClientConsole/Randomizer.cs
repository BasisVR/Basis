using Basis.Scripts.Networking.Compression;
using System;

namespace BasisNetworkClientConsole
{
    public class Randomizer
    {
        private static readonly Random _random = new Random();
        public static Vector3 GetRandomOffset()
        {
            return new Vector3(
                (float)(_random.NextDouble() * 2 - 1),
                (float)(_random.NextDouble() * 2 - 1),
                (float)(_random.NextDouble() * 2 - 1)
            );
        }
    }
}
