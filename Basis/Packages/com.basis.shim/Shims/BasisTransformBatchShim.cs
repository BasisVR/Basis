using UnityEngine;

namespace Basis.Shims
{
	public static class BasisTransformBatchShim
	{
		public static void SetLocalPositions(Transform[] targets, Vector3[] positions)
		{
			SetLocalPositions(targets, positions, 0u);
		}

		public static void SetLocalPositions(Transform[] targets, Vector3[] positions, uint skipMask)
		{
			if (targets == null || positions == null)
			{
				return;
			}
			int count = Mathf.Min(targets.Length, positions.Length);
			for (int i = 0; i < count; i++)
			{
				if (i < 32 && ((skipMask >> i) & 1u) != 0u)
				{
					continue;
				}
				Transform target = targets[i];
				if (target != null)
				{
					target.SetLocalPosition(positions[i]);
				}
			}
		}

		public static void SetPositions(Transform[] targets, Vector3[] positions)
		{
			if (targets == null || positions == null)
			{
				return;
			}
			int count = Mathf.Min(targets.Length, positions.Length);
			for (int i = 0; i < count; i++)
			{
				Transform target = targets[i];
				if (target != null)
				{
					target.SetPosition(positions[i]);
				}
			}
		}

		public static void SetLocalRotations(Transform[] targets, Quaternion[] rotations)
		{
			if (targets == null || rotations == null)
			{
				return;
			}
			int count = Mathf.Min(targets.Length, rotations.Length);
			for (int i = 0; i < count; i++)
			{
				Transform target = targets[i];
				if (target != null)
				{
					target.SetLocalRotation(rotations[i]);
				}
			}
		}

		public static void SetRotations(Transform[] targets, Quaternion[] rotations)
		{
			if (targets == null || rotations == null)
			{
				return;
			}
			int count = Mathf.Min(targets.Length, rotations.Length);
			for (int i = 0; i < count; i++)
			{
				Transform target = targets[i];
				if (target != null)
				{
					target.SetRotation(rotations[i]);
				}
			}
		}

		public static void SetLocalScales(Transform[] targets, Vector3[] scales)
		{
			if (targets == null || scales == null)
			{
				return;
			}
			int count = Mathf.Min(targets.Length, scales.Length);
			for (int i = 0; i < count; i++)
			{
				Transform target = targets[i];
				if (target != null)
				{
					target.SetLocalScale(scales[i]);
				}
			}
		}

		public static void GetLocalPositions(Transform[] sources, Vector3[] into)
		{
			if (sources == null || into == null)
			{
				return;
			}
			int count = Mathf.Min(sources.Length, into.Length);
			for (int i = 0; i < count; i++)
			{
				Transform source = sources[i];
				into[i] = source != null ? source.GetLocalPosition() : Vector3.zero;
			}
		}

		public static void GetPositions(Transform[] sources, Vector3[] into)
		{
			if (sources == null || into == null)
			{
				return;
			}
			int count = Mathf.Min(sources.Length, into.Length);
			for (int i = 0; i < count; i++)
			{
				Transform source = sources[i];
				into[i] = source != null ? source.GetPosition() : Vector3.zero;
			}
		}

		public static void RotateWorld(Transform[] targets, Transform space, float[] x, float[] y, float[] z)
		{
			if (targets == null || x == null || y == null || z == null)
			{
				return;
			}
			int count = Mathf.Min(targets.Length, Mathf.Min(x.Length, Mathf.Min(y.Length, z.Length)));
			Quaternion spaceRotation = space != null ? space.GetRotation() : Quaternion.identity;
			for (int i = 0; i < count; i++)
			{
				Transform target = targets[i];
				if (target == null)
				{
					continue;
				}
				Vector3 rotation = new Vector3(x[i], y[i], z[i]);
				float radians = rotation.magnitude;
				if (!(radians > 0f) || float.IsInfinity(radians))
				{
					continue;
				}
				Vector3 axis = spaceRotation * (rotation / radians);
				target.SetRotation(Quaternion.AngleAxis(radians * Mathf.Rad2Deg, axis) * target.GetRotation());
			}
		}
	}
}
