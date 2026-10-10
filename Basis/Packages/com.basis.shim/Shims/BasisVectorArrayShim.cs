using UnityEngine;

namespace Basis.Shims
{
	public static class BasisVectorArrayShim
	{
		public static void Split(Vector3[] source, float[] x, float[] y, float[] z)
		{
			if (source == null)
			{
				return;
			}
			Split(source, 0, source.Length, x, y, z);
		}

		public static void Split(Vector3[] source, int start, int count, float[] x, float[] y, float[] z)
		{
			if (source == null || x == null || y == null || z == null || start < 0)
			{
				return;
			}
			int limit = Mathf.Min(source.Length, Mathf.Min(x.Length, Mathf.Min(y.Length, z.Length)));
			int end = count < limit - start ? start + count : limit;
			for (int i = start; i < end; i++)
			{
				Vector3 value = source[i];
				x[i] = value.x;
				y[i] = value.y;
				z[i] = value.z;
			}
		}

		public static void Join(float[] x, float[] y, float[] z, Vector3[] destination)
		{
			if (destination == null)
			{
				return;
			}
			Join(x, y, z, 0, destination.Length, destination);
		}

		public static void Join(float[] x, float[] y, float[] z, int start, int count, Vector3[] destination)
		{
			if (destination == null || x == null || y == null || z == null || start < 0)
			{
				return;
			}
			int limit = Mathf.Min(destination.Length, Mathf.Min(x.Length, Mathf.Min(y.Length, z.Length)));
			int end = count < limit - start ? start + count : limit;
			for (int i = start; i < end; i++)
			{
				destination[i] = new Vector3(x[i], y[i], z[i]);
			}
		}

		public static void Split(Vector2[] source, float[] x, float[] y)
		{
			if (source == null || x == null || y == null)
			{
				return;
			}
			int count = Mathf.Min(source.Length, Mathf.Min(x.Length, y.Length));
			for (int i = 0; i < count; i++)
			{
				Vector2 value = source[i];
				x[i] = value.x;
				y[i] = value.y;
			}
		}

		public static void Join(float[] x, float[] y, Vector2[] destination)
		{
			if (destination == null || x == null || y == null)
			{
				return;
			}
			int count = Mathf.Min(destination.Length, Mathf.Min(x.Length, y.Length));
			for (int i = 0; i < count; i++)
			{
				destination[i] = new Vector2(x[i], y[i]);
			}
		}
	}
}
