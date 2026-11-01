using System;
using System.Collections.Generic;
using Basis.Scripts.Drivers;
using Cilbox;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
#if STEAMAUDIO_ENABLED && !UNITY_WEBGL
using SteamAudio;
using NativeMaterial = SteamAudio.Material;
using NativeVector3 = SteamAudio.Vector3;
#endif

namespace Basis.Shims
{
	[AutoStaticsCleanup]
	public static partial class BasisSteamAudioShim
	{
		public const int MaxAddedSourcesWorld = 64, MaxAddedSourcesProp = 8, MaxAddedSourcesAvatar = 4;
		public const int MaxReflectionSourcesWorld = 16, MaxReflectionSourcesProp = 2, MaxReflectionSourcesAvatar = 1;
		public const int MaxOcclusionSamplesShared = 16, MaxTransmissionSurfacesWorld = 8, MaxTransmissionSurfacesShared = 4;
		public const int MaxGeometryTrianglesWorld = 512000;
#if STEAMAUDIO_ENABLED && !UNITY_WEBGL
		private struct Tracked { public SteamAudioSource source; public CilboxBasisCommon box; }
		private sealed class Geometry { public MeshFilter filter; public CilboxBasisCommon box; public NativeMaterial material; public Scene sub, parent; public InstancedMesh instance; public bool inScene; public int triangles; public UnityEngine.Matrix4x4 pose; }
		private static readonly List<Tracked> added = new List<Tracked>(), reflecting = new List<Tracked>();
		private static readonly List<Geometry> geometry = new List<Geometry>();
		public static bool Add(object self, AudioSource source)
		{
			if (!TryOwned(self, source, out CilboxBasisCommon box) || SteamAudioManager.Singleton == null || source.gameObject.GetComponent<AudioSource>() != source) return false;
			if (source.TryGetComponent(out SteamAudioSource _))
			{
				source.spatialize = true;
				return true;
			}
			Prune(added);
			if (Count(added, box) >= Scope(box, MaxAddedSourcesWorld, MaxAddedSourcesProp, MaxAddedSourcesAvatar)) return false;
			SteamAudioSource created = source.gameObject.AddComponent<SteamAudioSource>();
			source.spatialize = true;
			added.Add(new Tracked { source = created, box = box });
			return true;
		}
		public static bool Has(object self, AudioSource source) => TryFind(self, source, out _, out _);
		public static bool SetHrtf(object self, AudioSource source, bool binaural, bool bilinear)
		{
			if (!TryFind(self, source, out SteamAudioSource s, out _)) return false;
			s.directBinaural = binaural;
			s.interpolation = bilinear ? HRTFInterpolation.Bilinear : HRTFInterpolation.Nearest;
			return Apply(s);
		}
		public static bool SetDistanceAttenuation(object self, AudioSource source, bool enabled, bool physicsBased)
		{
			if (!TryFind(self, source, out SteamAudioSource s, out _)) return false;
			s.distanceAttenuation = enabled;
			s.distanceAttenuationInput = physicsBased ? DistanceAttenuationInput.PhysicsBased : DistanceAttenuationInput.CurveDriven;
			return Apply(s);
		}
		public static bool SetAirAbsorption(object self, AudioSource source, bool enabled)
		{
			if (!TryFind(self, source, out SteamAudioSource s, out _)) return false;
			s.airAbsorption = enabled;
			s.airAbsorptionInput = AirAbsorptionInput.SimulationDefined;
			return Apply(s);
		}
		public static bool SetDirectivity(object self, AudioSource source, bool enabled, float dipoleWeight, float dipolePower)
		{
			if (float.IsNaN(dipoleWeight) || float.IsNaN(dipolePower) || !TryFind(self, source, out SteamAudioSource s, out _)) return false;
			s.directivity = enabled;
			s.directivityInput = DirectivityInput.SimulationDefined;
			s.dipoleWeight = Mathf.Clamp01(dipoleWeight);
			s.dipolePower = Mathf.Clamp(dipolePower, 0f, 4f);
			return Apply(s);
		}
		public static bool SetOcclusion(object self, AudioSource source, bool enabled) => SetOcclusion(self, source, enabled, false, 1f, 1);
		public static bool SetOcclusion(object self, AudioSource source, bool enabled, bool volumetric, float radius, int samples)
		{
			if (float.IsNaN(radius) || !TryFind(self, source, out SteamAudioSource s, out CilboxBasisCommon box)) return false;
			int limit = SteamAudioSettings.Singleton.maxOcclusionSamples;
			if (!(box is CilboxSceneBasis)) limit = Mathf.Min(limit, MaxOcclusionSamplesShared);
			s.occlusion = enabled;
			s.occlusionInput = OcclusionInput.SimulationDefined;
			s.occlusionType = volumetric ? OcclusionType.Volumetric : OcclusionType.Raycast;
			s.occlusionRadius = Mathf.Clamp(radius, 0f, 4f);
			s.occlusionSamples = Mathf.Clamp(samples, 1, limit);
			return Apply(s);
		}
		public static bool SetTransmission(object self, AudioSource source, bool enabled, bool perFrequency, int maxSurfaces)
		{
			if (!TryFind(self, source, out SteamAudioSource s, out CilboxBasisCommon box)) return false;
			s.transmission = enabled;
			s.transmissionInput = TransmissionInput.SimulationDefined;
			s.transmissionType = perFrequency ? TransmissionType.FrequencyDependent : TransmissionType.FrequencyIndependent;
			s.maxTransmissionSurfaces = Mathf.Clamp(maxSurfaces, 1, box is CilboxSceneBasis ? MaxTransmissionSurfacesWorld : MaxTransmissionSurfacesShared);
			return Apply(s);
		}
		public static bool SetDirectMix(object self, AudioSource source, float level)
		{
			if (float.IsNaN(level) || !TryFind(self, source, out SteamAudioSource s, out _)) return false;
			s.directMixLevel = Mathf.Clamp01(level);
			return Apply(s);
		}
		public static bool SetReflections(object self, AudioSource source, bool enabled, float mixLevel, bool binaural)
		{
			if (float.IsNaN(mixLevel) || !TryFind(self, source, out SteamAudioSource s, out CilboxBasisCommon box)) return false;
			Prune(reflecting);
			int index = IndexOf(reflecting, s);
			if (enabled && index < 0)
			{
				if (Count(reflecting, box) >= ReflectionCap(box) || reflecting.Count >= SteamAudioSettings.Singleton.realTimeMaxSources) return false;
				reflecting.Add(new Tracked { source = s, box = box });
			}
			else if (!enabled && index >= 0) reflecting.RemoveAt(index);
			s.reflections = enabled;
			s.reflectionsType = ReflectionsType.Realtime;
			s.reflectionsMixLevel = Mathf.Clamp(mixLevel, 0f, 10f);
			s.applyHRTFToReflections = binaural;
			return Apply(s);
		}
		public static float GetOcclusion(object self, AudioSource source) => TryFind(self, source, out SteamAudioSource s, out _) && s.occlusion ? s.occlusionValue : 1f;
		public static float GetTransmissionLow(object self, AudioSource source) => TryFind(self, source, out SteamAudioSource s, out _) && s.transmission ? s.transmissionLow : 1f;
		public static float GetTransmissionMid(object self, AudioSource source) => TryFind(self, source, out SteamAudioSource s, out _) && s.transmission ? s.transmissionMid : 1f;
		public static float GetTransmissionHigh(object self, AudioSource source) => TryFind(self, source, out SteamAudioSource s, out _) && s.transmission ? s.transmissionHigh : 1f;
		public static int RemainingAddedSources(object self)
		{
			if (!TryCaller(self, out _, out CilboxBasisCommon box)) return 0;
			Prune(added);
			return Mathf.Max(0, Scope(box, MaxAddedSourcesWorld, MaxAddedSourcesProp, MaxAddedSourcesAvatar) - Count(added, box));
		}
		public static int RemainingReflectionSources(object self)
		{
			if (!TryCaller(self, out _, out CilboxBasisCommon box)) return 0;
			Prune(reflecting);
			return Mathf.Max(0, Mathf.Min(ReflectionCap(box) - Count(reflecting, box), SteamAudioSettings.Singleton.realTimeMaxSources - reflecting.Count));
		}
		public static int RemainingGeometryTriangles(object self) => TryCaller(self, out _, out CilboxBasisCommon box) && box is CilboxSceneBasis ? Mathf.Max(0, MaxGeometryTrianglesWorld - UsedTriangles(box, null)) : 0;
		public static bool AddGeometry(object self, MeshFilter filter)
		{
			SteamAudioMaterial fallback = SteamAudioSettings.Singleton != null ? SteamAudioSettings.Singleton.defaultMaterial : null;
			return fallback != null && AddGeometryInternal(self, filter, fallback.GetMaterial());
		}
		public static bool AddGeometry(object self, MeshFilter filter, float absorptionLow, float absorptionMid, float absorptionHigh, float scattering, float transmissionLow, float transmissionMid, float transmissionHigh)
		{
			if (float.IsNaN(absorptionLow) || float.IsNaN(absorptionMid) || float.IsNaN(absorptionHigh) || float.IsNaN(scattering) || float.IsNaN(transmissionLow) || float.IsNaN(transmissionMid) || float.IsNaN(transmissionHigh)) return false;
			return AddGeometryInternal(self, filter, new NativeMaterial { absorptionLow = Mathf.Clamp01(absorptionLow), absorptionMid = Mathf.Clamp01(absorptionMid), absorptionHigh = Mathf.Clamp01(absorptionHigh), scattering = Mathf.Clamp01(scattering), transmissionLow = Mathf.Clamp01(transmissionLow), transmissionMid = Mathf.Clamp01(transmissionMid), transmissionHigh = Mathf.Clamp01(transmissionHigh) });
		}
		public static bool RemoveGeometry(object self, MeshFilter filter)
		{
			if (filter == null || !TryCaller(self, out CilboxProxy proxy, out CilboxBasisCommon box) || !(box is CilboxSceneBasis) || !SameContent(box, proxy, filter)) return false;
			int index = IndexOf(filter);
			if (index < 0) return false;
			Release(geometry[index]);
			geometry.RemoveAt(index);
			return true;
		}
		private static bool AddGeometryInternal(object self, MeshFilter filter, NativeMaterial material)
		{
			if (filter == null || !TryCaller(self, out CilboxProxy proxy, out CilboxBasisCommon box) || !(box is CilboxSceneBasis) || !SameContent(box, proxy, filter)) return false;
			if (SteamAudioManager.Singleton == null || SteamAudioManager.CurrentScene == null || SteamAudioManager.GetSceneType() == SceneType.Custom) return false;
			Mesh mesh = filter.sharedMesh;
			if (mesh == null || !mesh.isReadable) return false;
			int triangles = CountTriangles(mesh);
			if (triangles == 0 || UsedTriangles(box, filter) + triangles > MaxGeometryTrianglesWorld) return false;
			Geometry entry = new Geometry { filter = filter, box = box, material = material, triangles = triangles };
			if (!Build(entry)) return false;
			int index = IndexOf(filter);
			if (index >= 0)
			{
				Release(geometry[index]);
				geometry[index] = entry;
			}
			else geometry.Add(entry);
			BasisFrameClock.OnTick -= Tick;
			BasisFrameClock.OnTick += Tick;
			return true;
		}
		private static bool Build(Geometry entry)
		{
			Mesh mesh = entry.filter.sharedMesh;
			if (mesh == null || !mesh.isReadable) return false;
			Scene parent = SteamAudioManager.CurrentScene;
			try
			{
				UnityEngine.Vector3[] source = mesh.vertices;
				NativeVector3[] vertices = new NativeVector3[source.Length];
				for (int i = 0; i < source.Length; i++) vertices[i] = Common.ConvertVector(source[i]);
				int[] indices = mesh.triangles;
				Triangle[] triangles = new Triangle[indices.Length / 3];
				for (int i = 0; i < triangles.Length; i++) triangles[i] = new Triangle { index0 = indices[3 * i], index1 = indices[3 * i + 1], index2 = indices[3 * i + 2] };
				entry.sub = SteamAudioManager.CreateScene(SteamAudioManager.Context);
				StaticMesh built = new StaticMesh(SteamAudioManager.Context, entry.sub, vertices, triangles, new int[triangles.Length], new[] { entry.material });
				built.AddToScene(entry.sub);
				built.Release();
				entry.sub.Commit();
				entry.instance = new InstancedMesh(parent, entry.sub, entry.filter.transform);
			}
			catch (Exception e)
			{
				BasisDebug.LogError("Steam Audio geometry build failed: " + e.Message);
				entry.parent = null;
				Release(entry);
				return false;
			}
			entry.parent = parent;
			entry.pose = entry.filter.transform.localToWorldMatrix;
			entry.inScene = false;
			Sync(entry);
			return true;
		}
		private static void Release(Geometry entry)
		{
			SteamAudioManager.BlockUntilReflectionsIdle();
			if (entry.inScene && entry.instance != null && entry.parent != null && entry.parent == SteamAudioManager.CurrentScene)
			{
				entry.instance.RemoveFromScene(entry.parent);
				SteamAudioManager.ScheduleCommitScene();
			}
			entry.instance?.Release();
			entry.sub?.Release();
			entry.instance = null;
			entry.sub = null;
			entry.parent = null;
			entry.inScene = false;
		}
		private static void Sync(Geometry entry)
		{
			bool wanted = entry.filter.gameObject.activeInHierarchy;
			if (wanted == entry.inScene) return;
			if (wanted) entry.instance.AddToScene(entry.parent);
			else entry.instance.RemoveFromScene(entry.parent);
			entry.inScene = wanted;
			SteamAudioManager.ScheduleCommitScene();
		}
		private static void Tick()
		{
			for (int i = geometry.Count - 1; i >= 0; i--)
			{
				Geometry entry = geometry[i];
				if (entry.filter == null || entry.box == null || entry.box.disabled)
				{
					Release(entry);
					geometry.RemoveAt(i);
					continue;
				}
				Scene current = SteamAudioManager.CurrentScene;
				if (current == null) continue;
				if (entry.parent != current)
				{
					Release(entry);
					if (!Build(entry)) geometry.RemoveAt(i);
					continue;
				}
				Sync(entry);
				if (!entry.inScene) continue;
				UnityEngine.Matrix4x4 pose = entry.filter.transform.localToWorldMatrix;
				if (pose == entry.pose) continue;
				entry.pose = pose;
				entry.instance.UpdateTransform(current, entry.filter.transform);
				SteamAudioManager.ScheduleCommitScene();
			}
			if (geometry.Count == 0) BasisFrameClock.OnTick -= Tick;
		}
		private static int CountTriangles(Mesh mesh)
		{
			int total = 0;
			for (int i = 0; i < mesh.subMeshCount; i++) if (mesh.GetTopology(i) == MeshTopology.Triangles) total += (int)mesh.GetIndexCount(i) / 3;
			return total;
		}
		private static int UsedTriangles(CilboxBasisCommon box, MeshFilter except)
		{
			int total = 0;
			foreach (Geometry entry in geometry) if (entry.box == box && entry.filter != except) total += entry.triangles;
			return total;
		}
		private static int IndexOf(MeshFilter filter)
		{
			for (int i = 0; i < geometry.Count; i++) if (geometry[i].filter == filter) return i;
			return -1;
		}
		private static int IndexOf(List<Tracked> list, SteamAudioSource source)
		{
			for (int i = 0; i < list.Count; i++) if (list[i].source == source) return i;
			return -1;
		}
		private static int Count(List<Tracked> list, CilboxBasisCommon box)
		{
			int total = 0;
			foreach (Tracked item in list) if (item.box == box) total++;
			return total;
		}
		private static void Prune(List<Tracked> list)
		{
			for (int i = list.Count - 1; i >= 0; i--) if (list[i].source == null || list[i].box == null) list.RemoveAt(i);
		}
		private static bool Apply(SteamAudioSource source)
		{
			source.MarkCacheDirty();
			source.ForceUpdate();
			return true;
		}
		private static int ReflectionCap(CilboxBasisCommon box) => Scope(box, MaxReflectionSourcesWorld, MaxReflectionSourcesProp, MaxReflectionSourcesAvatar);
		private static bool TryFind(object self, AudioSource source, out SteamAudioSource found, out CilboxBasisCommon box)
		{
			found = null;
			return TryOwned(self, source, out box) && source.gameObject.GetComponent<AudioSource>() == source && source.TryGetComponent(out found);
		}
		private static bool TryOwned(object self, AudioSource source, out CilboxBasisCommon box)
		{
			box = null;
			return source != null && TryCaller(self, out CilboxProxy proxy, out box) && SameContent(box, proxy, source);
		}
#else
		public static bool Add(object self, AudioSource source) => false;
		public static bool Has(object self, AudioSource source) => false;
		public static bool SetHrtf(object self, AudioSource source, bool binaural, bool bilinear) => false;
		public static bool SetDistanceAttenuation(object self, AudioSource source, bool enabled, bool physicsBased) => false;
		public static bool SetAirAbsorption(object self, AudioSource source, bool enabled) => false;
		public static bool SetDirectivity(object self, AudioSource source, bool enabled, float dipoleWeight, float dipolePower) => false;
		public static bool SetOcclusion(object self, AudioSource source, bool enabled) => false;
		public static bool SetOcclusion(object self, AudioSource source, bool enabled, bool volumetric, float radius, int samples) => false;
		public static bool SetTransmission(object self, AudioSource source, bool enabled, bool perFrequency, int maxSurfaces) => false;
		public static bool SetDirectMix(object self, AudioSource source, float level) => false;
		public static bool SetReflections(object self, AudioSource source, bool enabled, float mixLevel, bool binaural) => false;
		public static float GetOcclusion(object self, AudioSource source) => 1f;
		public static float GetTransmissionLow(object self, AudioSource source) => 1f;
		public static float GetTransmissionMid(object self, AudioSource source) => 1f;
		public static float GetTransmissionHigh(object self, AudioSource source) => 1f;
		public static int RemainingAddedSources(object self) => 0;
		public static int RemainingReflectionSources(object self) => 0;
		public static int RemainingGeometryTriangles(object self) => 0;
		public static bool AddGeometry(object self, MeshFilter filter) => false;
		public static bool AddGeometry(object self, MeshFilter filter, float absorptionLow, float absorptionMid, float absorptionHigh, float scattering, float transmissionLow, float transmissionMid, float transmissionHigh) => false;
		public static bool RemoveGeometry(object self, MeshFilter filter) => false;
#endif
		private static int Scope(CilboxBasisCommon box, int world, int prop, int avatar) => box is CilboxSceneBasis ? world : box is CilboxAvatarBasis ? avatar : prop;
		private static bool TryCaller(object self, out CilboxProxy proxy, out CilboxBasisCommon box)
		{
			proxy = self as CilboxProxy;
			box = proxy != null ? proxy.box as CilboxBasisCommon : null;
			return box != null && !box.disabled && box.interpreterAccountingDepth > 0;
		}
		private static bool SameContent(CilboxBasisCommon box, CilboxProxy proxy, Component target) => CilboxBasisConsent.TryOwner(box, proxy.gameObject, out BasisContentBase mine) && BasisContent.TryResolve(target, out BasisContentBase theirs) && mine == theirs;
	}
}
