using System;
using System.IO;

namespace Basis.BasisUI
{
    /// <summary>
    /// Reveals a file or folder in the host OS file browser, defensively.
    ///
    /// This launches an OS process / URL handler, and some callers pass paths that embed
    /// server-influenced text (e.g. a pulled-log folder named after the remote server). To stop
    /// that becoming an argument- or URL-injection vector this:
    ///   • canonicalises the path and requires it to be a real, existing local file/folder, so a
    ///     hostile string (a process-flag or URL-scheme payload) can never reach a launcher;
    ///   • refuses any path containing a double-quote or control character — a legitimate reveal
    ///     target never has one, and a double-quote is the only thing that could break out of the
    ///     quoted argument we pass;
    ///   • invokes only absolute launchers, never a bare name a PATH or CWD entry could shadow:
    ///     explorer.exe on Windows, /usr/bin/open on macOS, and on Linux the first opener that is
    ///     actually installed in a system bin directory. Windows may hand that pinned explorer.exe
    ///     to ShellExecuteEx rather than CreateProcess, which is still not a shell interpreting a
    ///     command line; macOS and Linux never leave CreateProcess;
    ///   • swallows and logs every failure, so a UI callback can never be broken by it.
    /// No-op for an empty / invalid / non-existent path, and on the platforms with no user-facing
    /// file browser to hand a path to (Android, iOS, WebGL), where it reports false instead.
    ///
    /// Returns whether a launcher was actually reached, so a caller with a broader target (a file
    /// and the folder holding it) can fall back rather than leave the user with nothing.
    /// </summary>
    public static class BasisFileBrowserUtility
    {
        public static bool Reveal(string path, bool selectFile = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;

                // Resolve to an absolute path and require it to exist locally. Only a real on-disk
                // target ever reaches a launcher below.
                string fullPath = Path.GetFullPath(path);

                bool isFile = File.Exists(fullPath);
                bool isDirectory = Directory.Exists(fullPath);
                if (!isFile && !isDirectory)
                {
                    BasisDebug.LogWarning($"Could not reveal {fullPath} in the file browser: nothing is there.");
                    return false;
                }

                // A legitimate reveal target carries no double-quote or control character; if one
                // does (a crafted name), refuse rather than risk breaking out of the quoted argument.
                if (HasUnsafeCharacters(fullPath))
                {
                    BasisDebug.LogWarning("Refused to reveal a path containing a double-quote or control character.");
                    return false;
                }

                // Only a real file can be highlighted; otherwise reveal the directory itself.
                if (selectFile && !isFile) selectFile = false;
                string directory = isDirectory ? fullPath : (Path.GetDirectoryName(fullPath) ?? fullPath);

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                return RevealWindows(fullPath, directory, selectFile);
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
                // Absolute path (no PATH lookup to hijack) and no shell (UseShellExecute = false); the
                // quoted target has no quote/control chars, so it cannot inject extra `open` flags
                // (e.g. -a to launch an arbitrary application).
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    Arguments = selectFile ? $"-R \"{fullPath}\"" : $"\"{directory}\"",
                    UseShellExecute = false
                });
                return true;
#elif UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
                return RevealLinux(fullPath, directory, selectFile);
#else
                return false;
#endif
            }
            catch (Exception e)
            {
                // Best-effort convenience: a failure here must never propagate into the caller.
                BasisDebug.LogWarning($"Could not reveal path in file browser: {e.Message}");
                return false;
            }
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // Pin to the absolute system explorer so a CWD-planted "explorer.exe" cannot run instead.
        // explorer expects exactly "/select,<file>" to highlight one, and the bare path to open a
        // folder; both targets are verified to exist and carry no double-quote, so neither can break
        // out of the quoted argument.
        //
        // Which of the two ways to reach it works is machine-dependent, and picking just one has
        // broken this twice: a raw CreateProcess is refused outright on some setups (Mono reports it
        // as "Native error= Success"), and elsewhere it is accepted and then quietly opens nothing,
        // while ShellExecuteEx needs an interactive shell to execute through and so is the one that
        // is unavailable headless. Try both before reporting a dead button.
        private static bool RevealWindows(string fullPath, string directory, bool selectFile)
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            string target = (selectFile ? fullPath : directory).Replace('/', '\\');
            string arguments = selectFile ? "/select," + QuoteArgument(target) : QuoteArgument(target);

            if (StartExplorer(explorer, arguments, true)) return true;
            if (StartExplorer(explorer, arguments, false)) return true;

            BasisDebug.LogWarning($"Could not open {target} in Explorer: neither launch route reached one.");
            return false;
        }

        private static bool StartExplorer(string explorer, string arguments, bool useShellExecute)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = explorer,
                    Arguments = arguments,
                    UseShellExecute = useShellExecute,
                    CreateNoWindow = !useShellExecute
                };
                // ShellExecuteEx hands the request to the running shell and reports no new process of
                // its own, so only the CreateProcess route can be checked for one. Neither can prove
                // a window actually appeared, so name the route that was taken: if this is ever dead
                // again the log says which one to stop trusting instead of it being guessed at.
                using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo))
                {
                    if (!useShellExecute && process == null) return false;
                    BasisDebug.Log($"Opened {arguments} in Explorer via {(useShellExecute ? "ShellExecuteEx" : "CreateProcess")}.");
                    return true;
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"Explorer launch via {(useShellExecute ? "ShellExecuteEx" : "CreateProcess")} failed: {e.Message}");
                return false;
            }
        }

        // A trailing backslash immediately before the closing quote escapes it — "C:\dir\" parses
        // back out as C:\dir" — so double the run of them, the literal form Windows argument parsing
        // collapses to one. Reaches a drive root and any path handed in with a trailing separator.
        private static string QuoteArgument(string path)
        {
            int trailing = 0;
            while (trailing < path.Length && path[path.Length - 1 - trailing] == '\\') trailing++;
            return "\"" + path + new string('\\', trailing) + "\"";
        }
#endif

#if UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
        private static readonly string[] SystemBinaryDirectories = { "/usr/bin", "/bin", "/usr/local/bin" };

        private static readonly string[] FolderOpeners =
            { "xdg-open", "gio", "nautilus", "dolphin", "nemo", "thunar", "pcmanfm", "caja" };

        private static bool RevealLinux(string fullPath, string directory, bool selectFile)
        {
            if (selectFile && ShowItemsOverDBus(fullPath)) return true;

            foreach (string opener in FolderOpeners)
            {
                string executable = ResolveSystemBinary(opener);
                if (executable == null) continue;

                string arguments = opener == "gio" ? $"open \"{directory}\"" : $"\"{directory}\"";
                if (LaunchDetached(executable, arguments)) return true;
            }

            BasisDebug.LogWarning($"No file browser could be launched for {directory}.");
            return false;
        }

        private static bool ShowItemsOverDBus(string fullPath)
        {
            string executable = ResolveSystemBinary("dbus-send");
            if (executable == null) return false;

            // dbus-send splits array elements on commas, and AbsoluteUri leaves a comma in a file
            // name literal, so encode it back before it becomes two broken URIs.
            string uri = new Uri(fullPath).AbsoluteUri.Replace(",", "%2C");
            return LaunchDetached(executable,
                "--session --print-reply --dest=org.freedesktop.FileManager1 --type=method_call " +
                "/org/freedesktop/FileManager1 org.freedesktop.FileManager1.ShowItems " +
                $"array:string:\"{uri}\" string:\"\"");
        }

        private static string ResolveSystemBinary(string name)
        {
            foreach (string directory in SystemBinaryDirectories)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static bool LaunchDetached(string executable, string arguments)
        {
            System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            RestoreSystemLoaderEnvironment(startInfo);

            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo))
            {
                if (process == null) return false;
                // Still running after the grace period means a launcher that stays in the
                // foreground, which is a success; an exit tells us whether to try the next one.
                if (!process.WaitForExit(1500)) return true;
                return process.ExitCode == 0;
            }
        }

        // A Unity player, and anything Steam launches, runs with LD_LIBRARY_PATH aimed at its own
        // bundled libraries. A file manager inheriting that loads them instead of the system ones
        // and dies before it can show anything. Steam's own launcher restores the pre-launch values
        // it parked in SYSTEM_*; do the same, and drop LD_PRELOAD, which breaks a child the same way.
        private static void RestoreSystemLoaderEnvironment(System.Diagnostics.ProcessStartInfo startInfo)
        {
            string systemLibraryPath = Environment.GetEnvironmentVariable("SYSTEM_LD_LIBRARY_PATH");
            if (string.IsNullOrEmpty(systemLibraryPath)) startInfo.Environment.Remove("LD_LIBRARY_PATH");
            else startInfo.Environment["LD_LIBRARY_PATH"] = systemLibraryPath;

            string systemPath = Environment.GetEnvironmentVariable("SYSTEM_PATH");
            if (!string.IsNullOrEmpty(systemPath)) startInfo.Environment["PATH"] = systemPath;

            startInfo.Environment.Remove("LD_PRELOAD");
            startInfo.Environment["STEAM_RUNTIME"] = "0";
        }
#endif

        // Rejects the double-quote (the only character that can break out of the quoted argument we
        // pass to a launcher) plus control characters (which a real path never contains and which can
        // corrupt argument/log parsing). Spaces, single quotes and unicode are intentionally allowed.
        private static bool HasUnsafeCharacters(string value)
        {
            foreach (char c in value)
            {
                if (c == '"' || c < ' ' || c == (char)0x7F) return true;
            }
            return false;
        }
    }
}
