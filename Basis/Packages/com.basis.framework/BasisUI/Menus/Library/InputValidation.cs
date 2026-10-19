
using Unity.Scripting.LifecycleManagement;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Basis.Scripts.UI.UI_Panels;


namespace Basis.BasisUI
{
    /// <summary>
    /// This static class provides utility methods for validating user input in the library provider, such as validating URLs and applying platform-specific conversions to shared links.
    /// </summary>
    [NoAutoStaticsCleanup]
    public static class InputValidation
    {

        /// <summary>
        /// enum to represent the result of validating a library entry
        /// can be expanded with more specific error types as needed
        /// </summary>
        public enum EntryValidationResult
        {
            None = 0,

            EmptyUrl,
            InvalidUrlFormat,
            InvalidUrlScheme,
            EmptyPassword,
            DuplicateEntry,

            Success
        }

        /// <summary>
        /// struct to represent the response from validating a library entry, including the validation result and any processed data such as a converted URL or extracted password.
        /// </summary>
        public struct EntryValidationResponse
        {
            public EntryValidationResult Result;
            public string ProcessedUrl;
            public string Password;

            //public bool IsValid => Result == EntryValidationResult.Success;
        }

        /// <summary>
        /// Applies platform-specific conversions to shared links, such as converting Google Drive links to direct download URLs.
        /// </summary>
        public static bool ApplyPlatformConversionOfUrl(string sharedLink, out string convertedLink)
        {
            if (IsGoogleDriveLink(sharedLink))
            {
                BasisDebug.Log("Was a Google Drive Link Converting!");
                string fileId = ExtractFileId(sharedLink);
                if (!string.IsNullOrEmpty(fileId))
                {
                    convertedLink = $"https://drive.google.com/uc?export=download&id={fileId}";
                    return true;
                }
                else
                {
                    BasisDebug.LogError("Could not extract File ID from the shared link. Was detected as a google drive", BasisDebug.LogTag.System);
                }
            }

            convertedLink = string.Empty;
            return false;
        }

        /// <summary>
        /// Determines if a URL is a Google Drive link. using Regex
        /// </summary>
        private static bool IsGoogleDriveLink(string url)
        {
            return Regex.IsMatch(url ?? string.Empty, @"^https:\/\/drive\.google\.com\/file\/d\/[a-zA-Z0-9_-]+\/");
        }

        /// <summary>
        /// Extracts the Google Drive file ID from a shared link.
        /// </summary>
        private static string ExtractFileId(string url)
        {
            Match match = Regex.Match(url ?? string.Empty, @"\/file\/d\/([a-zA-Z0-9_-]+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Creates an EntryValidationResponse with a failure result and no processed data.
        /// </summary>
        private static EntryValidationResponse Fail(EntryValidationResult result)
        {
            return new EntryValidationResponse
            {
                Result = result,
                ProcessedUrl = null,
                Password = null
            };
        }

        /// <summary>
        /// Accepts a local filesystem path or <c>file://</c> URI to an existing BEE file and
        /// canonicalises it to an absolute <c>file://</c> URI. Returns false when the path does
        /// not resolve to an existing local file.
        /// </summary>
        private static bool TryNormalizeLocalBeePath(string raw, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            try
            {
                string path = raw;
                if (Uri.TryCreate(raw, UriKind.Absolute, out Uri uri) && uri.IsFile)
                    path = uri.LocalPath;

                if (!File.Exists(path)) return false;

                normalized = new Uri(Path.GetFullPath(path)).AbsoluteUri;
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Quote characters that only ever turn up wrapping a pasted URL. Windows' "Copy as path"
        /// hands over <c>"C:\folder\thing.bee"</c>, chat clients wrap links the same way, and a phone
        /// keyboard swaps in the curly forms. Apostrophes and backticks are in the set because they
        /// are only ever peeled off the ends — one inside a path segment is left alone.
        /// </summary>
        private static bool IsWrappingQuote(char c)
        {
            switch (c)
            {
                case '"':
                case '\'':
                case '`':
                case '\u201C': // “
                case '\u201D': // ”
                case '\u2018': // ‘
                case '\u2019': // ’
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Peels wrapping quotes off a pasted URL, so a copied path or link that arrived with them
        /// attached is not rejected as a malformed URL. Both ends are stripped independently — a lone
        /// leading quote is just as broken as a matched pair — and whitespace sitting inside the
        /// quotes is trimmed afterwards.
        /// </summary>
        public static string StripSurroundingQuotes(string rawUrl)
        {
            string value = (rawUrl ?? string.Empty).Trim();
            if (value.Length == 0) return value;
            if (value.Contains("&amp;")) value = value.Replace("&amp;", "&");

            int start = 0;
            int end = value.Length;

            while (start < end && IsWrappingQuote(value[start])) start++;
            while (end > start && IsWrappingQuote(value[end - 1])) end--;

            if (start == 0 && end == value.Length) return value;

            return value.Substring(start, end - start).Trim();
        }

        /// <summary>
        /// TMP_InputField character filter for the BEE URL box. A double quote is never legal
        /// unencoded in a URL or a Windows path, so one arriving can only have come from a wrapped
        /// paste — drop it as it lands rather than leaving the user staring at a field that looks
        /// right and refuses to load. This only covers in-place editing (typing, Ctrl+V); the virtual
        /// keyboard's paste assigns <c>.text</c> directly, which TMP does not run through this, so
        /// <see cref="StripSurroundingQuotes"/> is what guarantees the quotes are gone by validation.
        /// </summary>
        public static char RejectQuoteCharacter(string text, int charIndex, char addedChar)
        {
            switch (addedChar)
            {
                case '"':
                case '\u201C': // “
                case '\u201D': // ”
                    return '\0';
                default:
                    return addedChar;
            }
        }

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Splits an optional <c>url#password</c> fragment off the URL. If the
        /// caller passed a non-empty <paramref name="rawPassword"/>, that takes
        /// precedence over the fragment; otherwise the fragment is extracted as the
        /// password (supporting raw plaintext/hex strings and legacy base64-encoded strings).
        /// Used by both the in-game add dialog and the admin "default library" add path so the two stay in lockstep.
        /// <para>Wrapping quotes come off before the fragment is split, so a trailing quote on a
        /// pasted <c>"url#pass"</c> share string does not end up corrupting the password.</para>
        /// </summary>
        public static void SplitUrlFragmentPassword(string rawUrl, string rawPassword, out string url, out string password)
        {
            url = StripSurroundingQuotes(rawUrl);
            password = (rawPassword ?? string.Empty).Trim();

            int hashIndex = url.IndexOf('#');
            if (hashIndex < 0) return;

            string fragment = url.Substring(hashIndex + 1);
            url = url.Substring(0, hashIndex);

            if (string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(fragment))
            {
                password = ResolveFragmentPassword(fragment);
            }
        }

        /// <summary>
        /// Resolves a URL fragment into a password string. Handles raw hex keys (e.g. 64-char strings),
        /// URL-encoded strings, legacy base64-encoded strings, and plain text passwords.
        /// </summary>
        public static string ResolveFragmentPassword(string fragment)
        {
            if (string.IsNullOrEmpty(fragment))
                return string.Empty;

            string unescaped = Uri.UnescapeDataString(fragment).Trim();

            // Try decoding as base64 only if it is valid base64, not a raw hex key,
            // and decodes to valid UTF-8 text without unprintable control characters.
            if (TryDecodeBase64(unescaped, out string decoded))
            {
                return decoded;
            }

            return unescaped;
        }

        private static bool TryDecodeBase64(string input, out string decoded)
        {
            decoded = null;
            if (string.IsNullOrEmpty(input) || input.Length % 4 != 0)
                return false;

            // Hex keys (e.g. 32-char / 64-char hex strings generated by bundle builds) are raw passwords, not base64.
            if (IsHexKey(input))
                return false;

            try
            {
                byte[] bytes = Convert.FromBase64String(input);
                if (bytes == null || bytes.Length == 0)
                    return false;

                string text = StrictUtf8.GetString(bytes);

                // Ensure the decoded string is readable text (no null bytes or unprintable control characters)
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (char.IsControl(c) && c != '\t' && c != '\r' && c != '\n')
                        return false;
                }

                decoded = text;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsHexKey(string input)
        {
            if (input.Length != 32 && input.Length != 64)
                return false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return false;
            }

            return true;
        }

        /// <summary>
        /// Validates a library entry and returns an EntryValidationResponse.
        /// </summary>
        /// <param name="rawUrl">raw url from user</param>
        /// <param name="rawPassword">raw password from user</param>
        /// <param name="activeKeys">basis items key store used for determining if the item we are adding already exists</param>
        /// <returns></returns>
        public static EntryValidationResponse ValidateEntry(
            string rawUrl,
            string rawPassword,
            BasisDataStoreItemKeys.ItemKey[] activeKeys)
        {
            SplitUrlFragmentPassword(rawUrl, rawPassword, out string url, out string password);

            if (string.IsNullOrEmpty(url))
                return Fail(EntryValidationResult.EmptyUrl);

            //BasisDebug.Log($"password is now = {password}");

            // Platform conversion
            if (ApplyPlatformConversionOfUrl(url, out string converted))
                url = converted;

            // Normalize URL — http/https for a remote BEE file, or a local file path / file:// URI
            // for a BEE dropped on disk (Steam build local worlds).
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var builder = new UriBuilder(uri)
                {
                    Host = uri.Host.ToLowerInvariant()
                };

                url = builder.Uri.ToString().TrimEnd('/');
            }
            else if (TryNormalizeLocalBeePath(url, out string localUrl))
            {
                url = localUrl;
            }
            else if (uri != null && uri.IsAbsoluteUri && !uri.IsFile)
            {
                return Fail(EntryValidationResult.InvalidUrlScheme);
            }
            else
            {
                return Fail(EntryValidationResult.InvalidUrlFormat);
            }

            if (string.IsNullOrEmpty(password))
                return Fail(EntryValidationResult.EmptyPassword);

            // Duplicate check
            for (int i = 0; i < activeKeys.Length; i++)
            {
                var cur = activeKeys[i];
                if (cur != null && cur.Url == url)
                    return Fail(EntryValidationResult.DuplicateEntry);
            }

            return new EntryValidationResponse
            {
                Result = EntryValidationResult.Success,
                ProcessedUrl = url,
                Password = password
            };
        }
    }
}