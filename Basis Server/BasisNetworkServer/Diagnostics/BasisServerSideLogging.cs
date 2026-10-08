using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network
{
    public static class BasisServerSideLogging
    {
        private static string LogDirectory;
        private static string CurrentLogFileName => Path.Combine(LogDirectory, $"{DateTime.UtcNow:yyyy-MM-dd}.log");

        private static CancellationTokenSource _cancellationTokenSource;
        private static Task _loggingTask;
        private static readonly BlockingCollection<string> LogQueue = new(new ConcurrentQueue<string>(), 200);
        private static readonly SemaphoreSlim FileWriteSemaphore = new(1, 1);
        private static readonly object ScreenLock = new();

        static BasisServerSideLogging()
        {
        }
        public static bool UseLogging;
        public static bool WriteToScreen = true;
        /// <summary>
        /// Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
        /// </summary>
        /// <param name="config"></param>
        /// <param name="PathOutput"></param>
        public static void Initialize(Configuration config, string logDirectory)
        {
            UseLogging = config.HasFileSupport;
            LogDirectory = logDirectory;
            BNL.LogOutput -= Log;
            BNL.LogOutput += Log;
            BNL.LogWarningOutput -= LogWarning;
            BNL.LogWarningOutput += LogWarning;
            BNL.LogErrorOutput -= LogError;
            BNL.LogErrorOutput += LogError;

            if (UseLogging)
            {
                // Ensure the logs directory exists
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }
                Log("Logs are saved to " + CurrentLogFileName);
                StartLoggingTask();
            }
            else
            {
                Log("no logs will be saved");
            }
        }
        private static void StartLoggingTask()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            _loggingTask = Task.Run(async () =>
            {
                // Owned by this one task. Whatever queued up while the previous write was in flight
                // goes out in a single open/write/close — a burst of lines used to cost a file
                // handle apiece, which is what made a multi-line report expensive to emit. The
                // queue is bounded at 200, so a drain is bounded too.
                StringBuilder batch = new StringBuilder(1024);
                try
                {
                    while (!cancellationToken.IsCancellationRequested || !LogQueue.IsCompleted)
                    {
                        if (LogQueue.TryTake(out var logEntry, 50))
                        {
                            batch.Clear();
                            batch.Append(logEntry).Append(Environment.NewLine);
                            while (LogQueue.TryTake(out var queued))
                            {
                                batch.Append(queued).Append(Environment.NewLine);
                            }
                            await WriteToFileAsync(batch.ToString(), CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Task canceled, exit gracefully
                }
            }, cancellationToken);
        }

        private static async Task WriteToFileAsync(string text, CancellationToken cancellationToken)
        {
            // Outside the try: a cancelled wait never took the semaphore, and releasing one that was
            // never acquired hands out a second permit and lets two writers into the file at once.
            await FileWriteSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (var stream = new FileStream(CurrentLogFileName, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true))
                {
                    var logData = Encoding.UTF8.GetBytes(text);
                    await stream.WriteAsync(logData, 0, logData.Length, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                FileWriteSemaphore.Release();
            }
        }

        public static async Task ShutdownAsync()
        {
            _cancellationTokenSource?.Cancel();
            LogQueue?.CompleteAdding();

            try
            {
                await _loggingTask.ConfigureAwait(false);
            }
            catch (AggregateException)
            {
                // Suppress exceptions caused by cancellation
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
            }
        }

        /// <summary>
        /// A stamp and the two forms it is written in. The stamp has minute resolution, so building
        /// it per line spent a DateTime.Now — a timezone conversion — and two string allocations
        /// producing the same characters thousands of times over. Held as one immutable object
        /// swapped by reference so the minute and its text can never disagree; two threads racing
        /// here just compute the same value twice.
        /// </summary>
        private sealed class MinuteStamp
        {
            public long Minute;
            public string Plain;     // "10:31"    — the file record
            public string Bracketed; // "[10:31] " — the console prefix
        }

        private static MinuteStamp _stamp;

        private static MinuteStamp CurrentStamp()
        {
            DateTime now = DateTime.Now;
            long minute = now.Ticks / TimeSpan.TicksPerMinute;

            MinuteStamp cached = Volatile.Read(ref _stamp);
            if (cached != null && cached.Minute == minute) return cached;

            string plain = now.ToString("HH:mm");
            MinuteStamp fresh = new MinuteStamp
            {
                Minute = minute,
                Plain = plain,
                Bracketed = "[" + plain + "] ",
            };
            Volatile.Write(ref _stamp, fresh);
            return fresh;
        }

        /// <summary>
        /// Newlines and control characters would break the one-record-per-line shape of the log
        /// file. Almost every message is already clean, so scan for a character worth replacing
        /// before committing to a copy — the unconditional rebuild cost a StringBuilder and a string
        /// on every line the server ever wrote.
        /// </summary>
        private static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            int firstBad = -1;
            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                if (c < 0x20 && c != '\t') { firstBad = i; break; }
            }
            if (firstBad < 0) return message;

            StringBuilder sb = new StringBuilder(message.Length);
            sb.Append(message, 0, firstBad);
            for (int i = firstBad; i < message.Length; i++)
            {
                char c = message[i];
                if (c == '\n' || c == '\r') sb.Append(' ');
                else if (c < 0x20 && c != '\t') sb.Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void Log(string message) => Emit("INFO", "[INFO] ", ConsoleColor.DarkMagenta, message);

        public static void LogWarning(string message) => Emit("WARNING", "[WARNING] ", ConsoleColor.DarkYellow, message);

        public static void LogError(string message) => Emit("ERROR", "[ERROR] ", ConsoleColor.DarkRed, message);

        /// <summary>
        /// The three levels differed only in two labels and a colour. Sharing one body means the
        /// per-line costs — one timestamp, one sanitise pass, one console lock — are paid once and
        /// stay described in one place instead of drifting between three copies.
        /// </summary>
        private static void Emit(string level, string consoleLabel, ConsoleColor levelColor, string message)
        {
            if (!WriteToScreen && !UseLogging) return;

            message = Sanitize(message);
            MinuteStamp stamp = CurrentStamp();

            if (WriteToScreen) WriteScreenLine(stamp.Bracketed, consoleLabel, levelColor, message);

            if (UseLogging)
            {
                string formattedMessage = $"[{stamp.Plain}] [{level}] {message}";
                if (!LogQueue.TryAdd(formattedMessage))
                {
                    LogQueue.TryTake(out _); // Drop oldest log if the queue is full
                    LogQueue.TryAdd(formattedMessage); // Retry adding the new message
                }
            }
        }

        /// <summary>
        /// Writes one whole log line. The parts have to land together: they share the console's
        /// colour state, so two threads interleaving here mix up both the colours and the text.
        ///
        /// Every ForegroundColor assignment is a console attribute call, and saving and restoring
        /// around each of the three segments paid for six of them where four will do — read the
        /// incoming colour once, restore it once at the end.
        /// </summary>
        private static void WriteScreenLine(string stamp, string level, ConsoleColor levelColor, string message)
        {
            lock (ScreenLock)
            {
                ConsoleColor original = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.Write(stamp);
                Console.ForegroundColor = levelColor;
                Console.Write(level);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(message);
                Console.ForegroundColor = original;
            }
        }
    }
}
