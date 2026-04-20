
using System;
using System.Text;
using System.Text.RegularExpressions;
using Basis.Scripts.UI.UI_Panels;


namespace Basis.BasisUI
{
    /// <summary>
    /// This static class provides utility methods for validating user input in the library provider, such as validating URLs and applying platform-specific conversions to shared links.
    /// </summary>
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
            string url = (rawUrl ?? string.Empty).Trim();
            string password = (rawPassword ?? string.Empty).Trim();

            //BasisDebug.Log($"started as password = {password}");

            if (string.IsNullOrEmpty(url))
                return Fail(EntryValidationResult.EmptyUrl);

            // Extract fragment password
            int hashIndex = url.IndexOf('#');
            if (hashIndex >= 0)
            {
                string fragment = url.Substring(hashIndex + 1);
                url = url.Substring(0, hashIndex);

                //BasisDebug.Log("found # processing password");
                if (!string.IsNullOrEmpty(fragment))
                {
                    BasisDebug.Log($"fragment = {fragment}");
                    try
                    {
                        // TODO: need some example test cases for this
                        password = Encoding.UTF8.GetString(Convert.FromBase64String(fragment));

                        //BasisDebug.Log($"setting password = {password}");
                    }
                    catch
                    {
                        // ignore invalid base64
                        BasisDebug.LogWarning("InputValidation failure, failed to parse base64string fragment from url#pass entry.");
                    }
                }
            }

            //BasisDebug.Log($"password is now = {password}");

            // Platform conversion
            if (ApplyPlatformConversionOfUrl(url, out string converted))
                url = converted;

            // Normalize URL
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                return Fail(EntryValidationResult.InvalidUrlFormat);

            if (uri.Scheme != Uri.UriSchemeHttp &&
                uri.Scheme != Uri.UriSchemeHttps)
                return Fail(EntryValidationResult.InvalidUrlScheme);

            var builder = new UriBuilder(uri)
            {
                Host = uri.Host.ToLowerInvariant()
            };

            url = builder.Uri.ToString().TrimEnd('/');

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