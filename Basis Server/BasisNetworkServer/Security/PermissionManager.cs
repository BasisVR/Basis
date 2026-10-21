using Basis.Network.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Xml;
using static SerializableBasis;
namespace BasisPermissions
{
    // =========================
    // Data Model
    // =========================
    public sealed class PermissionUser
    {
        public string Uuid = "";
        // Raw nodes assigned to user (can include "-node" deny entries)
        public HashSet<string> Nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Group memberships
        public HashSet<string> Groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PermissionGroup
    {
        public string Name = "";
        // Raw nodes assigned to group (can include "-node" deny entries)
        public HashSet<string> Nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Parent group inheritance
        public HashSet<string> Parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PermissionStore
    {
        public Dictionary<string, PermissionUser> Users = new Dictionary<string, PermissionUser>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PermissionGroup> Groups = new Dictionary<string, PermissionGroup>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> SeededDefaults = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    }
    public sealed class EffectivePermissions
    {
        // Decision table: node => allow(true) / deny(false)
        // Contains exact nodes and wildcard nodes ("a.*", "*") after inheritance resolution.
        private readonly Dictionary<string, bool> _decisions;

        internal EffectivePermissions(Dictionary<string, bool> decisions)
        {
            _decisions = decisions;
        }

        // O(depth) check: a.b.c -> a.b.* -> a.* -> *
        public bool Has(string node)
        {
            if (string.IsNullOrWhiteSpace(node))
            {
                return false;
            }

            node = node.Trim();

            // exact
            if (_decisions.TryGetValue(node, out bool exact))
            {
                return exact;
            }

            // climb wildcards
            int idx = node.Length;
            while (true)
            {
                idx = node.LastIndexOf('.', idx - 1);
                if (idx < 0) break;

                string wildcard = node.Substring(0, idx) + ".*";
                if (_decisions.TryGetValue(wildcard, out bool w))
                {
                    return w;
                }
            }

            // global wildcard
            return _decisions.TryGetValue("*", out bool star) ? star : false;
        }

        // Returns all nodes with allow(true) in the decision map.
        // Note: This returns effective *rules*, not "expanded" node lists (expansion requires a registry of possible nodes).
        public IReadOnlyCollection<string> GetAllAllowedRules()
        {
            List<string> allowed = new List<string>(_decisions.Count);
            foreach (var kv in _decisions)
            {
                if (kv.Value)
                {
                    allowed.Add(kv.Key);
                }
            }

            return allowed;
        }

        public IReadOnlyCollection<string> GetAllDeniedRules()
        {
            List<string> denied = new List<string>(_decisions.Count);
            foreach (var kv in _decisions)
            {
                if (!kv.Value)
                {
                    denied.Add(kv.Key);
                }
            }

            return denied;
        }

        // For debugging/admin UIs
        public IReadOnlyDictionary<string, bool> GetDecisionMap() => _decisions;
    }

    // ============================================
    // Permission Manager (Thread-safe + Cached)
    // ============================================
    public sealed class PermissionManager
    {
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private PermissionStore _store = new PermissionStore();

        // Cache: uuid -> (version, effective perms)
        // Concurrent so the hit path in GetEffective can read without touching _lock: an
        // upgradeable read admits exactly one thread, which serialised every permission check
        // across all receive threads even when the answer was already cached.
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private int _version = 0;

        // File path for persistence
        private string _xmlPath = "permissions.xml";

        // Save debounce to avoid writing on every tiny change
        private readonly object _saveGate = new object();
        private Timer _saveTimer;
        private volatile bool _dirty = false;

        // Tune this
        public int SaveDebounceMs { get; set; } = 750;

        /// <summary>
        /// Fired after a permission mutation, outside the write lock.
        /// Argument is the affected UUID, or null when a group change affects all users.
        /// </summary>
        public Action<string> OnPermissionsChanged;

        // -------------
        // Public API
        // -------------
        public void SetXmlPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path cannot be empty");
            _xmlPath = path;
        }

        public string GetXmlPath() => _xmlPath;
        public void LoadFromXml(string pathOverride = null)
        {
            string path = pathOverride ?? _xmlPath;
            PermissionStore loaded = PermissionXml.Load(path);

            _lock.EnterWriteLock();
            try
            {
                _store = loaded;
                _version++;
                _cache.Clear();
                _dirty = false;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public void SaveToXml(string pathOverride = null)
        {
            string path = pathOverride ?? _xmlPath;
            PermissionStore snapshot = Snapshot();
            PermissionXml.Save(path, snapshot);

            _dirty = false;
        }

        // Debounced save: call this after edits
        public void SaveToXmlDebounced()
        {
            _dirty = true;
            lock (_saveGate)
            {
                if (_saveTimer == null)
                {
                    _saveTimer = new Timer(_ => DebouncedSaveTick(), null, SaveDebounceMs, Timeout.Infinite);
                }
                else
                {
                    _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
                }
            }
        }

        public void FlushPendingSave()
        {
            lock (_saveGate)
            {
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            if (_dirty) SaveToXml();
        }

        private void DebouncedSaveTick()
        {
            try
            {
                if (_dirty)
                    SaveToXml();
            }
            catch
            {
                // Swallow: you may want to log this
            }
        }

        public bool Has(string uuid, string node)
        {
            return GetEffective(uuid).Has(node);
        }

        /// <summary>
        /// True when the user belongs to <paramref name="group"/>, directly or through the parent
        /// chain of a group they are in. Walks the same edges <see cref="ApplyGroupRecursive_NoLock"/>
        /// does, so a role check and a node check never disagree about inheritance, and treats a
        /// user absent from the store as a member of the implicit "default" group for the same
        /// reason <see cref="BuildEffective_NoLock"/> does.
        ///
        /// A membership naming a group with no row still counts: the assignment on the user is the
        /// fact being asked about, and one can name a group that was never defined — AddUserToGroup
        /// does not create it, and hand-edited xml need not either. (Deleting a group is not such a
        /// path: DeleteGroup scrubs the membership off every user.)
        /// </summary>
        public bool IsInGroup(string uuid, string group)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(group))
            {
                return false;
            }

            group = group.Trim();

            _lock.EnterReadLock();
            try
            {
                HashSet<string> memberships = _store.Users.TryGetValue(uuid, out PermissionUser user)
                    ? user.Groups
                    : ImplicitDefaultGroups;

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string g in memberships)
                {
                    if (InheritsGroup_NoLock(g, group, visited))
                    {
                        return true;
                    }
                }

                return false;
            }
            finally { _lock.ExitReadLock(); }
        }

        private static readonly HashSet<string> ImplicitDefaultGroups =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "default" };

        private bool InheritsGroup_NoLock(string groupName, string target, HashSet<string> visited)
        {
            if (string.IsNullOrWhiteSpace(groupName))
            {
                return false;
            }

            groupName = groupName.Trim();

            // Also the cycle guard: a group graph with a loop would otherwise recurse forever.
            if (!visited.Add(groupName))
            {
                return false;
            }

            if (string.Equals(groupName, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!_store.Groups.TryGetValue(groupName, out PermissionGroup group))
            {
                return false;
            }

            foreach (string parent in group.Parents)
            {
                if (InheritsGroup_NoLock(parent, target, visited))
                {
                    return true;
                }
            }

            return false;
        }

        public IReadOnlyCollection<string> GetAllAllowedRules(string uuid)
        {
            return GetEffective(uuid).GetAllAllowedRules();
        }

        public IReadOnlyCollection<string> GetAllDeniedRules(string uuid)
        {
            return GetEffective(uuid).GetAllDeniedRules();
        }

        public bool TryGetUser(string uuid, out PermissionUser user)
        {
            _lock.EnterReadLock();
            try { return _store.Users.TryGetValue(uuid, out user!); }
            finally { _lock.ExitReadLock(); }
        }

        public bool TryGetGroup(string name, out PermissionGroup group)
        {
            _lock.EnterReadLock();
            try { return _store.Groups.TryGetValue(name, out group!); }
            finally { _lock.ExitReadLock(); }
        }

        // Create or get user
        public PermissionUser GetOrCreateUser(string uuid)
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                if (_store.Users.TryGetValue(uuid, out var u))
                {
                    return u;
                }

                _lock.EnterWriteLock();
                try
                {
                    if (_store.Users.TryGetValue(uuid, out u))
                    {
                        return u;
                    }

                    u = new PermissionUser { Uuid = uuid };
                    u.Groups.Add("default");
                    _store.Users[uuid] = u;
                    TouchUser(uuid);
                    return u;
                }
                finally { _lock.ExitWriteLock(); }
            }
            finally { _lock.ExitUpgradeableReadLock(); }
        }

        // Create or get group
        public PermissionGroup GetOrCreateGroup(string name)
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                if (_store.Groups.TryGetValue(name, out var g))
                    return g;

                _lock.EnterWriteLock();
                try
                {
                    if (_store.Groups.TryGetValue(name, out g))
                        return g;

                    g = new PermissionGroup { Name = name };
                    _store.Groups[name] = g;
                    TouchAll();
                    return g;
                }
                finally { _lock.ExitWriteLock(); }
            }
            finally { _lock.ExitUpgradeableReadLock(); }
        }

        // Mutators (invalidate cache)
        public void AddUserNode(string uuid, string node)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(node))
            {
                return;
            }

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                var u = GetOrCreateUser_NoLock(uuid);
                if (u.Nodes.Add(node.Trim()))
                {
                    TouchUser(uuid);
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(uuid);
        }

        public void RemoveUserNode(string uuid, string node)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(node)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                if (_store.Users.TryGetValue(uuid, out var u) && u.Nodes.Remove(node.Trim()))
                {
                    TouchUser(uuid);
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(uuid);
        }

        public void AddUserToGroup(string uuid, string group)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(group)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                var u = GetOrCreateUser_NoLock(uuid);
                if (u.Groups.Add(group.Trim()))
                {
                    TouchUser(uuid);
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(uuid);
        }

        public void RemoveUserFromGroup(string uuid, string group)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(group)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                if (_store.Users.TryGetValue(uuid, out var u) && u.Groups.Remove(group.Trim()))
                {
                    TouchUser(uuid);
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(uuid);
        }

        public void AddGroupNode(string groupName, string node)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(node)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                var g = GetOrCreateGroup_NoLock(groupName);
                if (g.Nodes.Add(node.Trim()))
                {
                    TouchAll();
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(null);
        }

        public void RemoveGroupNode(string groupName, string node)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(node)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                if (_store.Groups.TryGetValue(groupName, out var g) && g.Nodes.Remove(node.Trim()))
                {
                    TouchAll();
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(null);
        }

        public void AddGroupParent(string groupName, string parentName)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(parentName)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                var g = GetOrCreateGroup_NoLock(groupName);
                if (g.Parents.Add(parentName.Trim()))
                {
                    TouchAll();
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(null);
        }

        public void RemoveGroupParent(string groupName, string parentName)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(parentName)) return;

            bool changed = false;
            _lock.EnterWriteLock();
            try
            {
                if (_store.Groups.TryGetValue(groupName, out var g) && g.Parents.Remove(parentName.Trim()))
                {
                    TouchAll();
                    changed = true;
                }
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            if (changed) OnPermissionsChanged?.Invoke(null);
        }

        public bool DeleteGroup(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName)) return false;

            _lock.EnterWriteLock();
            try
            {
                if (!_store.Groups.Remove(groupName))
                    return false;

                // Remove this group from all users that reference it
                foreach (var u in _store.Users.Values)
                    u.Groups.Remove(groupName);

                // Remove this group as a parent from other groups
                foreach (var g in _store.Groups.Values)
                    g.Parents.Remove(groupName);

                TouchAll();
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
            OnPermissionsChanged?.Invoke(null);
            return true;
        }

        // Snapshot store for saving or admin viewing
        public PermissionStore Snapshot()
        {
            _lock.EnterReadLock();
            try
            {
                var copy = new PermissionStore();

                foreach (var u in _store.Users)
                {
                    copy.Users[u.Key] = new PermissionUser
                    {
                        Uuid = u.Value.Uuid,
                        Nodes = new HashSet<string>(u.Value.Nodes, StringComparer.OrdinalIgnoreCase),
                        Groups = new HashSet<string>(u.Value.Groups, StringComparer.OrdinalIgnoreCase)
                    };
                }

                foreach (var g in _store.Groups)
                {
                    copy.Groups[g.Key] = new PermissionGroup
                    {
                        Name = g.Value.Name,
                        Nodes = new HashSet<string>(g.Value.Nodes, StringComparer.OrdinalIgnoreCase),
                        Parents = new HashSet<string>(g.Value.Parents, StringComparer.OrdinalIgnoreCase)
                    };
                }

                foreach (var s in _store.SeededDefaults)
                {
                    copy.SeededDefaults[s.Key] = new HashSet<string>(s.Value, StringComparer.OrdinalIgnoreCase);
                }

                return copy;
            }
            finally { _lock.ExitReadLock(); }
        }
        private struct CacheEntry
        {
            public int Version;
            public EffectivePermissions Perms;
        }

        private void TouchUser(string uuid)
        {
            _version++;
            _cache.TryRemove(uuid, out _);
            _dirty = true;
        }

        private void TouchAll()
        {
            _version++;
            _cache.Clear();
            _dirty = true;
        }

        public void EvictUserCache(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            _lock.EnterWriteLock();
            try
            {
                _cache.TryRemove(uuid, out _);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private EffectivePermissions GetEffective(string uuid)
        {
            // Lock-free on a hit: the entry pairs its Version with the perms it was built
            // from, so a stale entry fails the version compare and falls through to the
            // locked rebuild. Only a miss (or an invalidated entry) pays for the write lock.
            if (_cache.TryGetValue(uuid, out var entry) && entry.Version == Volatile.Read(ref _version))
                return entry.Perms;

            _lock.EnterWriteLock();
            try
            {
                if (_cache.TryGetValue(uuid, out entry) && entry.Version == _version)
                    return entry.Perms;

                var built = BuildEffective_NoLock(uuid);
                _cache[uuid] = new CacheEntry { Version = _version, Perms = built };
                return built;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public EffectivePermissions BuildEffective_NoLock(string uuid)
        {
            // deny-wins decision table
            var decisions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // If the user doesn't exist in the store, treat them as part of the implicit "default" group.
            // This makes <Group name="default"> behave like an actual default.
            PermissionUser user;
            if (!_store.Users.TryGetValue(uuid, out user))
            {
                user = new PermissionUser { Uuid = uuid };
                user.Groups.Add("default"); // ✅ implicit default group
            }

            // 1) Apply groups w/ inheritance (parents first)
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in user.Groups)
                ApplyGroupRecursive_NoLock(g, visited, decisions);

            // 2) Apply user nodes last (user overrides groups; deny still wins)
            ApplyRawNodes(user.Nodes, decisions);

            return new EffectivePermissions(decisions);
        }

        private void ApplyGroupRecursive_NoLock(string groupName, HashSet<string> visited, Dictionary<string, bool> decisions)
        {
            if (string.IsNullOrWhiteSpace(groupName))
                return;

            groupName = groupName.Trim();

            if (!visited.Add(groupName))
                return;

            if (!_store.Groups.TryGetValue(groupName, out var group))
                return;

            // Parents first, then this group
            foreach (var p in group.Parents)
                ApplyGroupRecursive_NoLock(p, visited, decisions);

            ApplyRawNodes(group.Nodes, decisions);
        }

        // Raw nodes may include "-node" denies.
        // Deny always wins over allow.
        private static void ApplyRawNodes(HashSet<string> rawNodes, Dictionary<string, bool> decisions)
        {
            foreach (var raw in rawNodes)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string node = raw.Trim();
                bool allow = true;

                if (node.Length > 0 && node[0] == '-')
                {
                    allow = false;
                    node = node.Substring(1).Trim();
                    if (node.Length == 0) continue;
                }

                if (decisions.TryGetValue(node, out bool existing))
                {
                    if (!existing)
                    {
                        // already denied, never overwrite
                        continue;
                    }

                    // existing allow can be overridden by deny
                    if (!allow)
                        decisions[node] = false;
                    else
                        decisions[node] = true;
                }
                else
                {
                    decisions[node] = allow;
                }
            }
        }

        private PermissionUser GetOrCreateUser_NoLock(string uuid)
        {
            if (_store.Users.TryGetValue(uuid, out var u))
                return u;

            u = new PermissionUser { Uuid = uuid };
            u.Groups.Add("default");
            _store.Users[uuid] = u;
            return u;
        }

        private PermissionGroup GetOrCreateGroup_NoLock(string name)
        {
            if (_store.Groups.TryGetValue(name, out var g))
                return g;

            g = new PermissionGroup { Name = name };
            _store.Groups[name] = g;
            return g;
        }

        // -----------------------
        // Convenience: default setup
        // -----------------------
        private static readonly string[] DefaultGroupNodes =
        {
            PermNodes.help,
            PermNodes.ResourceLoadProp, PermNodes.ResourceUnloadProp,
            PermNodes.ResourceLoadAvatar, PermNodes.ResourceUnloadAvatar,
            PermNodes.ResourceLoadWorld, PermNodes.ResourceUnloadWorld,
            PermNodes.OwnershipTransfer, PermNodes.OwnershipRemove, PermNodes.OwnershipGet,
            PermNodes.ContentShareDelete, PermNodes.ContentShareCreate,
            PermNodes.StorageUse,
        };

        private static readonly string[] ModeratorGroupNodes =
        {
            PermNodes.PlayerModeration,
            PermNodes.ModerationBan, PermNodes.ModerationKick, PermNodes.ModerationIpBan,
            PermNodes.ModerationUnban, PermNodes.ModerationUnbanIp,
            PermNodes.ModerationMessage, PermNodes.ModerationMessageAll,
            PermNodes.ModerationTeleport, PermNodes.ModerationAnnounce,
            PermNodes.ModerationGlobalLock, PermNodes.ModerationHeadlessAudio, PermNodes.ModerationOpusBitrate,
            PermNodes.ModerationFullQualityBroadcast, PermNodes.ModerationForceAvatar, PermNodes.ModerationLocomotion,
            PermNodes.ModerationMute, PermNodes.ModerationRename,
            PermNodes.PermissionsView,
            PermNodes.ResourceLockBypassAvatar, PermNodes.ResourceLockBypassProp,
            PermNodes.ResourceLockBypassWorld, PermNodes.ResourceLockBypassServer,
            PermNodes.ChatLockBypass, PermNodes.VoiceLockBypass,
        };

        private static readonly string[] AdminGroupNodes = { PermNodes.All };

        public void EnsureDefaults()
        {
            _lock.EnterWriteLock();
            try
            {
                SeedGroup_NoLock("default", null, DefaultGroupNodes);
                SeedGroup_NoLock("moderator", "default", ModeratorGroupNodes);
                SeedGroup_NoLock("admin", "moderator", AdminGroupNodes);

                _version++;
                _cache.Clear();
                _dirty = true;
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
        }

        private void SeedGroup_NoLock(string name, string parent, string[] nodes)
        {
            if (!_store.SeededDefaults.TryGetValue(name, out HashSet<string> seeded))
            {
                seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _store.SeededDefaults[name] = seeded;
            }

            if (!_store.Groups.TryGetValue(name, out PermissionGroup group))
            {
                group = new PermissionGroup { Name = name };
                if (parent != null)
                    group.Parents.Add(parent);
                _store.Groups[name] = group;
                seeded.Clear();
            }

            foreach (string node in nodes)
            {
                if (seeded.Add(node) && !group.Nodes.Contains("-" + node))
                    group.Nodes.Add(node);
            }
        }

        // =========================================
        // XML Persistence (fast XmlReader/XmlWriter)
        // =========================================
        public static class PermissionXml
        {
            // XML format:
            // <Permissions>
            //   <Groups>
            //     <Group name="default">
            //       <Parent name="base"/>
            //       <Node value="basis.command.help"/>
            //       <Node value="-basis.command.kick"/>
            //     </Group>
            //   </Groups>
            //   <Users>
            //     <User uuid="abc">
            //       <Group name="admin"/>
            //       <Node value="basis.resource.load"/>
            //     </User>
            //   </Users>
            // </Permissions>

            /// <summary>
            /// Node names are stored verbatim in permissions.xml, so renaming one orphans every
            /// grant an operator already wrote. Rewrite retired spellings on the way in - a
            /// negated node ("-basis.moderation.shout") has to migrate too, or a deny silently
            /// stops denying, which is the dangerous direction.
            /// </summary>
            private static string MigrateLegacyNode(string node)
            {
                const string legacy = "basis.moderation.shout";
                if (node.Equals(legacy, StringComparison.OrdinalIgnoreCase))
                    return PermNodes.ModerationAnnounce;
                if (node.Length == legacy.Length + 1 && node[0] == '-'
                    && node.AsSpan(1).Equals(legacy.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    return "-" + PermNodes.ModerationAnnounce;
                return node;
            }

            public static PermissionStore Load(string path)
            {
                var store = new PermissionStore();
                if (!File.Exists(path))
                    return store;

                var settings = new XmlReaderSettings
                {
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    DtdProcessing = DtdProcessing.Prohibit
                };

                using var fs = File.OpenRead(path);
                using var xr = XmlReader.Create(fs, settings);

                PermissionGroup currentGroupDef = null;
                PermissionUser currentUser = null;
                HashSet<string> currentSeeded = null;

                // Context flags
                bool inGroups = false;
                bool inUsers = false;
                bool inSeeded = false;

                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element)
                    {
                        switch (xr.Name)
                        {
                            case "Groups":
                                inGroups = true; inUsers = false; inSeeded = false;
                                break;

                            case "Users":
                                inUsers = true; inGroups = false; inSeeded = false;
                                break;

                            case "SeededDefaults":
                                inSeeded = true; inGroups = false; inUsers = false;
                                break;

                            case "Group":
                                {
                                    // "Group" can mean:
                                    // - group definition when in <Groups>
                                    // - group membership when inside <User> and in <Users>
                                    string name = xr.GetAttribute("name") ?? "";

                                    if (inGroups)
                                    {
                                        currentGroupDef = new PermissionGroup { Name = name };
                                        store.Groups[name] = currentGroupDef;
                                    }
                                    else if (inUsers && currentUser != null)
                                    {
                                        if (!string.IsNullOrWhiteSpace(name))
                                            currentUser.Groups.Add(name.Trim());
                                    }
                                    else if (inSeeded)
                                    {
                                        if (!store.SeededDefaults.TryGetValue(name, out currentSeeded))
                                        {
                                            currentSeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                            store.SeededDefaults[name] = currentSeeded;
                                        }
                                    }
                                    break;
                                }

                            case "User":
                                {
                                    string uuid = xr.GetAttribute("uuid") ?? "";
                                    currentUser = new PermissionUser { Uuid = uuid };
                                    store.Users[uuid] = currentUser;
                                    break;
                                }

                            case "Parent":
                                {
                                    if (currentGroupDef != null)
                                    {
                                        string parent = xr.GetAttribute("name") ?? "";
                                        if (!string.IsNullOrWhiteSpace(parent))
                                            currentGroupDef.Parents.Add(parent.Trim());
                                    }
                                    break;
                                }

                            case "Node":
                                {
                                    string node = xr.GetAttribute("value") ?? "";
                                    if (string.IsNullOrWhiteSpace(node))
                                        break;

                                    node = MigrateLegacyNode(node.Trim());

                                    if (inGroups && currentGroupDef != null)
                                        currentGroupDef.Nodes.Add(node);
                                    else if (inUsers && currentUser != null)
                                        currentUser.Nodes.Add(node);
                                    else if (inSeeded && currentSeeded != null)
                                        currentSeeded.Add(node);

                                    break;
                                }
                        }
                    }
                    else if (xr.NodeType == XmlNodeType.EndElement)
                    {
                        switch (xr.Name)
                        {
                            case "Group":
                                // only clear group definition context (not user group membership)
                                if (inGroups)
                                    currentGroupDef = null;
                                else if (inSeeded)
                                    currentSeeded = null;
                                break;

                            case "User":
                                currentUser = null;
                                break;

                            case "Groups":
                                inGroups = false;
                                currentGroupDef = null;
                                break;

                            case "Users":
                                inUsers = false;
                                currentUser = null;
                                break;

                            case "SeededDefaults":
                                inSeeded = false;
                                currentSeeded = null;
                                break;
                        }
                    }
                }

                return store;
            }

            public static void Save(string path, PermissionStore store)
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    NewLineHandling = NewLineHandling.Entitize,
                    CloseOutput = true
                };

                using var fs = File.Create(path);
                using var xw = XmlWriter.Create(fs, settings);

                xw.WriteStartDocument();
                xw.WriteStartElement("Permissions");

                // Groups
                xw.WriteStartElement("Groups");
                foreach (var g in store.Groups.Values)
                {
                    xw.WriteStartElement("Group");
                    xw.WriteAttributeString("name", g.Name);

                    foreach (var p in g.Parents)
                    {
                        xw.WriteStartElement("Parent");
                        xw.WriteAttributeString("name", p);
                        xw.WriteEndElement();
                    }

                    foreach (var n in g.Nodes)
                    {
                        xw.WriteStartElement("Node");
                        xw.WriteAttributeString("value", n);
                        xw.WriteEndElement();
                    }

                    xw.WriteEndElement(); // Group
                }
                xw.WriteEndElement(); // Groups

                // Users
                xw.WriteStartElement("Users");
                foreach (var u in store.Users.Values)
                {
                    xw.WriteStartElement("User");
                    xw.WriteAttributeString("uuid", u.Uuid);

                    foreach (var g in u.Groups)
                    {
                        xw.WriteStartElement("Group");
                        xw.WriteAttributeString("name", g);
                        xw.WriteEndElement();
                    }

                    foreach (var n in u.Nodes)
                    {
                        xw.WriteStartElement("Node");
                        xw.WriteAttributeString("value", n);
                        xw.WriteEndElement();
                    }

                    xw.WriteEndElement(); // User
                }
                xw.WriteEndElement(); // Users

                xw.WriteStartElement("SeededDefaults");
                foreach (var s in store.SeededDefaults)
                {
                    xw.WriteStartElement("Group");
                    xw.WriteAttributeString("name", s.Key);

                    foreach (var n in s.Value)
                    {
                        xw.WriteStartElement("Node");
                        xw.WriteAttributeString("value", n);
                        xw.WriteEndElement();
                    }

                    xw.WriteEndElement(); // Group
                }
                xw.WriteEndElement(); // SeededDefaults

                xw.WriteEndElement(); // Permissions
                xw.WriteEndDocument();
            }
        }
        public static class PermissionIntegration
        {
            // Global singleton-style instance
            public static readonly PermissionManager Manager = new PermissionManager();

            // Per-player metadata stored at connect, used to rebuild ServerMetaDataMessage on permission changes
            private static readonly ConcurrentDictionary<string, ClientMetaDataMessage> _playerMeta =
                new ConcurrentDictionary<string, ClientMetaDataMessage>(StringComparer.OrdinalIgnoreCase);

            // Call at server startup
            public static void Init(string xmlPath)
            {
                Manager.SetXmlPath(xmlPath);

                // Load existing
                Manager.LoadFromXml();

                // Optional defaults if file was empty/nonexistent
                Manager.EnsureDefaults();

                // Ensure saved
                Manager.SaveToXmlDebounced();

                // Init runs on every StartServer against a process-lifetime Manager; -= first so a
                // restart cannot stack a second subscription (each would resend every update).
                Manager.OnPermissionsChanged -= HandlePermissionsChanged;
                Manager.OnPermissionsChanged += HandlePermissionsChanged;
            }
            public static void InitWithoutDisc()
            {
                // Optional defaults if file was empty/nonexistent
                Manager.EnsureDefaults();

                Manager.OnPermissionsChanged -= HandlePermissionsChanged;
                Manager.OnPermissionsChanged += HandlePermissionsChanged;
            }

            /// <summary>
            /// Store player metadata when they connect so we can rebuild ServerMetaDataMessage later.
            /// </summary>
            public static void StorePlayerMeta(string uuid, ClientMetaDataMessage meta)
            {
                _playerMeta[uuid] = meta;
            }

            /// <summary>
            /// Remove stored metadata when a player disconnects.
            /// </summary>
            public static void RemovePlayerMeta(string uuid)
            {
                _playerMeta.TryRemove(uuid, out _);
            }

            public static bool TryGetPlayerMeta(string uuid, out ClientMetaDataMessage meta)
            {
                return _playerMeta.TryGetValue(uuid, out meta);
            }

            public static void EvictPermissionCache(string uuid)
            {
                Manager.EvictUserCache(uuid);
            }

            public static bool HasValidRequirement(string uuid, string permNode)
            {
                // Has() resolves '*' itself as its last fallthrough, so a wildcard holder is still
                // allowed anything they have not been explicitly denied. Re-checking '*' here and
                // OR-ing it in would resurrect exactly the nodes a '-node' deny entry just refused.
                return Manager.Has(uuid, permNode);
            }
            public static bool HasValidRequirement(NetPeer peer, string permNode)
            {
                if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid))
                {
                    if (Manager.Has(uuid, permNode))
                    {
                        return true;
                    }
                    else
                    {
                        BNL.LogError($"Permission not found for UUID: {uuid} for perm node {permNode}");
                        return false;
                    }
                }
                else
                {
                    BNL.LogError($"UUID not found for peer: {peer.Id} ");
                    return false;
                }
            }

            private static void HandlePermissionsChanged(string uuid)
            {
                if (uuid != null)
                {
                    SendPermissionUpdate(uuid);
                }
                else
                {
                    // Group-level change: resend to all connected players
                    foreach (var kvp in _playerMeta)
                    {
                        SendPermissionUpdate(kvp.Key);
                    }
                }
            }

            /// <summary>
            /// Rebuild and resend ServerMetaDataMessage to a connected player with their current permissions.
            /// </summary>
            public static void SendPermissionUpdate(string uuid)
            {
                if (!_playerMeta.TryGetValue(uuid, out ClientMetaDataMessage meta))
                    return;

                if (!NetworkServer.AuthIdentity.UUIDToNetID(uuid, out int netId) ||
                    !NetworkServer.AuthenticatedPeers.TryGetValue(netId, out NetPeer peer))
                    return;

                Configuration config = NetworkServer.Configuration;
                ServerMetaDataMessage msg = new ServerMetaDataMessage
                {
                    ClientMetaDataMessage = meta,
                    SyncInterval = config.BSRSMillisecondDefaultInterval,
                    BaseMultiplier = config.BSRBaseMultiplier,
                    IncreaseRate = config.BSRSIncreaseRate,
                    SlowestSendRate = config.BSRSlowestSendRate,
                    PeerLimit = config.PeerLimit,
                    UplinkDeltaEnabled = config.EnableUplinkAvatarDelta,
                    ImageShareEgressMegabitsPerSecond = config.ImageShareEgressMegabitsPerSecond,
                    ImagePickupRangeMeters = Math.Max(0f, config.ImagePickupRangeMeters),
                    ServerUUID = config.ServerUUID,
                };
                msg.SetPermissions(Manager.GetAllAllowedRules(uuid), Manager.GetAllDeniedRules(uuid));

                NetDataWriter writer = NetworkServer.RentWriter();
                msg.Serialize(writer);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.metaDataChannel, DeliveryMethod.ReliableOrdered);
                NetworkServer.ReturnWriter(writer);
            }
        }
    }
}
