using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.UI.UI_Panels;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.BasisUI
{
    public class LibraryProviderDialogPickVersion
    {
        #region PromptUserToPickVersion

        /// <summary>
        /// Height handed to the grid page. The dialog's content column is a vertical layout that
        /// sizes children off their LayoutElement, and a tab page carries none, so without this the
        /// grid collapses to nothing. Leaves room for the dialog's own header above it.
        /// </summary>
        private const float GridHeight = 620f;

        /// <summary>
        /// Date form used on the cards. The full invariant timestamp does not fit a cell, but two
        /// uploads on the same day are common enough that the time has to stay.
        /// </summary>
        private const string ChipDateFormat = "yyyy-MM-dd HH:mm";

        /// <summary>
        /// Shows the stacked versions of one piece of content (newest first) and resolves with the
        /// version the user picked, or null when dismissed. The caller then opens the normal item
        /// overlay for the picked version.
        /// </summary>
        public static async Task<BasisDataStoreItemKeys.ItemKey> PromptUserToPickVersion(BasisMenuPanel panel, List<BasisDataStoreItemKeys.ItemKey> stack)
        {
            BasisDataStoreItemKeys.ItemKey face = stack[0];
            CachedMetaData.TryGetMeta(face.Url ?? string.Empty, out CachedMetaData.CachedContent faceMeta);
            string displayName = !string.IsNullOrEmpty(faceMeta?.Name) ? faceMeta.Name : face.Url;

            DialogBox<BasisDataStoreItemKeys.ItemKey> dialog = DialogBox<BasisDataStoreItemKeys.ItemKey>.Create(panel, new Vector2(1200, 900),
                displayName,
                BasisLocalization.Get("library.dialog.pickVersion.body"),
                AddressableAssets.Sprites.List);

            if (dialog.Descriptor == null)
            {
                return await dialog.WaitAsync();
            }

            PanelButton exitButton = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButton, dialog.Descriptor.Header);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            exitButton.OnClicked += () => dialog.Cancel(null);

            // A version is told apart by its thumbnail first and its date second, so the versions
            // are laid out as the same card grid the library itself uses rather than as a list of
            // rows — the cards the user just came from, one per version.
            PanelTabPage gridPage = PanelTabPage.CreateGrid(dialog.Descriptor.ContentParent);
            gridPage.Descriptor.SetHeight(GridHeight);

            for (int Index = 0; Index < stack.Count; Index++)
            {
                BasisDataStoreItemKeys.ItemKey version = stack[Index];
                CachedMetaData.TryGetMeta(version.Url ?? string.Empty, out CachedMetaData.CachedContent meta);

                PanelButton card = PanelButton.CreateNew(PanelButton.ButtonStyles.Prop, gridPage.Descriptor.ContentParent);
                var cardDescriptor = card.Descriptor;

                bool hasDate = meta != null && meta.Created.HasValue;
                string dateText = hasDate
                    ? meta.Created.Value.ToString(CultureInfo.InvariantCulture) + " UTC"
                    : null;

                // Content built before the connector carried a creation date has none — and that is
                // exactly the older content grouped by NAME, where the version lives in the name and
                // nowhere else. Labelling those cards by date alone made every one of them read "not
                // available", leaving the raw url as the only way to tell the versions apart.
                string versionName = string.IsNullOrWhiteSpace(meta?.Name)
                    ? null
                    : meta.Name;

                string label = versionName ?? dateText ?? BasisLocalization.Get("library.notAvailable");

                cardDescriptor.SetTitle(Index == 0
                    ? string.Format(BasisLocalization.Get("library.stack.latestEntry"), label)
                    : label);

                // Versions of the same upload share a name, so the date is the only thing that tells
                // them apart — and the library card prefab carries no description label, which makes
                // SetDescription a no-op on it. So the date is drawn over the thumbnail instead, in
                // a short form that fits a 200-wide cell. A version with no name has already been
                // given the date as its title above, where a chip would only repeat it.
                if (hasDate && versionName != null)
                {
                    AddDateChip(card, meta.Created.Value.ToString(ChipDateFormat, CultureInfo.InvariantCulture) + " UTC");
                }

                // The full timestamp and the address behind it are the details you only want when
                // you go looking, and a card has no room for either.
                cardDescriptor.SetTooltip(dateText != null
                    ? $"{dateText}\n{version.Url}"
                    : version.Url ?? string.Empty);

                Sprite thumbnail = meta != null ? CachedMetaData.CreateSpriteFromMetaData(meta) : null;
                if (thumbnail != null)
                {
                    card.SetIcon(thumbnail, false);
                }

                bool isActive;
                switch (version.Mode)
                {
                    case BundledContentHolder.Mode.Avatar:
                        isActive = version.Url == BasisLocalPlayer.Instance.AvatarMetaData.BasisRemoteBundleEncrypted.RemoteBeeFileLocation;
                        break;
                    default:
                        isActive = BasisRuntimeSpawnRegistry.CountIgnoreCase(version.Url) > 0;
                        break;
                }
                card.ButtonStyling.ShowIndicator(isActive);

                card.OnClicked += () =>
                {
                    if (dialog.IsBusy) return;
                    dialog.IsBusy = true;

                    dialog.CloseWithResult(version);
                };
            }

            dialog.Descriptor.ForceRebuild();

            return await dialog.WaitAsync();
        }

        /// <summary>
        /// Draws the version's date across the top of the card, over the thumbnail — the card's own
        /// title sits in the bottom strip and its prefab has no second label to hand this to. Copies
        /// the title's TMP font so it matches the rest of the panel, and auto-sizes down because a
        /// timestamp is wider than a 200-wide cell at the normal size.
        /// </summary>
        private static void AddDateChip(PanelButton card, string text)
        {
            var desc = card.Descriptor;

            GameObject chipGo = new GameObject("Version Date", typeof(RectTransform));
            RectTransform rt = (RectTransform)chipGo.transform;
            rt.SetParent(desc.rectTransform, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -14f);
            rt.sizeDelta = new Vector2(-30f, 34f);

            Image background = chipGo.AddComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.6f);
            background.raycastTarget = false;

            LayoutElement layoutElement = chipGo.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;

            GameObject textGo = new GameObject("Date", typeof(RectTransform));
            RectTransform textRt = (RectTransform)textGo.transform;
            textRt.SetParent(rt, false);
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(6f, 0f);
            textRt.offsetMax = new Vector2(-6f, 0f);

            TextMeshProUGUI label = textGo.AddComponent<TextMeshProUGUI>();
            if (desc.TitleLabel != null)
            {
                label.font = desc.TitleLabel.font;
                label.fontSharedMaterial = desc.TitleLabel.fontSharedMaterial;
                label.color = desc.TitleLabel.color;
            }
            label.text = text;
            label.alignment = TextAlignmentOptions.Center;
            label.enableAutoSizing = true;
            label.fontSizeMin = 12;
            label.fontSizeMax = 20;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            label.richText = false;
        }

        #endregion
    }
}
