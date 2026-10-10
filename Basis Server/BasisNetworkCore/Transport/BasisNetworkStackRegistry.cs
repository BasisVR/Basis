using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    public sealed class ServerProbeResult
    {
        public bool Reachable;
        public string Error;
        public bool TimedOut;
        public ushort Online;
        public ushort Max;
        public ushort ProtocolVersion;
        public string Name;
        public string Motd;
        public int RoundTripMs;
        public Dictionary<string, string> Extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// The IP address that successfully responded to the probe. Null when unreachable.
        /// Callers can use this to bypass DNS re-resolution and connect directly to the
        /// confirmed-reachable address family (supporting IPv6→IPv4 fallback).
        /// </summary>
        public IPAddress ResolvedAddress;
    }

    public delegate Task<ServerProbeResult> StackProbeDelegate(ConnectionTarget target, int timeoutMs, CancellationToken ct);
    public delegate IPeerIntroducer PeerIntroducerFactory(NetManager activeManager);
    public delegate IBasisP2PSocket P2PSocketFactory(EventBasedNetListener listener, BasisP2PIntroduced onIntroduced);

    public static class BasisNetworkStackRegistry
    {
        public const string LiteNetLibId = "litenetlib";
        public const string DefaultId = LiteNetLibId;

        public readonly struct StackInfo
        {
            public readonly string Id;
            public readonly string DisplayName;
            public StackInfo(string id, string displayName)
            {
                Id = id;
                DisplayName = displayName;
            }
        }

        public delegate NetManager NetManagerFactory(EventBasedNetListener listener, Configuration configuration);

        private sealed class Slot
        {
            public string Id;
            public string DisplayName;
            public NetManagerFactory Factory;
            public IConnectionTargetParser Parser;
            public StackProbeDelegate Probe;
            public Action Tick;
            public PeerIntroducerFactory IntroducerFactory;
            public P2PSocketFactory P2PSocketFactory;
            public Func<string, bool> AddressMatcher;
        }

        private static readonly Dictionary<string, Slot> _slots
            = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<StackInfo> _stacks = new List<StackInfo>();
        private static readonly object _lock = new object();
        private static string _activeStackId = string.Empty;

        public static event Action<string> ActiveStackChanged;

        private static Action[] _pumps = Array.Empty<Action>();

        public static void RegisterPump(Action pump)
        {
            if (pump == null) throw new ArgumentNullException(nameof(pump));
            lock (_lock)
            {
                if (Array.IndexOf(_pumps, pump) >= 0) return;
                Action[] next = new Action[_pumps.Length + 1];
                Array.Copy(_pumps, next, _pumps.Length);
                next[_pumps.Length] = pump;
                _pumps = next;
            }
        }

        public static List<string> ParseStackList(string ids)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(ids))
            {
                result.Add(DefaultId);
                return result;
            }
            foreach (string part in ids.Split(new[] { ',', ';', ' ', '+' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string id = part.Trim();
                if (id.Length == 0) continue;
                bool duplicate = false;
                foreach (string existing in result)
                {
                    if (string.Equals(existing, id, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate) result.Add(id);
            }
            if (result.Count == 0) result.Add(DefaultId);
            return result;
        }

        public static bool ContainsStack(string ids, string stackId)
        {
            foreach (string id in ParseStackList(ids))
            {
                if (string.Equals(id, stackId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static void Register(string id, string displayName, NetManagerFactory factory)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Stack id is required", nameof(id));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            lock (_lock)
            {
                if (_slots.ContainsKey(id)) return;
                _slots[id] = new Slot { Id = id, DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName, Factory = factory };
                _stacks.Add(new StackInfo(id, _slots[id].DisplayName));
            }
        }

        public static void RegisterParser(string stackId, IConnectionTargetParser parser)
        {
            if (string.IsNullOrEmpty(stackId)) throw new ArgumentException("Stack id is required", nameof(stackId));
            if (parser == null) throw new ArgumentNullException(nameof(parser));
            lock (_lock)
            {
                if (!_slots.TryGetValue(stackId, out Slot slot))
                {
                    BNL.LogWarning($"Cannot register parser for unknown stack '{stackId}'");
                    return;
                }
                slot.Parser = parser;
            }
        }

        public static void RegisterProbe(string stackId, StackProbeDelegate probe)
        {
            if (string.IsNullOrEmpty(stackId)) throw new ArgumentException("Stack id is required", nameof(stackId));
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            lock (_lock)
            {
                if (!_slots.TryGetValue(stackId, out Slot slot))
                {
                    BNL.LogWarning($"Cannot register probe for unknown stack '{stackId}'");
                    return;
                }
                slot.Probe = probe;
            }
        }

        public static void RegisterTick(string stackId, Action tick)
        {
            if (string.IsNullOrEmpty(stackId)) throw new ArgumentException("Stack id is required", nameof(stackId));
            if (tick == null) throw new ArgumentNullException(nameof(tick));
            lock (_lock)
            {
                if (!_slots.TryGetValue(stackId, out Slot slot))
                {
                    BNL.LogWarning($"Cannot register tick for unknown stack '{stackId}'");
                    return;
                }
                slot.Tick = tick;
            }
        }

        public static void RegisterIntroducerFactory(string stackId, PeerIntroducerFactory factory)
        {
            if (string.IsNullOrEmpty(stackId)) throw new ArgumentException("Stack id is required", nameof(stackId));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (_lock)
            {
                if (!_slots.TryGetValue(stackId, out Slot slot))
                {
                    BNL.LogWarning($"Cannot register peer introducer for unknown stack '{stackId}'");
                    return;
                }
                slot.IntroducerFactory = factory;
            }
        }

        public static void RegisterP2PSocketFactory(string stackId, P2PSocketFactory factory)
        {
            if (string.IsNullOrEmpty(stackId)) throw new ArgumentException("Stack id is required", nameof(stackId));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (_lock)
            {
                if (!_slots.TryGetValue(stackId, out Slot slot))
                {
                    BNL.LogWarning($"Cannot register a direct-connection socket for unknown stack '{stackId}'");
                    return;
                }
                slot.P2PSocketFactory = factory;
            }
        }

        public static void RegisterAddressMatcher(string stackId, Func<string, bool> matcher)
        {
            if (string.IsNullOrEmpty(stackId)) throw new ArgumentException("Stack id is required", nameof(stackId));
            if (matcher == null) throw new ArgumentNullException(nameof(matcher));
            lock (_lock)
            {
                if (!_slots.TryGetValue(stackId, out Slot slot))
                {
                    BNL.LogWarning($"Cannot register an address matcher for unknown stack '{stackId}'");
                    return;
                }
                slot.AddressMatcher = matcher;
            }
        }

        public static bool TryMatchAddress(string address, out string stackId)
        {
            stackId = null;
            if (string.IsNullOrWhiteSpace(address)) return false;
            List<KeyValuePair<string, Func<string, bool>>> matchers = new List<KeyValuePair<string, Func<string, bool>>>();
            lock (_lock)
            {
                foreach (StackInfo stack in _stacks)
                {
                    if (_slots.TryGetValue(stack.Id, out Slot slot) && slot.AddressMatcher != null)
                    {
                        matchers.Add(new KeyValuePair<string, Func<string, bool>>(stack.Id, slot.AddressMatcher));
                    }
                }
            }
            foreach (KeyValuePair<string, Func<string, bool>> matcher in matchers)
            {
                bool matched;
                try { matched = matcher.Value(address); }
                catch (Exception ex)
                {
                    BNL.LogError($"Address matcher for stack '{matcher.Key}' threw: {ex.Message}");
                    continue;
                }
                if (matched)
                {
                    stackId = matcher.Key;
                    return true;
                }
            }
            return false;
        }

        public static ConnectionTarget ParseTarget(string stackId, string raw)
        {
            string effective = string.IsNullOrEmpty(stackId) ? DefaultId : stackId;
            ConnectionTarget target = new ConnectionTarget(effective, raw);
            GetParser(effective)?.Parse(target);
            return target;
        }

        public static ConnectionTarget ParseAddress(string raw)
        {
            return ParseTarget(TryMatchAddress(raw, out string stackId) ? stackId : DefaultId, raw);
        }

        public static NetManager Create(string id, EventBasedNetListener listener, Configuration configuration)
        {
            List<string> requested = ParseStackList(id);
            List<string> stacks = new List<string>(requested.Count);
            lock (_lock)
            {
                foreach (string stackId in requested)
                {
                    if (_slots.ContainsKey(stackId))
                    {
                        stacks.Add(stackId);
                    }
                    else
                    {
                        BNL.LogWarning($"Network stack '{stackId}' is not registered and will not be started; the package that provides it is not installed");
                    }
                }
            }
            if (stacks.Count == 0)
            {
                if (!IsRegistered(DefaultId)) throw new InvalidOperationException(NoTransportMessage(id));
                BNL.LogWarning($"No registered network stack in '{id}', falling back to '{DefaultId}'");
                stacks.Add(DefaultId);
            }
            NetManager mgr = stacks.Count == 1
                ? CreateSingle(stacks[0], listener, configuration)
                : new BasisMultiTransportNetManager(stacks, listener, configuration, CreateSingle);
            SetActiveStackId(string.Join(",", stacks));
            return mgr;
        }

        public static NetManager CreateSingle(string id, EventBasedNetListener listener, Configuration configuration)
        {
            string effective = string.IsNullOrEmpty(id) ? DefaultId : id;
            Slot slot;
            lock (_lock)
            {
                if (!_slots.TryGetValue(effective, out slot))
                {
                    if (!_slots.TryGetValue(DefaultId, out slot)) throw new InvalidOperationException(NoTransportMessage(effective));
                    BNL.LogWarning($"Network stack '{effective}' is not registered (the package that provides it is not installed), falling back to '{DefaultId}'");
                }
            }
            return slot.Factory(listener, configuration);
        }

        private static string NoTransportMessage(string requested)
        {
            return $"Network stack '{requested}' is not registered and neither is the default '{DefaultId}'. Install a transport package, for example com.basis.transport.litenetlib.";
        }

        public static void ReplaceFactory(string id, NetManagerFactory factory)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Stack id is required", nameof(id));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (_lock)
            {
                if (_slots.TryGetValue(id, out Slot slot))
                {
                    slot.Factory = factory;
                }
            }
        }

        public static string ActiveStackId
        {
            get { lock (_lock) return _activeStackId; }
        }

        public static void SetActiveStackId(string id)
        {
            string normalized = string.IsNullOrEmpty(id) ? string.Empty : id;
            bool changed = false;
            lock (_lock)
            {
                if (!string.Equals(_activeStackId, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _activeStackId = normalized;
                    changed = true;
                }
            }
            if (changed)
            {
                try { ActiveStackChanged?.Invoke(normalized); }
                catch (Exception ex) { BNL.LogError($"ActiveStackChanged handler threw: {ex.Message}"); }
            }
        }

        public static IConnectionTargetParser GetParser(string stackId)
        {
            string effective = string.IsNullOrEmpty(stackId) ? DefaultId : stackId;
            lock (_lock)
            {
                if (_slots.TryGetValue(effective, out Slot slot) && slot.Parser != null) return slot.Parser;
                if (!string.Equals(effective, DefaultId, StringComparison.OrdinalIgnoreCase))
                {
                    BNL.LogWarning($"No connection-target parser registered for stack '{effective}', falling back to '{DefaultId}'");
                    if (_slots.TryGetValue(DefaultId, out Slot fallback) && fallback.Parser != null) return fallback.Parser;
                }
            }
            return HostPortParser;
        }

        private static readonly IConnectionTargetParser HostPortParser = new HostPortConnectionTargetParser();

        public static Task<ServerProbeResult> ProbeAsync(ConnectionTarget target, int timeoutMs, CancellationToken ct)
        {
            if (target == null) return Task.FromResult(new ServerProbeResult { Error = "Target is null" });
            string stackId = string.IsNullOrEmpty(target.StackId) ? DefaultId : target.StackId;
            StackProbeDelegate probe;
            lock (_lock)
            {
                if (!_slots.TryGetValue(stackId, out Slot slot) || slot.Probe == null)
                {
                    if (!string.Equals(stackId, DefaultId, StringComparison.OrdinalIgnoreCase))
                    {
                        BNL.LogWarning($"No probe registered for stack '{stackId}', falling back to '{DefaultId}'");
                    }
                    if (!_slots.TryGetValue(DefaultId, out Slot fallback) || fallback.Probe == null)
                    {
                        return Task.FromResult(new ServerProbeResult { Error = $"No probe registered for stack '{stackId}' (no fallback available)" });
                    }
                    probe = fallback.Probe;
                }
                else
                {
                    probe = slot.Probe;
                }
            }
            return probe(target, timeoutMs, ct);
        }

        public static void TickActive()
        {
            foreach (Action pump in _pumps)
            {
                try { pump(); }
                catch (Exception ex) { BNL.LogError($"Stack pump threw: {ex.Message}"); }
            }
            Action tick = null;
            Action[] ticks = null;
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(_activeStackId))
                {
                    if (_slots.TryGetValue(_activeStackId, out Slot slot))
                    {
                        tick = slot.Tick;
                    }
                    else if (_activeStackId.IndexOf(',') >= 0)
                    {
                        List<Action> found = null;
                        foreach (string id in _activeStackId.Split(','))
                        {
                            if (_slots.TryGetValue(id, out Slot part) && part.Tick != null)
                            {
                                if (found == null) found = new List<Action>();
                                found.Add(part.Tick);
                            }
                        }
                        ticks = found?.ToArray();
                    }
                }
            }
            if (tick != null)
            {
                try { tick(); }
                catch (Exception ex) { BNL.LogError($"Stack tick threw: {ex.Message}"); }
            }
            if (ticks != null)
            {
                foreach (Action each in ticks)
                {
                    try { each(); }
                    catch (Exception ex) { BNL.LogError($"Stack tick threw: {ex.Message}"); }
                }
            }
        }

        public static IPeerIntroducer CreateIntroducer(string stackId, NetManager activeManager)
        {
            string effective = string.IsNullOrEmpty(stackId) ? DefaultId : stackId;
            PeerIntroducerFactory factory;
            lock (_lock)
            {
                if (!_slots.TryGetValue(effective, out Slot slot) || slot.IntroducerFactory == null) return null;
                factory = slot.IntroducerFactory;
            }
            return factory(activeManager);
        }

        public static IBasisP2PSocket CreateP2PSocket(string stackId, EventBasedNetListener listener, BasisP2PIntroduced onIntroduced)
        {
            string effective = string.IsNullOrEmpty(stackId) ? DefaultId : stackId;
            P2PSocketFactory factory;
            lock (_lock)
            {
                if (!_slots.TryGetValue(effective, out Slot slot) || slot.P2PSocketFactory == null) return null;
                factory = slot.P2PSocketFactory;
            }
            return factory(listener, onIntroduced);
        }

        public static bool IsRegistered(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (_lock) return _slots.ContainsKey(id);
        }

        public static IReadOnlyList<StackInfo> Stacks
        {
            get { lock (_lock) return _stacks.ToArray(); }
        }

        public static string GetDisplayName(string id)
        {
            if (string.IsNullOrEmpty(id)) id = DefaultId;
            lock (_lock)
            {
                if (_slots.TryGetValue(id, out Slot slot)) return slot.DisplayName;
            }
            return id;
        }
    }
}
