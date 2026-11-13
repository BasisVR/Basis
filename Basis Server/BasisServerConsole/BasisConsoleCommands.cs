using Basis;
using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisPermissions;
using System.Reflection;
using static BasisPermissions.PermissionManager;
namespace BasisNetworkConsole
{
    public static class BasisConsoleCommands
    {
        public static Dictionary<string, Command> commands = new Dictionary<string, Command>();
        // Registering commands
        public static void RegisterCommand(string commandName, string Description, Action<string[]> handler)
        {
            commands[commandName.ToLower()] = new Command { Name = commandName, Description = Description, Handler = handler };
        }
        // Register commands for each configuration field
        public static void RegisterConfigurationCommands(Configuration config)
        {
            RegisterCommand("/config", "Lists every server setting. /config <name> [value] to read or change one.",
                (args) => HandleConfigRoot(args, config));

            var fields = typeof(Configuration).GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                // Register the command for each field
                string commandName = $"/config {field.Name.ToLower()}";
                RegisterCommand(commandName, string.Empty, (args) => HandleConfigField(args, field, config));
            }
        }

        private static void HandleConfigRoot(string[] args, Configuration config)
        {
            var fields = typeof(Configuration).GetFields(BindingFlags.Public | BindingFlags.Instance);

            if (args.Length == 0)
            {
                BNL.Log($"{fields.Length} settings. '*' takes effect on /restart, '+' applies to new joins only.");
                foreach (var field in fields)
                {
                    string marker = Configuration.RequiresRestart(field.Name) ? "*"
                        : Configuration.AppliesToNewJoinsOnly(field.Name) ? "+"
                        : " ";
                    BNL.Log($" {marker} {field.Name} = {DisplayValue(field, config)}");
                }
                return;
            }

            var match = Array.Find(fields, f => string.Equals(f.Name, args[0], StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                BNL.Log($"Unknown setting '{args[0]}'. Type /config to list them.");
                return;
            }

            HandleConfigField(args.Skip(1).ToArray(), match, config);
        }

        private static string DisplayValue(FieldInfo field, Configuration config)
        {
            if (Configuration.IsSecretFieldName(field.Name))
            {
                string raw = field.GetValue(config)?.ToString();
                return string.IsNullOrEmpty(raw) ? "<empty>" : "<redacted>";
            }
            return field.GetValue(config)?.ToString() ?? string.Empty;
        }
        public static void HandleConfigField(string[] args, FieldInfo field, Configuration config)
        {
            if (args.Length == 0)
            {
                string suffix = Configuration.RequiresRestart(field.Name) ? "  (takes effect on /restart)"
                    : Configuration.AppliesToNewJoinsOnly(field.Name) ? "  (applies to new joins only)"
                    : string.Empty;
                BNL.Log($"{field.Name}: {DisplayValue(field, config)}{suffix}");
                return;
            }

            // Rejoined rather than args[0]: ServerMotd and ServerName carry spaces, and splitting
            // them on the command line silently truncated the value to its first word.
            string newValue = string.Join(' ', args);
            object previous = field.GetValue(config);

            if (!TryParseConfigValue(field.FieldType, newValue, out object parsed))
            {
                BNL.Log($"Failed to set {field.Name} to '{newValue}'. Expected {DescribeType(field.FieldType)}.");
                return;
            }

            field.SetValue(config, parsed);

            try
            {
                config.SaveToXml(Configuration.GetDefaultPath());
            }
            catch (Exception e)
            {
                field.SetValue(config, previous);
                BNL.LogError($"Failed to persist {field.Name}, change reverted: {e.Message}");
                return;
            }

            string shown = Configuration.IsSecretFieldName(field.Name) ? "<redacted>" : newValue;

            if (Configuration.RequiresRestart(field.Name))
            {
                BNL.Log($"Set {field.Name} to {shown}. Saved — takes effect on /restart.");
                return;
            }

            NetworkServer.ApplyLiveConfiguration();

            BNL.Log(Configuration.AppliesToNewJoinsOnly(field.Name)
                ? $"Set {field.Name} to {shown}. Saved and applied to new joins."
                : $"Set {field.Name} to {shown}. Saved and applied live.");
        }

        private static bool TryParseConfigValue(Type type, string raw, out object parsed)
        {
            parsed = null;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;

            if (type == typeof(string)) { parsed = raw; return true; }
            if (type == typeof(bool)) { if (bool.TryParse(raw, out var v)) { parsed = v; return true; } return false; }
            if (type == typeof(int)) { if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, invariant, out var v)) { parsed = v; return true; } return false; }
            if (type == typeof(ushort)) { if (ushort.TryParse(raw, System.Globalization.NumberStyles.Integer, invariant, out var v)) { parsed = v; return true; } return false; }
            if (type == typeof(byte)) { if (byte.TryParse(raw, System.Globalization.NumberStyles.Integer, invariant, out var v)) { parsed = v; return true; } return false; }
            if (type == typeof(long)) { if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, invariant, out var v)) { parsed = v; return true; } return false; }
            if (type == typeof(float)) { if (float.TryParse(raw, System.Globalization.NumberStyles.Float, invariant, out var v)) { parsed = v; return true; } return false; }
            if (type == typeof(double)) { if (double.TryParse(raw, System.Globalization.NumberStyles.Float, invariant, out var v)) { parsed = v; return true; } return false; }
            if (type.IsEnum) { try { parsed = Enum.Parse(type, raw, true); return Enum.IsDefined(type, parsed); } catch { return false; } }

            return false;
        }

        private static string DescribeType(Type type)
        {
            if (type.IsEnum) return $"one of [{string.Join(", ", Enum.GetNames(type))}]";
            if (type == typeof(bool)) return "true or false";
            return type.Name;
        }
        private static Thread? consoleThread;
        public static void RegisterPermissionCommands()
        {
            // Root help
            RegisterCommand("/perm", "Permission system commands. Type /perm help", HandlePermRoot);
            RegisterCommand("/perm help", "Shows permission command help", HandlePermHelp);

            // IO / path
            RegisterCommand("/perm path", "Shows current permissions.xml path", HandlePermPath);
            RegisterCommand("/perm path set", "Sets permissions.xml path (no load). Usage: /perm path set <path>", HandlePermPathSet);
            RegisterCommand("/perm load", "Loads permissions.xml (current path)", HandlePermLoad);
            RegisterCommand("/perm load from", "Loads permissions.xml from path. Usage: /perm load from <path>", HandlePermLoadFrom);
            RegisterCommand("/perm save", "Saves permissions.xml (current path)", HandlePermSave);
            RegisterCommand("/perm save to", "Saves permissions.xml to path. Usage: /perm save to <path>", HandlePermSaveTo);
            RegisterCommand("/perm reload", "Save then load (current path)", HandlePermReload);
            RegisterCommand("/perm defaults", "Ensures default groups exist", HandlePermDefaults);

            // Users
            RegisterCommand("/perm user list", "Lists all users", HandlePermUserList);
            RegisterCommand("/perm user create", "Creates user. Usage: /perm user create <uuid>", HandlePermUserCreate);
            RegisterCommand("/perm user info", "Shows user raw nodes/groups. Usage: /perm user info <uuid>", HandlePermUserInfo);
            RegisterCommand("/perm user node add", "Adds user node. Usage: /perm user node add <uuid> <node>", HandlePermUserNodeAdd);
            RegisterCommand("/perm user node remove", "Removes user node. Usage: /perm user node remove <uuid> <node>", HandlePermUserNodeRemove);
            RegisterCommand("/perm user group add", "Adds user to group. Usage: /perm user group add <uuid> <group>", HandlePermUserGroupAdd);
            RegisterCommand("/perm user group remove", "Removes user from group. Usage: /perm user group remove <uuid> <group>", HandlePermUserGroupRemove);
            RegisterCommand("/perm user effective", "Shows effective allow/deny rules. Usage: /perm user effective <uuid>", HandlePermUserEffective);

            // Groups
            RegisterCommand("/perm group list", "Lists all groups", HandlePermGroupList);
            RegisterCommand("/perm group create", "Creates group. Usage: /perm group create <name>", HandlePermGroupCreate);
            RegisterCommand("/perm group info", "Shows group nodes/parents. Usage: /perm group info <name>", HandlePermGroupInfo);
            RegisterCommand("/perm group node add", "Adds group node. Usage: /perm group node add <group> <node>", HandlePermGroupNodeAdd);
            RegisterCommand("/perm group node remove", "Removes group node. Usage: /perm group node remove <group> <node>", HandlePermGroupNodeRemove);
            RegisterCommand("/perm group parent add", "Adds parent. Usage: /perm group parent add <group> <parent>", HandlePermGroupParentAdd);
            RegisterCommand("/perm group parent remove", "Removes parent. Usage: /perm group parent remove <group> <parent>", HandlePermGroupParentRemove);

            // Checks
            RegisterCommand("/perm check", "Checks a node. Usage: /perm check <uuid> <node>", HandlePermCheck);

            // Quality-of-life aliases
            RegisterCommand("/perm u", "Alias: /perm user ...", HandlePermHelp);
            RegisterCommand("/perm g", "Alias: /perm group ...", HandlePermHelp);
        }
        private static PermissionManager PM => PermissionIntegration.Manager;

        private static void HandlePermRoot(string[] args)
        {
            HandlePermHelp(args);
        }

        private static void HandlePermHelp(string[] args)
        {
            BNL.Log("Permission commands:");
            BNL.Log("/perm path");
            BNL.Log("/perm path set <path>");
            BNL.Log("/perm load");
            BNL.Log("/perm load from <path>");
            BNL.Log("/perm save");
            BNL.Log("/perm save to <path>");
            BNL.Log("/perm reload");
            BNL.Log("/perm defaults");
            BNL.Log("");
            BNL.Log("/perm user list");
            BNL.Log("/perm user create <uuid>");
            BNL.Log("/perm user info <uuid>");
            BNL.Log("/perm user node add <uuid> <node>");
            BNL.Log("/perm user node remove <uuid> <node>");
            BNL.Log("/perm user group add <uuid> <group>");
            BNL.Log("/perm user group remove <uuid> <group>");
            BNL.Log("/perm user effective <uuid>");
            BNL.Log("");
            BNL.Log("/perm group list");
            BNL.Log("/perm group create <name>");
            BNL.Log("/perm group info <name>");
            BNL.Log("/perm group node add <group> <node>");
            BNL.Log("/perm group node remove <group> <node>");
            BNL.Log("/perm group parent add <group> <parent>");
            BNL.Log("/perm group parent remove <group> <parent>");
            BNL.Log("");
            BNL.Log("/perm check <uuid> <node>");
            BNL.Log("Notes: Use '-node' to deny when adding nodes.");
        }

        // -------- IO / path --------

        private static void HandlePermPath(string[] args)
        {
            BNL.Log($"permissions.xml path: {PM.GetXmlPath()}");
        }

        private static void HandlePermPathSet(string[] args)
        {
            if (args.Length < 1)
            {
                BNL.Log("Usage: /perm path set <path>");
                return;
            }

            string path = string.Join(' ', args).Trim(); // allow spaces
            PM.SetXmlPath(path);
            BNL.Log($"Set permissions.xml path to: {PM.GetXmlPath()}");
        }

        private static void HandlePermLoad(string[] args)
        {
            PM.LoadFromXml();
            BNL.Log($"Loaded permissions from: {PM.GetXmlPath()}");
        }

        private static void HandlePermLoadFrom(string[] args)
        {
            if (args.Length < 1)
            {
                BNL.Log("Usage: /perm load from <path>");
                return;
            }

            string path = string.Join(' ', args).Trim();
            PM.LoadFromXml(path);
            PM.SetXmlPath(path);
            BNL.Log($"Loaded permissions from: {path}");
        }

        private static void HandlePermSave(string[] args)
        {
            PM.SaveToXml();
            BNL.Log($"Saved permissions to: {PM.GetXmlPath()}");
        }

        private static void HandlePermSaveTo(string[] args)
        {
            if (args.Length < 1)
            {
                BNL.Log("Usage: /perm save to <path>");
                return;
            }

            string path = string.Join(' ', args).Trim();
            PM.SaveToXml(path);
            BNL.Log($"Saved permissions to: {path}");
        }

        private static void HandlePermReload(string[] args)
        {
            PM.SaveToXml();
            PM.LoadFromXml();
            BNL.Log("Reloaded permissions (save -> load).");
        }

        private static void HandlePermDefaults(string[] args)
        {
            PM.EnsureDefaults();
            BNL.Log("Ensured default permission groups.");
        }

        // -------- Users --------

        private static void HandlePermUserList(string[] args)
        {
            var snap = PM.Snapshot();
            if (snap.Users.Count == 0)
            {
                BNL.Log("No users.");
                return;
            }

            BNL.Log($"Users ({snap.Users.Count}):");
            foreach (var u in snap.Users.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                BNL.Log($"- {u}");
        }

        private static void HandlePermUserCreate(string[] args)
        {
            if (args.Length < 1)
            {
                BNL.Log("Usage: /perm user create <uuid>");
                return;
            }

            string uuid = args[0].Trim();
            PM.GetOrCreateUser(uuid);
            PM.SaveToXmlDebounced();
            BNL.Log($"User ensured: {uuid}");
        }

        private static void HandlePermUserInfo(string[] args)
        {
            if (args.Length < 1)
            {
                BNL.Log("Usage: /perm user info <uuid>");
                return;
            }

            string uuid = args[0].Trim();
            if (!PM.TryGetUser(uuid, out var user))
            {
                BNL.Log($"User not found: {uuid}");
                return;
            }

            BNL.Log($"User: {user.Uuid}");
            BNL.Log($"Groups ({user.Groups.Count}): {(user.Groups.Count == 0 ? "(none)" : string.Join(", ", user.Groups.OrderBy(x => x)))}");
            BNL.Log($"Nodes ({user.Nodes.Count}): {(user.Nodes.Count == 0 ? "(none)" : string.Join(", ", user.Nodes.OrderBy(x => x)))}");
        }

        private static void HandlePermUserNodeAdd(string[] args)
        {
            if (args.Length < 2)
            {
                BNL.Log("Usage: /perm user node add <uuid> <node>");
                return;
            }

            string uuid = args[0].Trim();
            string node = string.Join(' ', args.Skip(1)).Trim(); // allow weird node strings
            PM.AddUserNode(uuid, node);
            BNL.Log($"Added user node: {uuid} -> {node}");
        }

        private static void HandlePermUserNodeRemove(string[] args)
        {
            if (args.Length < 2)
            {
                BNL.Log("Usage: /perm user node remove <uuid> <node>");
                return;
            }

            string uuid = args[0].Trim();
            string node = string.Join(' ', args.Skip(1)).Trim();
            PM.RemoveUserNode(uuid, node);
            BNL.Log($"Removed user node: {uuid} -> {node}");
        }

        private static void HandlePermUserGroupAdd(string[] args)
        {
            if (args.Length < 2)
            {
                BNL.Log("Usage: /perm user group add <uuid> <group>");
                return;
            }

            string uuid = args[0].Trim();
            string group = string.Join(' ', args.Skip(1)).Trim();
            PM.AddUserToGroup(uuid, group);
            BNL.Log($"Added user to group: {uuid} -> {group}");
        }

        private static void HandlePermUserGroupRemove(string[] args)
        {
            if (args.Length < 2)
            {
                BNL.Log("Usage: /perm user group remove <uuid> <group>");
                return;
            }

            string uuid = args[0].Trim();
            string group = string.Join(' ', args.Skip(1)).Trim();
            PM.RemoveUserFromGroup(uuid, group);
            BNL.Log($"Removed user from group: {uuid} -> {group}");
        }

        private static void HandlePermUserEffective(string[] args)
        {
            if (args.Length < 1)
            {
                BNL.Log("Usage: /perm user effective <uuid>");
                return;
            }

            string uuid = args[0].Trim();

            var allowed = PM.GetAllAllowedRules(uuid).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var denied = PM.GetAllDeniedRules(uuid).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

            BNL.Log($"Effective rules for {uuid}:");
            BNL.Log($"Allowed ({allowed.Length}): {(allowed.Length == 0 ? "(none)" : string.Join(", ", allowed))}");
            BNL.Log($"Denied ({denied.Length}): {(denied.Length == 0 ? "(none)" : string.Join(", ", denied))}");
        }

        // -------- Groups --------

        private static void HandlePermGroupList(string[] args)
        {
            var snap = PM.Snapshot();
            if (snap.Groups.Count == 0)
            {
                BNL.Log("No groups.");
                return;
            }

            BNL.Log($"Groups ({snap.Groups.Count}):");
            foreach (var g in snap.Groups.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                BNL.Log($"- {g}");
        }

        private static void HandlePermGroupCreate(string[] args)
        {
            if (args.Length < 1)
            {
                BNL.Log("Usage: /perm group create <name>");
                return;
            }

            string name = string.Join(' ', args).Trim();
            PM.GetOrCreateGroup(name);
            PM.SaveToXmlDebounced();
            BNL.Log($"Group ensured: {name}");
        }

        private static void HandlePermGroupInfo(string[] args)
        {
            if (args.Length < 1)
            {
                BNL.Log("Usage: /perm group info <name>");
                return;
            }

            string name = string.Join(' ', args).Trim();
            if (!PM.TryGetGroup(name, out var group))
            {
                BNL.Log($"Group not found: {name}");
                return;
            }

            BNL.Log($"Group: {group.Name}");
            BNL.Log($"Parents ({group.Parents.Count}): {(group.Parents.Count == 0 ? "(none)" : string.Join(", ", group.Parents.OrderBy(x => x)))}");
            BNL.Log($"Nodes ({group.Nodes.Count}): {(group.Nodes.Count == 0 ? "(none)" : string.Join(", ", group.Nodes.OrderBy(x => x)))}");
        }

        private static void HandlePermGroupNodeAdd(string[] args)
        {
            if (args.Length < 2)
            {
                BNL.Log("Usage: /perm group node add <group> <node>");
                return;
            }

            string group = args[0].Trim();
            string node = string.Join(' ', args.Skip(1)).Trim();
            PM.AddGroupNode(group, node);
            BNL.Log($"Added group node: {group} -> {node}");
        }

        private static void HandlePermGroupNodeRemove(string[] args)
        {
            if (args.Length < 2)
            {
                BNL.Log("Usage: /perm group node remove <group> <node>");
                return;
            }

            string group = args[0].Trim();
            string node = string.Join(' ', args.Skip(1)).Trim();
            PM.RemoveGroupNode(group, node);
            BNL.Log($"Removed group node: {group} -> {node}");
        }

        private static void HandlePermGroupParentAdd(string[] args)
        {
            if (args.Length < 2)
            {
                BNL.Log("Usage: /perm group parent add <group> <parent>");
                return;
            }

            string group = args[0].Trim();
            string parent = string.Join(' ', args.Skip(1)).Trim();
            PM.AddGroupParent(group, parent);
            BNL.Log($"Added parent: {group} -> {parent}");
        }

        private static void HandlePermGroupParentRemove(string[] args)
        {
            if (args.Length < 2)
            {
                BNL.Log("Usage: /perm group parent remove <group> <parent>");
                return;
            }

            string group = args[0].Trim();
            string parent = string.Join(' ', args.Skip(1)).Trim();
            PM.RemoveGroupParent(group, parent);
            BNL.Log($"Removed parent: {group} -> {parent}");
        }

        // -------- Checks --------

        private static void HandlePermCheck(string[] args)
        {
            if (args.Length < 2)
            {
                BNL.Log("Usage: /perm check <uuid> <node>");
                return;
            }

            string uuid = args[0].Trim();
            string node = string.Join(' ', args.Skip(1)).Trim();

            bool has = PM.Has(uuid, node);
            BNL.Log($"Check: uuid={uuid} node={node} => {(has ? "ALLOW" : "DENY")}");
        }
        public static readonly object ExecutionGate = new object();

        public static int Execute(string input)
        {
            string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return BasisControlProtocol.CommandRan;

            // Try to match the longest possible command
            for (int i = parts.Length; i > 0; i--)
            {
                string potentialCommand = string.Join(' ', parts.Take(i)).ToLower();

                if (commands.TryGetValue(potentialCommand, out var command))
                {
                    string[] args = parts.Skip(i).ToArray();
                    lock (ExecutionGate)
                    {
                        try
                        {
                            command.Handler(args);
                            return BasisControlProtocol.CommandRan;
                        }
                        catch (Exception ex)
                        {
                            BNL.LogError($"Error executing command '{potentialCommand}': {ex.Message}");
                            return BasisControlProtocol.CommandFailed;
                        }
                    }
                }
            }

            BNL.Log("Unknown command. Type /help for available commands.");
            return BasisControlProtocol.CommandUnknown;
        }

        public static void StartConsoleListener()
        {
            BasisConsoleDriver.Initialize();
            consoleThread = new Thread(() =>
            {
                while (Program.isRunning)
                {
                    string? line = BasisConsoleDriver.ReadLine();
                    if (line == null) break; // end of input: nothing left to read, don't spin on it

                    string input = line.Trim();
                    if (input.Length == 0) continue;

                    Execute(input);
                }
            });

            consoleThread.IsBackground = true;
            consoleThread.Start();
        }
        public static void HandleShowPlayers(string[] args)
        {
            string ConnectedPlayerNames = $"Connected Player count is {NetworkServer.AuthenticatedPeers.Count} ";
            foreach (NetPeer Peer in NetworkServer.AuthenticatedPeers.Values)
            {
                if (BasisSavedState.GetLastPlayerMetaData(Peer, out SerializableBasis.ClientMetaDataMessage Message))
                {
                    ConnectedPlayerNames += $"Player: {Message.playerDisplayName} UUID: {Message.playerUUID}, ";
                }
            }
            BNL.Log(ConnectedPlayerNames);
        }
        public static void HandleStatus(string[] args)
        {
            Configuration config = NetworkServer.Configuration;
            TimeSpan uptime = DateTime.UtcNow - Program.StartedUtc;
            BNL.Log($"{config.ServerName}: {NetworkServer.AuthenticatedPeers.Count} of {config.PeerLimit} players, UDP port {config.SetPort}, protocol v{BasisNetworkVersion.ServerVersion}, up {(int)uptime.TotalDays}d {uptime:hh\\:mm\\:ss}");
        }

        public static void HandleShutdown(string[] args)
        {
            BNL.Log("Shutting down the server...");
            Program.RequestShutdown(BasisExitCode.Clean, "/shutdown");
        }

        /// <summary>Passed to the process /restart launches so it waits for its predecessor to release the port.</summary>
        public const string AwaitPidArgument = "--await-pid=";

        /// <summary>
        /// Blocks until the process that launched this one via /restart has exited, so the UDP bind
        /// does not race it. Returns immediately when the argument is absent, malformed, or names a
        /// process that is already gone.
        /// </summary>
        public static void WaitForPredecessorExit(string[] args)
        {
            string? argument = Array.Find(args, a => a.StartsWith(AwaitPidArgument, StringComparison.OrdinalIgnoreCase));
            if (argument == null) return;

            if (!int.TryParse(argument.Substring(AwaitPidArgument.Length), out int pid)) return;

            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(pid);
                BNL.Log($"Waiting for the previous server process ({pid}) to exit...");
                if (!previous.WaitForExit(30000))
                {
                    BNL.LogWarning($"Previous server process ({pid}) is still running after 30s; binding anyway.");
                }
            }
            catch (ArgumentException)
            {
                // Already exited, which is the common case — nothing to wait for.
            }
            catch (Exception e)
            {
                BNL.LogWarning($"Could not wait on the previous server process ({pid}): {e.Message}");
            }
        }

        public static void HandleRestart(string[] args)
        {
            if (BasisSystemd.IsManaged)
            {
                BNL.Log("Restarting the server. systemd starts it again once this process exits.");
                Program.RequestShutdown(BasisExitCode.Restart, "/restart");
                return;
            }

            string exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                BNL.LogError("Cannot restart: the host process path is unavailable. Use /shutdown and start the server again.");
                return;
            }

            // Launched before this process exits, so the operator keeps a running server rather than
            // being left with nothing if the relaunch fails. The replacement binds the same UDP port,
            // so it is told to wait for this process to go away first — otherwise it races us for the
            // socket and dies on startup.
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true,
                };
                string[] commandLine = Environment.GetCommandLineArgs();
                if (commandLine.Length > 0 && string.Equals(Path.GetFileNameWithoutExtension(exePath), "dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    start.ArgumentList.Add(commandLine[0]);
                }
                foreach (string argument in commandLine.Skip(1))
                {
                    if (argument.StartsWith(AwaitPidArgument, StringComparison.OrdinalIgnoreCase)) continue;
                    start.ArgumentList.Add(argument);
                }
                start.ArgumentList.Add($"{AwaitPidArgument}{Environment.ProcessId}");

                BNL.Log("Restarting the server...");
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception e)
            {
                BNL.LogError($"Restart failed to launch a replacement process, server left running: {e.Message}");
                return;
            }

            Program.RequestShutdown(BasisExitCode.Clean, "/restart");
        }

        public static void HandleHelp(string[] args)
        {
            BNL.Log("Available commands:");
            foreach (var kvp in commands)
            {
                var command = kvp.Value;
                if (string.IsNullOrEmpty(command.Description))
                {
                    BNL.Log($"{command.Name}");
                }
                else
                {
                    BNL.Log($"{command.Name} - {command.Description}");
                }
            }
        }
        public static void HandleClear(string[] args)
        {
            BasisConsoleDriver.Clear();
        }
        // Command class to store command info
        public class Command
        {
            public required string Name { get; set; }
            public required string Description { get; set; }
            public Action<string[]> Handler { get; set; }
        }
    }
}
