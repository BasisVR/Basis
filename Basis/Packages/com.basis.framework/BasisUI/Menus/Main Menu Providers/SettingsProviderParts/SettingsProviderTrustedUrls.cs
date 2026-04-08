using Basis.BasisUI;
using System.Collections.Generic;
using UnityEngine;

public static class SettingsProviderTrustedUrls
{
    public static PanelTabPage TrustedUrlsTab(PanelTabGroup tabGroup)
    {
        PanelTabPage tab = PanelTabPage.CreateVertical(tabGroup.Descriptor.ContentParent);
        PanelElementDescriptor descriptor = tab.Descriptor;
        descriptor.SetIcon(AddressableAssets.Sprites.Settings);
        descriptor.SetTitle("Trusted Video URLs");

        RectTransform container = descriptor.ContentParent;

        PanelElementDescriptor infoGroup =
            PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
        infoGroup.SetTitle("Trusted Video URLs");
        infoGroup.SetDescription("URLs in this list are automatically accepted by video players without prompting.");

        List<string> urls = BasisTrustedVideoUrls.GetAll();

        if (urls.Count == 0)
        {
            PanelPasswordField emptyField = PanelPasswordField.CreateNew(infoGroup.ContentParent);
            emptyField.Descriptor.SetTitle("No Trusted URLs");
            emptyField.SetPassword("Accept a video URL to add it here.");
        }
        else
        {
            PanelPasswordField countField = PanelPasswordField.CreateNew(infoGroup.ContentParent);
            countField.Descriptor.SetTitle("Trusted URLs");
            countField.SetPassword($"{urls.Count} URL(s)");

            PanelButton clearAllButton = PanelButton.CreateNew(container);
            clearAllButton.Descriptor.SetTitle("Clear All Trusted URLs");
            clearAllButton.Descriptor.SetDescription("Remove all trusted URLs. Video players will prompt for approval again.");
            clearAllButton.OnClicked += () =>
            {
                BasisMainMenu.Instance.OpenDialogue(
                    "Clear All Trusted URLs",
                    $"Remove all {urls.Count} trusted URL(s)? Video players will prompt for each URL again.",
                    "Clear All",
                    "Cancel",
                    value =>
                    {
                        if (!value) return;
                        BasisTrustedVideoUrls.ClearAll();
                        BasisMainMenu.Close();
                        SettingsProvider.OpenToTab("Trusted URLs");
                    });
            };

            PanelElementDescriptor urlGroup =
                PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            urlGroup.SetTitle("URL List");
            urlGroup.SetDescription("Click a URL to remove it from the trusted list.");

            foreach (string url in urls)
            {
                string capturedUrl = url;
                PanelButton urlButton = PanelButton.CreateNew(urlGroup.ContentParent);
                urlButton.Descriptor.SetTitle(capturedUrl);
                urlButton.Descriptor.SetDescription("Click to remove");
                urlButton.OnClicked += () =>
                {
                    BasisMainMenu.Instance.OpenDialogue(
                        "Remove Trusted URL",
                        $"Remove this URL from trusted list?\n{capturedUrl}",
                        "Remove",
                        "Cancel",
                        value =>
                        {
                            if (!value) return;
                            BasisTrustedVideoUrls.Remove(capturedUrl);
                            BasisMainMenu.Close();
                            SettingsProvider.OpenToTab("Trusted URLs");
                        });
                };
            }
        }

        SettingsProvider.AddResetPageButton(container, "Trusted URLs", () =>
        {
            BasisTrustedVideoUrls.ClearAll();
        });

        descriptor.ForceRebuild();
        return tab;
    }
}
