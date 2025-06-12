//======= Copyright (c) Valve Corporation, All rights reserved. ===============
//
// Purpose: Utilities for working with SteamVR
//
//=============================================================================

using UnityEngine;
using Valve.VR;
using System.IO;

public static class SteamVR_Utils
{
	public static bool IsValid(Quaternion rotation)
	{
		return (float.IsNaN(rotation.x) == false && float.IsNaN(rotation.y) == false && float.IsNaN(rotation.z) == false && float.IsNaN(rotation.w) == false) &&
			(rotation.x != 0 || rotation.y != 0 || rotation.z != 0 || rotation.w != 0);
	}
	private static float _copysign(float sizeval, float signval)
	{
		return Mathf.Sign(signval) == 1 ? Mathf.Abs(sizeval) : -Mathf.Abs(sizeval);
	}

	public static Quaternion GetRotation(this Matrix4x4 matrix)
	{
		Quaternion q = new Quaternion();
		q.w = Mathf.Sqrt(Mathf.Max(0, 1 + matrix.m00 + matrix.m11 + matrix.m22)) / 2;
		q.x = Mathf.Sqrt(Mathf.Max(0, 1 + matrix.m00 - matrix.m11 - matrix.m22)) / 2;
		q.y = Mathf.Sqrt(Mathf.Max(0, 1 - matrix.m00 + matrix.m11 - matrix.m22)) / 2;
		q.z = Mathf.Sqrt(Mathf.Max(0, 1 - matrix.m00 - matrix.m11 + matrix.m22)) / 2;
		q.x = _copysign(q.x, matrix.m21 - matrix.m12);
		q.y = _copysign(q.y, matrix.m02 - matrix.m20);
		q.z = _copysign(q.z, matrix.m10 - matrix.m01);
		return q;
	}
	private const string secretKey = "foobar";

	public static string GetBadMD5Hash(string usedString)
	{
		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(usedString + secretKey);

		return GetBadMD5Hash(bytes);
	}
	public static string GetBadMD5Hash(byte[] bytes)
	{
		System.Security.Cryptography.MD5CryptoServiceProvider md5 = new System.Security.Cryptography.MD5CryptoServiceProvider();
		byte[] hash = md5.ComputeHash(bytes);

		System.Text.StringBuilder sb = new System.Text.StringBuilder();
		for (int i = 0; i < hash.Length; i++)
		{
			sb.Append(hash[i].ToString("x2"));
		}

		return sb.ToString();
	}
	public static string GetBadMD5HashFromFile(string filePath)
	{
		if (File.Exists(filePath) == false)
			return null;

		string data = File.ReadAllText(filePath);
		return GetBadMD5Hash(data + secretKey);
	}

	public static string SanitizePath(string path, bool allowLeadingSlash = true)
	{
		if (path.Contains("\\\\"))
			path = path.Replace("\\\\", "\\");
		if (path.Contains("//"))
			path = path.Replace("//", "/");

		if (allowLeadingSlash == false)
		{
			if (path[0] == '/' || path[0] == '\\')
				path = path.Substring(1);
		}

		return path;
	}

	public static System.Type FindType(string typeName)
	{
		var type = System.Type.GetType(typeName);
		if (type != null) return type;
		foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies())
		{
			type = a.GetType(typeName);
			if (type != null)
				return type;
		}
		return null;
	}

	[System.Serializable]
	public struct RigidTransform
	{
		public Vector3 pos;
		public Quaternion rot;
		public RigidTransform(Vector3 pos, Quaternion rot)
		{
			this.pos = pos;
			this.rot = rot;
		}
		public RigidTransform(Transform from, Transform to)
		{
			var inv = Quaternion.Inverse(from.rotation);
			rot = inv * to.rotation;
			pos = inv * (to.position - from.position);
		}

		public RigidTransform(HmdMatrix34_t pose)
		{
			var m = Matrix4x4.identity;

			m[0, 0] = pose.m0;
			m[0, 1] = pose.m1;
			m[0, 2] = -pose.m2;
			m[0, 3] = pose.m3;

			m[1, 0] = pose.m4;
			m[1, 1] = pose.m5;
			m[1, 2] = -pose.m6;
			m[1, 3] = pose.m7;

			m[2, 0] = -pose.m8;
			m[2, 1] = -pose.m9;
			m[2, 2] = pose.m10;
			m[2, 3] = -pose.m11;

			this.pos = m.GetPosition();
			this.rot = m.GetRotation();
		}
		public HmdMatrix44_t ToHmdMatrix44()
		{
			var m = Matrix4x4.TRS(pos, rot, Vector3.one);
			var pose = new HmdMatrix44_t();

			pose.m0 = m[0, 0];
			pose.m1 = m[0, 1];
			pose.m2 = -m[0, 2];
			pose.m3 = m[0, 3];

			pose.m4 = m[1, 0];
			pose.m5 = m[1, 1];
			pose.m6 = -m[1, 2];
			pose.m7 = m[1, 3];

			pose.m8 = -m[2, 0];
			pose.m9 = -m[2, 1];
			pose.m10 = m[2, 2];
			pose.m11 = -m[2, 3];

			pose.m12 = m[3, 0];
			pose.m13 = m[3, 1];
			pose.m14 = -m[3, 2];
			pose.m15 = m[3, 3];

			return pose;
		}

		public HmdMatrix34_t ToHmdMatrix34()
		{
			var m = Matrix4x4.TRS(pos, rot, Vector3.one);
			var pose = new HmdMatrix34_t();

			pose.m0 = m[0, 0];
			pose.m1 = m[0, 1];
			pose.m2 = -m[0, 2];
			pose.m3 = m[0, 3];

			pose.m4 = m[1, 0];
			pose.m5 = m[1, 1];
			pose.m6 = -m[1, 2];
			pose.m7 = m[1, 3];

			pose.m8 = -m[2, 0];
			pose.m9 = -m[2, 1];
			pose.m10 = m[2, 2];
			pose.m11 = -m[2, 3];

			return pose;
		}
		public override int GetHashCode()
		{
			return pos.GetHashCode() ^ rot.GetHashCode();
		}
	}
}