using System;
using System.Collections.Generic;
using System.IO;
using Basis.BasisUI;
using UnityEngine;

namespace Basis.Cinematics
{
    /// <summary>
    /// Saved dolly tracks, and the folder they are traded through.
    ///
    /// <para>Two storage shapes for two jobs, both pointed at the same records. The saved list is
    /// one file, read whole the moment the panel opens, exactly as saved camera modes are — it is
    /// a dropdown, so "give me all of them" is the only read that happens. Export writes one preset
    /// to its own file in a folder beside it, because a preset that cannot leave the machine on its
    /// own is not something anybody can be given: a track is a shot somebody built, and being able
    /// to hand it over is most of the point of saving one.</para>
    ///
    /// <para>Import is the same door in reverse — everything in the folder is read in — rather than
    /// a file picker, which there is no way to drive from inside a headset.</para>
    /// </summary>
    public static class BasisCameraDollyPresets
    {
        public const string PresetsJson = "DollyPresets.json";

        /// <summary>Folder inside the data directory that exported presets are written to and read back from.</summary>
        public const string ExportFolderName = "DollyPresets";

        /// <summary>
        /// Enough that nobody sensible will meet it, low enough that a stuck save loop cannot grow
        /// the file without bound. Matches the cap on saved camera modes.
        /// </summary>
        public const int MaxPresets = 64;

        /// <summary>
        /// What a received track with no usable name of its own is called. Not localized: this
        /// becomes the stored record's name, and a name that read differently depending on the
        /// language selected when it arrived would not be one name.
        /// </summary>
        public const string UnnamedSharedTrack = "Shared Track";

        [Serializable]
        private class PresetFile
        {
            public int version = 1;
            public List<BasisCameraDollyPreset> presets = new List<BasisCameraDollyPreset>();
        }

        private static List<BasisCameraDollyPreset> _presets;
        private static int _count;
        private static int _revision;

        /// <summary>Raised after the list changes, so an open panel can rebuild its dropdown.</summary>
        public static event Action OnChanged;

        public static IReadOnlyList<BasisCameraDollyPreset> Presets
        {
            get
            {
                EnsureLoaded();
                return _presets;
            }
        }

        public static int Count
        {
            get
            {
                EnsureLoaded();
                return _count;
            }
        }

        /// <summary>
        /// Bumped by every change. Rebuilding the dropdown throws away the entries an open one is
        /// showing, so the panel only does it when something actually moved — and the count alone
        /// cannot tell it, since overwriting a preset leaves the count still.
        /// </summary>
        public static int Revision => _revision;

        public static BasisCameraDollyPreset Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            EnsureLoaded();
            for (int Index = 0; Index < _presets.Count; Index++)
            {
                if (BasisCameraDollyPreset.NamesMatch(_presets[Index].name, name)) return _presets[Index];
            }
            return null;
        }

        public static bool Exists(string name) => Find(name) != null;

        /// <summary>
        /// Saves a preset, replacing any of the same name in place. Overwriting is the point:
        /// saving onto a name you already have is how a track gets updated once you have moved a
        /// point, and keeping its slot means the one you just picked does not jump to the bottom of
        /// the list you picked it from.
        /// </summary>
        /// <param name="error">Localization key describing why nothing was saved, or null on success.</param>
        public static bool Store(BasisCameraDollyPreset preset, out string error)
        {
            error = null;
            if (preset == null)
            {
                error = "camera.dollyPreset.error.empty";
                return false;
            }

            string cleaned = BasisCameraDollyPreset.SanitizeName(preset.name);
            if (cleaned == null)
            {
                error = "camera.dollyPreset.error.empty";
                return false;
            }
            if (preset.Count == 0)
            {
                error = "camera.dollyPreset.error.noPoints";
                return false;
            }

            preset.name = cleaned;
            EnsureLoaded();

            for (int Index = 0; Index < _presets.Count; Index++)
            {
                if (!BasisCameraDollyPreset.NamesMatch(_presets[Index].name, cleaned)) continue;

                _presets[Index] = preset;
                Save();
                return true;
            }

            if (_presets.Count >= MaxPresets)
            {
                error = "camera.dollyPreset.error.full";
                return false;
            }

            _presets.Add(preset);
            Save();
            return true;
        }

        public static bool Remove(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            EnsureLoaded();
            for (int Index = 0; Index < _presets.Count; Index++)
            {
                if (!BasisCameraDollyPreset.NamesMatch(_presets[Index].name, name)) continue;

                _presets.RemoveAt(Index);
                Save();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Takes in a track that came from somebody else. Unlike <see cref="Store"/> this never
        /// writes over a saved track of the same name: the name on a received track is the sharer's
        /// word, not an instruction to replace yours, so a clash is given a free name beside it.
        /// Re-accepting a track you already hold is recognised and stored once.
        /// </summary>
        /// <param name="storedName">The name it went in under, or null when nothing was stored.</param>
        /// <param name="error">Localization key describing why nothing was stored, or null on success.</param>
        public static bool Adopt(BasisCameraDollyPreset preset, out string storedName, out string error)
        {
            storedName = null;
            error = null;

            if (preset == null)
            {
                error = "camera.dollyPreset.error.empty";
                return false;
            }

            Repair(preset);
            if (preset.Count == 0)
            {
                error = "camera.dollyPreset.error.noPoints";
                return false;
            }

            string cleaned = BasisCameraDollyPreset.SanitizeName(preset.name) ?? UnnamedSharedTrack;

            BasisCameraDollyPreset existing = Find(cleaned);
            if (existing != null && existing.SameShapeAs(preset))
            {
                storedName = existing.name;
                return true;
            }

            preset.name = existing == null ? cleaned : FreeNameBeside(cleaned);
            if (preset.name == null)
            {
                error = "camera.dollyPreset.error.full";
                return false;
            }

            if (!Store(preset, out error)) return false;

            storedName = preset.name;
            return true;
        }

        /// <summary>
        /// "Sweep" already taken becomes "Sweep 2". Bounded by the preset cap: past that there is no
        /// free name to find because there is no room to store one either.
        /// </summary>
        private static string FreeNameBeside(string taken)
        {
            for (int Suffix = 2; Suffix <= MaxPresets + 1; Suffix++)
            {
                string tail = " " + Suffix;
                // Sanitize truncates at the length cap, so a name already at it would come back
                // unchanged and collide forever. Make room for the suffix rather than append past it.
                string head = taken.Length + tail.Length > BasisCameraDollyPreset.MaxNameLength
                    ? taken.Substring(0, BasisCameraDollyPreset.MaxNameLength - tail.Length)
                    : taken;

                string candidate = BasisCameraDollyPreset.SanitizeName(head + tail);
                if (candidate == null || Exists(candidate)) continue;

                return candidate;
            }
            return null;
        }

        // ---- The traded folder -------------------------------------------------------------

        public static string ExportFolder => Path.Combine(StorageDirectory, ExportFolderName);

        /// <summary>Writes one preset to its own file in the export folder, returning where it landed.</summary>
        /// <param name="error">Localization key describing why nothing was written, or null on success.</param>
        public static bool Export(BasisCameraDollyPreset preset, out string path, out string error)
        {
            path = null;
            error = null;

            if (preset == null || preset.Count == 0)
            {
                error = "camera.dollyPreset.error.noPoints";
                return false;
            }

            string cleaned = BasisCameraDollyPreset.SanitizeName(preset.name);
            if (cleaned == null)
            {
                error = "camera.dollyPreset.error.empty";
                return false;
            }

            try
            {
                string folder = ExportFolder;
                Directory.CreateDirectory(folder);

                string target = Path.Combine(folder, cleaned + ".json");
                File.WriteAllText(target, JsonUtility.ToJson(preset, true));
                path = target;
                return true;
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisCameraDollyPresets] Failed to export '{preset.name}': {ex.Message}");
                error = "camera.dollyPreset.error.exportFailed";
                return false;
            }
        }

        /// <summary>
        /// Reads every preset file in the export folder into the saved list, overwriting any of the
        /// same name. Returns how many were taken in; a folder with nothing in it is not a failure,
        /// it is the answer.
        /// </summary>
        public static bool Import(out int imported, out string error)
        {
            imported = 0;
            error = null;

            string folder = ExportFolder;
            string[] files;
            try
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    return true;
                }
                files = Directory.GetFiles(folder, "*.json");
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisCameraDollyPresets] Failed to read the preset folder: {ex.Message}");
                error = "camera.dollyPreset.error.importFailed";
                return false;
            }

            for (int Index = 0; Index < files.Length; Index++)
            {
                try
                {
                    BasisCameraDollyPreset preset = JsonUtility.FromJson<BasisCameraDollyPreset>(
                        File.ReadAllText(files[Index]));
                    if (preset == null) continue;

                    // A file dropped in by hand can be called anything, and the list is keyed by
                    // the name inside it, so an empty one falls back to what it is called on disk.
                    if (BasisCameraDollyPreset.SanitizeName(preset.name) == null)
                    {
                        preset.name = Path.GetFileNameWithoutExtension(files[Index]);
                    }

                    Repair(preset);
                    if (Store(preset, out _)) imported++;
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError(
                        $"[BasisCameraDollyPresets] Skipped '{Path.GetFileName(files[Index])}': {ex.Message}");
                }
            }

            return true;
        }

        /// <summary>Opens the export folder, making it first so there is something to open.</summary>
        public static bool RevealExportFolder()
        {
            try
            {
                Directory.CreateDirectory(ExportFolder);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisCameraDollyPresets] Failed to create the preset folder: {ex.Message}");
                return false;
            }
            return BasisFileBrowserUtility.Reveal(ExportFolder);
        }

        // ---- Storage -----------------------------------------------------------------------

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Where the presets live, when it is not where they live. Tests must never open the real
        /// file or the real folder: they save, import and delete, and what they would be doing that
        /// to is the player's own saved tracks.
        /// </summary>
        public static string DirectoryOverrideForTest;

        private static string StorageDirectory =>
            string.IsNullOrEmpty(DirectoryOverrideForTest) ? Application.persistentDataPath : DirectoryOverrideForTest;
#else
        private static string StorageDirectory => Application.persistentDataPath;
#endif

        private static string FilePath => Path.Combine(StorageDirectory, PresetsJson);

        private static void EnsureLoaded()
        {
            if (_presets != null) return;

            _presets = new List<BasisCameraDollyPreset>();
            LoadFromDisk();

            _count = _presets.Count;
            _revision++;
        }

        private static void LoadFromDisk()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;

                PresetFile file = JsonUtility.FromJson<PresetFile>(File.ReadAllText(path));
                if (file?.presets == null) return;

                for (int Index = 0; Index < file.presets.Count; Index++)
                {
                    BasisCameraDollyPreset preset = file.presets[Index];
                    if (preset == null) continue;

                    preset.name = BasisCameraDollyPreset.SanitizeName(preset.name);
                    if (preset.name == null) continue;

                    Repair(preset);
                    if (preset.Count == 0) continue;
                    if (Exists(preset.name)) continue;

                    _presets.Add(preset);
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisCameraDollyPresets] Failed to load saved presets: {ex.Message}");
            }
        }

        /// <summary>
        /// Makes a record off disk or off the wire safe to lay out. These are text and are meant to
        /// be passed around, so every one of them has been somewhere this code has not: a zero scale
        /// divides the shape away, an unrotated quaternion arrives as four zeroes rather than
        /// identity, and a move carrying an ease that does not exist would index past the curve
        /// table. Every float is forced finite as well, because a NaN reaching a waypoint spreads
        /// into the bounds of everything drawn near it and does not come back out.
        /// </summary>
        internal static void Repair(BasisCameraDollyPreset preset)
        {
            preset.points ??= new List<BasisCameraDollyPresetPoint>();
            while (preset.points.Count > BasisCameraDollyPreset.MaxPoints)
            {
                preset.points.RemoveAt(preset.points.Count - 1);
            }

            for (int Index = 0; Index < preset.points.Count; Index++)
            {
                BasisCameraDollyPresetPoint point = preset.points[Index];
                Quaternion rotation = point.rotation;
                float length = rotation.x * rotation.x + rotation.y * rotation.y +
                               rotation.z * rotation.z + rotation.w * rotation.w;

                point.rotation = !IsFinite(length) || length < 1e-6f ? Quaternion.identity : rotation.normalized;
                point.position = Finite(point.position);
                preset.points[Index] = point;
            }

            preset.anchorPosition = Finite(preset.anchorPosition);
            preset.anchorYaw = Finite(preset.anchorYaw, 0f);
            if (!(preset.anchorScale > 0.001f))
            {
                preset.anchorScale = 1f;
            }
            preset.gridSize = Mathf.Clamp(Finite(preset.gridSize, 0.25f), 0.05f, 2f);

            BasisCameraDollySettings motion = preset.motion;
            motion.playing = false;
            motion.syncMode = BasisCameraDollySync.LocalOnly;
            motion.position = Finite(motion.position, 0f);
            motion.damping = Mathf.Max(0f, Finite(motion.damping, 0f));
            motion.speed = Finite(motion.speed, BasisCameraDollySettings.Default.speed);
            motion.offset = Finite(motion.offset);
            if (!Enum.IsDefined(typeof(BasisCameraDollyMode), motion.mode))
            {
                motion.mode = BasisCameraDollyMode.Manual;
            }
            if (!BasisCameraEasing.IsDefined(motion.easeIn))
            {
                motion.easeIn = BasisCameraEase.Linear;
            }
            if (!BasisCameraEasing.IsDefined(motion.easeOut))
            {
                motion.easeOut = BasisCameraEase.Linear;
            }
            motion.easeInPortion = Mathf.Clamp(Finite(motion.easeInPortion, 0f), 0f, BasisCameraDollySpeed.MaximumEasePortion);
            motion.easeOutPortion = Mathf.Clamp(Finite(motion.easeOutPortion, 0f), 0f, BasisCameraDollySpeed.MaximumEasePortion);
            preset.motion = motion;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static float Finite(float value, float fallback) => IsFinite(value) ? value : fallback;

        private static Vector3 Finite(Vector3 value) =>
            new Vector3(Finite(value.x, 0f), Finite(value.y, 0f), Finite(value.z, 0f));

        private static void Save()
        {
            _count = _presets.Count;
            _revision++;

            try
            {
                PresetFile file = new PresetFile();
                file.presets.AddRange(_presets);

                string path = FilePath;
                Directory.CreateDirectory(StorageDirectory);

                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(file, true));
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[BasisCameraDollyPresets] Failed to save presets: {ex.Message}");
            }

            // Raised even where the write failed: the in-memory list has already changed, and an
            // open panel showing the old one would be lying about what picking a preset will do.
            OnChanged?.Invoke();
        }

#if UNITY_INCLUDE_TESTS
        public static void ResetCacheForTest() => _presets = null;

        public static void ClearForTest()
        {
            EnsureLoaded();
            _presets.Clear();
            Save();
        }
#endif
    }
}
