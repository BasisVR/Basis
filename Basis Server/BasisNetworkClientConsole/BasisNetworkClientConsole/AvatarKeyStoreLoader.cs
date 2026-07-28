using System.Text.Json;

namespace Basis.Config
{
    public static class AvatarKeyStoreLoader
    {
        public readonly struct Entry
        {
            public readonly string Url;
            public readonly string Password;
            public readonly byte LoadMode;

            public Entry(string url, string password, byte loadMode)
            {
                Url = url;
                Password = password;
                LoadMode = loadMode;
            }
        }

        private const int ModeAvatar = 0;
        private const byte LoadModeNetworkDownloadable = 0;

        // Unity puts the keystore under Application.persistentDataPath =
        // LocalLow\<companyName>\<productName>. The project still ships as "Basis Unity"
        // (ProjectSettings.asset — the BasisVR rename never touched company/product), so probe
        // the future name first and fall back to the current one.
        private static readonly string[][] DefaultLocations =
        {
            new[] { "BasisVR", "BasisVR" },
            new[] { "Basis Unity", "Basis Unity" },
        };

        public static string ResolveDefaultPath()
        {
            string localLow = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
            foreach (string[] location in DefaultLocations)
            {
                string candidate = Path.Combine(localLow, location[0], location[1], "ItemKeyStore.json");
                if (File.Exists(candidate)) return candidate;
            }
            return Path.Combine(localLow, DefaultLocations[0][0], DefaultLocations[0][1], "ItemKeyStore.json");
        }

        public static List<Entry> Load(string configuredPath, byte fallbackLoadMode)
        {
            string path = string.IsNullOrWhiteSpace(configuredPath) ? ResolveDefaultPath() : configuredPath.Trim();
            var result = new List<Entry>();

            if (!File.Exists(path))
            {
                BNL.LogWarning($"Avatar keystore not found at [{path}] (also probed the other default LocalLow company/product folders).");
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));

                if (!doc.RootElement.TryGetProperty("Value", out var value) ||
                    !value.TryGetProperty("Data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    BNL.LogWarning($"Avatar keystore at [{path}] has no Value.Data array.");
                    return result;
                }

                foreach (var element in data.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object ||
                        !element.TryGetProperty("Mode", out var modeProp) ||
                        modeProp.ValueKind != JsonValueKind.Number ||
                        modeProp.GetInt32() != ModeAvatar)
                    {
                        continue;
                    }

                    string? url = element.TryGetProperty("Url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String
                        ? urlProp.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    string pass = element.TryGetProperty("Pass", out var passProp) && passProp.ValueKind == JsonValueKind.String
                        ? passProp.GetString() ?? string.Empty
                        : string.Empty;

                    byte loadMode = IsRemoteUrl(url) ? LoadModeNetworkDownloadable : fallbackLoadMode;
                    result.Add(new Entry(url.Trim(), pass, loadMode));
                }
            }
            catch (Exception ex)
            {
                BNL.LogError($"Failed to read avatar keystore at [{path}]: {ex.Message}");
                return new List<Entry>();
            }

            return result;
        }

        private static bool IsRemoteUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return false;
            }

            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }
    }
}
