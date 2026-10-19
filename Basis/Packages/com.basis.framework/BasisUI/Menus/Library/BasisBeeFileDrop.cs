using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.Platform;
using Basis.Scripts.UI.UI_Panels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Turns a BEE file dropped onto the window into a library entry.
    ///
    /// A BEE is encrypted, so a path alone is not enough — the password has to come from
    /// somewhere. The SDK's bundle build already writes one next to the file it produced
    /// (<c>dontuploadmepassword.txt</c>), so a drop looks beside the BEE first; see
    /// <see cref="TryFindSidecarPassword"/> for the full search order. When that password opens
    /// the bundle and its metadata names a content type, the entry is added and the library
    /// switches to the tab it landed on.
    ///
    /// Everything else — no password beside the file, a password that does not open it, a bundle
    /// with no content metadata, or something already in the library — opens the normal add
    /// dialog pre-filled with whatever was worked out, plus a line saying why. Nothing is ever
    /// added on a guess: an entry the user did not confirm would sit in their library failing to
    /// load with no obvious cause.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisBeeFileDrop
    {
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.System;

        /// <summary>
        /// Ceiling on a candidate password file. A password is a line of text; anything larger is
        /// a text file that merely shares the name, and reading it whole to find that out is waste.
        /// </summary>
        private const long MaxPasswordFileBytes = 4 * 1024;

        /// <summary>
        /// Folder-wide password files, tried after the per-file names so a folder holding several
        /// BEEs with their own passwords still resolves each one correctly.
        /// </summary>
        [NoAutoStaticsCleanup] private static readonly string[] SharedPasswordFileNames =
        {
            "dontuploadmepassword.txt", // what BasisBundleBuild writes beside every BEE it builds
            "password.txt",
        };

        // One drop at a time. The no-password path ends in a dialog the user has to answer, and a
        // second batch arriving mid-answer would stack a second dialog over the first.
        private static bool _handling;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            BasisDesktopFileDrop.RegisterAcceptedExtensions(BasisBeeConstants.BasisEncryptedExtension);
            BasisDesktopFileDrop.OnFilesDropped -= OnFilesDropped;
            BasisDesktopFileDrop.OnFilesDropped += OnFilesDropped;
        }

        private static void OnFilesDropped(string[] paths)
        {
            List<string> beeFiles = CollectBeeFiles(paths);
            if (beeFiles.Count == 0) return;

            HandleDroppedBeeFiles(beeFiles);
        }

        /// <summary>
        /// Picks the BEE files out of a dropped batch. The batch is shared with every other drop
        /// consumer, so anything that is not a BEE is left alone rather than reported as an error.
        /// </summary>
        private static List<string> CollectBeeFiles(string[] paths)
        {
            var beeFiles = new List<string>();
            if (paths == null) return beeFiles;

            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!HasBeeExtension(path)) continue;
                if (!File.Exists(path)) continue;

                beeFiles.Add(path);
            }
            return beeFiles;
        }

        public static bool HasBeeExtension(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                return string.Equals(Path.GetExtension(path), BasisBeeConstants.BasisEncryptedExtension, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                // Path.GetExtension rejects paths with invalid characters; a drop can carry one.
                return false;
            }
        }

        private static async void HandleDroppedBeeFiles(List<string> beeFiles)
        {
            if (_handling)
            {
                BasisDebug.LogWarning($"BEE drop: still handling the previous drop, ignoring {beeFiles.Count:N0} file(s).", LogTag);
                return;
            }
            _handling = true;

            try
            {
                // Open the library up front: the metadata probe below reads and decrypts the
                // bundle header, which is not instant, and a drop that appears to do nothing for
                // a second reads as a drop that did not register.
                BasisMainMenu.OpenWithProvider(BasisLocalization.Get("menu.provider.library"));

                for (int i = 0; i < beeFiles.Count; i++)
                {
                    try
                    {
                        await HandleDroppedBeeFile(beeFiles[i]);
                    }
                    catch (Exception exception)
                    {
                        BasisDebug.LogError($"BEE drop: {beeFiles[i]} failed ({exception}).", LogTag);
                    }
                }
            }
            catch (Exception exception)
            {
                // async void: an escaping exception has no caller to catch it.
                BasisDebug.LogError($"BEE drop: could not open the library ({exception}).", LogTag);
            }
            finally
            {
                _handling = false;
            }
        }

        private static async Task HandleDroppedBeeFile(string beePath)
        {
            string fileName = Path.GetFileName(beePath);
            bool foundPassword = TryFindSidecarPassword(beePath, out string password, out string passwordSource);

            if (foundPassword)
            {
                BasisDebug.Log($"BEE drop: {fileName} took its password from {passwordSource}.", LogTag);
            }

            // Hand ValidateEntry a file:// URI rather than the raw path. It splits an optional
            // `url#base64password` fragment off first, which would eat the tail of any path holding
            // a '#' and then decode the remains as a password; percent-encoding the path first
            // takes that whole class of path away from it.
            if (!TryBuildFileUri(beePath, out string beeUri))
            {
                BasisDebug.LogWarning($"BEE drop: could not turn {beePath} into a file URI.", LogTag);
                return;
            }

            // ValidateEntry canonicalises the URI and rejects duplicates, so the drop and the
            // typed-in entry agree on what counts as the same item.
            InputValidation.EntryValidationResponse validation = InputValidation.ValidateEntry(
                beeUri, password, BasisDataStoreItemKeys.DisplayKeys());

            switch (validation.Result)
            {
                case InputValidation.EntryValidationResult.Success:
                    break;

                case InputValidation.EntryValidationResult.EmptyPassword:
                    // Expected whenever nothing was sitting beside the file — hand it to the user.
                    await PromptWithNotice(beeUri, password, "library.drop.notice.noPassword", fileName);
                    return;

                case InputValidation.EntryValidationResult.DuplicateEntry:
                    await PromptWithNotice(beeUri, password, "library.drop.notice.duplicate", fileName);
                    return;

                default:
                    BasisDebug.LogWarning($"BEE drop: {fileName} rejected ({validation.Result}).", LogTag);
                    await PromptWithNotice(beeUri, password, "library.drop.notice.unreadable", fileName);
                    return;
            }

            BundledContentHolder.Mode itemType;
            try
            {
                itemType = (await LibraryProvider.TryDetectModeFromUrl(validation.ProcessedUrl, validation.Password)).Mode;
            }
            catch (Exception exception)
            {
                BasisDebug.LogWarning($"BEE drop: metadata probe threw for {fileName} ({exception.Message}).", LogTag);
                itemType = BundledContentHolder.Mode.Legacy;
            }

            // Legacy covers both "the password did not open it" and "it opened but predates content
            // metadata" — reaching here at all means a password was found, since a missing one is
            // already out as EmptyPassword. Either way the type is unknown, so the dialog asks: it
            // already owns the legacy prompt, and its Add button re-runs this same detection with
            // whatever the user corrects the password to.
            if (itemType == BundledContentHolder.Mode.Legacy)
            {
                await PromptWithNotice(validation.ProcessedUrl, validation.Password, "library.drop.notice.needsType", fileName);
                return;
            }

            await LibraryProvider.AddNewNewItemKey(itemType, validation.ProcessedUrl, validation.Password);
            BasisDebug.Log($"BEE drop: added {fileName} as {itemType}.", LogTag);

            // The library can be closed again between the probe starting and finishing.
            if (LibraryProvider.panel == null || LibraryProvider.panel.IsReleased) return;

            LibraryProvider.TrySwitchToTabFromItemType(itemType);
            await LibraryProvider.RefreshCurrentTab();
        }

        /// <summary>
        /// Turns a dropped path into the absolute <c>file://</c> URI the library stores, so every
        /// character a Windows path is allowed to hold survives the trip through the URL field.
        /// </summary>
        private static bool TryBuildFileUri(string beePath, out string uri)
        {
            uri = null;
            try
            {
                uri = new Uri(Path.GetFullPath(beePath)).AbsoluteUri;
                return true;
            }
            catch (Exception exception)
            {
                BasisDebug.LogWarning($"BEE drop: could not resolve {beePath} ({exception.Message}).", LogTag);
                return false;
            }
        }

        /// <summary>
        /// Opens the add dialog pre-filled with what the drop worked out, headed by the reason it
        /// could not finish on its own.
        /// </summary>
        private static async Task PromptWithNotice(string beeUri, string password, string noticeKey, string fileName)
        {
            if (LibraryProvider.panel == null || LibraryProvider.panel.IsReleased)
            {
                BasisDebug.LogWarning($"BEE drop: library panel is gone, dropping the prompt for {fileName}.", LogTag);
                return;
            }

            await LibraryProviderDialogAdd.PromptUserForNewContent(
                LibraryProvider.panel,
                beeUri,
                password,
                BasisLocalization.Get(noticeKey),
                BasisLocalization.Get(noticeKey + ".description", fileName));
        }

        /// <summary>
        /// Looks beside a BEE file for the password that opens it, in most-specific-first order:
        /// <list type="number">
        /// <item><c>&lt;name&gt;.txt</c> — the BEE's own name with a .txt extension.</item>
        /// <item><c>&lt;name&gt;.BEE.txt</c> — the whole filename with .txt appended.</item>
        /// <item><c>dontuploadmepassword.txt</c>, then <c>password.txt</c> — folder-wide, and the
        /// first of those is what the SDK's bundle build writes, so an untouched build output
        /// folder resolves with no renaming at all.</item>
        /// </list>
        /// Returns the first candidate that exists and holds a usable line of text.
        /// </summary>
        public static bool TryFindSidecarPassword(string beePath, out string password, out string sourceFile)
        {
            password = null;
            sourceFile = null;
            if (string.IsNullOrWhiteSpace(beePath)) return false;

            string directory;
            string fileName;
            try
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(beePath));
                fileName = Path.GetFileName(beePath);
            }
            catch (Exception exception)
            {
                BasisDebug.LogWarning($"BEE drop: could not resolve {beePath} ({exception.Message}).", LogTag);
                return false;
            }

            if (string.IsNullOrEmpty(directory)) return false;

            var candidates = new List<string>(2 + SharedPasswordFileNames.Length)
            {
                Path.ChangeExtension(fileName, ".txt"),
                fileName + ".txt",
            };
            candidates.AddRange(SharedPasswordFileNames);

            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.IsNullOrEmpty(candidates[i])) continue;

                string candidatePath = Path.Combine(directory, candidates[i]);
                if (!TryReadPasswordFile(candidatePath, out string candidatePassword)) continue;

                password = candidatePassword;
                sourceFile = candidatePath;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reads the first non-blank line of a candidate password file. Anything the build could
        /// not have written — missing, oversized, unreadable, blank — is reported as "no password"
        /// rather than as an error, because the next candidate may well be the real one.
        /// </summary>
        private static bool TryReadPasswordFile(string path, out string password)
        {
            password = null;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0 || info.Length > MaxPasswordFileBytes) return false;

                // SaveFileAsync writes the password with no trailing newline, but a user editing the
                // file by hand will leave one. ReadAllLines eats a byte-order mark; the explicit
                // U+FEFF trim catches one that survived a re-encode as a literal character.
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i]?.Trim('\uFEFF').Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    password = line;
                    return true;
                }
            }
            catch (Exception exception)
            {
                BasisDebug.LogWarning($"BEE drop: could not read {path} ({exception.Message}).", LogTag);
            }
            return false;
        }
    }
}
