using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;

namespace Basis.BasisUI
{
    [AutoStaticsCleanup]
    public static partial class BasisSpawnAnchors
    {
        [Serializable]
        public class SpawnAnchor
        {
            public string Name;
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.identity;
            public bool OverrideScale;
            public float Scale = 1f;
        }

        [Serializable]
        public class SpawnAnchorFile
        {
            public SpawnAnchor[] Anchors = Array.Empty<SpawnAnchor>();
            public int SelectedIndex = -1;
        }

        public const float MinScale = 0.1f;
        public const float MaxScale = 5f;
        public const string DefaultFileName = "SpawnAnchors.json";
        private static readonly Vector3 PlacementHalfExtents = new Vector3(0.06f, 0.06f, 0.06f);

        public static readonly List<SpawnAnchor> Anchors = new List<SpawnAnchor>();
        public static int SelectedIndex { get; private set; } = -1;
        public static event Action<bool> OnChanged;

        private static readonly List<BasisSpawnAnchorHandle> handles = new List<BasisSpawnAnchorHandle>();
        private static string defaultFilePath;
        private static bool loaded;
        private static bool handlesVisible;
        private static bool ticking;
        private static bool hooked;

        public static string DefaultFilePath
        {
            get => defaultFilePath ??= Path.Combine(Application.persistentDataPath, DefaultFileName);
            set
            {
                defaultFilePath = value;
                loaded = false;
            }
        }

        public static int Count
        {
            get
            {
                EnsureLoaded();
                return Anchors.Count;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Hook()
        {
            if (hooked)
            {
                return;
            }
            hooked = true;
            BasisSettingsDefaults.SpawnAnchorHandles.OnChanged += SetHandlesVisible;
            BasisSettingsDefaults.SpawnAnchorPositionSnap.OnChanged += OnSnapSettingChanged;
            BasisSettingsDefaults.SpawnAnchorPositionSnapSize.OnChanged += OnSnapSettingChanged;
            BasisSettingsDefaults.SpawnAnchorRotationSnap.OnChanged += OnSnapSettingChanged;
            BasisSettingsDefaults.SpawnAnchorRotationSnapDegrees.OnChanged += OnSnapSettingChanged;
            BasisGizmoManager.OnUseGizmosChanged += OnUseGizmosChanged;
            BasisLocalPlayer.OnLocalPlayerInitialized += OnLocalPlayerInitialized;
            if (BasisLocalPlayer.PlayerReady)
            {
                OnLocalPlayerInitialized();
            }
        }

        private static void OnLocalPlayerInitialized()
        {
            SetHandlesVisible(BasisSettingsDefaults.SpawnAnchorHandles.RawValue);
        }

        private static void OnSnapSettingChanged(bool value) => RefreshHandles();

        private static void OnSnapSettingChanged(float value) => RefreshHandles();

        private static void OnUseGizmosChanged(bool state)
        {
            if (state)
            {
                return;
            }
            for (int i = 0; i < handles.Count; i++)
            {
                if (handles[i] != null)
                {
                    handles[i].ForgetGizmos();
                }
            }
        }

        public static bool TryGetSelected(out SpawnAnchor anchor)
        {
            EnsureLoaded();
            if (SelectedIndex >= 0 && SelectedIndex < Anchors.Count)
            {
                anchor = Anchors[SelectedIndex];
                return true;
            }
            anchor = null;
            return false;
        }

        public static void Select(int index)
        {
            EnsureLoaded();
            int next = index >= 0 && index < Anchors.Count ? index : -1;
            if (next == SelectedIndex)
            {
                return;
            }
            SelectedIndex = next;
            Changed(false);
        }

        public static SpawnAnchor Add(string name, Vector3 position, Quaternion rotation, bool select = true)
        {
            EnsureLoaded();
            SpawnAnchor anchor = new SpawnAnchor
            {
                Name = string.IsNullOrEmpty(name) ? NextName() : name,
                Position = position,
                Rotation = rotation,
            };
            Anchors.Add(anchor);
            if (select)
            {
                SelectedIndex = Anchors.Count - 1;
            }
            Changed(true);
            return anchor;
        }

        public static string NextName()
        {
            EnsureLoaded();
            int number = Anchors.Count + 1;
            string name = BasisLocalization.Get("settings.developer.spawnAnchors.name", number);
            while (HasName(name))
            {
                number++;
                name = BasisLocalization.Get("settings.developer.spawnAnchors.name", number);
            }
            return name;
        }

        private static bool HasName(string name)
        {
            for (int i = 0; i < Anchors.Count; i++)
            {
                if (Anchors[i].Name == name)
                {
                    return true;
                }
            }
            return false;
        }

        public static SpawnAnchor AddAtPlayer()
        {
            BasisLocalPlayer player = BasisLocalPlayer.Instance;
            if (player == null || player.PlayerSelf == null)
            {
                BasisDebug.LogError("Spawn anchor: no local player to place an anchor at.");
                return null;
            }
            Vector3 forward = Vector3.ProjectOnPlane(BasisLocalCameraDriver.HeadForward(), Vector3.up);
            if (forward.sqrMagnitude <= Mathf.Epsilon)
            {
                forward = player.PlayerSelf.forward;
            }
            Vector3 position = player.PlayerSelf.position;
            Quaternion rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
            SnapPose(ref position, ref rotation);
            return Add(null, position, rotation);
        }

        public static async Task<SpawnAnchor> PlaceWithRaycast()
        {
            BasisDeviceManagement device = BasisDeviceManagement.Instance;
            if (device == null)
            {
                BasisDebug.LogError("Spawn anchor placement failed: no device manager.");
                return null;
            }
            if (!device.FindDevice(out BasisInput input, BasisDominantHand.DominantRole) &&
                !device.FindDevice(out input, BasisDominantHand.NonDominantRole) &&
                !device.FindDevice(out input, BasisBoneTrackedRole.CenterEye))
            {
                BasisDebug.LogError("Spawn anchor placement failed: no suitable device found (LeftHand/RightHand/CenterEye).");
                return null;
            }

            BasisMainMenu.Close();
            (Vector3 pos, Quaternion rot, Vector3 scale) placed;
            try
            {
                placed = await PlacementManager.BeginPlacement(input, PlacementHalfExtents, Vector3.up * PlacementHalfExtents.y);
            }
            catch (TaskCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
                return null;
            }
            Vector3 position = placed.pos;
            Quaternion rotation = placed.rot;
            SnapPose(ref position, ref rotation);
            return Add(null, position, rotation);
        }

        public static void SnapPose(ref Vector3 position, ref Quaternion rotation)
        {
            if (BasisSettingsDefaults.SpawnAnchorPositionSnap.RawValue)
            {
                position = SnapPosition(position, BasisSettingsDefaults.SpawnAnchorPositionSnapSize.RawValue);
            }
            if (BasisSettingsDefaults.SpawnAnchorRotationSnap.RawValue)
            {
                rotation = SnapRotation(rotation, BasisSettingsDefaults.SpawnAnchorRotationSnapDegrees.RawValue);
            }
        }

        public static Vector3 SnapPosition(Vector3 position, float size)
        {
            if (size <= 0f)
            {
                return position;
            }
            return new Vector3(Mathf.Round(position.x / size) * size, Mathf.Round(position.y / size) * size, Mathf.Round(position.z / size) * size);
        }

        public static Quaternion SnapRotation(Quaternion rotation, float degrees)
        {
            if (degrees <= 0f)
            {
                return rotation;
            }
            Vector3 euler = rotation.eulerAngles;
            euler.x = Mathf.Round(euler.x / degrees) * degrees;
            euler.y = Mathf.Round(euler.y / degrees) * degrees;
            euler.z = Mathf.Round(euler.z / degrees) * degrees;
            return Quaternion.Euler(euler);
        }

        public static void Remove(int index)
        {
            EnsureLoaded();
            if (index < 0 || index >= Anchors.Count)
            {
                return;
            }
            Anchors.RemoveAt(index);
            if (SelectedIndex == index)
            {
                SelectedIndex = -1;
            }
            else if (SelectedIndex > index)
            {
                SelectedIndex--;
            }
            Changed(true);
        }

        public static void RemoveSelected() => Remove(SelectedIndex);

        public static void Clear()
        {
            EnsureLoaded();
            Anchors.Clear();
            SelectedIndex = -1;
            Changed(true);
        }

        public static void SetPose(SpawnAnchor anchor, Vector3 position, Quaternion rotation)
        {
            if (anchor == null)
            {
                return;
            }
            anchor.Position = position;
            anchor.Rotation = rotation;
            Changed(false);
        }

        public static void SetScaleOverride(SpawnAnchor anchor, bool overrideScale, float scale)
        {
            if (anchor == null)
            {
                return;
            }
            anchor.OverrideScale = overrideScale;
            anchor.Scale = Mathf.Clamp(scale, MinScale, MaxScale);
            Changed(false);
        }

        public static void SetName(SpawnAnchor anchor, string name)
        {
            name = (name ?? string.Empty).Trim();
            if (anchor == null || name.Length == 0 || name == anchor.Name)
            {
                return;
            }
            anchor.Name = name;
            Changed(false);
        }

        public static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return DefaultFilePath;
            }
            path = path.Trim();
            return Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(DefaultFilePath), path);
        }

        public static bool Save(string path)
        {
            EnsureLoaded();
            string resolved = ResolvePath(path);
            try
            {
                string folder = Path.GetDirectoryName(resolved);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                SpawnAnchorFile file = new SpawnAnchorFile { Anchors = Anchors.ToArray(), SelectedIndex = SelectedIndex };
                File.WriteAllText(resolved, JsonUtility.ToJson(file, true));
                return true;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Spawn anchors: could not save to {resolved}: {e.Message}");
                return false;
            }
        }

        public static bool Load(string path)
        {
            string resolved = ResolvePath(path);
            if (!ReadFile(resolved, out List<SpawnAnchor> read, out int selected, true))
            {
                return false;
            }
            Anchors.Clear();
            Anchors.AddRange(read);
            SelectedIndex = selected;
            loaded = true;
            Changed(true);
            return true;
        }

        private static bool ReadFile(string path, out List<SpawnAnchor> anchors, out int selected, bool logMissing)
        {
            anchors = new List<SpawnAnchor>();
            selected = -1;
            try
            {
                if (!File.Exists(path))
                {
                    if (logMissing)
                    {
                        BasisDebug.LogWarning($"Spawn anchors: no file at {path}");
                    }
                    return false;
                }
                SpawnAnchorFile file = JsonUtility.FromJson<SpawnAnchorFile>(File.ReadAllText(path));
                if (file?.Anchors != null)
                {
                    for (int i = 0; i < file.Anchors.Length; i++)
                    {
                        SpawnAnchor anchor = file.Anchors[i];
                        if (anchor == null)
                        {
                            continue;
                        }
                        anchors.Add(Sanitize(anchor, anchors.Count + 1));
                    }
                }
                if (file != null && file.SelectedIndex >= 0 && file.SelectedIndex < anchors.Count)
                {
                    selected = file.SelectedIndex;
                }
                return true;
            }
            catch (Exception e)
            {
                BasisDebug.LogError($"Spawn anchors: could not read {path}: {e.Message}");
                anchors.Clear();
                selected = -1;
                return false;
            }
        }

        private static SpawnAnchor Sanitize(SpawnAnchor anchor, int number)
        {
            if (string.IsNullOrEmpty(anchor.Name))
            {
                anchor.Name = "Anchor " + number;
            }
            Quaternion rotation = anchor.Rotation;
            float lengthSq = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            anchor.Rotation = lengthSq <= Mathf.Epsilon || float.IsNaN(lengthSq) ? Quaternion.identity : Quaternion.Normalize(rotation);
            if (float.IsNaN(anchor.Position.x) || float.IsNaN(anchor.Position.y) || float.IsNaN(anchor.Position.z))
            {
                anchor.Position = Vector3.zero;
            }
            anchor.Scale = float.IsNaN(anchor.Scale) ? 1f : Mathf.Clamp(anchor.Scale, MinScale, MaxScale);
            return anchor;
        }

        private static void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }
            loaded = true;
            if (ReadFile(DefaultFilePath, out List<SpawnAnchor> read, out int selected, false))
            {
                Anchors.Clear();
                Anchors.AddRange(read);
                SelectedIndex = selected;
            }
        }

        private static void Changed(bool structural)
        {
            Save(DefaultFilePath);
            if (handlesVisible && BasisLocalPlayer.PlayerReady)
            {
                if (structural)
                {
                    RebuildHandles();
                }
                else
                {
                    RefreshHandles();
                }
            }
            OnChanged?.Invoke(structural);
        }

        public static bool HandlesVisible => handlesVisible;

        public static void SetHandlesVisible(bool visible)
        {
            handlesVisible = visible;
            if (!visible)
            {
                DestroyHandles();
                return;
            }
            if (BasisLocalPlayer.PlayerReady)
            {
                RebuildHandles();
            }
        }

        private static void RebuildHandles()
        {
            DestroyHandles();
            EnsureLoaded();
            Transform parent = BasisDeviceManagement.Instance != null ? BasisDeviceManagement.Instance.transform : null;
            for (int i = 0; i < Anchors.Count; i++)
            {
                BasisSpawnAnchorHandle handle = BasisSpawnAnchorHandle.Spawn(parent);
                handle.OnGrabbed = OnHandleGrabbed;
                handle.OnReleased = OnHandleReleased;
                handle.OnScaleGesture = OnHandleScaled;
                handles.Add(handle);
            }
            RefreshHandles();
            if (!ticking)
            {
                BasisLocalPlayer.AfterSimulateOnLate.AddAction(122, Tick);
                ticking = true;
            }
        }

        private static void DestroyHandles()
        {
            for (int i = 0; i < handles.Count; i++)
            {
                if (handles[i] != null)
                {
                    handles[i].Despawn();
                }
            }
            handles.Clear();
            if (ticking)
            {
                BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(122, Tick);
                ticking = false;
            }
        }

        private static void RefreshHandles()
        {
            for (int i = 0; i < handles.Count && i < Anchors.Count; i++)
            {
                if (handles[i] != null)
                {
                    handles[i].Apply(Anchors[i], i == SelectedIndex);
                }
            }
        }

        private static void Tick()
        {
            Vector3 camPos = BasisLocalCameraDriver.Position;
            float scale = BasisHeightDriver.ScaledToMatchValue;
            if (scale <= 0f)
            {
                scale = 1f;
            }
            for (int i = 0; i < handles.Count; i++)
            {
                BasisSpawnAnchorHandle handle = handles[i];
                if (handle == null)
                {
                    continue;
                }
                if (handle.IsGrabbed && i < Anchors.Count)
                {
                    SpawnAnchor anchor = Anchors[i];
                    anchor.Position = handle.transform.position;
                    anchor.Rotation = handle.transform.rotation;
                }
                handle.Tick(camPos, scale);
            }
        }

        private static void OnHandleGrabbed(BasisSpawnAnchorHandle handle)
        {
            Select(handles.IndexOf(handle));
        }

        private static void OnHandleReleased(BasisSpawnAnchorHandle handle)
        {
            int index = handles.IndexOf(handle);
            if (index < 0 || index >= Anchors.Count)
            {
                return;
            }
            SpawnAnchor anchor = Anchors[index];
            anchor.Position = handle.transform.position;
            anchor.Rotation = handle.transform.rotation;
            Changed(false);
        }

        private static void OnHandleScaled(BasisSpawnAnchorHandle handle, float scale)
        {
            int index = handles.IndexOf(handle);
            if (index < 0 || index >= Anchors.Count)
            {
                return;
            }
            SpawnAnchor anchor = Anchors[index];
            anchor.OverrideScale = true;
            anchor.Scale = Mathf.Clamp(scale, MinScale, MaxScale);
            handle.Apply(anchor, index == SelectedIndex);
        }
    }
}
