using Basis.Network;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace BasisNetworkConsole
{
    public enum BasisLogTarget
    {
        Console,
        Journal,
        Syslog,
    }

    public static class BasisSystemLog
    {
        public const string TargetVariable = "BASIS_LOG_TARGET";
        public const string Identifier = "basis-server";
        public const string SyslogPath = "/dev/log";
        private const int FacilityDaemon = 3;
        private static readonly object Gate = new object();
        private static bool installed;
        private static volatile bool serverLoggingActive;
        private static bool syslogFailed;
        private static Socket? syslog;

        public static BasisLogTarget Target { get; private set; } = BasisLogTarget.Console;

        public static event Action<BasisLogLevel, string>? Line;

        public static void Install()
        {
            if (installed) return;
            installed = true;

            string? configured = Environment.GetEnvironmentVariable(TargetVariable);
            Target = ChooseTarget(configured, StdoutIsJournal());
            if (Target == BasisLogTarget.Journal) BasisServerSideLogging.WriteToScreen = false;

            BNL.LogOutput += message => Emit(BasisLogLevel.Info, message);
            BNL.LogWarningOutput += message => Emit(BasisLogLevel.Warning, message);
            BNL.LogErrorOutput += message => Emit(BasisLogLevel.Error, message);

            if (!string.IsNullOrWhiteSpace(configured) && !IsKnownTarget(configured))
            {
                BNL.LogWarning($"[Log] {TargetVariable}={configured} is not one of auto, journal, syslog or console; using {Target.ToString().ToLowerInvariant()}.");
            }
        }

        public static void ServerLoggingStarted() => serverLoggingActive = true;

        public static BasisLogTarget ChooseTarget(string? configured, bool stdoutIsJournal)
        {
            switch (configured?.Trim().ToLowerInvariant())
            {
                case "journal":
                case "journald":
                    return BasisLogTarget.Journal;
                case "syslog":
                    return BasisLogTarget.Syslog;
                case "console":
                    return BasisLogTarget.Console;
                default:
                    return stdoutIsJournal ? BasisLogTarget.Journal : BasisLogTarget.Console;
            }
        }

        public static bool MatchesJournalStream(string? journalStream, string? stdoutLink, bool stdoutRedirected)
        {
            if (string.IsNullOrEmpty(journalStream)) return false;

            int colon = journalStream.IndexOf(':');
            if (colon <= 0 || colon == journalStream.Length - 1) return false;
            if (stdoutLink == null) return stdoutRedirected;
            return stdoutLink == "socket:[" + journalStream.Substring(colon + 1) + "]";
        }

        public static string FormatJournal(BasisLogLevel level, string message)
        {
            string prefix = "<" + Severity(level).ToString(CultureInfo.InvariantCulture) + ">";
            if (!BasisControlProtocol.NeedsCleaning(message)) return prefix + message + "\n";

            StringBuilder builder = new StringBuilder(message.Length + 16);
            foreach (string line in BasisControlProtocol.CleanLines(message)) builder.Append(prefix).Append(line).Append('\n');
            return builder.ToString();
        }

        public static string FormatSyslog(BasisLogLevel level, string line, DateTime localTime, int processId)
        {
            int priority = FacilityDaemon * 8 + Severity(level);
            string stamp = localTime.ToString("MMM", CultureInfo.InvariantCulture) + " " + localTime.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2) + " " + localTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return "<" + priority.ToString(CultureInfo.InvariantCulture) + ">" + stamp + " " + Identifier + "[" + processId.ToString(CultureInfo.InvariantCulture) + "]: " + line;
        }

        private static bool IsKnownTarget(string configured)
        {
            switch (configured.Trim().ToLowerInvariant())
            {
                case "auto":
                case "journal":
                case "journald":
                case "syslog":
                case "console":
                    return true;
                default:
                    return false;
            }
        }

        private static int Severity(BasisLogLevel level)
        {
            switch (level)
            {
                case BasisLogLevel.Error: return 3;
                case BasisLogLevel.Warning: return 4;
                default: return 6;
            }
        }

        private static bool StdoutIsJournal()
        {
            if (!OperatingSystem.IsLinux()) return false;

            string? stream = Environment.GetEnvironmentVariable("JOURNAL_STREAM");
            if (string.IsNullOrEmpty(stream)) return false;

            string? link = null;
            try
            {
                link = new FileInfo("/proc/self/fd/1").LinkTarget;
            }
            catch
            {
            }
            return MatchesJournalStream(stream, link, Console.IsOutputRedirected);
        }

        private static void Emit(BasisLogLevel level, string message)
        {
            try
            {
                message ??= string.Empty;
                switch (Target)
                {
                    case BasisLogTarget.Journal:
                        string journal = FormatJournal(level, message);
                        BasisConsoleWriter.Enqueue(() => Console.Out.Write(journal));
                        break;
                    case BasisLogTarget.Syslog:
                        SendSyslog(level, message);
                        if (!serverLoggingActive) BasisConsoleWriter.Enqueue(() => WriteConsole(level, message));
                        break;
                    default:
                        if (!serverLoggingActive) BasisConsoleWriter.Enqueue(() => WriteConsole(level, message));
                        break;
                }
                Line?.Invoke(level, message);
            }
            catch
            {
            }
        }

        private static void WriteConsole(BasisLogLevel level, string message)
        {
            lock (Gate)
            {
                ConsoleColor original = Console.ForegroundColor;
                Console.ForegroundColor = level == BasisLogLevel.Error ? ConsoleColor.Red : level == BasisLogLevel.Warning ? ConsoleColor.Yellow : ConsoleColor.White;
                Console.WriteLine(message);
                Console.ForegroundColor = original;
            }
        }

        private static void SendSyslog(BasisLogLevel level, string message)
        {
            Socket? socket = syslog ?? OpenSyslog();
            if (socket == null) return;

            DateTime now = DateTime.Now;
            int processId = Environment.ProcessId;
            if (!BasisControlProtocol.NeedsCleaning(message))
            {
                SendDatagram(socket, FormatSyslog(level, message, now, processId));
                return;
            }
            foreach (string line in BasisControlProtocol.CleanLines(message)) SendDatagram(socket, FormatSyslog(level, line, now, processId));
        }

        private static Socket? OpenSyslog()
        {
            lock (Gate)
            {
                if (syslog != null || syslogFailed) return syslog;

                Socket? socket = null;
                try
                {
                    socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                    socket.Connect(new UnixDomainSocketEndPoint(SyslogPath));
                    socket.Blocking = false;
                    syslog = socket;
                }
                catch (Exception e)
                {
                    socket?.Dispose();
                    syslogFailed = true;
                    WriteConsole(BasisLogLevel.Warning, $"[Log] {TargetVariable}=syslog but {SyslogPath} could not be opened ({e.Message}). Logging to the console only.");
                }
                return syslog;
            }
        }

        private static void SendDatagram(Socket socket, string datagram)
        {
            try
            {
                socket.Send(Encoding.UTF8.GetBytes(datagram));
            }
            catch (SocketException e) when (e.SocketErrorCode != SocketError.WouldBlock)
            {
                lock (Gate)
                {
                    if (ReferenceEquals(syslog, socket)) syslog = null;
                }
                socket.Dispose();
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
