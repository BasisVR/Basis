using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Basis.Scripts.Common
{
    /// <summary>
    /// One-time migration of user-owned persistent data from the old "Basis Unity" location to
    /// the current <see cref="Application.persistentDataPath"/>. Cache and diagnostic data are
    /// intentionally left behind so a product-name migration does not drag many gigabytes of
    /// disposable data into the new install.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisPersistentDataMigration
    {
        const string OldCompany = "Basis Unity";
        const string OldProduct = "Basis Unity";
        const string MarkerName = ".migrated-from-basis-unity";

        // Root-level persistent files are overwhelmingly settings and small user stores. Keep the
        // portable/user-owned formats while excluding logs, temporary output and diagnostics.
        [NoAutoStaticsCleanup] static readonly string[] UserRootExtensions = { ".json", ".bas", ".xml", ".txt" };
        [NoAutoStaticsCleanup] static readonly string[] ExcludedSuffixes =
        {
            ".log", ".csv", ".bak", ".tmp", ".filterstack", ".report.txt", ".corrupt_backup",
        };
        static readonly HashSet<string> ExcludedRootFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "TestResults.xml",
            MarkerName,
        };

        // These folders contain authored/user-owned state. Deliberately absent: BEEData (download
        // cache), GraphicsState (render cache), CrashReports, PulledServerLogs and developer/debug
        // capture folders. Recordings/backups are included because telling a user the old directory
        // is safe to delete must not strand content they explicitly created there.
        [NoAutoStaticsCleanup] static readonly string[] UserFolders =
        {
            "PlayerSettings",
            "BasisActions",
            "AvatarRecordings",
            "VoiceRecordings",
            "Backups",
            "Basis", // Saved image pickups on non-Windows desktop platforms.
        };

        public readonly struct MigrationRequest
        {
            public readonly string SourcePath;
            public readonly string DestinationPath;

            public MigrationRequest(string sourcePath, string destinationPath)
            {
                SourcePath = sourcePath;
                DestinationPath = destinationPath;
            }
        }

        public readonly struct MigrationNotice
        {
            public readonly string SourcePath;
            public readonly string DestinationPath;
            public readonly int MovedFiles;
            public readonly int ConflictingFiles;

            public MigrationNotice(string sourcePath, string destinationPath, int movedFiles, int conflictingFiles)
            {
                SourcePath = sourcePath;
                DestinationPath = destinationPath;
                MovedFiles = movedFiles;
                ConflictingFiles = conflictingFiles;
            }
        }

        struct MigrationStats
        {
            public int MovedFiles;
            public int ConflictingFiles;
        }

        static MigrationRequest? pendingMigration;

        public static void DismissPendingMigration()
        {
            pendingMigration = null;
        }

        public static bool MigratePending(out MigrationNotice notice)
        {
            notice = default;
            if (!pendingMigration.HasValue)
            {
                return false;
            }

            MigrationRequest request = pendingMigration.Value;
            try
            {
                Directory.CreateDirectory(request.DestinationPath);
                // The migration prompt is only offered when Basis had to create a fresh settings
                // store this launch, so files created in the new location during that first boot
                // are defaults and may be replaced by the user's previous data.
                MigrationStats stats = MoveSelectedUserData(
                    request.SourcePath,
                    request.DestinationPath,
                    replaceExisting: true);
                File.WriteAllText(Path.Combine(request.DestinationPath, MarkerName), DateTime.UtcNow.ToString("O"));

                notice = new MigrationNotice(
                    request.SourcePath,
                    request.DestinationPath,
                    stats.MovedFiles,
                    stats.ConflictingFiles);
                pendingMigration = null;

                BasisDebug.Log(
                    $"Persistent data migration completed: moved {stats.MovedFiles} user file(s) from " +
                    $"\"{request.SourcePath}\" to \"{request.DestinationPath}\". The old directory was not deleted.");

                if (stats.ConflictingFiles > 0)
                {
                    BasisDebug.LogWarning(
                        $"Persistent data migration left {stats.ConflictingFiles} user file(s) in \"{request.SourcePath}\" " +
                        "because matching files already existed in the new directory.");
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("Persistent data migration failed: " + e.Message);
                return false;
            }
        }

        public static bool TryPrepareMigration(out MigrationRequest request)
        {
            pendingMigration = null;
            request = default;

            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    break;
                default:
                    return false;
            }

            try
            {
                string newPath = Application.persistentDataPath;
                DirectoryInfo root = Directory.GetParent(newPath)?.Parent;
                if (root == null)
                {
                    return false;
                }

                string oldPath = Path.Combine(root.FullName, OldCompany, OldProduct);
                if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string marker = Path.Combine(newPath, MarkerName);
                if (File.Exists(marker) || !Directory.Exists(oldPath))
                {
                    return false;
                }

                pendingMigration = new MigrationRequest(oldPath, newPath);
                request = pendingMigration.Value;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("Persistent data migration skipped: " + e.Message);
                return false;
            }
        }

        static MigrationStats MoveSelectedUserData(string source, string destination, bool replaceExisting = false)
        {
            MigrationStats stats = default;
            Directory.CreateDirectory(destination);

            foreach (string file in Directory.GetFiles(source))
            {
                if (!IsUserRootFile(Path.GetFileName(file)))
                {
                    continue;
                }

                MoveFile(file, Path.Combine(destination, Path.GetFileName(file)), replaceExisting, ref stats);
            }

            foreach (string folder in UserFolders)
            {
                string sourceFolder = Path.Combine(source, folder);
                if (!Directory.Exists(sourceFolder))
                {
                    continue;
                }

                MoveUserFolder(sourceFolder, Path.Combine(destination, folder), replaceExisting, ref stats);
            }

            return stats;
        }

        static void MoveUserFolder(string source, string destination, bool replaceExisting, ref MigrationStats stats)
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            Directory.CreateDirectory(destination);

            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (HasExcludedSuffix(name))
                {
                    continue;
                }

                MoveFile(file, Path.Combine(destination, name), replaceExisting, ref stats);
            }

            foreach (string directory in Directory.GetDirectories(source))
            {
                MoveUserFolder(directory, Path.Combine(destination, Path.GetFileName(directory)), replaceExisting, ref stats);
            }

            // Remove only directories that became empty. Cache/temp/conflict files intentionally keep
            // their source directory alive so users can inspect what Basis did not migrate.
            if (Directory.GetFileSystemEntries(source).Length == 0)
            {
                Directory.Delete(source);
            }
        }

        static void MoveFile(string source, string destination, bool replaceExisting, ref MigrationStats stats)
        {
            if (File.Exists(destination))
            {
                if (!replaceExisting)
                {
                    stats.ConflictingFiles++;
                    return;
                }

                File.Delete(destination);
            }

            string parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Move(source, destination);
            stats.MovedFiles++;
        }

        static bool IsUserRootFile(string fileName)
        {
            if (ExcludedRootFiles.Contains(fileName) || HasExcludedSuffix(fileName))
            {
                return false;
            }

            string extension = Path.GetExtension(fileName);
            foreach (string included in UserRootExtensions)
            {
                if (string.Equals(extension, included, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        static bool HasExcludedSuffix(string fileName)
        {
            foreach (string suffix in ExcludedSuffixes)
            {
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
