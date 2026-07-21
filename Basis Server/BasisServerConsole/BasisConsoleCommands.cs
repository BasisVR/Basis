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
            var fields = typeof(Configuration).GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                // Register the command for each field
                string commandName = $"/config {field.Name.ToLower()}";
                RegisterCommand(commandName, string.Empty, (args) => HandleConfigField(args, field, config));
            }
        }
        public static void HandleConfigField(string[] args, FieldInfo field, Configuration config)
        {
            if (args.Length == 0)
            {
                // Display the current value
                BNL.Log($"{field.Name}: {field.GetValue(config)}");
            }
            else if (args.Length == 1)
            {
                // Try to set the value
                string newValue = args[0];
                bool success = false;

                // Handle different types of fields
                if (field.FieldType == typeof(int))
                {
                    if (int.TryParse(newValue, out int intValue))
                    {
                        field.SetValue(config, intValue);
                        success = true;
                    }
                }
                else if (field.FieldType == typeof(ushort))
                {
                    if (ushort.TryParse(newValue, out ushort ushortValue))
                    {
                        field.SetValue(config, ushortValue);
                        success = true;
                    }
                }
                else if (field.FieldType == typeof(bool))
                {
                    if (bool.TryParse(newValue, out bool boolValue))
                    {
                        field.SetValue(config, boolValue);
                        success = true;
                    }
                }
                else if (field.FieldType == typeof(float))
                {
                    if (float.TryParse(newValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float floatValue))
                    {
                        field.SetValue(config, floatValue);
                        success = true;
                    }
                }
                else if (field.FieldType == typeof(string))
                {
                    field.SetValue(config, newValue);
                    success = true;
                }

                if (success)
                {
                    BNL.Log($"Set {field.Name} to {newValue}");
                }
                else
                {
                    BNL.Log($"Failed to set {field.Name} to {newValue}. Invalid type or value.");
                }
            }
            else
            {
                BNL.Log($"Usage: /config {field.Name.ToLower()} [value]");
            }
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
        public static void StartConsoleListener()
        {
            consoleThread = new Thread(() =>
            {
                while (Program.isRunning)
                {
                    string? input = Console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(input)) continue;

                    string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    bool matched = false;

                    // Try to match the longest possible command
                    for (int i = parts.Length; i > 0; i--)
                    {
                        string potentialCommand = string.Join(' ', parts.Take(i)).ToLower();

                        if (commands.TryGetValue(potentialCommand, out var command))
                        {
                            string[] args = parts.Skip(i).ToArray();
                            try
                            {
                                command.Handler(args);
                            }
                            catch (Exception ex)
                            {
                                BNL.Log($"Error executing command '{potentialCommand}': {ex.Message}");
                            }
                            matched = true;
                            break;
                        }
                    }

                    if (!matched)
                    {
                        BNL.Log("Unknown command. Type /help for available commands.");
                    }
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
            // Example of showing server status
            BNL.Log("Server is running and healthy.");
            // You can add more status details here as needed
        }

        public static void HandleShutdown(string[] args)
        {
            BNL.Log("Shutting down the server...");
            Program.isRunning = false;
            NetworkServer.StopServer();
            Environment.Exit(0);
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
            BNL.ClearConsole();
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
