using Basis.Scripts.Networking.Compression;
using static Org.BouncyCastle.Asn1.Cmp.Challenge;

namespace BasisNetworkClientConsole
{
    public class Randomizer
    {
        public static Vector3 GetRandomPosition(Vector3 min, Vector3 max)
        {
            Random random = new Random();

            float randomX = GetRandomFloat(random, min.x, max.x);
            float randomY = GetRandomFloat(random, min.y, max.y);
            float randomZ = GetRandomFloat(random, min.z, max.z);

            return new Vector3(randomX, randomY, randomZ);
        }

        public static float GetRandomFloat(Random random, float min, float max)
        {
            return (float)(random.NextDouble() * (max - min) + min);
        }
        public static Vector3 GetRandomOffset(float maxOffset = 0.5f)
        {
            Random random = new Random();
            float x = (float)(random.NextDouble() * 2 - 1) * maxOffset;
            float y = (float)(random.NextDouble() * 2 - 1) * maxOffset;
            float z = (float)(random.NextDouble() * 2 - 1) * maxOffset;
            return new Vector3(x, y, z);
        }
    }
}
