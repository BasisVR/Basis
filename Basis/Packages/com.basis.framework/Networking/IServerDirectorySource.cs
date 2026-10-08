using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Scripts.Networking
{
    public sealed class ServerDirectoryEntry
    {
        public string Id;
        public string SourceId;
        public string DisplayName;
        public string Description;
        public string Password;
        public bool HasPassword;
        public ConnectionTarget Target;
        public bool CanEdit;
        public bool CanRemove;
    }

    public interface IServerDirectorySource
    {
        string SourceId { get; }
        string DisplayName { get; }
        bool SupportsAdd { get; }
        Task<IReadOnlyList<ServerDirectoryEntry>> ListAsync(CancellationToken ct);
        Task RefreshAsync(CancellationToken ct);
        event Action SourceChanged;
    }

    [AutoStaticsCleanup]
    public static partial class BasisServerDirectoryRegistry
    {
        private static readonly List<IServerDirectorySource> _sources = new List<IServerDirectorySource>();
        [NoAutoStaticsCleanup] private static readonly object _lock = new object();

        public static event Action SourcesChanged;

        public static void Register(IServerDirectorySource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrEmpty(source.SourceId)) throw new ArgumentException("SourceId is required", nameof(source));

            lock (_lock)
            {
                foreach (IServerDirectorySource s in _sources)
                {
                    if (string.Equals(s.SourceId, source.SourceId, StringComparison.OrdinalIgnoreCase)) return;
                }
                _sources.Add(source);
            }
            try { SourcesChanged?.Invoke(); }
            catch (Exception ex) { BasisDebug.LogError($"SourcesChanged handler threw: {ex.Message}"); }
        }

        public static void Unregister(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId))
            {
                return;
            }

            bool removed = false;
            lock (_lock)
            {
                for (int i = _sources.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_sources[i].SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                    {
                        _sources.RemoveAt(i);
                        removed = true;
                    }
                }
            }
            if (!removed)
            {
                return;
            }

            try { SourcesChanged?.Invoke(); }
            catch (Exception ex) { BasisDebug.LogError($"SourcesChanged handler threw: {ex.Message}"); }
        }

        public static IReadOnlyList<IServerDirectorySource> Sources
        {
            get { lock (_lock) return _sources.ToArray(); }
        }

        public static IServerDirectorySource Get(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) return null;
            lock (_lock)
            {
                foreach (IServerDirectorySource s in _sources)
                {
                    if (string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)) return s;
                }
            }
            return null;
        }
    }
}
