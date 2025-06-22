using Basis;
using System.Reflection;

namespace BasisNetworkConsole
{
    public static class BasisConsoleCommands
    {
        public static Dictionary<string, Command> commands = new Dictionary<string, Command>();
        // Registering commands
        public static void RegisterCommand(string commandName, Action<string[]> handler)
        {
            commands[commandName.ToLower()] = new Command { Name = commandName, Handler = handler };
        }
        // Register commands for each configuration field
        public static void RegisterConfigurationCommands(Configuration config)
        {
            var fields = typeof(Configuration).GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                // Register the command for each field
                string commandName = $"config {field.Name.ToLower()}";
                RegisterCommand(commandName, (args) => HandleConfigField(args, field, config));
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
                    if (float.TryParse(newValue, out float floatValue))
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

        public static void StartConsoleListener()
        {
            consoleThread = new Thread(() =>
            {
                while (Program.isRunning)
                {
                    string? input = Console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(input)) continue;

                    string[] parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    string commandName = parts[0].ToLowerInvariant();

                    if (commands.TryGetValue(commandName, out var command))
                    {
                        string[] args = parts.Length > 1 ? parts.Skip(1).ToArray() : Array.Empty<string>();
                        try
                        {
                            command.Handler(args);
                        }
                        catch (Exception ex)
                        {
                            BNL.Log($"Error executing command '{commandName}': {ex.Message}");
                        }
                    }
                    else
                    {
                        BNL.Log("Unknown command. Type /help for available commands.");
                    }
                }
            });

            consoleThread.IsBackground = true;
            consoleThread.Start();
        }
        // Example command handlers
        public static void HandleAddAdmin(string[] args)
        {
            if (args.Length >= 1)
            {
                string value = args[0];
                if (NetworkServer.authIdentity.AddNetPeerAsAdmin(value))
                {
                    BNL.Log($"Added Admin {value}");
                }
                else
                {
                    BNL.Log("Already Have Admin Added");
                }
            }
            else
            {
                BNL.Log("Usage: /admin add <username>");
            }
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
            Program.isRunning = false;  // Gracefully stop the server
            Environment.Exit(0); // Exit the application
        }

        public static void HandleHelp(string[] args)
        {
            BNL.Log("Available commands:");
            BNL.Log("/admin add <username> - Adds a user as an admin.");
            BNL.Log("/status - Shows the server status.");
            BNL.Log("/shutdown - Shuts down the server.");
        }
        // Command class to store command info
        public class Command
        {
            public string Name { get; set; }
            public Action<string[]> Handler { get; set; }
        }
    }
}
