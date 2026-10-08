using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisPlayerSettingsManager
{
    [NoAutoStaticsCleanup] private static readonly string Dir = Path.Combine(Application.persistentDataPath, "PlayerSettings");
    [NoAutoStaticsCleanup] private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    // One lock per file
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new ConcurrentDictionary<string, SemaphoreSlim>();

    // In-memory cache so repeated requests for the same UUID don't hit disk.
    private static readonly ConcurrentDictionary<string, BasisPlayerSettingsData> cache = new ConcurrentDictionary<string, BasisPlayerSettingsData>();

    static BasisPlayerSettingsManager()
    {
        Directory.CreateDirectory(Dir);
    }

    /// <summary>
    /// Forces the static constructor to run on whichever thread calls this. The field
    /// initializers read <see cref="Application.persistentDataPath"/>, which is main-thread-only,
    /// so the avatar load thread calls this from <c>Initialize</c> (on the main thread) rather
    /// than tripping it itself on a first <see cref="Warm"/>.
    /// </summary>
    public static void EnsureInitialized()
    {
    }

    /// <summary>
    /// Fire-and-forget cache fill for a UUID that is about to be requested several times.
    /// A joining player is asked for by the avatar loader, the jiggle collider setup and the
    /// nameplate's block state; warming from the avatar load thread turns the first of those
    /// from a disc read plus JSON parse into a dictionary hit.
    /// <para>Failures are swallowed on purpose, and that is what makes this safe to call off the
    /// main thread: the warm has no synchronization context, so the read's continuation — the
    /// <see cref="JsonUtility.FromJson{T}(string)"/> of a plain struct — resumes on a pool thread.
    /// If that were ever rejected it throws BEFORE <c>cache[key]</c> is written while the
    /// <c>finally</c> still releases the per-file semaphore, so the awaited request every caller
    /// still makes redoes the work correctly on their own thread. Worst case is today's
    /// behaviour.</para>
    /// </summary>
    public static void Warm(string uuid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(uuid) || cache.ContainsKey(Sanitize(uuid)))
            {
                return;
            }
            _ = RequestPlayerSettings(uuid).ContinueWith(
                static t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception e)
        {
            // Nothing this method can fail at is worth losing the caller's work over — a join
            // decode calls it mid-flight, and a dropped exception here would drop that player.
            BasisDebug.LogWarning($"Player settings warm for {uuid} failed: {e.Message}");
        }
    }

    public static async Task<BasisPlayerSettingsData> RequestPlayerSettings(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            BasisDebug.LogError("Missing UUID");
            return default;
        }

        var key = Sanitize(uuid);

        // Hot path: already cached. No disk, no thread hop — critical during join storms where every
        // remote player is requested by several subsystems (nameplate, audio, avatar, init).
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var path = GetPath(key);
        var sem = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();

        try
        {
            if (cache.TryGetValue(key, out cached))
            {
                return cached;
            }

            // Read off the main thread; only the (trivial) JSON parse resumes on it. A missing file
            // returns defaults WITHOUT writing — the record is created only when a setting actually
            // changes (SetPlayerSettings), so joining never touches the disk for write.
            string json = await BasisTasks.Run(() => File.Exists(path) ? File.ReadAllText(path) : null);

            BasisPlayerSettingsData data = default;
            bool valid = false;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    BasisPlayerSettingsData loaded = JsonUtility.FromJson<BasisPlayerSettingsData>(json);
                    if (loaded.Version != 0)
                    {
                        if (string.IsNullOrEmpty(loaded.UUID)) loaded.UUID = uuid;
                        loaded.UpgradeSchema();
                        data = loaded;
                        valid = true;
                    }
                }
                catch (Exception e)
                {
                    BasisDebug.LogWarning($"Player settings at {path} were unreadable ({e.Message}); using defaults.");
                }
            }

            if (!valid) data = CreateDefaults(uuid);

            cache[key] = data;
            return data;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Synchronous cache probe for per-frame paths that must never await. Returns false
    /// until <see cref="RequestPlayerSettings"/> has populated the cache for this UUID.
    /// </summary>
    public static bool TryGetCached(string uuid, out BasisPlayerSettingsData data)
    {
        data = default;
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return false;
        }
        return cache.TryGetValue(Sanitize(uuid), out data);
    }

    public static async Task SetPlayerSettings(BasisPlayerSettingsData settings)
    {
        if (string.IsNullOrWhiteSpace(settings.UUID))
        {
            BasisDebug.LogError("Invalid Settings");
            return;
        }

        settings.VolumeLevel = Mathf.Clamp(settings.VolumeLevel, 0f, 5f);

        var key = Sanitize(settings.UUID);
        var path = GetPath(key);

        var sem = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();

        try
        {
            cache[key] = settings;
            string json = JsonUtility.ToJson(settings, false);
            await BasisTasks.Run(() => File.WriteAllText(path, json));
        }
        finally
        {
            sem.Release();
        }
    }

    // --- internals ---

    private static BasisPlayerSettingsData CreateDefaults(string uuid)
    {
        return new BasisPlayerSettingsData(uuid, 1.0f, true, true);
    }

    private static string GetPath(string key)
    {
        return Path.Combine(Dir, $"{key}.json");
    }

    private static string Sanitize(string s)
    {
        foreach (var c in InvalidChars)
        {
            s = s.Replace(c, '_');
        }

        return s;
    }
}
