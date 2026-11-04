using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace BasisNetworkServer.Storage
{
    public sealed class BasisFileStorageBackend : IBasisStorageBackend, IBasisStorageCatalog
    {
        public const string PlayersFolderName = "players", RecordExtension = ".bsr", TempExtension = ".tmp", LockFileName = ".lock";
        private readonly string root, players;
        private FileStream lockStream;
        public string Name => "file";
        public string Root => root;
        public BasisFileStorageBackend(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A storage folder is required.", nameof(root));
            this.root = Path.GetFullPath(root);
            players = Path.Combine(this.root, PlayersFolderName);
        }
        public void Open()
        {
            Directory.CreateDirectory(players);
            lockStream = new FileStream(Path.Combine(root, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            foreach (string folder in Directory.EnumerateDirectories(players))
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (string.Equals(Path.GetExtension(file), TempExtension, StringComparison.Ordinal)) File.Delete(file);
                }
            }
        }
        public bool TryLoad(string ownerKey, out byte[] blob) => TryLoad(ownerKey, out blob, out _);
        public bool TryLoad(string ownerKey, out byte[] blob, out long lastUsed)
        {
            blob = null;
            lastUsed = 0;
            string path = PathOf(ownerKey);
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long length = stream.Length;
                    if (length > BasisStorageRecord.MaxBlobBytes) throw new InvalidDataException($"Storage record {ownerKey} is {length} bytes, over the {BasisStorageRecord.MaxBlobBytes} byte ceiling.");
                    byte[] data = new byte[length];
                    for (int read = 0, got; read < data.Length; read += got)
                    {
                        if ((got = stream.Read(data, read, data.Length - read)) <= 0) throw new EndOfStreamException($"Storage record {ownerKey} ended early.");
                    }
                    blob = data;
                }
                lastUsed = UnixMilliseconds(File.GetLastWriteTimeUtc(path));
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        public bool TrySave(string ownerKey, byte[] blob, long expectedRevision, long revision)
        {
            if (blob == null || blob.Length > BasisStorageRecord.MaxBlobBytes) throw new ArgumentException("Storage record is missing or too large.", nameof(blob));
            string path = PathOf(ownerKey), temp = path + TempExtension;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(blob, 0, blob.Length);
                    stream.Flush(true);
                }
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        return true;
                    }
                    catch (IOException) when (attempt < 3)
                    {
                        Thread.Sleep(15 * attempt);
                    }
                }
            }
            catch
            {
                try { File.Delete(temp); } catch (Exception) { }
                throw;
            }
        }
        public bool TryDelete(string ownerKey, long expectedRevision)
        {
            string path = PathOf(ownerKey);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        public void Quarantine(string ownerKey)
        {
            string path = PathOf(ownerKey);
            if (File.Exists(path)) File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
        }
        public void Measure(out long records, out long bytes)
        {
            records = 0;
            bytes = 0;
            if (!Directory.Exists(players)) return;
            foreach (string folder in Directory.EnumerateDirectories(players))
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (!string.Equals(Path.GetExtension(file), RecordExtension, StringComparison.Ordinal) || !BasisStorageRecord.IsOwnerKey(Path.GetFileNameWithoutExtension(file))) continue;
                    records++;
                    bytes += new FileInfo(file).Length;
                }
            }
        }
        public void Touch(string ownerKey, long lastUsed)
        {
            string path = PathOf(ownerKey);
            try
            {
                if (File.Exists(path) && UnixMilliseconds(File.GetLastWriteTimeUtc(path)) < lastUsed) File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(lastUsed).UtcDateTime);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        public void List(long usedBefore, Func<BasisStorageRecordInfo, bool> visit)
        {
            DirectoryInfo folder = new DirectoryInfo(players);
            if (!folder.Exists) return;
            foreach (DirectoryInfo shard in folder.EnumerateDirectories())
            {
                foreach (FileInfo file in shard.EnumerateFiles("*" + RecordExtension))
                {
                    if (!string.Equals(file.Extension, RecordExtension, StringComparison.Ordinal)) continue;
                    string ownerKey = Path.GetFileNameWithoutExtension(file.Name);
                    if (!BasisStorageRecord.IsOwnerKey(ownerKey)) continue;
                    long lastUsed = UnixMilliseconds(file.LastWriteTimeUtc);
                    if (lastUsed < usedBefore && !visit(new BasisStorageRecordInfo { OwnerKey = ownerKey, Bytes = file.Length, LastUsed = lastUsed })) return;
                }
            }
        }
        public bool TryExpire(string ownerKey, long usedBefore, out long bytes)
        {
            bytes = 0;
            FileInfo file = new FileInfo(PathOf(ownerKey));
            if (!file.Exists || UnixMilliseconds(file.LastWriteTimeUtc) >= usedBefore) return false;
            long length = file.Length;
            try
            {
                file.Delete();
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            bytes = length;
            return true;
        }
        public void Dispose()
        {
            lockStream?.Dispose();
            lockStream = null;
        }
        private static long UnixMilliseconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        private string PathOf(string ownerKey)
        {
            if (!BasisStorageRecord.IsOwnerKey(ownerKey)) throw new ArgumentException("Storage owner keys are 64 lowercase hex characters.", nameof(ownerKey));
            return Path.Combine(players, ownerKey.Substring(0, 2), ownerKey + RecordExtension);
        }
    }
}
