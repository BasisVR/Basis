using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Basis.Scripts.Device_Management;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

/// <summary>
/// Packs everything this install keeps for the user into one portable archive, and unpacks it
/// again on another machine.
///
/// Two halves are covered: files under <c>Application.persistentDataPath</c> (settings, the avatar
/// and item key stores, saved servers, trust lists, tracker pairings, per-player settings, input
/// action bindings) and the <c>PlayerPrefs</c> entries that live outside persistentDataPath and so
/// cannot be copied as files between machines (identity key pair, per-mode microphone setup,
/// keyboard rebinds). The downloaded <c>BEEData</c> bundle cache is opt-in — the key stores alone
/// are enough to pull the content down again, and the cache is often tens of gigabytes.
///
/// The archive is an ordinary zip:
/// <code>
///   manifest.json          format version, timestamp, build, what was included
///   prefs.json             exported PlayerPrefs entries
///   data/&lt;relative path&gt;   mirror of the selected persistentDataPath tree
/// </code>
/// Root files are selected by extension minus an exclusion list rather than by a fixed list of
/// names, so settings files added later are covered without touching this class. Subfolders are
/// allow-listed, because most of them under persistentDataPath are recordings or debug dumps.
/// </summary>
public static class BasisUserDataBackup
{
    public const string ArchiveExtension = ".basisbackup";
    public const string BackupsFolderName = "Backups";

    /// <summary>Bumped when the layout changes in a way an older client cannot read.</summary>
    public const int FormatVersion = 1;

    private const string ManifestEntryName = "manifest.json";
    private const string PrefsEntryName = "prefs.json";
    private const string DataPrefix = "data/";

    private static readonly string[] IncludedExtensions = { ".json", ".bas", ".xml", ".txt" };

    private static readonly string[] ExcludedSuffixes =
    {
        ".log", ".csv", ".bak", ".tmp", ".filterstack", ".report.txt", ".corrupt_backup",
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "TestResults.xml",
        ".migrated-from-basis-unity",
    };

    private static readonly string[] IncludedFolders = { "PlayerSettings", "BasisActions" };

    /// <summary>
    /// Restoring is offered on Windows and Linux only — the desktop targets where the user can drop
    /// an archive next to the game and where a relaunch after restore is available.
    /// </summary>
    public static bool RestoreSupported
    {
        get
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_LINUX
            return true;
#elif UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>Where <see cref="CreateAsync"/> writes to and <see cref="ListArchives"/> reads from.</summary>
    public static string BackupsFolder => Path.Combine(ResolveRoot(), BackupsFolderName);

    public struct BackupResult
    {
        public bool Success;
        public string ArchivePath;
        public int FileCount;
        public int PrefCount;
        public long ArchiveBytes;
        public string Error;
    }

    public struct RestoreResult
    {
        public bool Success;
        public int FileCount;
        public int PrefCount;
        public string Error;
    }

    public struct ArchiveInfo
    {
        public string Path;
        public string FileName;
        public long SizeBytes;
        public DateTime WrittenLocal;

        /// <summary>
        /// Filled in by <see cref="ListArchivesAsync"/>; null when the file could not be read as an
        /// archive of this format. <see cref="ListArchives"/> leaves it null.
        /// </summary>
        public Manifest Manifest;
    }

    [Serializable]
    public class Manifest
    {
        public int FormatVersion;
        public string CreatedUtc;
        public string AppVersion;
        public string UnityVersion;
        public string Platform;
        public bool IncludesCachedContent;
        public bool IncludesIdentity;
        public int FileCount;
        public int PrefCount;
    }

    /// <summary>
    /// Writes a new archive into <see cref="BackupsFolder"/>. Call from the main thread — PlayerPrefs
    /// and Application are read there, the zip itself is built on a worker.
    /// </summary>
    public static async Task<BackupResult> CreateAsync(bool includeCachedContent, bool includeIdentity)
    {
        string root;
        string prefsJson;
        Manifest manifest;
        string destination;

        try
        {
            root = ResolveRoot();
            List<PrefEntry> prefs = ExportPrefs(includeIdentity);
            prefsJson = JsonUtility.ToJson(new PrefPayload { Entries = prefs }, true);

            manifest = new Manifest
            {
                FormatVersion = FormatVersion,
                CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                AppVersion = Application.version,
                UnityVersion = Application.unityVersion,
                Platform = Application.platform.ToString(),
                IncludesCachedContent = includeCachedContent,
                IncludesIdentity = includeIdentity,
                PrefCount = prefs.Count,
            };

            destination = Path.Combine(root, BackupsFolderName, BuildFileName());
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning("Backup could not be prepared: " + e);
            return new BackupResult { Success = false, Error = e.Message };
        }

        return await Task.Run(() => WriteArchive(root, destination, prefsJson, manifest, includeCachedContent));
    }

    private static BackupResult WriteArchive(
        string root, string destination, string prefsJson, Manifest manifest, bool includeCachedContent)
    {
        string temporary = destination + ".tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (File.Exists(temporary)) File.Delete(temporary);

            List<(string FullPath, string Relative)> files = CollectFiles(root, includeCachedContent);
            manifest.FileCount = files.Count;

            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteTextEntry(archive, ManifestEntryName, JsonUtility.ToJson(manifest, true));
                WriteTextEntry(archive, PrefsEntryName, prefsJson);

                foreach ((string fullPath, string relative) in files)
                {
                    try
                    {
                        ZipArchiveEntry entry = archive.CreateEntry(DataPrefix + relative, CompressionLevel.Optimal);
                        using FileStream source = new FileStream(
                            fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using Stream target = entry.Open();
                        source.CopyTo(target);
                    }
                    catch (Exception e)
                    {
                        BasisDebug.LogWarning($"Backup skipped \"{relative}\": {e.Message}");
                    }
                }
            }

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temporary, destination);

            long size = new FileInfo(destination).Length;
            BasisDebug.Log($"Backup written to \"{destination}\" ({files.Count} files, {FormatBytes(size)}).");

            return new BackupResult
            {
                Success = true,
                ArchivePath = destination,
                FileCount = files.Count,
                PrefCount = manifest.PrefCount,
                ArchiveBytes = size,
            };
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning("Backup failed: " + e);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return new BackupResult { Success = false, Error = e.Message };
        }
    }

    private static List<(string FullPath, string Relative)> CollectFiles(string root, bool includeCachedContent)
    {
        List<(string, string)> files = new();

        foreach (string file in Directory.GetFiles(root))
        {
            if (IsBackedUpRootFile(Path.GetFileName(file))) files.Add((file, Path.GetFileName(file)));
        }

        foreach (string folder in IncludedFolders) AddFolder(root, folder, files);
        if (includeCachedContent) AddFolder(root, BasisBeeConstants.AssetBundlesFolder, files);

        return files;
    }

    private static void AddFolder(string root, string folderName, List<(string, string)> files)
    {
        string folder = Path.Combine(root, folderName);
        if (!Directory.Exists(folder)) return;

        foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (HasExcludedSuffix(Path.GetFileName(file))) continue;
            files.Add((file, ToRelative(root, file)));
        }
    }

    private static bool IsBackedUpRootFile(string fileName)
    {
        if (ExcludedFileNames.Contains(fileName)) return false;
        if (HasExcludedSuffix(fileName)) return false;

        string extension = Path.GetExtension(fileName);
        foreach (string included in IncludedExtensions)
        {
            if (string.Equals(extension, included, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasExcludedSuffix(string fileName)
    {
        foreach (string suffix in ExcludedSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string ToRelative(string root, string fullPath)
    {
        string relative = fullPath.Substring(root.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Replace('\\', '/');
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string contents)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using StreamWriter writer = new StreamWriter(entry.Open());
        writer.Write(contents);
    }

    private static string BuildFileName()
    {
        return "BasisBackup_"
            + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)
            + ArchiveExtension;
    }

    /// <summary>Archives sitting in <see cref="BackupsFolder"/>, newest first. Manifests are not read.</summary>
    public static List<ArchiveInfo> ListArchives() => ListArchives(ResolveBackupsFolder());

    /// <summary>
    /// <see cref="ListArchives"/> with every manifest read as well, all of it on a worker thread.
    /// Opening a zip parses its whole central directory, and an archive that carries the content
    /// cache lists tens of thousands of files, so the menu must never do this on the main thread.
    /// Call from the main thread: the folder is resolved there before the scan starts, and the
    /// manifests are parsed there once the reads come back.
    /// </summary>
    public static async Task<List<ArchiveInfo>> ListArchivesAsync()
    {
        string folder = ResolveBackupsFolder();
        if (folder == null) return new List<ArchiveInfo>();

        (List<ArchiveInfo> Archives, List<string> Manifests) scanned = await Task.Run(() =>
        {
            List<ArchiveInfo> archives = ListArchives(folder);
            List<string> manifests = new(archives.Count);
            foreach (ArchiveInfo archive in archives)
            {
                manifests.Add(ReadManifestJson(archive.Path));
            }
            return (archives, manifests);
        });

        for (int i = 0; i < scanned.Archives.Count; i++)
        {
            ArchiveInfo archive = scanned.Archives[i];
            archive.Manifest = ParseManifest(scanned.Manifests[i]);
            scanned.Archives[i] = archive;
        }

        return scanned.Archives;
    }

    /// <summary><see cref="BackupsFolder"/>, or null (with a warning logged) when the data root cannot be resolved.</summary>
    private static string ResolveBackupsFolder()
    {
        try
        {
            return BackupsFolder;
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning("Could not list backups: " + e.Message);
            return null;
        }
    }

    private static List<ArchiveInfo> ListArchives(string folder)
    {
        List<ArchiveInfo> archives = new();
        if (string.IsNullOrEmpty(folder)) return archives;

        try
        {
            if (!Directory.Exists(folder)) return archives;

            foreach (string file in Directory.GetFiles(folder, "*" + ArchiveExtension))
            {
                FileInfo info = new FileInfo(file);
                archives.Add(new ArchiveInfo
                {
                    Path = file,
                    FileName = info.Name,
                    SizeBytes = info.Length,
                    WrittenLocal = info.LastWriteTime,
                });
            }
            archives.Sort((a, b) => b.WrittenLocal.CompareTo(a.WrittenLocal));
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning("Could not list backups: " + e.Message);
        }
        return archives;
    }

    /// <summary>
    /// Reads just the manifest so the UI can describe an archive before the user commits to
    /// restoring it. Null if the file is not a readable archive of this format.
    /// </summary>
    public static Manifest ReadManifest(string archivePath) => ParseManifest(ReadManifestJson(archivePath));

    /// <summary>
    /// The manifest entry's text, or null if the file is not a readable archive of this format.
    /// Plain file I/O, so it is safe on a worker thread; parsing is left to the caller.
    /// </summary>
    private static string ReadManifestJson(string archivePath)
    {
        try
        {
            using FileStream stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read);

            ZipArchiveEntry entry = archive.GetEntry(ManifestEntryName);
            if (entry == null) return null;

            using StreamReader reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning($"Could not read backup manifest from \"{archivePath}\": {e.Message}");
            return null;
        }
    }

    private static Manifest ParseManifest(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            return JsonUtility.FromJson<Manifest>(json);
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning("Could not parse backup manifest: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Unpacks an archive over this install, overwriting existing files. Call from the main thread;
    /// PlayerPrefs are applied once extraction completes.
    ///
    /// The client holds settings in memory and rewrites them on the next change, so the caller must
    /// prompt for a restart immediately afterwards or the running session overwrites what was just
    /// restored.
    /// </summary>
    public static async Task<RestoreResult> RestoreAsync(string archivePath, bool restoreIdentity)
    {
        if (!RestoreSupported)
        {
            return new RestoreResult { Success = false, Error = "Restore is not supported on this platform." };
        }

        string root;
        try
        {
            root = ResolveRoot();
        }
        catch (Exception e)
        {
            return new RestoreResult { Success = false, Error = e.Message };
        }

        (RestoreResult Result, List<PrefEntry> Prefs) extracted;
        try
        {
            extracted = await Task.Run(() => Extract(root, archivePath));
        }
        catch (Exception e)
        {
            BasisDebug.LogWarning("Restore failed: " + e);
            return new RestoreResult { Success = false, Error = e.Message };
        }

        if (!extracted.Result.Success) return extracted.Result;

        RestoreResult final = extracted.Result;
        final.PrefCount = ApplyPrefs(extracted.Prefs, restoreIdentity);

        BasisDebug.Log(
            $"Restored {final.FileCount} files and {final.PrefCount} preference entries from \"{archivePath}\".");
        return final;
    }

    private static (RestoreResult, List<PrefEntry>) Extract(string root, string archivePath)
    {
        List<PrefEntry> prefs = new();

        if (!File.Exists(archivePath))
        {
            return (new RestoreResult { Success = false, Error = "Backup file not found." }, prefs);
        }

        using FileStream stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read);

        ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestEntryName);
        if (manifestEntry == null)
        {
            return (new RestoreResult { Success = false, Error = "Not a Basis backup archive." }, prefs);
        }

        Manifest manifest;
        using (StreamReader reader = new StreamReader(manifestEntry.Open()))
        {
            manifest = JsonUtility.FromJson<Manifest>(reader.ReadToEnd());
        }

        if (manifest == null || manifest.FormatVersion <= 0)
        {
            return (new RestoreResult { Success = false, Error = "Backup manifest is unreadable." }, prefs);
        }
        if (manifest.FormatVersion > FormatVersion)
        {
            return (new RestoreResult
            {
                Success = false,
                Error = $"Backup format {manifest.FormatVersion} is newer than this client supports ({FormatVersion}).",
            }, prefs);
        }

        ZipArchiveEntry prefsEntry = archive.GetEntry(PrefsEntryName);
        if (prefsEntry != null)
        {
            using StreamReader reader = new StreamReader(prefsEntry.Open());
            PrefPayload payload = JsonUtility.FromJson<PrefPayload>(reader.ReadToEnd());
            if (payload?.Entries != null) prefs = payload.Entries;
        }

        string rootFull = Path.GetFullPath(root);
        int written = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(DataPrefix, StringComparison.Ordinal)) continue;
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;

            string relative = entry.FullName.Substring(DataPrefix.Length);
            if (string.IsNullOrEmpty(relative)) continue;

            string target = Path.GetFullPath(
                Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                BasisDebug.LogWarning($"Restore skipped entry outside the data folder: \"{entry.FullName}\".");
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Stream source = entry.Open())
                using (FileStream destination =
                    new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(destination);
                }
                written++;
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"Restore skipped \"{relative}\": {e.Message}");
            }
        }

        return (new RestoreResult { Success = true, FileCount = written }, prefs);
    }

    private enum PrefKind
    {
        String = 0,
        Int = 1,
        Float = 2,
    }

    [Serializable]
    private struct PrefEntry
    {
        public string Key;
        public int Kind;
        public string Value;
        public bool Identity;
    }

    [Serializable]
    private class PrefPayload
    {
        public List<PrefEntry> Entries = new();
    }

    /// <summary>
    /// PlayerPrefs has no key enumeration API, so the exported set is declared explicitly here. That
    /// also keeps the export auditable — the identity key pair is the one genuinely sensitive value
    /// in a backup and only leaves the machine when the caller asks for it.
    /// </summary>
    private static readonly string[] IdentityKeys = { "PrivateKeyDID", "PublicKeyDID", "DIDID" };

    private static readonly string[] MicrophoneModes =
    {
        BasisConstants.Desktop,
        BasisConstants.OpenVRLoader,
        BasisConstants.OpenXRLoader,
        BasisConstants.SimulateXR,
    };

    private static readonly (string Name, PrefKind Kind)[] MicrophoneKeys =
    {
        ("Microphone", PrefKind.String),
        ("Volume01", PrefKind.Float),
        ("Denoiser", PrefKind.Int),
        ("LimitThreshold", PrefKind.Float),
        ("LimitKnee", PrefKind.Float),
        ("DenoiseMakeupDb", PrefKind.Float),
        ("DenoiseWet", PrefKind.Float),
        ("UseAGC", PrefKind.Int),
        ("AgcMaxGainDbV2", PrefKind.Float),
        ("AgcAttackV2", PrefKind.Float),
        ("AgcReleaseV3", PrefKind.Float),
        ("UseNoiseGate", PrefKind.Int),
        ("AutoNoiseGate", PrefKind.Int),
        ("NoiseGateThreshold", PrefKind.Float),
        ("NoiseGateAttackV2", PrefKind.Float),
        ("NoiseGateRelease", PrefKind.Float),
        ("TalkMode", PrefKind.Int),
    };

    private static readonly (string Key, PrefKind Kind)[] GeneralKeys =
    {
        ("InputBindingOverrides", PrefKind.String),
        ("MicrophoneState", PrefKind.Int),
        ("Basis.ImagePickup.ReceiveEnabled", PrefKind.Int),
    };

    private static List<PrefEntry> ExportPrefs(bool includeIdentity)
    {
        List<PrefEntry> entries = new();

        if (includeIdentity)
        {
            foreach (string key in IdentityKeys) Capture(entries, key, PrefKind.String, true);
        }

        foreach ((string key, PrefKind kind) in GeneralKeys) Capture(entries, key, kind, false);

        foreach (string mode in MicrophoneModes)
        {
            foreach ((string name, PrefKind kind) in MicrophoneKeys)
            {
                Capture(entries, $"{mode}_Mic_{name}", kind, false);
            }
        }

        return entries;
    }

    private static void Capture(List<PrefEntry> entries, string key, PrefKind kind, bool identity)
    {
        if (!PlayerPrefs.HasKey(key)) return;

        string value = kind switch
        {
            PrefKind.Int => PlayerPrefs.GetInt(key).ToString(CultureInfo.InvariantCulture),
            PrefKind.Float => PlayerPrefs.GetFloat(key).ToString("R", CultureInfo.InvariantCulture),
            _ => PlayerPrefs.GetString(key),
        };

        entries.Add(new PrefEntry { Key = key, Kind = (int)kind, Value = value, Identity = identity });
    }

    private static int ApplyPrefs(List<PrefEntry> entries, bool restoreIdentity)
    {
        int applied = 0;

        foreach (PrefEntry entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Key)) continue;
            if (entry.Identity && !restoreIdentity) continue;

            try
            {
                switch ((PrefKind)entry.Kind)
                {
                    case PrefKind.Int:
                        if (!int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                            continue;
                        PlayerPrefs.SetInt(entry.Key, i);
                        break;
                    case PrefKind.Float:
                        if (!float.TryParse(entry.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                            continue;
                        PlayerPrefs.SetFloat(entry.Key, f);
                        break;
                    default:
                        PlayerPrefs.SetString(entry.Key, entry.Value ?? string.Empty);
                        break;
                }
                applied++;
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"Restore could not apply preference \"{entry.Key}\": {e.Message}");
            }
        }

        if (applied > 0) PlayerPrefs.Save();
        return applied;
    }

    private static string ResolveRoot()
    {
        string cached = BasisIOManagement.PersistentDataPath;
        return string.IsNullOrEmpty(cached) ? Application.persistentDataPath : cached;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double value = bytes / 1024d;
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }
        return value.ToString(value >= 100d ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
