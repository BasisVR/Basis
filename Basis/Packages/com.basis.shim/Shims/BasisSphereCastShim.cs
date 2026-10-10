using System;
using UnityEngine;

namespace Basis.Shims
{
	public static class BasisSphereCastShim
	{
		public static int SweepSpheres(float originX, float originY, float originZ, float directionX, float directionY, float directionZ,
			float[] centersX, float[] centersY, float[] centersZ, int count, uint skipMask, float contactDistanceSq, float overlapDistanceSq,
			ref float nearest, out bool overlapping)
		{
			overlapping = false;
			if (centersX == null || centersY == null || centersZ == null)
			{
				return -1;
			}
			count = Mathf.Min(count, Mathf.Min(centersX.Length, Mathf.Min(centersY.Length, centersZ.Length)));
			int hit = -1;
			for (int i = 0; i < count; i++)
			{
				if (i < 32 && ((skipMask >> i) & 1u) != 0u)
				{
					continue;
				}
				float hx = (float)(centersX[i] - originX);
				float hy = (float)(centersY[i] - originY);
				float hz = (float)(centersZ[i] - originZ);
				float hh = dot(hx, hy, hz, hx, hy, hz);
				if (hh < overlapDistanceSq)
				{
					overlapping = true;
					return i;
				}
				float lf = dot(directionX, directionY, directionZ, hx, hy, hz);
				if (lf < 0f)
				{
					continue;
				}
				float s = (float)((float)(contactDistanceSq - hh) + (float)(lf * lf));
				if (s < 0.0f)
				{
					continue;
				}
				float distance = (float)(lf - (float)Math.Sqrt(s));
				if (distance < nearest)
				{
					nearest = distance;
					hit = i;
				}
			}
			return hit;
		}

		public static int SweepSpheres(Vector3 origin, Vector3 direction, Vector3[] centers, uint skipMask, float contactDistanceSq, float overlapDistanceSq,
			ref float nearest, out bool overlapping)
		{
			overlapping = false;
			if (centers == null)
			{
				return -1;
			}
			int hit = -1;
			for (int i = 0; i < centers.Length; i++)
			{
				if (i < 32 && ((skipMask >> i) & 1u) != 0u)
				{
					continue;
				}
				Vector3 center = centers[i];
				float hx = (float)(center.x - origin.x);
				float hy = (float)(center.y - origin.y);
				float hz = (float)(center.z - origin.z);
				float hh = dot(hx, hy, hz, hx, hy, hz);
				if (hh < overlapDistanceSq)
				{
					overlapping = true;
					return i;
				}
				float lf = dot(direction.x, direction.y, direction.z, hx, hy, hz);
				if (lf < 0f)
				{
					continue;
				}
				float s = (float)((float)(contactDistanceSq - hh) + (float)(lf * lf));
				if (s < 0.0f)
				{
					continue;
				}
				float distance = (float)(lf - (float)Math.Sqrt(s));
				if (distance < nearest)
				{
					nearest = distance;
					hit = i;
				}
			}
			return hit;
		}

		public static int RaySpheres(float originX, float originY, float originZ, float directionX, float directionY, float directionZ,
			float[] centersX, float[] centersY, float[] centersZ, int count, float radiusSq, ref float nearest)
		{
			if (centersX == null || centersY == null || centersZ == null)
			{
				return -1;
			}
			count = Mathf.Min(count, Mathf.Min(centersX.Length, Mathf.Min(centersY.Length, centersZ.Length)));
			normalize(directionX, directionY, directionZ, out float nx, out float ny, out float nz);
			int hit = -1;
			for (int i = 0; i < count; i++)
			{
				if (!raySphere(originX, originY, originZ, nx, ny, nz, centersX[i], centersY[i], centersZ[i], radiusSq, out float px, out float py, out float pz))
				{
					continue;
				}
				float distance = length((float)(originX - px), (float)(originY - py), (float)(originZ - pz));
				if (distance < nearest)
				{
					nearest = distance;
					hit = i;
				}
			}
			return hit;
		}

		public static uint RaySpheresMask(Vector3 origin, Vector3 direction, Vector3[] centers, uint skipMask, float radiusSq)
		{
			if (centers == null)
			{
				return 0u;
			}
			int count = Mathf.Min(centers.Length, 32);
			normalize(direction.x, direction.y, direction.z, out float nx, out float ny, out float nz);
			uint hits = 0u;
			for (int i = 0; i < count; i++)
			{
				uint bit = 1u << i;
				if ((skipMask & bit) != 0u)
				{
					continue;
				}
				Vector3 center = centers[i];
				if (raySphere(origin.x, origin.y, origin.z, nx, ny, nz, center.x, center.y, center.z, radiusSq, out float px, out float py, out float pz))
				{
					hits |= bit;
				}
			}
			return hits;
		}

		public static bool RaySphere(float originX, float originY, float originZ, float directionX, float directionY, float directionZ,
			float centerX, float centerY, float centerZ, float radiusSq, ref float nearest)
		{
			normalize(directionX, directionY, directionZ, out float nx, out float ny, out float nz);
			if (!raySphere(originX, originY, originZ, nx, ny, nz, centerX, centerY, centerZ, radiusSq, out float px, out float py, out float pz))
			{
				return false;
			}
			float distance = length((float)(originX - px), (float)(originY - py), (float)(originZ - pz));
			if (distance < nearest)
			{
				nearest = distance;
				return true;
			}
			return false;
		}

		public static bool RaySphere(float originX, float originY, float originZ, float directionX, float directionY, float directionZ,
			float centerX, float centerY, float centerZ, float radiusSq, out float hitX, out float hitY, out float hitZ)
		{
			normalize(directionX, directionY, directionZ, out float nx, out float ny, out float nz);
			return raySphere(originX, originY, originZ, nx, ny, nz, centerX, centerY, centerZ, radiusSq, out hitX, out hitY, out hitZ);
		}

		public static bool RaySphere(Vector3 origin, Vector3 direction, Vector3 center, float radiusSq, out Vector3 hit)
		{
			normalize(direction.x, direction.y, direction.z, out float nx, out float ny, out float nz);
			bool result = raySphere(origin.x, origin.y, origin.z, nx, ny, nz, center.x, center.y, center.z, radiusSq, out float px, out float py, out float pz);
			hit = new Vector3(px, py, pz);
			return result;
		}

		static bool raySphere(float sx, float sy, float sz, float nx, float ny, float nz, float cx, float cy, float cz, float radiusSq,
			out float px, out float py, out float pz)
		{
			px = sx;
			py = sy;
			pz = sz;
			float hx = (float)(cx - sx);
			float hy = (float)(cy - sy);
			float hz = (float)(cz - sz);
			float lf = dot(nx, ny, nz, hx, hy, hz);
			float s = (float)((float)(radiusSq - dot(hx, hy, hz, hx, hy, hz)) + (float)(lf * lf));
			if (s < 0.0f)
			{
				return false;
			}
			s = (float)Math.Sqrt(s);
			if (lf < s)
			{
				if ((float)(lf + s) >= 0)
				{
					s = -s;
				}
				else
				{
					return false;
				}
			}
			float t = (float)(lf - s);
			px = (float)(sx + (float)(nx * t));
			py = (float)(sy + (float)(ny * t));
			pz = (float)(sz + (float)(nz * t));
			return true;
		}

		public static bool SpherePlane(float originX, float originY, float originZ, float directionX, float directionY, float directionZ,
			float planeX, float planeY, float planeZ, float normalX, float normalY, float normalZ, float radius, ref float nearest)
		{
			if (!spherePlane(originX, originY, originZ, directionX, directionY, directionZ, planeX, planeY, planeZ, normalX, normalY, normalZ, radius,
				out float cx, out float cy, out float cz))
			{
				return false;
			}
			float distance = length((float)(originX - cx), (float)(originY - cy), (float)(originZ - cz));
			if (distance < nearest)
			{
				nearest = distance;
				return true;
			}
			return false;
		}

		public static bool SpherePlane(Vector3 origin, Vector3 direction, Vector3 planePoint, Vector3 planeNormal, float radius, out Vector3 center)
		{
			bool result = spherePlane(origin.x, origin.y, origin.z, direction.x, direction.y, direction.z, planePoint.x, planePoint.y, planePoint.z,
				planeNormal.x, planeNormal.y, planeNormal.z, radius, out float cx, out float cy, out float cz);
			center = new Vector3(cx, cy, cz);
			return result;
		}

		static bool spherePlane(float sx, float sy, float sz, float dx, float dy, float dz, float tx, float ty, float tz, float nx, float ny, float nz, float radius,
			out float cx, out float cy, out float cz)
		{
			cx = sx;
			cy = sy;
			cz = sz;
			if (dot(dx, dy, dz, nx, ny, nz) > 0)
			{
				return false;
			}
			float lx = (float)(sx - tx);
			float ly = (float)(sy - ty);
			float lz = (float)(sz - tz);
			normalize(dx, dy, dz, out dx, out dy, out dz);
			float startPointHeight = dot(nx, ny, nz, lx, ly, lz);
			float shootAngle = angle(-nx, -ny, -nz, dx, dy, dz);
			float cos = (float)Math.Cos((float)(shootAngle * Mathf.Deg2Rad));
			float hx = (float)((float)(lx + (float)((float)(dx * startPointHeight) / cos)) + tx);
			float hy = (float)((float)(ly + (float)((float)(dy * startPointHeight) / cos)) + ty);
			float hz = (float)((float)(lz + (float)((float)(dz * startPointHeight) / cos)) + tz);
			if (dot(dx, dy, dz, (float)(hx - sx), (float)(hy - sy), (float)(hz - sz)) < 0)
			{
				return false;
			}
			projectOnPlane((float)(hx - sx), (float)(hy - sy), (float)(hz - sz), nx, ny, nz, out float fx, out float fy, out float fz);
			float height = dot(nx, ny, nz, (float)(sx - hx), (float)(sy - hy), (float)(sz - hz));
			float ratioUp = (float)(radius / height);
			cx = (float)((float)(hx - (float)(fx * ratioUp)) + (float)(nx * radius));
			cy = (float)((float)(hy - (float)(fy * ratioUp)) + (float)(ny * radius));
			cz = (float)((float)(hz - (float)(fz * ratioUp)) + (float)(nz * radius));
			return true;
		}

		public static int FirstOverlap(float x, float y, float z, float[] centersX, float[] centersY, float[] centersZ, int count, uint skipMask, float distanceSq)
		{
			if (centersX == null || centersY == null || centersZ == null)
			{
				return -1;
			}
			count = Mathf.Min(count, Mathf.Min(centersX.Length, Mathf.Min(centersY.Length, centersZ.Length)));
			for (int i = 0; i < count; i++)
			{
				if (i < 32 && ((skipMask >> i) & 1u) != 0u)
				{
					continue;
				}
				float ox = (float)(x - centersX[i]);
				float oy = (float)(y - centersY[i]);
				float oz = (float)(z - centersZ[i]);
				if (dot(ox, oy, oz, ox, oy, oz) < distanceSq)
				{
					return i;
				}
			}
			return -1;
		}

		public static int FirstOverlap(Vector3 point, Vector3[] centers, uint skipMask, float distanceSq)
		{
			if (centers == null)
			{
				return -1;
			}
			for (int i = 0; i < centers.Length; i++)
			{
				if (i < 32 && ((skipMask >> i) & 1u) != 0u)
				{
					continue;
				}
				Vector3 center = centers[i];
				float ox = (float)(point.x - center.x);
				float oy = (float)(point.y - center.y);
				float oz = (float)(point.z - center.z);
				if (dot(ox, oy, oz, ox, oy, oz) < distanceSq)
				{
					return i;
				}
			}
			return -1;
		}

		static float dot(float ax, float ay, float az, float bx, float by, float bz)
		{
			return (float)((float)((float)(ax * bx) + (float)(ay * by)) + (float)(az * bz));
		}

		static float length(float x, float y, float z)
		{
			return (float)Math.Sqrt(dot(x, y, z, x, y, z));
		}

		static void normalize(float x, float y, float z, out float nx, out float ny, out float nz)
		{
			float magnitude = length(x, y, z);
			if (magnitude > 1E-05f)
			{
				nx = (float)(x / magnitude);
				ny = (float)(y / magnitude);
				nz = (float)(z / magnitude);
			}
			else
			{
				nx = 0f;
				ny = 0f;
				nz = 0f;
			}
		}

		static void projectOnPlane(float x, float y, float z, float nx, float ny, float nz, out float px, out float py, out float pz)
		{
			float sqrMagnitude = dot(nx, ny, nz, nx, ny, nz);
			if (sqrMagnitude < Mathf.Epsilon)
			{
				px = x;
				py = y;
				pz = z;
				return;
			}
			float scale = (float)(dot(x, y, z, nx, ny, nz) / sqrMagnitude);
			px = (float)(x - (float)(nx * scale));
			py = (float)(y - (float)(ny * scale));
			pz = (float)(z - (float)(nz * scale));
		}

		static float angle(float ax, float ay, float az, float bx, float by, float bz)
		{
			float denominator = (float)Math.Sqrt((float)(dot(ax, ay, az, ax, ay, az) * dot(bx, by, bz, bx, by, bz)));
			if (denominator < 1E-15f)
			{
				return 0f;
			}
			float cosine = Mathf.Clamp((float)(dot(ax, ay, az, bx, by, bz) / denominator), -1f, 1f);
			return (float)((float)Math.Acos(cosine) * Mathf.Rad2Deg);
		}
	}
}
