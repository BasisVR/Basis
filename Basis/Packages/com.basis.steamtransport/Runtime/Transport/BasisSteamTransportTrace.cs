using System;
using System.IO;
using UnityEngine;

namespace Basis.Scripts.Networking.Steam
{
    public static class BasisSteamTransportTrace
    {
        private static readonly object sync = new object();
        private static string logPath;
        public static bool Enabled { get; private set; }

        public static string LogPath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(logPath))
                {
                    logPath = Path.Combine(Application.persistentDataPath, "BasisSteamTransport.log");
                }

                return logPath;
            }
        }

        public static void Clear()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                lock (sync)
                {
                    File.WriteAllText(LogPath, $"[{DateTime.UtcNow:O}] BasisSteamTransport log start{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BasisSteamTransportTrace] Failed to clear log: {ex.Message}");
            }
        }

        public static void Configure(bool enabled)
        {
            Enabled = enabled;
        }

        public static void Log(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        private static void Write(string level, string message)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                string line = $"[{DateTime.UtcNow:O}] [{level}] {message}{Environment.NewLine}";
                lock (sync)
                {
                    File.AppendAllText(LogPath, line);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BasisSteamTransportTrace] Failed to write log: {ex.Message}");
            }
        }
    }
}
