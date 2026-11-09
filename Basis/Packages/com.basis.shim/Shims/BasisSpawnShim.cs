using System;
using System.Collections.Generic;
using Basis.EventDriver;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Cilbox;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Shims
{
	public static class BasisSpawnShim
	{
		public const string SpawnCallback = "OnSpawnRequested", RespawnCallback = "OnRespawnRequested";
		public static bool SetSpawnLocation(object self, Vector3 position, Quaternion rotation) => CilboxScriptedSpawn.Set(self, position, rotation);
		public static void ClearSpawnLocation(object self) => CilboxScriptedSpawn.Clear(self);
		public static bool IsScriptedSpawn() => BasisSceneFactory.BasisScene != null && BasisSceneFactory.BasisScene.SpawnMode == BasisSceneSpawnMode.CilboxCallback;
	}
}

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxScriptedSpawn
	{
		private const float PendingSeconds = 120f, PollSeconds = 0.25f;
		private static readonly List<CilboxProxy> found = new List<CilboxProxy>(), proxies = new List<CilboxProxy>();
		private static CilboxSceneBasis owner;
		private static BasisScene pendingScene;
		private static Vector3 location;
		private static Quaternion facing = Quaternion.identity;
		private static bool hasLocation, pending, pendingRespawn, hooked;
		private static float pendingUntil, nextPoll;
		[NoAutoStaticsCleanup] private static readonly Action tickHandler = Tick;
		[NoAutoStaticsCleanup] private static readonly BasisSceneFactory.SpawnResolver resolver = Resolve;
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Register()
		{
			BasisSceneFactory.ScriptedSpawn = resolver;
		}
		private static bool MainSceneBox(CilboxProxy proxy, out CilboxSceneBasis box)
		{
			box = proxy != null ? proxy.box as CilboxSceneBasis : null;
			BasisScene scene = BasisSceneFactory.BasisScene;
			return box != null && !box.disabled && !proxy.disabled && scene != null && proxy.gameObject.scene == scene.gameObject.scene;
		}
		internal static bool Set(object self, Vector3 position, Quaternion rotation)
		{
			CilboxProxy proxy = self as CilboxProxy;
			if (!MainSceneBox(proxy, out CilboxSceneBasis box) || box.interpreterAccountingDepth == 0) return false;
			if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z) || !Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w)) return false;
			float length = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
			facing = length < 1e-4f ? Quaternion.identity : new Quaternion(rotation.x / length, rotation.y / length, rotation.z / length, rotation.w / length);
			location = position;
			owner = box;
			hasLocation = true;
			return true;
		}
		internal static void Clear(object self)
		{
			CilboxProxy proxy = self as CilboxProxy;
			if (!MainSceneBox(proxy, out CilboxSceneBasis box) || box.interpreterAccountingDepth == 0) return;
			hasLocation = false;
			owner = null;
		}
		private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
		private static bool Stored(BasisScene scene)
		{
			if (!hasLocation) return false;
			if (owner != null && !owner.disabled && owner.gameObject.scene == scene.gameObject.scene) return true;
			hasLocation = false;
			owner = null;
			return false;
		}
		private static void Collect(BasisScene scene, out bool waiting, out bool answerable, string callback)
		{
			waiting = false;
			answerable = false;
			proxies.Clear();
			GameObject[] roots = scene.gameObject.scene.GetRootGameObjects();
			for (int r = 0; r < roots.Length; r++)
			{
				roots[r].GetComponentsInChildren(true, found);
				for (int i = 0; i < found.Count; i++)
				{
					CilboxProxy proxy = found[i];
					if (!(proxy.box is CilboxSceneBasis box) || box.disabled || proxy.disabled) continue;
					CilboxClass cls = proxy.cls;
					if (cls == null || cls.methodNameToIndex == null)
					{
						if (proxy.gameObject.activeInHierarchy) waiting = true;
						continue;
					}
					if (Find(cls, callback) == null && Find(cls, Basis.Shims.BasisSpawnShim.SpawnCallback) == null) continue;
					answerable = true;
					proxies.Add(proxy);
				}
			}
			found.Clear();
		}
		private static CilboxMethod Find(CilboxClass cls, string name)
		{
			if (!cls.methodNameToIndex.TryGetValue(name, out uint index)) return null;
			CilboxMethod method = cls.methods[index];
			if (method.isStatic) return null;
			int count = method.signatureParameters != null ? method.signatureParameters.Length : 0;
			return count == 0 ? method : null;
		}
		private static bool Resolve(BasisScene scene, bool respawn, out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			Unhook();
			if (scene == null) return false;
			string callback = respawn ? Basis.Shims.BasisSpawnShim.RespawnCallback : Basis.Shims.BasisSpawnShim.SpawnCallback;
			Collect(scene, out bool waiting, out _, callback);
			for (int i = 0; i < proxies.Count; i++)
			{
				CilboxProxy proxy = proxies[i];
				if (proxy == null || proxy.disabled || proxy.box == null || proxy.box.disabled) continue;
				CilboxMethod method = Find(proxy.cls, callback) ?? Find(proxy.cls, Basis.Shims.BasisSpawnShim.SpawnCallback);
				if (method == null) continue;
				try
				{
					method.Interpret(proxy, null);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
			proxies.Clear();
			if (Stored(scene))
			{
				position = location;
				rotation = facing;
				return true;
			}
			if (waiting)
			{
				pending = true;
				pendingRespawn = respawn;
				pendingScene = scene;
				pendingUntil = Time.realtimeSinceStartup + PendingSeconds;
				nextPoll = 0f;
				Hook();
			}
			return false;
		}
		private static void Tick()
		{
			BasisScene scene = BasisSceneFactory.BasisScene;
			if (!pending || scene == null || scene != pendingScene || scene.SpawnMode != BasisSceneSpawnMode.CilboxCallback || Time.realtimeSinceStartup > pendingUntil)
			{
				Unhook();
				return;
			}
			if (Time.realtimeSinceStartup < nextPoll) return;
			nextPoll = Time.realtimeSinceStartup + PollSeconds;
			Collect(scene, out bool waiting, out bool answerable, pendingRespawn ? Basis.Shims.BasisSpawnShim.RespawnCallback : Basis.Shims.BasisSpawnShim.SpawnCallback);
			proxies.Clear();
			if (!answerable && !Stored(scene))
			{
				if (!waiting) Unhook();
				return;
			}
			bool respawn = pendingRespawn;
			Unhook();
			BasisLocalPlayer player = BasisLocalPlayer.Instance;
			if (player != null) BasisSceneFactory.SpawnPlayer(player, respawn);
		}
		private static void Hook()
		{
			if (hooked) return;
			hooked = true;
			BasisEventDriver.OnUpdate += tickHandler;
		}
		private static void Unhook()
		{
			pending = false;
			pendingScene = null;
			if (!hooked) return;
			hooked = false;
			BasisEventDriver.OnUpdate -= tickHandler;
		}
	}
}
