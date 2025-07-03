namespace Basis.Scripts.Networking.Compression
{
    public static class MathExtensions
    {
        public static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
    public struct Vector3
    {
        public float x;
        public float y;
        public float z;
        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
        // Subtraction operator for convenience
        public static Vector3 operator -(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        }
        public static Vector3 operator +(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        }

        // Squared magnitude (squared length of a vector)
        public float SquaredMagnitude()
        {
            return x * x + y * y + z * z;
        }

    }
    public struct Vector4
    {
        public float x;
        public float y;
        public float z;
        public float w;
    }
    public struct Quaternion
    {
        public Vector4 value;
        public Quaternion(float x, float y, float z, float w) : this()
        {
            value.x = x;
            value.y = y;
            value.z = z;
            value.w = w;
        }
    }
    public struct float3
    {
        public float x;
        public float y;
        public float z;
    }
}
