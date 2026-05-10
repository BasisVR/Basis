using System;
using Basis.BasisUI;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace HVR.LicenseReview
{
    public static class SettingsProviderLicenses
    {
        private const int MaxLicenseUrlLength = 2048;

        [RuntimeInitializeOnLoadMethod]
        static void Register()
        {
            SettingsProvider.LicensesBuilder = BuildSection;
        }

        static void BuildSection(RectTransform container)
        {
            var licenseManifest = Addressables.LoadAssetAsync<LicenseManifest>("BasisFrameworkLicenseManifest")
                .WaitForCompletion();

            foreach (var license in licenseManifest.baked)
            {
                var toggle = PanelToggle.CreateNewEntry(container);
                toggle.Descriptor.SetTitle(license.productName);
                toggle.Descriptor.SetDescription(license.fullLicenseName);
                toggle.SetValueWithoutNotify(false);

                var licenseText = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
                licenseText.SetTitle($"{license.packageName}/{license.licensePath}");
                licenseText.SetDescription(license.fullLicenseText);

                var openUrl = PanelButton.CreateNew(PanelButton.ButtonStyles.StandardButton, licenseText);
                openUrl.SetSize(new Vector2(540, 60));
                openUrl.Descriptor.SetTitle(BasisLocalization.Get("settings.thirdpartylicenses.openlicenseurl"));
                var capturedUrl = license.licenseUrl;
                openUrl.OnClicked += () =>
                {
                    if (!TryGetSafeHttpsUrl(capturedUrl, out var safeUrl))
                    {
                        return;
                    }

                    BasisMainMenu.Instance.OpenDialogue(
                        "Open external link?",
                        $"Do you want to open this URL in your web browser?\n\n{safeUrl}",
                        "Open",
                        "Cancel",
                        confirmed =>
                        {
                            if (confirmed) Application.OpenURL(safeUrl);
                        });
                };

                licenseText.SetActive(false);
                toggle.OnValueChanged += value =>
                {
                    licenseText.SetActive(value);
                };
            }
        }

        static bool TryGetSafeHttpsUrl(string raw, out string safeUrl)
        {
            safeUrl = null;
            if (string.IsNullOrEmpty(raw)) return false;
            if (raw.Length > MaxLicenseUrlLength) return false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c < 0x20 || c == 0x7F) return false;
            }
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (string.IsNullOrEmpty(uri.Host)) return false;
            safeUrl = uri.AbsoluteUri;
            return true;
        }
    }
}
