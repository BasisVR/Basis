using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cilbox
{
	[AutoStaticsCleanup]
	internal static partial class CilboxProtectedObjects
	{
		const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;
		const int MaxScenes = 16;
		static readonly Scene[] sceneKeys = new Scene[MaxScenes];
		static readonly bool[] sceneWorld = new bool[MaxScenes];
		static int sceneCount;
		static readonly List<Transform> parentedPlayers = new List<Transform>();
		static readonly HashSet<EntityId> warned = new HashSet<EntityId>();
		static int playersFrame = -1;
		static bool hooked;
		[NoAutoStaticsCleanup] static readonly UnityEngine.Events.UnityAction<Scene, LoadSceneMode> loadedHandler = (scene, mode) => sceneCount = 0;
		[NoAutoStaticsCleanup] static readonly UnityEngine.Events.UnityAction<Scene> unloadedHandler = scene => sceneCount = 0;

		internal static bool IsRedirect(Type declaringType, string name)
		{
			if (declaringType == typeof(GameObject)) return name == nameof(GameObject.SetActive);
			return declaringType == typeof(UnityEngine.Object) && (name == nameof(UnityEngine.Object.Destroy) || name == nameof(UnityEngine.Object.DestroyImmediate));
		}

		internal static MethodInfo Resolve(CilboxUsage usage, Type declaringType, string name, SerializedTypeDescriptor[] parametersIn)
		{
			if (usage == null) return null;
			Type[] parameters = usage.DescriptorsToArrayOfNativeTypes(parametersIn);
			if (parameters == null) return null;
			for (int i = 0; i < parameters.Length; i++)
			{
				if (parameters[i] == null) return null;
			}
			if (declaringType == typeof(GameObject))
			{
				return parameters.Length == 1 && parameters[0] == typeof(bool) ? typeof(CilboxProtectedObjects).GetMethod(nameof(SetActive), Statics, null, new[] { typeof(GameObject), typeof(bool) }, null) : null;
			}
			return typeof(CilboxProtectedObjects).GetMethod(name, Statics, null, parameters, null);
		}

		internal static void SetActive(GameObject gameObject, bool value)
		{
			if (ReferenceEquals(gameObject, null)) throw new NullReferenceException();
			if (gameObject.activeSelf == value) return;
			if (Refused(gameObject)) return;
			gameObject.SetActive(value);
		}

		internal static void Destroy(UnityEngine.Object target)
		{
			if (!Refused(target)) UnityEngine.Object.Destroy(target);
		}

		internal static void Destroy(UnityEngine.Object target, float delay)
		{
			if (!Refused(target)) UnityEngine.Object.Destroy(target, delay);
		}

		internal static void DestroyImmediate(UnityEngine.Object target)
		{
			if (!Refused(target)) UnityEngine.Object.DestroyImmediate(target);
		}

		internal static void DestroyImmediate(UnityEngine.Object target, bool allowDestroyingAssets)
		{
			if (!Refused(target)) UnityEngine.Object.DestroyImmediate(target, allowDestroyingAssets);
		}

		static bool Refused(UnityEngine.Object target)
		{
			if (target == null) return false;
			Transform transform = target is GameObject gameObject ? gameObject.transform : target is Component component ? component.transform : null;
			if (ReferenceEquals(transform, null) || !Protected(transform)) return false;
			if (warned.Add(transform.GetEntityId())) Debug.LogWarning($"[Cilbox] A script tried to change or destroy '{transform.name}', which belongs to Basis or a player. Refused.");
			return true;
		}

		internal static bool Protected(Transform transform)
		{
			BasisContentBase content = transform.GetComponentInParent<BasisContentBase>(true);
			if (content == null)
			{
				if (!WorldScene(transform.gameObject)) return true;
				if (transform.GetComponentInParent<BasisPlayer>(true) != null) return true;
			}
			if (playersFrame != Time.frameCount) RefreshPlayers();
			for (int i = 0; i < parentedPlayers.Count; i++)
			{
				Transform player = parentedPlayers[i];
				if (player == null) continue;
				if (player.IsChildOf(transform)) return true;
				if (transform.IsChildOf(player) && (content == null || !content.transform.IsChildOf(player))) return true;
			}
			return false;
		}

		static bool WorldScene(GameObject gameObject)
		{
			Scene scene = gameObject.scene;
			for (int i = 0; i < sceneCount; i++)
			{
				if (sceneKeys[i] == scene) return sceneWorld[i];
			}
			if (!hooked)
			{
				hooked = true;
				SceneManager.sceneLoaded -= loadedHandler;
				SceneManager.sceneUnloaded -= unloadedHandler;
				SceneManager.sceneLoaded += loadedHandler;
				SceneManager.sceneUnloaded += unloadedHandler;
			}
			bool world = BasisContent.TryResolve(gameObject, out BasisContentBase content) && content is BasisScene;
			if (sceneCount == MaxScenes) sceneCount = 0;
			sceneKeys[sceneCount] = scene;
			sceneWorld[sceneCount++] = world;
			return world;
		}

		static void RefreshPlayers()
		{
			playersFrame = Time.frameCount;
			parentedPlayers.Clear();
			BasisLocalPlayer local = BasisLocalPlayer.Instance;
			if (local != null && local.transform.parent != null) parentedPlayers.Add(local.transform);
			BasisNetworkReceiver[] receivers = BasisNetworkPlayers.ReceiversSnapshot;
			int count = Math.Min(BasisNetworkPlayers.ReceiverCount, receivers.Length);
			for (int i = 0; i < count; i++)
			{
				Transform mouth = receivers[i] != null && receivers[i].RemotePlayer != null ? receivers[i].RemotePlayer.MouthTransform : null;
				if (mouth != null && mouth.parent != null) parentedPlayers.Add(mouth);
			}
		}
	}
}
