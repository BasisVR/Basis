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
    // Permission Node Constants
    // =========================
    public static class PermNodes
    {
        public const string All = "*";
        public const string help = "basis.command.help";
        public const string ServerStats = "basis.server.stats";

        public const string ResourceLoadWorld = "basis.resource.load.world";
        public const string ResourceUnloadWorld = "basis.resource.unload.world";

        public const string ResourceLoadProp = "basis.resource.load.prop";
        public const string ResourceUnloadProp = "basis.resource.unload.prop";

        public const string ResourceLoadAvatar = "basis.resource.load.avatar";
        public const string ResourceUnloadAvatar = "basis.resource.unload.avatar";

        // Bypass the global lockouts (BasisGlobalLockManager). Users without
        // the matching bypass node are blocked from loading while the lock is on.
        public const string ResourceLockBypassAvatar = "basis.resource.lockbypass.avatar";
        public const string ResourceLockBypassProp = "basis.resource.lockbypass.prop";
        public const string ResourceLockBypassWorld = "basis.resource.lockbypass.world";
        /// <summary>Bypass <c>ServersLocked</c> when initiating a server share.</summary>
        public const string ResourceLockBypassServer = "basis.resource.lockbypass.server";
        /// <summary>Bypass <c>TextChatLocked</c> — keep sending text chat while the global chat lock is on.</summary>
        public const string ChatLockBypass = "basis.chat.lockbypass";
        /// <summary>Bypass <c>VoiceChatLocked</c> — keep transmitting voice while the global voice lock is on.</summary>
        public const string VoiceLockBypass = "basis.voice.lockbypass";

        public const string OwnershipTransfer = "basis.ownership.transfer";
        public const string OwnershipRemove = "basis.ownership.remove";
        public const string OwnershipGet = "basis.ownership.get";

        public const string ContentShareDelete = "basis.contentshare.delete";
        public const string ContentShareCreate = "basis.contentshare.create";

        /// <summary>
        /// used to indicate that this persons actions are protected from interference
        /// </summary>
        public const string protection = "basis.protection";

        public const string ConfigurationEditor = "basis.configuration";

        public const string PlayerModeration = "basis.moderation";

        public const string ModerationBan = "basis.moderation.ban";
        public const string ModerationKick = "basis.moderation.kick";
        public const string ModerationIpBan = "basis.moderation.ipban";
        public const string ModerationUnban = "basis.moderation.unban";
        public const string ModerationUnbanIp = "basis.moderation.unbanip";
        public const string ModerationMessage = "basis.moderation.message";
        public const string ModerationMessageAll = "basis.moderation.messageall";
        public const string ModerationTeleport = "basis.moderation.teleport";
        public const string ModerationShout = "basis.moderation.shout";
        public const string ModerationGlobalLock = "basis.moderation.globallock";
        public const string ModerationHeadlessAudio = "basis.moderation.headlessaudio";
        public const string ModerationOpusBitrate = "basis.moderation.opusbitrate";
        public const string ModerationFullQualityBroadcast = "basis.moderation.fullqualitybroadcast";
        /// <summary>Add/remove UUIDs on the server's allow-list (separate from ban management).</summary>
        public const string ModerationAllowlist = "basis.moderation.whitelist";
        public const string AdminLogs = "basis.admin.logs";

        public const string PermissionsView = "basis.permissions.view";
        public const string PermissionsEdit = "basis.permissions.edit";
    }

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
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private int _version = 0;

        // File path for persistence
        private string _xmlPath = "permissions.xml";

        // Save debounce to avoid writing on every tiny change
        private readonly object _saveGate = new object();
        private Timer? _saveTimer;
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
        public void LoadFromXml(string? pathOverride = null)
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

        public void SaveToXml(string? pathOverride = null)
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
            _cache.Remove(uuid);
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
                _cache.Remove(uuid);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private EffectivePermissions GetEffective(string uuid)
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                if (_cache.TryGetValue(uuid, out var entry) && entry.Version == _version)
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
            finally { _lock.ExitUpgradeableReadLock(); }
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
        public void EnsureDefaults()
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_store.Groups.ContainsKey("default"))
                {
                    PermissionGroup def = new PermissionGroup { Name = "default" };
                    // existing example
                    def.Nodes.Add(PermNodes.help);
                    // ✅ default users should have these
                    def.Nodes.Add(PermNodes.ResourceLoadProp);
                    def.Nodes.Add(PermNodes.ResourceUnloadProp);

                    def.Nodes.Add(PermNodes.ResourceLoadAvatar);
                    def.Nodes.Add(PermNodes.ResourceUnloadAvatar);

                    def.Nodes.Add(PermNodes.ResourceLoadWorld);
                    def.Nodes.Add(PermNodes.ResourceUnloadWorld);

                    def.Nodes.Add(PermNodes.OwnershipTransfer);
                    def.Nodes.Add(PermNodes.OwnershipRemove);
                    def.Nodes.Add(PermNodes.OwnershipGet);

                    def.Nodes.Add(PermNodes.ContentShareDelete);
                    def.Nodes.Add(PermNodes.ContentShareCreate);

                    _store.Groups["default"] = def;
                }
                if (!_store.Groups.ContainsKey("moderator"))
                {
                    var adm = new PermissionGroup { Name = "moderator" };
                    adm.Parents.Add("default");
                    adm.Nodes.Add(PermNodes.ModerationBan);
                    adm.Nodes.Add(PermNodes.ModerationKick);
                    adm.Nodes.Add(PermNodes.ModerationIpBan);
                    adm.Nodes.Add(PermNodes.ModerationUnban);
                    adm.Nodes.Add(PermNodes.ModerationUnbanIp);
                    adm.Nodes.Add(PermNodes.ModerationMessage);
                    adm.Nodes.Add(PermNodes.ModerationMessageAll);
                    adm.Nodes.Add(PermNodes.ModerationTeleport);
                    adm.Nodes.Add(PermNodes.ModerationShout);
                    adm.Nodes.Add(PermNodes.ModerationGlobalLock);
                    adm.Nodes.Add(PermNodes.ModerationHeadlessAudio);
                    adm.Nodes.Add(PermNodes.ModerationOpusBitrate);
                    adm.Nodes.Add(PermNodes.ModerationFullQualityBroadcast);
                    adm.Nodes.Add(PermNodes.PermissionsView);

                    adm.Nodes.Add(PermNodes.ResourceLockBypassAvatar);
                    adm.Nodes.Add(PermNodes.ResourceLockBypassProp);
                    adm.Nodes.Add(PermNodes.ResourceLockBypassWorld);
                    adm.Nodes.Add(PermNodes.ResourceLockBypassServer);
                    adm.Nodes.Add(PermNodes.ChatLockBypass);
                    adm.Nodes.Add(PermNodes.VoiceLockBypass);

                    _store.Groups["moderator"] = adm;
                }
                if (!_store.Groups.ContainsKey("admin"))
                {
                    var adm = new PermissionGroup { Name = "admin" };
                    adm.Nodes.Add("*");
                    adm.Parents.Add("moderator");
                    _store.Groups["admin"] = adm;
                }

                _version++;
                _cache.Clear();
                _dirty = true;
            }
            finally { _lock.ExitWriteLock(); }

            SaveToXmlDebounced();
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

                PermissionGroup? currentGroupDef = null;
                PermissionUser? currentUser = null;

                // Context flags
                bool inGroups = false;
                bool inUsers = false;

                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element)
                    {
                        switch (xr.Name)
                        {
                            case "Groups":
                                inGroups = true; inUsers = false;
                                break;

                            case "Users":
                                inUsers = true; inGroups = false;
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

                                    node = node.Trim();

                                    if (inGroups && currentGroupDef != null)
                                        currentGroupDef.Nodes.Add(node);
                                    else if (inUsers && currentUser != null)
                                        currentUser.Nodes.Add(node);

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
                        }
                    }
                }

                return store;
            }

            public static void Save(string path, PermissionStore store)
            {
                string? dir = Path.GetDirectoryName(path);
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
