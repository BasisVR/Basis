using Basis.Scripts.Device_Management.EyeTracking;
using Basis.Scripts.Drivers;
using Cilbox;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Shims
{
	public enum BasisEyeGazeSource
	{
		None = 0,
		Headset = 1,
		FaceTracking = 2,
	}

	public static class BasisEyeTrackingShim
	{
		public const int OriginX = 0, OriginY = 1, OriginZ = 2, DirectionX = 3, DirectionY = 4, DirectionZ = 5;
		public const int LeftYaw = 6, LeftPitch = 7, RightYaw = 8, RightPitch = 9, LeftOpenness = 10, RightOpenness = 11, Length = 12;
		public const int FlagGaze = 1, FlagAngles = 2, FlagOpenness = 4, FlagHead = 8;
		public static bool IsAvailable(object self) => CilboxEyeTracking.Caller(self) && CilboxEyeTracking.Gaze(out _, out _);
		public static bool HasOpenness(object self) => CilboxEyeTracking.Caller(self) && BasisEyeTrackingManager.Current.HasOpenness;
		public static BasisEyeGazeSource GetSource(object self)
		{
			if (!CilboxEyeTracking.Caller(self) || !CilboxEyeTracking.Gaze(out _, out _)) return BasisEyeGazeSource.None;
			BasisEyeTrackingManager.BasisEyeArbitration arbitration = BasisEyeTrackingManager.Arbitration;
			return arbitration.GazeFromHmd ? BasisEyeGazeSource.Headset : arbitration.GazeFromOsc ? BasisEyeGazeSource.FaceTracking : BasisEyeGazeSource.None;
		}
		public static bool TryGetGaze(object self, out Vector3 origin, out Vector3 direction)
		{
			origin = Vector3.zero;
			direction = Vector3.forward;
			return CilboxEyeTracking.Caller(self) && CilboxEyeTracking.Gaze(out origin, out direction);
		}
		public static bool GetGazeOrHead(object self, out Vector3 origin, out Vector3 direction)
		{
			origin = Vector3.zero;
			direction = Vector3.forward;
			if (!CilboxEyeTracking.Caller(self)) return false;
			if (CilboxEyeTracking.Gaze(out origin, out direction)) return true;
			CilboxEyeTracking.Head(out origin, out direction);
			return false;
		}
		public static bool TryGetEyeAngles(object self, out Vector2 left, out Vector2 right)
		{
			left = Vector2.zero;
			right = Vector2.zero;
			if (!CilboxEyeTracking.Caller(self) || !CilboxEyeTracking.Angles(out float4 angles)) return false;
			left = new Vector2(angles.x, angles.y);
			right = new Vector2(angles.z, angles.w);
			return true;
		}
		public static bool TryGetOpenness(object self, out float left, out float right)
		{
			left = 1f;
			right = 1f;
			return CilboxEyeTracking.Caller(self) && CilboxEyeTracking.Openness(out left, out right);
		}
		public static int Fill(object self, float[] state)
		{
			if (state == null || state.Length < Length || !CilboxEyeTracking.Caller(self)) return 0;
			int flags;
			if (CilboxEyeTracking.Gaze(out Vector3 origin, out Vector3 direction)) flags = FlagGaze;
			else
			{
				flags = CilboxEyeTracking.Head(out origin, out direction) ? FlagHead : 0;
			}
			state[OriginX] = origin.x; state[OriginY] = origin.y; state[OriginZ] = origin.z;
			state[DirectionX] = direction.x; state[DirectionY] = direction.y; state[DirectionZ] = direction.z;
			if (CilboxEyeTracking.Angles(out float4 angles)) flags |= FlagAngles;
			state[LeftYaw] = angles.x; state[LeftPitch] = angles.y; state[RightYaw] = angles.z; state[RightPitch] = angles.w;
			if (CilboxEyeTracking.Openness(out float leftOpen, out float rightOpen)) flags |= FlagOpenness;
			state[LeftOpenness] = leftOpen; state[RightOpenness] = rightOpen;
			return flags;
		}
		public static int FindLookTarget(object self, float[] centersX, float[] centersY, float[] centersZ, float[] radii, int count, float toleranceDegrees, bool headFallback)
		{
			if (!CilboxEyeTracking.Caller(self) || centersX == null || centersY == null || centersZ == null) return -1;
			if (!CilboxEyeTracking.Gaze(out Vector3 origin, out Vector3 direction) && (!headFallback || !CilboxEyeTracking.Head(out origin, out direction))) return -1;
			return CilboxEyeTracking.Pick(origin, direction, centersX, centersY, centersZ, radii, count, toleranceDegrees);
		}
	}
}

namespace Cilbox
{
	internal static class CilboxEyeTracking
	{
		private const float MaxToleranceDegrees = 30f;
		internal static bool Caller(object self)
		{
			CilboxProxy proxy = self as CilboxProxy;
			CilboxBasisCommon box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			if (proxy == null || proxy.disabled || box == null || box.disabled || box.interpreterAccountingDepth == 0) return false;
			return CilboxBasisConsent.ForLocalPlayer(box, proxy.gameObject);
		}
		private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
		internal static bool Gaze(out Vector3 origin, out Vector3 direction)
		{
			BasisEyeTrackingData data = BasisEyeTrackingManager.Current;
			origin = data.GazeOrigin;
			direction = data.GazeDirection;
			float lengthSq = direction.sqrMagnitude;
			if (data.HasWorldRay && Finite(origin) && Finite(direction) && lengthSq > 1e-8f)
			{
				direction /= Mathf.Sqrt(lengthSq);
				return true;
			}
			origin = Vector3.zero;
			direction = Vector3.forward;
			return false;
		}
		internal static bool Head(out Vector3 origin, out Vector3 direction)
		{
			if (!BasisLocalCameraDriver.HasInstance)
			{
				origin = Vector3.zero;
				direction = Vector3.forward;
				return false;
			}
			origin = BasisLocalCameraDriver.Position;
			direction = BasisLocalCameraDriver.Rotation * Vector3.forward;
			return true;
		}
		internal static bool Angles(out float4 degrees)
		{
			BasisEyeTrackingData data = BasisEyeTrackingManager.Current;
			degrees = math.degrees(new float4(data.LeftAngles, data.RightAngles));
			if (data.HasPerEyeAngles && math.all(math.isfinite(degrees))) return true;
			degrees = float4.zero;
			return false;
		}
		internal static bool Openness(out float left, out float right)
		{
			BasisEyeTrackingData data = BasisEyeTrackingManager.Current;
			if (data.HasOpenness && float.IsFinite(data.LeftOpenness) && float.IsFinite(data.RightOpenness))
			{
				left = Mathf.Clamp01(data.LeftOpenness);
				right = Mathf.Clamp01(data.RightOpenness);
				return true;
			}
			left = 1f;
			right = 1f;
			return false;
		}
		internal static int Pick(Vector3 origin, Vector3 direction, float[] centersX, float[] centersY, float[] centersZ, float[] radii, int count, float toleranceDegrees)
		{
			count = Mathf.Min(count, Mathf.Min(centersX.Length, Mathf.Min(centersY.Length, centersZ.Length)));
			float tolerance = Mathf.Clamp(float.IsFinite(toleranceDegrees) ? toleranceDegrees : 0f, 0f, MaxToleranceDegrees) * Mathf.Deg2Rad;
			int best = -1;
			float bestScore = float.PositiveInfinity;
			for (int i = 0; i < count; i++)
			{
				Vector3 to = new Vector3(centersX[i] - origin.x, centersY[i] - origin.y, centersZ[i] - origin.z);
				float distance = to.magnitude;
				if (!(distance > 1e-4f)) continue;
				float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(direction, to) / distance, -1f, 1f));
				float radius = radii != null && i < radii.Length && radii[i] > 0f ? radii[i] : 0f;
				float size = radius >= distance ? Mathf.PI * 0.5f : Mathf.Asin(radius / distance);
				float score = angle - size;
				if (score > tolerance) continue;
				if (score <= 0f) score = angle - Mathf.PI;
				if (score >= bestScore) continue;
				bestScore = score;
				best = i;
			}
			return best;
		}
	}
}
